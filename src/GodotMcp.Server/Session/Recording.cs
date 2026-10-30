using System.Globalization;
using System.Text.Json.Serialization;
using GodotMcp.Server.Tools;

namespace GodotMcp.Server.Session;

/// <summary>One clip of a recording: from its start frame up to, not including, its stop frame; to the end when there is no stop.</summary>
internal sealed record ClipSpan(long StartFrame, long? StopFrame)
{
    /// <summary>Where the cut clip is moved, checked at its start mark; null, it stays beside the movie.</summary>
    public OutputTarget? Destination { get; init; }

    /// <summary>Whether the move may replace a file at <see cref="Destination"/>.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>A run of movie frames from <paramref name="From"/> to <paramref name="To"/>, both included.</summary>
internal readonly record struct FrameRange(long From, long To);

/// <summary>
/// What record_mark returns for a Movie Maker recording: the mark, the movie frame it fell on, that frame's time in the movie,
/// the mode, always <see cref="Recording.MovieMode"/>, and for a start with a path, where its clip will be moved.
/// </summary>
internal sealed record MarkResult(string Mark, long Frame, double Seconds)
{
    public string Mode { get; } = Recording.MovieMode;

    /// <summary>The full path the clip this start opens is moved to once it is cut; null without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Destination { get; init; }
}

/// <summary>
/// The movie one start of a recording run writes, the marks an agent set on it, and once the run has ended and the clips are
/// cut, how that went (<see cref="Outcome"/>). Marks alternate start, stop, start…; a start left open clips to the end.
/// </summary>
internal sealed class Recording(string path)
{
    public const string StartMark = "start";
    public const string StopMark = "stop";

    /// <summary>The mode a Movie Maker recording's marks report.</summary>
    public const string MovieMode = "movie";

    private readonly Lock _lock = new();
    private readonly List<ClipSpan> _clips = [];
    private readonly List<FrameRange> _calls = [];
    private ClipSpan? _open;

    /// <summary>The full movie's absolute path.</summary>
    public string Path { get; } = path;

    /// <summary>Whether the cut clips keep only the frames drawn during tool calls, with their audio.</summary>
    public bool DropIdle { get; init; }

    /// <summary>How the recording ended; null while its run goes on.</summary>
    public RecordingResult? Outcome { get; set; }

    /// <summary>Whether the game was killed rather than quitting, which leaves the movie unfinalised.</summary>
    public bool Killed { get; set; }

    /// <summary>
    /// A new recording's file: <c>&lt;project&gt;/.godot/godot-mcp/recordings/&lt;UTC yyyyMMdd-HHmmss-fff&gt;-&lt;session&gt;.avi</c>.
    /// </summary>
    public static string PathFor(string projectDir, string session, DateTime utcNow) =>
        System.IO.Path.Combine(
            projectDir,
            ".godot",
            "godot-mcp",
            "recordings",
            $"{utcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{session}.avi"
        );

    /// <summary>A frame's time in the movie, in seconds.</summary>
    public static double SecondsAt(long frame) => (double)frame / GodotCommandLine.MovieFramesPerSecond;

    /// <summary>Notes a start or stop mark at <paramref name="frame"/>, the number of frames written so far.</summary>
    /// <exception cref="SessionException">A start while a clip is open, a stop with none open, or a stop on its start's frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mark"/> is neither start nor stop.</exception>
    public MarkResult Mark(string mark, long frame) => Mark(mark, frame, null, overwrite: false);

    /// <summary>
    /// Notes a start or stop mark at <paramref name="frame"/>; a start keeps <paramref name="destination"/> and
    /// <paramref name="overwrite"/> for its clip, once it is accepted.
    /// </summary>
    /// <exception cref="SessionException">A start while a clip is open, a stop with none open, or a stop on its start's frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mark"/> is neither start nor stop.</exception>
    public MarkResult Mark(string mark, long frame, OutputTarget? destination, bool overwrite)
    {
        lock (_lock)
        {
            switch (mark)
            {
                case StartMark:
                    Start(new ClipSpan(frame, null) { Destination = destination, Overwrite = overwrite });
                    break;
                case StopMark:
                    Stop(frame);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mark), mark, "A mark is start or stop.");
            }
        }

