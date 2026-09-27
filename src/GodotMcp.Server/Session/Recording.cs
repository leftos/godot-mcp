using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>One clip of a recording: from its start frame up to, not including, its stop frame; to the end when there is no stop.</summary>
internal sealed record ClipSpan(long StartFrame, long? StopFrame);

/// <summary>What record_mark returns: the mark, the movie frame it fell on, and that frame's time in the movie.</summary>
internal sealed record MarkResult(string Mark, long Frame, double Seconds);

/// <summary>
/// The movie one start of a recording run writes, the marks an agent set on it, and once the run has ended and the clips are
/// cut, how that went (<see cref="Outcome"/>). Marks alternate start, stop, start…; a start left open clips to the end.
/// </summary>
internal sealed class Recording(string path)
{
    public const string StartMark = "start";
    public const string StopMark = "stop";

    private readonly Lock _lock = new();
    private readonly List<ClipSpan> _clips = [];
    private long? _openStart;

    /// <summary>The full movie's absolute path.</summary>
    public string Path { get; } = path;

    /// <summary>Whether the cut clips drop frames identical to the one before, and their audio.</summary>
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
    public MarkResult Mark(string mark, long frame)
    {
        lock (_lock)
        {
            switch (mark)
            {
                case StartMark:
                    Start(frame);
                    break;
                case StopMark:
                    Stop(frame);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mark), mark, "A mark is start or stop.");
            }
        }

        return new MarkResult(mark, frame, SecondsAt(frame));
    }

    /// <summary>Every clip in mark order, a start still open running to the end of the movie.</summary>
    public IReadOnlyList<ClipSpan> Clips()
    {
        lock (_lock)
        {
            return _openStart is { } open ? [.. _clips, new ClipSpan(open, null)] : [.. _clips];
        }
    }

    private void Start(long frame)
    {
        if (_openStart is { } open)
        {
            throw new SessionException($"A clip is already open, started at frame {open}; mark stop before the next start.");
        }

        _openStart = frame;
    }

    private void Stop(long frame)
    {
        if (_openStart is not { } start)
        {
            throw new SessionException("No clip is open; mark start first.");
        }

        if (frame <= start)
        {
            throw new SessionException($"A clip needs at least one frame: stop is at frame {frame} and its start at frame {start}.");
        }

        _clips.Add(new ClipSpan(start, frame));
        _openStart = null;
    }
}
