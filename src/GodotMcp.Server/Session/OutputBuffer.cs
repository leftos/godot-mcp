namespace GodotMcp.Server.Session;

/// <summary>
/// A run of consecutive lines of one stream: the number of the first (null when there are none), the lines, and the drive time
/// each was received (null when the page was built without times).
/// </summary>
internal sealed record OutputPage(long? FirstLine, IReadOnlyList<string> Lines, IReadOnlyList<double>? Times = null);

/// <summary>
/// The last <c>capacity</c> lines of one output stream, and how many lines it has produced in all. Lines are numbered from
/// 1 across everything the stream has had, so a line keeps its number while the buffer rolls, and each line keeps the drive
/// time it was received while it stays held.
/// </summary>
internal sealed class OutputBuffer(int capacity)
{
    /// <summary>The longest a line of <see cref="Page"/> may be before it is cut.</summary>
    public const int MaxLineLength = 1000;

    private readonly Lock _lock = new();
    private readonly Queue<string> _lines = new(capacity);
    private readonly Queue<double> _times = new(capacity);
    private long _totalLines;

    public long TotalLines
    {
        get
        {
            lock (_lock)
            {
                return _totalLines;
            }
        }
    }

    /// <summary>Adds a line received now, stamped with the drive clock.</summary>
    public void Add(string line) => Add(line, DriveClock.NowMs);

    /// <summary>
    /// Adds a line received at drive time <paramref name="receivedMs"/>, dropping the oldest line, and its time, when the ring
    /// is full.
    /// </summary>
    public void Add(string line, double receivedMs)
    {
        lock (_lock)
        {
            if (_lines.Count == capacity)
            {
                _lines.Dequeue();
                _times.Dequeue();
            }

            _lines.Enqueue(line);
            _times.Enqueue(receivedMs);
            _totalLines++;
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> held lines ending just before line <paramref name="before"/>, or the newest ones when it
    /// is null, oldest first, each cut to <see cref="MaxLineLength"/> characters, with their receipt times.
    /// </summary>
    public OutputPage Page(int limit, long? before)
    {
        lock (_lock)
        {
            long oldest = _totalLines - _lines.Count + 1;
            long end = Math.Min(before ?? long.MaxValue, _totalLines + 1);
            long start = Math.Max(oldest, end - limit);
            if (end <= start)
            {
                return new OutputPage(null, []);
            }

            int first = (int)(start - oldest);
            int count = (int)(end - start);
            string[] held = [.. _lines];
            List<string> lines = [.. held.Skip(first).Take(count).Select(line => Cut(line, MaxLineLength))];
            return new OutputPage(start, lines, [.. _times.Skip(first).Take(count)]);
        }
    }

    /// <summary>The held lines uncut, oldest first, with the number of the first (null when there are none) and their times.</summary>
    public OutputPage Raw()
    {
        lock (_lock)
        {
            return new OutputPage(_lines.Count == 0 ? null : _totalLines - _lines.Count + 1, [.. _lines], [.. _times]);
        }
    }

    /// <summary>
    /// <paramref name="text"/> when it fits in <paramref name="max"/> characters, else its start and "… (+N chars)"; the cut
    /// never splits a surrogate pair, so it keeps one character fewer when the last one kept would be a pair's first half.
    /// </summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        int kept = max > 0 && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return $"{text[..kept]}… (+{text.Length - kept} chars)";
    }

    /// <summary>The newest <paramref name="limit"/> lines, oldest first.</summary>
    public List<string> Tail(int limit)
    {
        lock (_lock)
        {
            return [.. _lines.Skip(Math.Max(0, _lines.Count - limit))];
        }
    }
}
