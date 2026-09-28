using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>
/// Cumulative CPU counters in 100 ns units: the machine's idle, kernel (idle included) and user time summed over every
/// processor, and the CPU the server and the processes it started have used.
/// </summary>
internal readonly record struct LoadCounters(long Idle, long Kernel, long User, long OwnCpu);

/// <summary>Where the <see cref="LoadClock"/> reads the machine's CPU counters.</summary>
internal interface ILoadSource
{
    int Processors { get; }

    /// <returns>The counters now, or null when they cannot be read; the clock then runs at wall time.</returns>
    LoadCounters? Read();
}

/// <summary>A machine whose load cannot be read: the clock runs at wall time.</summary>
internal sealed class NoLoadSource : ILoadSource
{
    public int Processors => Environment.ProcessorCount;

    public LoadCounters? Read() => null;
}

/// <summary>The share of the machine that work other than the server's own leaves free.</summary>
internal static class LoadMath
{
    /// <summary>The lowest share the clock runs at, so a saturated machine still ends every deadline in 20x its budget.</summary>
    public const double MinimumFree = 0.05;

    /// <summary>
    /// The free share between two samples: the busy share of all processors, less the cores the server's own work used,
    /// is the other work's load; what it leaves of <paramref name="processors"/> is free, never below
    /// <see cref="MinimumFree"/>. Counters that did not move read as a free machine.
    /// </summary>
    public static double FreeShare(LoadCounters before, LoadCounters after, int processors, TimeSpan interval)
    {
        long total = after.Kernel - before.Kernel + (after.User - before.User);
        if (total <= 0 || interval <= TimeSpan.Zero || processors <= 0)
        {
            return 1;
        }

        double busy = Math.Clamp(1 - ((double)(after.Idle - before.Idle) / total), 0, 1);
        double ownCores = Math.Max(0, (double)(after.OwnCpu - before.OwnCpu) / interval.Ticks);
        double others = Math.Max(0, (busy * processors) - ownCores);
        return Math.Clamp((processors - others) / processors, MinimumFree, 1);
    }
}

/// <summary>Why a <see cref="LoadDeadline"/> expired.</summary>
internal enum DeadlineReason
{
    /// <summary>Its budget of load-adjusted time ran out.</summary>
    Ceiling,

    /// <summary><see cref="LoadClock.BackstopFactor"/> times its budget passed in wall time first.</summary>
    Backstop,
}

/// <summary>
/// A clock that runs slower while other work loads the machine: each second of wall time counts as the share of the
/// machine that work other than the server's own left free. One timer samples the machine every
/// <see cref="SamplePeriod"/> and publishes the free share and the running adjusted total; every
/// <see cref="LoadDeadline"/> reads them.
/// </summary>
internal sealed class LoadClock(TimeProvider time, ILoadSource source) : IDisposable
{
    /// <summary>How many times its budget a deadline may run in wall time before its backstop ends it.</summary>
    public const int BackstopFactor = 5;

    public static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private ITimer? _sampler;
    private LoadCounters? _counters;
    private long _sampledAt;
    private TimeSpan _integral;
    private double _free = 1;
    private bool _disposed;

    /// <summary>The server's clock: the system's time and, on Windows, the machine's CPU counters.</summary>
    public static LoadClock Shared { get; } = new(TimeProvider.System, OperatingSystem.IsWindows() ? new SystemLoadSource() : new NoLoadSource());

    public TimeProvider Time { get; } = time;

    /// <summary>The free share last published, 1 before the first sample.</summary>
    public double Free
    {
        get
        {
            lock (_lock)
            {
                return _free;
            }
        }
    }

    /// <summary>The load-adjusted time since sampling started: the published total, plus the time since at the published share.</summary>
    internal TimeSpan AdjustedNow => Reading().Adjusted;

    /// <summary>Starts a deadline of <paramref name="budget"/> in load-adjusted time, and the sampler if it is not running.</summary>
    /// <param name="budget">The load-adjusted time the deadline allows; its backstop is <see cref="BackstopFactor"/> times it in wall time.</param>
    /// <param name="linked">Cancels the deadline's token without a reason.</param>
    public LoadDeadline Start(TimeSpan budget, CancellationToken linked = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        EnsureSampling();
        return new LoadDeadline(this, budget, linked);
    }

    /// <summary>The adjusted total and the free share, read together.</summary>
    internal (TimeSpan Adjusted, double Free) Reading()
    {
        lock (_lock)
        {
            return (_integral + (Time.GetElapsedTime(_sampledAt) * _free), _free);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _sampler?.Dispose();
        }
    }

    private void EnsureSampling()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sampler is not null)
            {
                return;
            }

            _counters = source.Read();
            _sampledAt = Time.GetTimestamp();
            _sampler = Time.CreateTimer(_ => Sample(), null, SamplePeriod, SamplePeriod);
        }
    }

    /// <summary>
    /// Adds the time since the last sample at the share published then, so the adjusted total never jumps, and publishes
    /// the share measured over it.
    /// </summary>
    private void Sample()
    {
        LoadCounters? now = source.Read();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            long at = Time.GetTimestamp();
            TimeSpan interval = Time.GetElapsedTime(_sampledAt, at);
            _integral += interval * _free;
            _free = (_counters, now) is ({ } before, { } after) ? LoadMath.FreeShare(before, after, source.Processors, interval) : 1;
            _counters = now;
            _sampledAt = at;
        }
    }
}

