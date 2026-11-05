using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace GodotMcp.Server.Session;

/// <summary>What one reading of a game's private memory tells a scene's memory watch.</summary>
internal enum MemoryReading
{
    /// <summary>The reading is under the ceiling, or the play it watched is already cancelled: the watch goes on.</summary>
    Watching,

    /// <summary>The reading passed the ceiling, was recorded and the play cancelled.</summary>
    Breached,

    /// <summary>The game has exited or cannot be read: the watch ends quietly, the walk seeing the game gone itself.</summary>
    Exited,
}

/// <summary>
/// A scene's memory ceiling watched while its steps play: the game process's private memory read once a second, and the first
/// reading above the ceiling recorded, then the play it watches cancelled.
/// </summary>
internal sealed class ScratchMemoryWatch : IAsyncDisposable
{
    /// <summary>The bytes in a megabyte, what a ceiling in megabytes is compared with and its kill reason counts.</summary>
    internal const long Megabyte = 1048576;

    /// <summary>Nothing recorded yet: the breach every reading is compared with.</summary>
    internal const long None = -1;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ScratchMemoryCeiling _ceiling;
    private readonly long _limit;
    private readonly CancellationTokenSource _play;
    private readonly CancellationTokenSource _stop;
    private readonly TimeProvider _time;
    private Task? _poll;
    private long _breach = None;

    /// <summary>
    /// Watches <paramref name="play"/>'s game against <paramref name="ceiling"/>, reading memory on <paramref name="time"/>'s
    /// clock; nothing is read until <see cref="Start"/>.
    /// </summary>
    internal ScratchMemoryWatch(ScratchMemoryCeiling ceiling, TimeProvider time, CancellationTokenSource play)
    {
        _ceiling = ceiling;
        _limit = (long)(ceiling.Megabytes * Megabyte);
        _time = time;
        _play = play;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(play.Token);
    }

    /// <summary>The private memory of the game at the first breach recorded, in bytes; <see cref="None"/> while none.</summary>
    internal long BreachBytes => Volatile.Read(ref _breach);

    /// <summary>Why the scene was killed at its memory ceiling, from the breach recorded; null while none.</summary>
    internal string? KillReason => BreachBytes < 0 ? null : Reason(_ceiling, BreachBytes);

    /// <summary>Whether one reading passes <paramref name="limitBytes"/>: strictly above it, a null reading never.</summary>
    internal static bool Passed(long limitBytes, long? reading) => reading is { } bytes && bytes > limitBytes;

    /// <summary>
    /// Why a scene was killed at <paramref name="ceiling"/>, the private memory of its game read <paramref name="bytes"/> at
    /// the breach, in whole megabytes rounded down.
    /// </summary>
    internal static string Reason(ScratchMemoryCeiling ceiling, long bytes) =>
        $"the scene's game passed its memory ceiling of {ceiling.Megabytes.ToString(CultureInfo.InvariantCulture)} MB "
        + $"({bytes / Megabyte} MB private), set by {ceiling.Source}, so its game was stopped";

    /// <summary>Starts reading the private memory of the game process <paramref name="processId"/> once a second.</summary>
    internal void Start(int processId) => _poll = PollAsync(processId, _stop.Token);

    /// <summary>
    /// Checks one reading: whether the ceiling is passed, whether the game is gone, or that the watch goes on. A breach records
    /// the reading before it cancels the play, keeps only the first one, and records none once the play is cancelled already.
    /// </summary>
    internal MemoryReading Read(long? reading)
    {
        if (reading is not { } bytes)
        {
            return MemoryReading.Exited;
        }

        if (!Passed(_limit, bytes))
        {
            return MemoryReading.Watching;
        }

        if (_play.IsCancellationRequested || Interlocked.CompareExchange(ref _breach, bytes, None) != None)
        {
            return MemoryReading.Watching;
        }

        _play.Cancel();
        return MemoryReading.Breached;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_poll is { } poll)
        {
            await poll.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task PollAsync(int processId, CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Read(PrivateBytes(processId)) is not MemoryReading.Watching)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The play ended, its ceiling reached or the walk unwinding: the watch stops with it.
        }
    }

    /// <summary>The game's private memory in bytes; null when it has exited or cannot be read.</summary>
    private static long? PrivateBytes(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            return process.HasExited ? null : process.PrivateMemorySize64;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
