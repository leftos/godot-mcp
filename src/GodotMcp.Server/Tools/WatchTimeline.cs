using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The shape of a watch's timeline: a value, a signal's argument or a voice's stream over <see cref="MaxValueLength"/>
/// characters of JSON as {valuePreview, valueLength}; the signal tracks' events shared fairly up to <see cref="MaxEvents"/>
/// and the audio players' voices up to <see cref="MaxVoices"/>; and a timeline over <see cref="MaxResultLength"/> characters
/// cut from the middle of its longest lists (a track's points, the events, or the voices), each counting its cut.
/// </summary>
internal static class WatchTimeline
{
    /// <summary>The most characters of JSON a point's value, a track's first or its last keeps before its preview.</summary>
    internal const int MaxValueLength = 200;

    /// <summary>The most characters of JSON a timeline keeps, get_game_state's list budget.</summary>
    internal const int MaxResultLength = 40_000;

    /// <summary>The most events a timeline keeps across its signal tracks.</summary>
    internal const int MaxEvents = 300;

    /// <summary>The most voices a timeline keeps across its audio players.</summary>
    internal const int MaxVoices = 300;

    /// <summary>The most players' paths a timeline's voiceCounts keeps.</summary>
    internal const int MaxVoiceCountPaths = 100;

    /// <summary>Where the bridge's event names the signal track it belongs to, after [frame, gameMs, node, signal, args].</summary>
    private const int EventTrackIndex = 5;

    /// <summary>Where a voice, [frame, gameMs, event, path, {...}], names its player.</summary>
    private const int VoicePlayerIndex = 3;

    /// <summary>Where a voice holds its {stream?, bus?, maxPolyphony?, position?, reason?}.</summary>
    private const int VoiceInfoIndex = 4;

    /// <summary>
    /// A timeline as the tool returns it: each track's long values and each event's long arguments previewed, the events
    /// shared fairly across signal tracks (<see cref="ShareEvents"/>), and the points and events cut from the middle, longest
    /// list first, until the whole is at most <paramref name="maxLength"/> characters; a track that lost points says how
    /// many in cut, and lost events are counted in eventsCut. A list keeps at least its first and last item.
    /// </summary>
    internal static JsonObject Shape(JsonObject reply, int maxLength)
    {
        foreach (JsonObject track in Tracks(reply))
        {
            PreviewMember(track, "first");
            PreviewMember(track, "last");
            foreach (JsonArray point in (track["points"] as JsonArray ?? []).OfType<JsonArray>())
            {
                if (point.Count > 2 && RuntimeTools.ValuePreview(point[2], MaxValueLength) is JsonObject preview)
                {
                    point[2] = preview;
                }
            }
        }

        ShareEvents(reply);
        ShareVoices(reply);
        CutLists(reply, maxLength);
        return reply;
    }

    /// <summary>
    /// Each of <paramref name="available"/>'s tracks' share of <paramref name="capacity"/>: capacity divided by the tracks
    /// left, the tracks with the fewest first, so a track with fewer than its share lends the rest to the tracks after it.
    /// </summary>
    internal static int[] FairShares(IReadOnlyList<int> available, int capacity)
    {
        int[] shares = new int[available.Count];
        int remaining = capacity;
        int left = available.Count;
        foreach (int index in Enumerable.Range(0, available.Count).OrderBy(index => available[index]))
        {
            shares[index] = Math.Min(available[index], remaining / left);
            remaining -= shares[index];
            left--;
        }

        return shares;
    }

    private static IEnumerable<JsonObject> Tracks(JsonObject reply) => (reply["tracks"] as JsonArray ?? []).OfType<JsonObject>();

    private static void PreviewMember(JsonObject track, string member)
    {
        if (RuntimeTools.ValuePreview(track[member], MaxValueLength) is JsonObject preview)
        {
            track[member] = preview;
        }
    }

    /// <summary>
    /// Keeps each signal track's fair share of the bridge's events (<see cref="FairShares"/>), its earliest, in emission order;
    /// counts every emission left out in eventsDropped, the bridge's uncounted overflow included; drops each event's track
    /// index and the bridge's per-track counts (eventTracks: [{kept, total}]), and previews each long argument.
    /// </summary>
    private static void ShareEvents(JsonObject reply)
    {
        var counts = reply["eventTracks"] as JsonArray;
        reply.Remove("eventTracks");
        if (reply["events"] is not JsonArray events)
        {
            return;
        }

        int[] kept = KeptPerTrack(events, counts?.Count ?? 0);
        int total = TotalEmissions(counts, kept);
        int[] shares = FairShares(kept, MaxEvents);
        for (int index = events.Count - 1; index >= 0; index--)
        {
            KeepOrRemove(events, index, shares, kept);
        }

        if (total > events.Count)
        {
            reply["eventsDropped"] = total - events.Count;
        }
    }

