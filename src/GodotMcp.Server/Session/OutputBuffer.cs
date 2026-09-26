namespace GodotMcp.Server.Session;

/// <summary>The last <c>capacity</c> lines of one output stream, and how many lines it has produced in all.</summary>
internal sealed class OutputBuffer(int capacity)
{
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
    /// The lines produced after the first <paramref name="mark"/> lines (a past <see cref="TotalLines"/>) that the buffer
    /// still holds, oldest first.
    /// </summary>
    public List<string> Since(long mark)
    {
        lock (_lock)
        {
            int count = (int)Math.Clamp(_totalLines - mark, 0, _lines.Count);
            return [.. _lines.Skip(_lines.Count - count)];
        }
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
