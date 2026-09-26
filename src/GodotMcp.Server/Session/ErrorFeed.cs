using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Session;

/// <summary>
/// One error or warning the bridge's logger reported: its sequence number in the session's feed, its type ("error" or
/// "warning"), the message, where it is located (the most recent script frame when the engine raised it on a script's
/// behalf, with the engine's own "file:line" in <see cref="Engine"/>, else empty), and Godot's script stack, most recent
/// frame first, as "file:line in function".
/// </summary>
internal sealed record ErrorEntry(
    long Seq,
    string Type,
    string Message,
    string File,
    int Line,
    string Function,
    IReadOnlyList<string> Stack,
    string Engine
)
{
    /// <summary>The file of a script run_script compiled from source.</summary>
    public const string SourceScriptPrefix = "gdscript://";

    public bool IsError => Type == ErrorFeed.ErrorType;

    /// <summary>Whether the error is located in a script compiled from source (run_script's), or its most recent frame is.</summary>
    public bool IsInSourceScript =>
        File.StartsWith(SourceScriptPrefix, StringComparison.Ordinal)
        || (Stack.Count > 0 && Stack[0].StartsWith(SourceScriptPrefix, StringComparison.Ordinal));
}

/// <summary>
/// A session's errors and warnings as the bridge's logger reports them: the last <see cref="Capacity"/> entries, numbered
/// from 1 in arrival order, and how many were lost, evicted here or dropped by the bridge over its own cap. The bridge
/// connection's read loop adds while tools read, so every member is thread-safe.
/// </summary>
internal sealed class ErrorFeed
{
    public const int Capacity = 500;
    public const string ErrorType = "error";
    public const string WarningType = "warning";
    private readonly Lock _lock = new();
    private readonly Queue<ErrorEntry> _entries = new(Capacity);
    private long _lastSeq;
    private long _dropped;

    /// <summary>How many entries were lost: evicted from the ring, or dropped by the bridge before it sent them.</summary>
    public long Dropped
    {
        get
        {
            lock (_lock)
            {
                return _dropped;
            }
        }
    }

    /// <summary>The last sequence number handed out; every entry added after this call has a greater one.</summary>
    public long Mark()
    {
        lock (_lock)
        {
            return _lastSeq;
        }
    }

    /// <summary>
    /// Adds the entries of one bridge frame <c>{type: "errors", entries, dropped}</c>. A malformed entry keeps the fields it
    /// has, so nothing the bridge sends can fault the connection's read loop.
    /// </summary>
    public void Receive(JsonObject frame)
    {
        List<ErrorEntry> entries = frame["entries"] is JsonArray list ? [.. list.OfType<JsonObject>().Select(Parse)] : [];
        Add(entries, ReadNumber(frame["dropped"]));
    }

    /// <summary>Numbers and keeps <paramref name="entries"/> (their own Seq is ignored) and adds the bridge's dropped count.</summary>
    public void Add(IReadOnlyList<ErrorEntry> entries, long bridgeDropped)
    {
        lock (_lock)
        {
            _dropped += Math.Max(0, bridgeDropped);
            foreach (ErrorEntry entry in entries)
            {
                if (_entries.Count == Capacity)
                {
                    _entries.Dequeue();
                    _dropped++;
                }

                _entries.Enqueue(entry with { Seq = ++_lastSeq });
            }
        }
    }

    /// <summary>Up to <paramref name="limit"/> kept entries, errors and warnings, numbered after <paramref name="seq"/>, oldest first.</summary>
    public IReadOnlyList<ErrorEntry> Since(long seq, int limit)
    {
        lock (_lock)
        {
            return [.. _entries.Where(entry => entry.Seq > seq).Take(limit)];
        }
    }

    /// <summary>Every kept entry of type error numbered after <paramref name="seq"/>, oldest first.</summary>
    public IReadOnlyList<ErrorEntry> ErrorsSince(long seq)
    {
        lock (_lock)
        {
            return [.. _entries.Where(entry => entry.Seq > seq && entry.IsError)];
        }
    }

    private static ErrorEntry Parse(JsonObject item)
    {
        List<string> stack = item["stack"] is JsonArray frames ? [.. frames.OfType<JsonValue>().Select(frame => frame.ToString())] : [];
        return new ErrorEntry(
            0,
            HandshakeExpectation.ReadString(item, "type") == WarningType ? WarningType : ErrorType,
            HandshakeExpectation.ReadString(item, "message") ?? string.Empty,
            HandshakeExpectation.ReadString(item, "file") ?? string.Empty,
            (int)ReadNumber(item["line"]),
            HandshakeExpectation.ReadString(item, "function") ?? string.Empty,
            stack,
            HandshakeExpectation.ReadString(item, "engine") ?? string.Empty
        );
    }

    // The bridge writes an int as 7 and a float as 7.0, so a number may arrive either way.
    private static long ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out long whole))
        {
            return whole;
        }

        return value.TryGetValue(out double number) && double.IsFinite(number) ? (long)number : 0;
    }
}
