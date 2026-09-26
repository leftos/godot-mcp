namespace GodotMcp.Server.Session;

/// <summary>A run of consecutive lines of one stream: the number of the first (null when there are none) and the lines.</summary>
internal sealed record OutputPage(long? FirstLine, IReadOnlyList<string> Lines);

/// <summary>
/// The last <c>capacity</c> lines of one output stream, and how many lines it has produced in all. Lines are numbered from
/// 1 across everything the stream has had, so a line keeps its number while the buffer rolls.
/// </summary>
internal sealed class OutputBuffer(int capacity)
{
    /// <summary>The longest a line of <see cref="Page"/> may be before it is cut.</summary>
    public const int MaxLineLength = 1000;

    private readonly Lock _lock = new();
    private readonly Queue<string> _lines = new(capacity);
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

    public void Add(string line)
    {
        lock (_lock)
        {
            if (_lines.Count == capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
            _totalLines++;
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> held lines ending just before line <paramref name="before"/>, or the newest ones when it
    /// is null, oldest first, each cut to <see cref="MaxLineLength"/> characters.
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

            List<string> lines = [.. _lines.Skip((int)(start - oldest)).Take((int)(end - start)).Select(line => Cut(line, MaxLineLength))];
            return new OutputPage(start, lines);
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
