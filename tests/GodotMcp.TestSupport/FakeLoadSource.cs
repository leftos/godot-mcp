using GodotMcp.Server.Session;

namespace GodotMcp.TestSupport;

/// <summary>
/// A machine's CPU counters that grow with <paramref name="time"/> at the load <see cref="SetLoad"/> last set: all
/// <paramref name="processors"/> busy by the given share, of which the given number of cores is the server's own work.
/// </summary>
public sealed class FakeLoadSource(TimeProvider time, int processors) : ILoadSource
{
    private readonly Lock _lock = new();
    private long _accruedAt = time.GetTimestamp();
    private double _busyShare;
    private double _ownCores;
    private long _idle;
    private long _kernel;
    private long _user;
    private long _own;
    private int _reads;

    public int Processors => processors;

    /// <summary>Makes every read fail, as a machine whose counters cannot be read.</summary>
    public bool Unavailable { get; set; }

    /// <summary>How many times the counters were read.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Sets the load from now on: the busy share of all processors, and the cores of it that are the server's own.</summary>
    public void SetLoad(double busyShare, double ownCores)
    {
        lock (_lock)
        {
            Accrue();
            _busyShare = busyShare;
            _ownCores = ownCores;
        }
    }

    LoadCounters? ILoadSource.Read()
    {
        lock (_lock)
        {
            _reads++;
            Accrue();
            return Unavailable ? null : new LoadCounters(_idle, _kernel, _user, _own);
        }
    }

    private void Accrue()
    {
        long now = time.GetTimestamp();
        double ticks = time.GetElapsedTime(_accruedAt, now).Ticks;
        _accruedAt = now;
        double all = ticks * processors;
        _idle += (long)(all * (1 - _busyShare));
        _kernel += (long)(all * (1 - _busyShare));
        _user += (long)(all * _busyShare);
        _own += (long)(ticks * _ownCores);
    }
}
