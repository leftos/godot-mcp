namespace GodotMcp.Server.Wire;

/// <summary>
/// A watch on one batch step's first request reaching a game's socket: a parallel group starts its next member once the one
/// before has sent its request, or has ended without one. The step's calls find it through an <see cref="AsyncLocal{T}"/>,
/// since a tool's signature carries no batch; a call made outside a watched step finds none.
/// </summary>
internal sealed class WriteWatch
{
    private static readonly AsyncLocal<WriteWatch?> Current = new();

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sent;

    /// <summary>Whether a request of the watched step was written to a game's socket.</summary>
    public bool Sent => Volatile.Read(ref _sent) == 1;

    /// <summary>Completes once the step has sent its first request, has read its output baseline, or has ended.</summary>
    public Task Released => _released.Task;

    /// <summary>Makes this the watch of every call the current flow makes from here on.</summary>
    public void Watch() => Current.Value = this;

    /// <summary>
    /// Leaves the current flow unwatched, for a setup request a call sends before its own: called at the top of an async
    /// method, it lasts to that method's end, since an AsyncLocal change made inside an async method never flows back to
    /// its caller.
    /// </summary>
    public static void Unwatch() => Current.Value = null;

    /// <summary>Releases the next member: the step ended, whether or not it sent anything.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Marks the current flow's request as on its socket; a bridge connection calls it right after the write.</summary>
    public static void Written()
    {
        if (Current.Value is { } watch)
        {
            Interlocked.Exchange(ref watch._sent, 1);
            watch.Release();
        }
    }

    /// <summary>Releases the current flow's watch with nothing sent: a wait the server answers itself has read its baseline.</summary>
    public static void Ready() => Current.Value?.Release();
}