    /// <summary>The events each signal track has in <paramref name="events"/>, for at least <paramref name="tracks"/> tracks.</summary>
    private static int[] KeptPerTrack(JsonArray events, int tracks)
    {
        int[] kept = new int[tracks];
        foreach (JsonArray item in events.OfType<JsonArray>())
        {
            int track = TrackOf(item);
            if (track >= kept.Length)
            {
                Array.Resize(ref kept, track + 1);
            }

            kept[track]++;
        }

        return kept;
    }

    /// <summary>Every emission the bridge counted: each track's total, else the events it kept.</summary>
    private static int TotalEmissions(JsonArray? counts, int[] kept) =>
        counts is null ? kept.Sum() : counts.Select((count, track) => Count(count?["total"]) ?? kept[track]).Sum();

    /// <summary>
    /// Keeps the event at <paramref name="index"/>, walked from the last, while its track has more events left than its share,
    /// else removes it; <paramref name="left"/> counts each track's events not walked yet.
    /// </summary>
    private static void KeepOrRemove(JsonArray events, int index, int[] shares, int[] left)
    {
        if (events[index] is not JsonArray item)
        {
            events.RemoveAt(index);
            return;
        }

        int track = TrackOf(item);
        left[track]--;
        if (left[track] >= shares[track])
        {
            events.RemoveAt(index);
            return;
        }

        if (item.Count > EventTrackIndex)
        {
            item.RemoveAt(EventTrackIndex);
        }

        if (item.Count > 4 && item[4] is JsonArray args)
        {
            for (int arg = 0; arg < args.Count; arg++)
            {
                if (RuntimeTools.ValuePreview(args[arg], MaxValueLength) is JsonObject preview)
                {
                    args[arg] = preview;
                }
            }
        }
    }

    /// <summary>
    /// Keeps each audio player's fair share of the bridge's voices (<see cref="EarliestFairShares"/>), its earliest, in the
    /// order they were read; counts every voice left out in voicesDropped, from the bridge's count of every voice it read
    /// (voiceTotal, which the result leaves out); cuts voiceCounts to its busiest paths (<see cref="CutVoiceCounts"/>); and
    /// previews each kept voice's long stream.
    /// </summary>
    private static void ShareVoices(JsonObject reply)
    {
        if (reply["audio"] is not JsonObject audio)
        {
            return;
        }

        int? total = Count(audio["voiceTotal"]);
        audio.Remove("voiceTotal");
        CutVoiceCounts(audio);
        if (audio["voices"] is not JsonArray voices)
        {
            return;
        }

        (List<string> players, Dictionary<string, int> left) = VoicesPerPlayer(voices);
        int[] fair = EarliestFairShares([.. players.Select(player => left[player])], MaxVoices);
        var shares = players.Zip(fair).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        int read = voices.Count;
        for (int index = voices.Count - 1; index >= 0; index--)
        {
            KeepOrRemoveVoice(voices, index, shares, left);
        }

        int dropped = (total ?? read) - voices.Count;
        if (dropped > 0)
        {
            audio["voicesDropped"] = dropped;
        }
    }

    /// <summary>Each player's voices in <paramref name="voices"/>, the players in the order of their first voice.</summary>
    private static (List<string> Players, Dictionary<string, int> Counts) VoicesPerPlayer(JsonArray voices)
    {
        List<string> players = [];
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (JsonNode? voice in voices)
        {
            string player = PlayerOf(voice);
            if (!counts.TryGetValue(player, out int count))
            {
                players.Add(player);
            }

            counts[player] = count + 1;
        }

        return (players, counts);
    }

    /// <summary>
    /// Each of <paramref name="available"/>'s players' share of <paramref name="capacity"/>, the players in the order of their
    /// first voice: each keeps up to the highest level that fits (<see cref="ShareLevel"/>), and what that leaves goes one
    /// voice each to the earliest players holding more, so with more players than capacity the earliest keep one each.
    /// </summary>
    private static int[] EarliestFairShares(IReadOnlyList<int> available, int capacity)
    {
        int level = ShareLevel(available, capacity);
        int[] shares = [.. available.Select(count => Math.Min(count, level))];
        int left = capacity - shares.Sum();
        for (int index = 0; index < shares.Length && left > 0; index++)
        {
            if (available[index] > level)
            {
                shares[index]++;
                left--;
            }
        }

        return shares;
    }

