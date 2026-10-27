namespace GodotMcp.Capture;

/// <summary>One span of a run's clip over which the window was minimized, on the clip's clock.</summary>
internal readonly record struct MinimizedSpan(TimeSpan Start, TimeSpan End)
{
    /// <summary>The helper's line for the span, without its name prefix; the server reads the numbers back from it.</summary>
    public string Line => FormattableString.Invariant($"minimized from {Start.TotalSeconds:0.0} s to {End.TotalSeconds:0.0} s of the clip");
}

/// <summary>
/// The spans a run's window was minimized over, from the minimized state read on each frame tick. A span opens at the first
/// tick that sees the window minimized and closes at the first that sees it restored, or at the run's end. A minimize seen on
/// one tick only lasted less than a tick, held no frame for long, and is not a span.
/// </summary>
internal sealed class MinimizedSpans
{
    private TimeSpan? _start;
    private int _ticks;

    /// <summary>Notes one tick's state; returns the span this tick closed, or null.</summary>
    public MinimizedSpan? Tick(bool minimized, TimeSpan elapsed)
    {
        if (minimized)
        {
            _start ??= elapsed;
            _ticks++;
            return null;
        }

        return Finish(elapsed);
    }

    /// <summary>Closes a span still open at the run's end, at <paramref name="elapsed"/>; null when none is open.</summary>
    public MinimizedSpan? Finish(TimeSpan elapsed)
    {
        MinimizedSpan? closed = _start is { } start && _ticks > 1 ? new MinimizedSpan(start, elapsed) : null;
        _start = null;
        _ticks = 0;
        return closed;
    }
}