        return new MarkResult(mark, frame, SecondsAt(frame)) { Destination = destination?.FullPath };
    }

    /// <summary>Refuses a destination an earlier clip of this recording already goes to, so its delivery cannot be refused late.</summary>
    /// <exception cref="SessionException">An earlier clip's start named a path reaching the same final path, in any case.</exception>
    public void CheckDestination(OutputTarget? destination)
    {
        if (destination is null)
        {
            return;
        }

        lock (_lock)
        {
            List<ClipSpan> clips = _open is { } open ? [.. _clips, open] : _clips;
            for (int index = 0; index < clips.Count; index++)
            {
                if (string.Equals(clips[index].Destination?.FinalPath, destination.FinalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SessionException(
                        $"path {destination.Requested} is already where clip {index + 1} of this recording goes; name another file"
                    );
                }
            }
        }
    }

    /// <summary>Every clip in mark order, a start still open running to the end of the movie.</summary>
    public IReadOnlyList<ClipSpan> Clips()
    {
        lock (_lock)
        {
            return _open is { } open ? [.. _clips, open] : [.. _clips];
        }
    }

    /// <summary>Refuses a mark the clip state cannot take now, as <see cref="Mark(string, long, OutputTarget?, bool)"/> would.</summary>
    /// <exception cref="SessionException">A start while a clip is open, or a stop with none open.</exception>
    public void CheckMark(string mark)
    {
        lock (_lock)
        {
            if (mark == StartMark && _open is { } open)
            {
                throw new SessionException($"A clip is already open, started at frame {open.StartFrame}; mark stop before the next start.");
            }

            if (mark == StopMark && _open is null)
            {
                throw new SessionException("No clip is open; mark start first.");
            }
        }
    }

    /// <summary>
    /// Notes the frames one call ran in, from the frame its request was read in to the one it was answered in, both included;
    /// a call overlapping or adjacent to one already noted merges with it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="read"/> is after <paramref name="replied"/>.</exception>
    public void AddCall(long read, long replied)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(read, replied);
        lock (_lock)
        {
            long from = read;
            long to = replied;
            int index = 0;
            while (index < _calls.Count && _calls[index].To + 1 < from)
            {
                index++;
            }

            while (index < _calls.Count && _calls[index].From <= to + 1)
            {
                from = Math.Min(from, _calls[index].From);
                to = Math.Max(to, _calls[index].To);
                _calls.RemoveAt(index);
            }

            _calls.Insert(index, new FrameRange(from, to));
        }
    }

    /// <summary>
    /// The merged frame ranges calls ran in, in frame order, clipped to <paramref name="clip"/>: from its start frame up to, not
    /// including, its stop frame, or to the last call when it has no stop.
    /// </summary>
    public IReadOnlyList<FrameRange> CallRanges(ClipSpan clip)
    {
        long from = clip.StartFrame;
        long to = clip.StopFrame is { } stop ? stop - 1 : long.MaxValue;
        lock (_lock)
        {
            return
            [
                .. _calls
                    .Where(range => range.To >= from && range.From <= to)
                    .Select(range => new FrameRange(Math.Max(range.From, from), Math.Min(range.To, to))),
            ];
        }
    }

    private void Start(ClipSpan opened)
    {
        CheckMark(StartMark);
        CheckDestination(opened.Destination);
        _open = opened;
    }

    private void Stop(long frame)
    {
        CheckMark(StopMark);
        ClipSpan open = _open!;
        if (frame <= open.StartFrame)
        {
            throw new SessionException($"A clip needs at least one frame: stop is at frame {frame} and its start at frame {open.StartFrame}.");
        }

        _clips.Add(open with { StopFrame = frame });
        _open = null;
    }
}
