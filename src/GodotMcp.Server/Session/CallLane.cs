using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>Who holds a session's call lane, and since when: the call's description and the time since it entered.</summary>
public sealed record CallLaneHolder(string What, TimeSpan Age);

/// <summary>
/// A session's call lane: one request that runs game code through a call at a time. The MCP SDK starts every request on the
/// thread pool in no set order, so the server does not queue: a second request tries once and is refused, naming the holder.
/// </summary>
internal sealed class CallLane
{
    private Entry? _entry;

    /// <summary>The description of the call holding the lane; null while the lane is free.</summary>
    public string? Holder => Volatile.Read(ref _entry)?.What;

    /// <summary>
    /// Takes the lane for <paramref name="what"/> without waiting, freeing it when the returned scope is disposed; null, with
    /// <paramref name="held"/> naming the call that keeps it, when another call holds the lane.
    /// </summary>
    public IDisposable? TryEnter(string what, out CallLaneHolder? held)
    {
        Entry entry = new(this, what);
        if (Interlocked.CompareExchange(ref _entry, entry, null) is { } current)
        {
            held = new CallLaneHolder(current.What, Stopwatch.GetElapsedTime(current.Entered));
            return null;
        }

        held = null;
        return entry;
    }

    /// <summary>One entry into the lane; disposing it frees the lane only while this entry still holds it.</summary>
    private sealed class Entry(CallLane lane, string what) : IDisposable
    {
        internal string What { get; } = what;

        internal long Entered { get; } = Stopwatch.GetTimestamp();

        public void Dispose() => Interlocked.CompareExchange(ref lane._entry, null, this);
    }
}
