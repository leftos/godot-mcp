namespace GodotMcp.Server.Session;

/// <summary>
/// One game session's clock offset from the drive clock. A pong carrying the bridge's reading <c>t</c> microseconds, sent at
/// drive time <c>S</c> and received at <c>R</c>, bounds the offset to <c>[S - t / 1000, R - t / 1000]</c> milliseconds; the
/// clock keeps the largest lower and the smallest upper bound over the replies, takes the offset as their midpoint, and
/// reports their half-width as a converted stamp's error. A hello carries no send time, so it can only lower the upper bound.
/// </summary>
internal sealed class SessionClock
{
    private readonly Lock _lock = new();
    private double? _lowerMs;
    private double? _upperMs;

    /// <summary>
    /// The half-width of the tightest bounds, rounded to 0.1 ms as every drive stamp is; infinite before a pong has given
    /// both a lower and an upper bound, since then no offset can be placed.
    /// </summary>
    public double ErrorMs
    {
        get
        {
            lock (_lock)
            {
                return HalfWidth();
            }
        }
    }

    /// <summary>Whether the bounds are wider than 2 ms, so a stamp converted now would be less precise than the design asks.</summary>
    public bool IsWide => ErrorMs > 2;

    /// <summary>
    /// Adds a pong's bounds: the bridge read <paramref name="ticksUsec"/> microseconds at a request sent at drive time
    /// <paramref name="sentMs"/> and received at <paramref name="receivedMs"/>.
    /// </summary>
    public void Add(long ticksUsec, double sentMs, double receivedMs)
    {
        double remoteMs = ticksUsec / 1000.0;
        double lower = sentMs - remoteMs;
        double upper = receivedMs - remoteMs;
        if (upper < lower)
        {
            // A receipt before its own send time: nothing can be read from it.
            return;
        }

        lock (_lock)
        {
            if ((_lowerMs is { } heldLower && upper < heldLower) || (_upperMs is { } heldUpper && lower > heldUpper))
            {
                // No intersection: the held bounds and this pong disagree (a reordered reply, or a drifted clock), so the pong
                // is the newest evidence and the bounds start over from it.
                _lowerMs = lower;
                _upperMs = upper;
                return;
            }

            _lowerMs = _lowerMs is { } previousLower ? Math.Max(previousLower, lower) : lower;
            _upperMs = _upperMs is { } previousUpper ? Math.Min(previousUpper, upper) : upper;
        }
    }

    /// <summary>
    /// Adds a hello's bound: received at drive time <paramref name="receivedMs"/> with the bridge's reading
    /// <paramref name="ticksUsec"/>, the offset can be no larger than their difference; it never gives a lower bound.
    /// </summary>
    public void AddHello(long ticksUsec, double receivedMs)
    {
        double upper = receivedMs - (ticksUsec / 1000.0);
        lock (_lock)
        {
            if (_lowerMs is { } lower && upper < lower)
            {
                // The hello cannot prove an upper bound beneath a pong's lower one; the pongs are the tighter evidence.
                return;
            }

            _upperMs = _upperMs is { } held ? Math.Min(held, upper) : upper;
        }
    }

    /// <summary>
    /// Converts a remote reading to drive time from the bounds' midpoint, rounded to 0.1 ms as <see cref="DriveClock.NowMs"/>
    /// is, with the bounds' half-width as its error; false before a pong gave both bounds, when nothing can be converted.
    /// </summary>
    public bool TryToDriveMs(long ticksUsec, out double driveMs, out double errorMs)
    {
        if (!TryGetOffset(out double offsetMs, out errorMs))
        {
            driveMs = 0;
            return false;
        }

        driveMs = ToDriveMs(offsetMs, ticksUsec);
        return true;
    }

    /// <summary>
    /// Reads both bounds once: <paramref name="offsetMs"/> is their midpoint, unrounded, and <paramref name="errorMs"/> their
    /// half-width rounded to 0.1 ms; false before a pong gave both bounds. Readings converted with one offset
    /// (<see cref="ToDriveMs"/>) keep their order even if a pong narrows the bounds meanwhile.
    /// </summary>
    public bool TryGetOffset(out double offsetMs, out double errorMs)
    {
        lock (_lock)
        {
            if (_lowerMs is not { } lower || _upperMs is not { } upper)
            {
                offsetMs = 0;
                errorMs = 0;
                return false;
            }

            offsetMs = (lower + upper) / 2;
            errorMs = Math.Round((upper - lower) / 2, 1);
            return true;
        }
    }

    /// <summary>A remote reading of <paramref name="ticksUsec"/> as drive time through <paramref name="offsetMs"/>
    /// (<see cref="TryGetOffset"/>), rounded to 0.1 ms.</summary>
    public static double ToDriveMs(double offsetMs, long ticksUsec) => Math.Round(offsetMs + (ticksUsec / 1000.0), 1);

    /// <summary>The bounds' half-width, or infinite while one of them is missing.</summary>
    private double HalfWidth()
    {
        if (_lowerMs is not { } lower || _upperMs is not { } upper)
        {
            return double.PositiveInfinity;
        }

        return Math.Round((upper - lower) / 2, 1);
    }
}