/// <summary>
/// A budget of load-adjusted time, bounded by <see cref="LoadClock.BackstopFactor"/> times it in wall time. Its own
/// one-shot timer checks it at most every <see cref="LoadClock.SamplePeriod"/>, sooner when the budget is due sooner at the
/// published share; on expiry its token is cancelled without blocking the timer.
/// </summary>
internal sealed class LoadDeadline : IDisposable
{
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly LoadClock _clock;
    private readonly CancellationTokenSource _cancellation;
    private readonly Lock _lock = new();
    private readonly long _startWall;
    private readonly TimeSpan _startAdjusted;
    private readonly ITimer _timer;
    private bool _ended;
    private TimeSpan _endWall;
    private TimeSpan _endAdjusted;

    internal LoadDeadline(LoadClock clock, TimeSpan budget, CancellationToken linked)
    {
        _clock = clock;
        Budget = budget;
        Backstop = budget * LoadClock.BackstopFactor;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(linked);
        (TimeSpan adjusted, double free) = clock.Reading();
        _startAdjusted = adjusted;
        _startWall = clock.Time.GetTimestamp();
        lock (_lock)
        {
            _timer = clock.Time.CreateTimer(_ => Check(), null, NextWait(TimeSpan.Zero, TimeSpan.Zero, free), Timeout.InfiniteTimeSpan);
        }
    }

    public TimeSpan Budget { get; }

    public TimeSpan Backstop { get; }

    /// <summary>Cancelled when the deadline expires or the linked token is cancelled.</summary>
    public CancellationToken Token => _cancellation.Token;

    public bool Expired => Reason is not null;

    public DeadlineReason? Reason
    {
        get
        {
            lock (_lock)
            {
                return field;
            }
        }
        private set;
    }

    /// <summary>The wall time since the start, frozen when the deadline ends.</summary>
    public TimeSpan Wall => Measure().Wall;

    /// <summary>The load-adjusted time since the start, frozen when the deadline ends.</summary>
    public TimeSpan Adjusted => Measure().Adjusted;

    /// <summary>The share of the machine free on average since the start: <see cref="Adjusted"/> over <see cref="Wall"/>.</summary>
    public double MeanFree
    {
        get
        {
            (TimeSpan wall, TimeSpan adjusted) = Measure();
            return wall > TimeSpan.Zero ? Math.Min(1, adjusted / wall) : 1;
        }
    }

    /// <summary>"; that is 5 x its 60 s ceiling in wall time, the backstop (load-adjusted 41 s, machine 14% free on average)".</summary>
    public string BackstopClause() =>
        $"; that is {LoadClock.BackstopFactor} x its {Seconds(Budget)} s ceiling in wall time, the backstop "
        + $"(load-adjusted {Seconds(Adjusted)} s, machine {Percent(MeanFree)}% free on average)";

    /// <summary>"within 300 s of load-adjusted time (wall 812 s, machine 37% free on average)".</summary>
    public string CeilingClause() =>
        $"within {Seconds(Budget)} s of load-adjusted time (wall {Seconds(Wall)} s, machine {Percent(MeanFree)}% free on average)";

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_ended)
            {
                End(null);
            }
        }

        _timer.Dispose();
        _cancellation.Dispose();
    }

    internal static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    internal static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture);

    private (TimeSpan Wall, TimeSpan Adjusted) Measure()
    {
        lock (_lock)
        {
            return _ended ? (_endWall, _endAdjusted) : Elapsed();
        }
    }

    private (TimeSpan Wall, TimeSpan Adjusted) Elapsed() => (_clock.Time.GetElapsedTime(_startWall), _clock.Reading().Adjusted - _startAdjusted);

    private void Check()
    {
        lock (_lock)
        {
            if (_ended)
            {
                return;
            }

            (TimeSpan wall, TimeSpan adjusted) = Elapsed();
            DeadlineReason? reason =
                adjusted >= Budget ? DeadlineReason.Ceiling
                : wall >= Backstop ? DeadlineReason.Backstop
                : null;
            if (reason is not null || _cancellation.IsCancellationRequested)
            {
                End(reason);
                return;
            }

            _timer.Change(NextWait(wall, adjusted, _clock.Free), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Freezes the times; on expiry cancels the token on the thread pool, so a timer callback never runs the token's callbacks.</summary>
    private void End(DeadlineReason? reason)
    {
        (_endWall, _endAdjusted) = Elapsed();
        _ended = true;
        Reason = reason;
        if (reason is not null)
        {
            _ = _cancellation.CancelAsync();
        }
    }

    private TimeSpan NextWait(TimeSpan wall, TimeSpan adjusted, double free)
    {
        TimeSpan toCeiling = (Budget - adjusted) / free;
        TimeSpan toBackstop = Backstop - wall;
        var wait = TimeSpan.FromTicks(Math.Min(Math.Min(toCeiling.Ticks, toBackstop.Ticks), LoadClock.SamplePeriod.Ticks));
        return wait < MinimumWait ? MinimumWait : wait;
    }
}

/// <summary>A wait that ran out of its <see cref="LoadDeadline"/>; the deadline says which limit ended it and how loaded the machine was.</summary>
internal sealed class LoadTimeoutException(string message, LoadDeadline deadline) : TimeoutException(message)
{
    public LoadDeadline Deadline { get; } = deadline;
}
