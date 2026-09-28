namespace GodotMcp.Server.Session;

/// <summary>
/// Whether a tool process has stalled: neither its output nor its tree's CPU has moved for <paramref name="limit"/>. Every
/// step of a build, an import or an encode prints or burns CPU, so a tree that does neither is blocked.
/// </summary>
internal sealed class StallWatch(TimeProvider time, TimeSpan limit)
{
    private long _progressAt = time.GetTimestamp();
    private long _lines;
    private long _cpu;

    /// <summary>How long neither the output nor the CPU has moved, as of the last <see cref="Observe"/>.</summary>
    public TimeSpan Quiet { get; private set; }

    /// <param name="outputLines">The lines the process has written so far.</param>
    /// <param name="cpuTicks">The CPU its tree has used so far.</param>
    /// <returns>True once neither has moved for the limit.</returns>
    public bool Observe(long outputLines, long cpuTicks)
    {
        long now = time.GetTimestamp();
        if (outputLines != _lines || cpuTicks != _cpu)
        {
            _lines = outputLines;
            _cpu = cpuTicks;
            _progressAt = now;
        }

        Quiet = time.GetElapsedTime(_progressAt, now);
        return Quiet >= limit;
    }
}