    /// <summary>The highest level such that every player keeping up to it, or all it has when that is fewer, fits in
    /// <paramref name="capacity"/>.</summary>
    private static int ShareLevel(IReadOnlyList<int> available, int capacity)
    {
        int low = 0;
        int high = available.Count == 0 ? 0 : available.Max();
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (available.Sum(count => Math.Min(count, middle)) <= capacity)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// Keeps voiceCounts' <see cref="MaxVoiceCountPaths"/> paths with the most starts and ends, the earliest on ties (the
    /// bridge adds a path when it first counts it), in their order, and counts the rest in voiceCountsCut.
    /// </summary>
    private static void CutVoiceCounts(JsonObject audio)
    {
        if (audio["voiceCounts"] is not JsonObject counts || counts.Count <= MaxVoiceCountPaths)
        {
            return;
        }

        string[] cut =
        [
            .. counts
                .Select((pair, order) => (Path: pair.Key, Voices: StartsAndEnds(pair.Value), Order: order))
                .OrderByDescending(path => path.Voices)
                .ThenBy(path => path.Order)
                .Skip(MaxVoiceCountPaths)
                .Select(path => path.Path),
        ];
        foreach (string path in cut)
        {
            counts.Remove(path);
        }

        audio["voiceCountsCut"] = cut.Length;
    }

    private static int StartsAndEnds(JsonNode? count) => (Count(count?["starts"]) ?? 0) + (Count(count?["ends"]) ?? 0);

    /// <summary>
    /// Keeps the voice at <paramref name="index"/>, walked from the last, while its player has more voices left than its share,
    /// previewing its stream, else removes it; <paramref name="left"/> counts each player's voices not walked yet.
    /// </summary>
    private static void KeepOrRemoveVoice(JsonArray voices, int index, Dictionary<string, int> shares, Dictionary<string, int> left)
    {
        string player = PlayerOf(voices[index]);
        left[player]--;
        if (left[player] >= shares[player])
        {
            voices.RemoveAt(index);
            return;
        }

        if (voices[index] is JsonArray { Count: > VoiceInfoIndex } voice && voice[VoiceInfoIndex] is JsonObject info)
        {
            PreviewMember(info, "stream");
        }
    }

    private static string PlayerOf(JsonNode? voice) =>
        voice is JsonArray { Count: > VoicePlayerIndex } item && item[VoicePlayerIndex] is JsonValue path && path.TryGetValue(out string? name)
            ? name
            : "";

    private static int TrackOf(JsonArray item) => item.Count > EventTrackIndex ? Count(item[EventTrackIndex]) ?? 0 : 0;

    // GDScript's JSON may write an integer as a float, so a count is read as a double.
    private static int? Count(JsonNode? node) =>
        node is null ? null
        : node.AsValue().TryGetValue(out int count) ? count
        : (int)node.GetValue<double>();

    private static void CutLists(JsonObject reply, int maxLength)
    {
        List<CutList> lists = ListsOf(reply);
        int excess = reply.ToJsonString().Length - maxLength;
        while (excess > 0)
        {
            CutList? longest = lists.Where(list => list.CanCut).MaxBy(list => list.Length);
            if (longest is null)
            {
                return;
            }

            while (excess > 0 && longest.CanCut)
            {
                excess -= longest.CutMiddle();
                if (lists.Any(list => list.CanCut && list.Length > longest.Length))
                {
                    break;
                }
            }

            if (excess <= 0)
            {
                // The cut counts add characters of their own.
                excess = reply.ToJsonString().Length - maxLength;
            }
        }
    }

    /// <summary>The lists a cut may shorten: each track's points, the events when the watch has signal tracks, and the
    /// voices when it has an audio track.</summary>
    private static List<CutList> ListsOf(JsonObject reply)
    {
        List<CutList> lists = [.. Tracks(reply).Select(track => new CutList(track, "points", "cut"))];
        if (reply["events"] is JsonArray)
        {
            lists.Add(new CutList(reply, "events", "eventsCut"));
        }

        if (reply["audio"] is JsonObject audio && audio["voices"] is JsonArray)
        {
            lists.Add(new CutList(audio, "voices", "voicesCut"));
        }

        return lists;
    }

    /// <summary>A list in the timeline (a track's points, or the events) and each item's length in JSON, cut from the middle
    /// one item at a time and counted in its owner's <c>cutKey</c>.</summary>
    private sealed class CutList
    {
        private readonly JsonObject _owner;
        private readonly string _cutKey;
        private readonly JsonArray _items;
        private readonly List<int> _lengths;

        public CutList(JsonObject owner, string listKey, string cutKey)
        {
            _owner = owner;
            _cutKey = cutKey;
            _items = owner[listKey] as JsonArray ?? [];
            _lengths = [.. _items.Select(item => (item?.ToJsonString().Length ?? 4) + 1)];
            Length = _lengths.Sum();
        }

        /// <summary>The characters the items take, a comma each.</summary>
        public int Length { get; private set; }

        /// <summary>Whether an item is left between the first and the last.</summary>
        public bool CanCut => _items.Count > 2;

        /// <summary>Removes the middle item, counts it in the owner's cut, and returns the characters it took.</summary>
        public int CutMiddle()
        {
            int index = _items.Count / 2;
            int length = _lengths[index];
            _items.RemoveAt(index);
            _lengths.RemoveAt(index);
            Length -= length;
            _owner[_cutKey] = (_owner[_cutKey]?.GetValue<int>() ?? 0) + 1;
            return length;
        }
    }
}
