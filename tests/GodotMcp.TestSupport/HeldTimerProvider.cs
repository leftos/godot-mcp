namespace GodotMcp.TestSupport;

/// <summary>
/// A clock that passes time and timers through to <paramref name="time"/>, except that its timers can be held: a callback
/// that comes due while held does not run, and <see cref="Release"/> runs each timer that came due once, as a timer thread
/// starved past several periods and then given a turn does.
/// </summary>
public sealed class HeldTimerProvider(TimeProvider time) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<HeldTimer> _timers = [];
    private bool _holding;

    public override long TimestampFrequency => time.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

    /// <summary>Keeps every callback that comes due from running until <see cref="Release"/>.</summary>
    public void Hold()
    {
        lock (_lock)
        {
            _holding = true;
        }
    }

    /// <summary>Lets the timers run again, running each timer that came due while held once, oldest first.</summary>
    public void Release()
    {
        List<HeldTimer> due;
        lock (_lock)
        {
            _holding = false;
            due = [.. _timers.Where(timer => timer.IsHeld)];
            foreach (HeldTimer timer in due)
            {
                timer.IsHeld = false;
            }
        }

        foreach (HeldTimer timer in due)
        {
            timer.Fire();
        }
    }

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

    public override long GetTimestamp() => time.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        HeldTimer timer = new(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer.Start(time, dueTime, period);
    }

    /// <summary>Whether a callback that comes due now is kept back for <see cref="Release"/>, and records that it did.</summary>
    private bool HoldDue(HeldTimer timer)
    {
        lock (_lock)
        {
            if (!_holding)
            {
                return false;
            }

            timer.IsHeld = true;
            return true;
        }
    }

    private void Forget(HeldTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>One of the clock's timers, whose callback is kept back while the clock is held.</summary>
    private sealed class HeldTimer(HeldTimerProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private ITimer? _inner;

        /// <summary>Whether a callback came due while held, so <see cref="Release"/> runs it once.</summary>
        internal bool IsHeld { get; set; }

        private ITimer Inner => _inner ?? throw new InvalidOperationException("The held timer is not started.");

        public bool Change(TimeSpan dueTime, TimeSpan period) => Inner.Change(dueTime, period);

        public void Dispose()
        {
            owner.Forget(this);
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            owner.Forget(this);
            return Inner.DisposeAsync();
        }

        /// <summary>Wraps the timer on the underlying clock, which comes back to this one when it is due.</summary>
        internal HeldTimer Start(TimeProvider clock, TimeSpan dueTime, TimeSpan period)
        {
            _inner = clock.CreateTimer(_ => CameDue(), state, dueTime, period);
            return this;
        }

        internal void Fire() => callback(state);

        private void CameDue()
        {
            if (!owner.HoldDue(this))
            {
                callback(state);
            }
        }
    }
}
