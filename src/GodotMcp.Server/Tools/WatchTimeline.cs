using System.Text.Json.Nodes;

namespace GodotMcp.Server.Tools;

/// <summary>
/// The shape of a watch's timeline: a value over
/// <see cref="MaxValueLength"/> characters of JSON as {valuePreview, valueLength}; and a timeline over
/// <see cref="MaxResultLength"/> characters cut from the middle of its longest tracks' points, each track counting its cut.
/// </summary>
internal static class WatchTimeline
{
    /// <summary>The most characters of JSON a point's value, a track's first or its last keeps before its preview.</summary>
    internal const int MaxValueLength = 200;

    /// <summary>The most characters of JSON a timeline keeps, get_game_state's list budget.</summary>
    internal const int MaxResultLength = 40_000;

    /// <summary>
    /// A timeline as the tool returns it: each track's long values previewed, and its points cut from the middle,
    /// longest track first, until the whole is at most <paramref name="maxLength"/> characters; a track that lost points says
    /// how many in cut. A track keeps at least its first and last point.
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

        CutPoints(reply, maxLength);
        return reply;
    }

    private static IEnumerable<JsonObject> Tracks(JsonObject reply) => (reply["tracks"] as JsonArray ?? []).OfType<JsonObject>();

    private static void PreviewMember(JsonObject track, string member)
    {
        if (RuntimeTools.ValuePreview(track[member], MaxValueLength) is JsonObject preview)
        {
            track[member] = preview;
        }
    }

    private static void CutPoints(JsonObject reply, int maxLength)
    {
        List<TrackPoints> tracks = [.. Tracks(reply).Select(track => new TrackPoints(track))];
        int excess = reply.ToJsonString().Length - maxLength;
        while (excess > 0)
        {
            TrackPoints? longest = tracks.Where(track => track.CanCut).MaxBy(track => track.Length);
            if (longest is null)
            {
                return;
            }

            while (excess > 0 && longest.CanCut)
            {
                excess -= longest.CutMiddle();
                if (tracks.Any(track => track.CanCut && track.Length > longest.Length))
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

    /// <summary>A track's points and each one's length in JSON, cut from the middle one at a time.</summary>
    private sealed class TrackPoints
    {
        private readonly JsonObject _track;
        private readonly JsonArray _points;
        private readonly List<int> _lengths;

        public TrackPoints(JsonObject track)
        {
            _track = track;
            _points = track["points"] as JsonArray ?? [];
            _lengths = [.. _points.Select(point => (point?.ToJsonString().Length ?? 4) + 1)];
            Length = _lengths.Sum();
        }

        /// <summary>The characters the points take, a comma each.</summary>
        public int Length { get; private set; }

        /// <summary>Whether a point is left between the first and the last.</summary>
        public bool CanCut => _points.Count > 2;

        /// <summary>Removes the middle point, counts it in the track's cut, and returns the characters it took.</summary>
        public int CutMiddle()
        {
            int index = _points.Count / 2;
            int length = _lengths[index];
            _points.RemoveAt(index);
            _lengths.RemoveAt(index);
            Length -= length;
            _track["cut"] = (_track["cut"]?.GetValue<int>() ?? 0) + 1;
            return length;
        }
    }
}
