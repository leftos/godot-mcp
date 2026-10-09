using System.Diagnostics;
using System.Globalization;

namespace GodotMcp.Capture;

/// <summary>
/// When a run ends: the instant a file holds, one line, a UTC instant in round-trip ("o") format. The file is rewritten
/// while the run goes on (a stop moves it to now), so its end can move after launch. The video and the audio of one run
/// share one instance, and both ask it from their own threads.
/// </summary>
internal sealed class Deadline
{
    /// <summary>How stale the file's value may get; the video and audio loops ask far more often than this.</summary>
    private static readonly TimeSpan RereadInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Stopwatch _sinceRead = Stopwatch.StartNew();
    private DateTime _endUtc;
    private bool _warned;
    private bool _ended;

    private Deadline(string path, DateTime endUtc)
    {
        _path = path;
        _endUtc = endUtc;
    }

    /// <summary>
    /// The end a file holds. Returns null, with the reason in <paramref name="error"/>, when the file cannot be read or holds
    /// no instant.
    /// </summary>
    public static Deadline? FromFile(string path, out string? error) =>
        TryRead(path, out DateTime endUtc, out error) ? new Deadline(path, endUtc) : null;

    /// <summary>
    /// True once the end has passed. A file that has gone missing or holds something unreadable keeps the last end it
    /// held: a rewrite caught half-way must not stop a run, and a deleted file must not make one endless.
    /// </summary>
    public bool HasPassed()
    {
        lock (_gate)
        {
            if (!_ended && _sinceRead.Elapsed >= RereadInterval)
            {
                Reread();
            }
            return _ended || DateTime.UtcNow >= _endUtc;
        }
    }

    /// <summary>
    /// Ends the run now, whatever the file says from here on: the video stopping for any reason (the window closed or
    /// resized, the reader gone) stops the audio with it.
    /// </summary>
    public void EndNow()
    {
        lock (_gate)
        {
            _ended = true;
        }
    }

    public string Describe() => $"until {_path} says stop";

    private void Reread()
    {
        _sinceRead.Restart();
        if (TryRead(_path, out DateTime endUtc, out string? error))
        {
            _endUtc = endUtc;
        }
        else if (!_warned)
        {
            _warned = true;
            Console.Error.WriteLine(FormattableString.Invariant($"godot-mcp-capture: {error}; the run keeps the last end it read, {_endUtc:o}."));
        }
    }

    private static bool TryRead(string path, out DateTime endUtc, out string? error)
    {
        string text;
        try
        {
            // Delete sharing lets a writer replace the file by a move while it is open here.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            text = reader.ReadToEnd().Trim();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            endUtc = default;
            error = $"the deadline file {path} could not be read ({failure.Message})";
            return false;
        }
        if (DateTime.TryParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime instant))
        {
            endUtc = ToUtc(instant);
            error = null;
            return true;
        }
        endUtc = default;
        error = $"the deadline file {path} holds '{text}', not a UTC instant in round-trip (\"o\") format";
        return false;
    }

    /// <summary>An instant with an offset is converted; one with neither "Z" nor an offset is taken as UTC, as the file's format says.</summary>
    private static DateTime ToUtc(DateTime instant) =>
        instant.Kind switch
        {
            DateTimeKind.Utc => instant,
            DateTimeKind.Local => instant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
        };
}
