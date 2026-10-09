using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// The warm headless hosts, at most one per GDScript-only project folder, keyed by its normalised path. A C# project gets
/// none: its headless requests run one process each. Requests on a folder reach its host under the folder's prep lock,
/// which the caller holds; a host found stale (the folder's fingerprint changed since its last reply) or gone is replaced.
/// A host that ends while idle is dropped at once, so it holds its folder no longer. A host taken out of the pool leaves its
/// release behind as the folder's pending one, and no new host starts on the folder until it has finished, since the new
/// host writes the same log. A host idle for <see cref="IdleLimit"/> is stopped, and a new host that would make more than
/// <see cref="MaxHosts"/> live ones (starts under way included) first stops the idle one whose last reply is oldest; a
/// busy host is never stopped for either. A reply that says a resource its request named stayed cached (<c>stale</c>)
/// stops its host. Those three stops, and a session stop's, run in the background as the folder's pending release.
/// </summary>
internal sealed class HeadlessHosts(SessionRegistry registry)
{
    /// <summary>The most live hosts the pool keeps; a new one beyond it stops the least recently used idle one.</summary>
    public const int MaxHosts = 4;

    /// <summary>How long a host is kept after its last reply, in wall time on the registry's clock.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);

    /// <summary>The key a host adds to its result object when a resource its request named stayed cached after the request.</summary>
    private const string StaleKey = "stale";

    /// <summary>What a call gets once the server is shutting down.</summary>
    private const string ShuttingDown = "The server is shutting down, so no headless host starts now.";

    /// <summary>How two normalised folders compare as keys: without case on Windows, whose file system ignores it.</summary>
    private static readonly StringComparer FolderComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, HeadlessHost> _hosts = new(FolderComparer);

    // Set once the server is shutting down, under _lock: no request acquires a host, and a start under way stops its own.
    private bool _closed;

    // The release of each folder's last host taken out of the pool, under _lock; a finished one stays until replaced.
    private readonly Dictionary<string, Task> _releases = new(FolderComparer);

    // Hosts being started and not yet in _hosts, under _lock: each holds a place under the cap.
    private int _starting;

    /// <summary>
    /// How an idle timer's check runs: on the thread pool, off the timer's thread. A test sets it to hold the check back, as a
    /// busy pool would.
    /// </summary>
    internal Action<Action> IdleDispatch { get; set; } = check => _ = Task.Run(check);

    /// <summary>The process id of the folder's host; null when it has none.</summary>
    public int? ProcessIdOf(string projectDir)
    {
        lock (_lock)
        {
            return _hosts.TryGetValue(ProjectPaths.Normalise(projectDir), out HeadlessHost? host) ? host.ProcessId : null;
        }
    }

    /// <summary>
    /// The folder's host, marked busy for one request: the one it has when its fingerprint is unchanged since its last reply,
    /// else a new one, started once the old one is stopped and the folder's last release has finished (at most
    /// <see cref="HeadlessHost.ReleaseLimit"/>). Null for a C# project, which runs its request cold; a host it kept from
    /// before it became one is stopped. Call under the folder's prep lock, and hand the host to <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="projectDir">The project folder.</param>
    /// <param name="what">"The headless &lt;op&gt; run on &lt;project&gt;", which a failure to start begins with.</param>
    /// <param name="cancellationToken">Withdraws the waits for a stop, a release and a host's start.</param>
    /// <exception cref="SessionException">
    /// A new host could not be started, or ended before its first request, or the server is shutting down.
    /// </exception>
    public async Task<HeadlessHost?> AcquireAsync(string projectDir, string what, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_closed)
            {
                throw new SessionException(ShuttingDown);
            }
        }

        string folder = ProjectPaths.Normalise(projectDir);
        if (PrepScan.FindCsproj(folder).Kind != CsprojKind.None)
        {
            StopHost(folder, "the project became a C# project", cancellationToken);
            return null;
        }

        if (Find(folder) is { } current)
        {
            if (IsFresh(current) && current.TryBeginRequest())
            {
                return current;
            }

            // Stop does nothing for a host that has already ended, which Retire releases instead.
            string reason = current.IsAlive ? "the project's files changed since its last reply" : "it had ended";
            try
            {
                current.Stop(reason, cancellationToken);
            }
            finally
            {
                // A host a claimer took out of the pool (an idle or cap stop under way) is its to let go of, after its shutdown.
                RetireIfHeld(folder, current);
            }
        }

        return await StartAsync(folder, what, cancellationToken);
    }

    /// <summary>
    /// Runs one request on a host <see cref="AcquireAsync"/> gave; after the reply the folder's fingerprint is retaken, so the
    /// host's own writes never count as a change. A host the request ended is dropped; one whose reply says it is stale is
    /// stopped, so the folder's next request starts a new one, and the flag is taken out of the result.
    /// </summary>
    /// <exception cref="SessionException">The request failed; see <see cref="HeadlessHost.RequestAsync"/>.</exception>
    public async Task<JsonObject> RunAsync(HeadlessHost host, HostRequest request, CancellationToken cancellationToken)
    {
        bool stale = false;
        try
        {
            JsonObject result = await host.RequestAsync(request, cancellationToken);
            stale = TakeStale(result);
            return result;
        }
        finally
        {
            AfterRequest(host, stale);
        }
    }

    /// <summary>
    /// Stops the folder's host, if it has one; the caller holds the folder's prep lock, so none of its requests runs.
    /// </summary>
    /// <returns>Whether the folder had a host.</returns>
    public bool StopFolder(string projectDir, string reason) => StopHost(ProjectPaths.Normalise(projectDir), reason, CancellationToken.None);

    /// <summary>
    /// Stops the folder's host if it is idle now, without the folder's prep lock, and waits for the stop; a host running a
    /// request is left running.
    /// </summary>
    /// <returns>Whether a host was stopped.</returns>
    public async Task<bool> StopIfIdleAsync(string projectDir, string reason)
    {
        string folder = ProjectPaths.Normalise(projectDir);
        Task stop;
        lock (_lock)
        {
            if (!_hosts.TryGetValue(folder, out HeadlessHost? host) || !host.TryClaimIdle())
            {
                return false;
            }

            stop = QueueStopLocked(folder, host, reason);
        }

        await stop;
        return true;
    }

    /// <summary>The hosts in the pool, ordered by folder, each as list_sessions shows it.</summary>
    public IReadOnlyList<HeadlessHostInfo> List()
    {
        HeadlessHost[] hosts;
        lock (_lock)
        {
            hosts = [.. _hosts.Values];
        }

        DateTimeOffset now = registry.Clock.Time.GetUtcNow();
        return [.. hosts.Select(host => host.Describe(now)).OrderBy(info => info.ProjectPath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Closes the pool, so no request acquires a host from here on and a start still under way stops its own host.
    /// Stops every host and waits for each process to be gone, then for every release still under way, at most
    /// <see cref="HeadlessHost.ReleaseLimit"/> of wall time; one that runs past it is logged. For the server's exit.
    /// </summary>
    public void Shutdown()
    {
        HeadlessHost[] hosts;
        lock (_lock)
        {
            _closed = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
        }

        // Each stop waits for its host to answer and exit, so they run at once rather than adding up; a failed one is reported.
        (HeadlessHost Host, Task Stop)[] stops =
        [
            .. hosts.Select(host => (host, Task.Run(() => host.Stop("the server is shutting down", CancellationToken.None)))),
        ];
        Task.WhenAll(stops.Select(stop => stop.Stop)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
        foreach ((HeadlessHost host, Task stop) in stops.Where(stop => stop.Stop.IsFaulted))
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine(
                $"godot-mcp: stopping the warm headless host of {host.ProjectDir} at shutdown failed: {stop.Exception!.InnerException?.Message}"
            );
        }

        KeyValuePair<string, Task>[] pending;
        lock (_lock)
        {
            // A host that had already ended when it was taken (a request failing it) is joined through its own release.
            pending =
            [
                .. _releases.Where(release => !release.Value.IsCompleted),
                .. hosts.Select(host => KeyValuePair.Create(host.ProjectDir, host.ReleaseAsync())).Where(release => !release.Value.IsCompleted),
            ];
        }

        // A release that failed does not hold up the exit; one still running past the limit is logged and left behind.
        Task.WhenAll(pending.Select(release => release.Value))
            .WaitAsync(HeadlessHost.ReleaseLimit)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)
            .GetAwaiter()
            .GetResult();
        foreach ((string folder, _) in pending.Where(release => !release.Value.IsCompleted))
        {
            Log.HeadlessHostReleaseUnfinishedAtExit(registry.Logger, folder, HeadlessHost.ReleaseLimit.TotalSeconds);
        }
    }

    private static bool IsFresh(HeadlessHost host) =>
        host.IsAlive && host.Fingerprint is { } fingerprint && fingerprint.Equals(ProjectFingerprint.Take(host.ProjectDir));

    /// <summary>Takes the host's <c>stale</c> flag out of its result object; true when it was there and true.</summary>
    private static bool TakeStale(JsonObject result) =>
        result.Remove(StaleKey, out JsonNode? flag) && flag is JsonValue value && value.TryGetValue(out bool stale) && stale;

    /// <summary>
    /// Settles a host after its request: a stale one is stopped, an ended one dropped, and a live one has the folder's
    /// fingerprint retaken and is marked idle, which re-arms its idle timer.
    /// </summary>
    private void AfterRequest(HeadlessHost host, bool stale)
    {
        if (stale && host.TryMarkEnded())
        {
            lock (_lock)
            {
                _ = QueueStopLocked(host.ProjectDir, host, "a resource its request named stayed cached after its reply");
            }

            return;
        }

        if (host.HasEnded)
        {
            _ = Retire(host.ProjectDir, host);
            return;
        }

        host.Fingerprint = ProjectFingerprint.Take(host.ProjectDir);
        host.EndRequest();
    }

    private HeadlessHost? Find(string folder)
    {
        lock (_lock)
        {
            return _hosts.GetValueOrDefault(folder);
        }
    }

    /// <summary>Takes the folder's host out of the pool, if it has one, stops it and releases it.</summary>
    /// <returns>Whether the folder had a host.</returns>
    /// <exception cref="OperationCanceledException">The caller stopped waiting for the stop; the host is still released.</exception>
    private bool StopHost(string folder, string reason, CancellationToken cancellationToken)
    {
        HeadlessHost? host;
        lock (_lock)
        {
            if (!_hosts.Remove(folder, out host))
            {
                return false;
            }
        }

        try
        {
            host.Stop(reason, cancellationToken);
        }
        finally
        {
            _ = Retire(folder, host);
        }

        return true;
    }

    /// <summary>
    /// Holds a place under the cap for a host about to start, counting the live hosts and the starts under way together, so
    /// starts on several folders at once never pass <see cref="MaxHosts"/>. Over it, the idle host whose last reply is oldest
    /// is claimed and stopped in the background; one that turns busy or ends first is passed over for the next. A busy host
    /// is never stopped, so with every host busy the pool grows past the cap. It takes no prep lock: the host it stops is
    /// idle, and a request on its folder then finds none and starts one after its release.
    /// </summary>
    private void ReserveSlot()
    {
        lock (_lock)
        {
            int live = _hosts.Values.Count(host => host.IsAlive && !host.HasEnded);
            if (live + _starting + 1 > MaxHosts)
            {
                HeadlessHost[] oldestFirst = [.. _hosts.Values.Where(host => host.IsAlive).OrderBy(host => host.LastReply)];
                if (oldestFirst.FirstOrDefault(host => host.TryClaimIdle()) is { } evicted)
                {
                    _ = QueueStopLocked(evicted.ProjectDir, evicted, "cap");
                }
            }

            // Counted last, so a throw above holds no place.
            _starting++;
        }
    }

    /// <summary>
    /// Claims and stops, in the background, a host whose idle timer fired, if it is still idle and has been for the whole idle
    /// limit; outside the folder's prep lock, so a request arriving meanwhile finds no host and starts one after the release.
    /// </summary>
    private void StopIdle(string folder, HeadlessHost host)
    {
        lock (_lock)
        {
            if (host.TryClaimExpired())
            {
                _ = QueueStopLocked(folder, host, "idle");
            }
        }
    }

    /// <summary>
    /// Starts the folder's new host once its last release has finished, holding a place under the cap while it starts, and
    /// marks it busy; one that is gone before its first request is a start failure.
    /// </summary>
    /// <exception cref="SessionException">The host could not be started, or ended before its first request.</exception>
    private async Task<HeadlessHost> StartAsync(string folder, string what, CancellationToken cancellationToken)
    {
        await AwaitReleaseAsync(folder, cancellationToken);
        ReserveSlot();
        bool placed = false;
        try
        {
            HeadlessHost started = await HeadlessHost.StartAsync(registry, folder, what, cancellationToken);
            if (!started.TryBeginRequest())
            {
                SessionException failed = await started.EndAtStartAsync(what);
                _ = Retire(folder, started);
                throw failed;
            }

            bool closed;
            lock (_lock)
            {
                closed = _closed;
                if (!closed)
                {
                    _hosts[folder] = started;
                    _starting--;
                    placed = true;
                }
            }

            if (closed)
            {
                // The shutdown already emptied the pool this host would join, so the host it never took must not outlive it.
                started.Stop("the server is shutting down", CancellationToken.None);
                _ = Retire(folder, started);
                throw new SessionException(ShuttingDown);
            }

            _ = DropWhenGoneAsync(folder, started);
            started.WatchIdle(IdleLimit, () => IdleDispatch(() => StopIdle(folder, started)));
            return started;
        }
        finally
        {
            if (!placed)
            {
                lock (_lock)
                {
                    _starting--;
                }
            }
        }
    }

    /// <summary>
    /// Takes an ended host out of the pool, unless another has replaced it there, and releases it (or joins its release under
    /// way), recording the release as the folder's pending one in the same step, so no start can slip between the two.
    /// </summary>
    /// <returns>The host's release.</returns>
    private Task Retire(string folder, HeadlessHost host)
    {
        lock (_lock)
        {
            Task release = host.ReleaseAsync();
            TrackLocked(folder, host, release);
            return release;
        }
    }

    /// <summary>
    /// <see cref="Retire"/> for a host still in the pool; nothing for one another caller has already taken out, whose release
    /// that caller records.
    /// </summary>
    private void RetireIfHeld(string folder, HeadlessHost host)
    {
        lock (_lock)
        {
            if (_hosts.TryGetValue(folder, out HeadlessHost? held) && ReferenceEquals(held, host))
            {
                TrackLocked(folder, host, host.ReleaseAsync());
            }
        }
    }

    /// <summary>
    /// Takes a host the caller has claimed out of the pool and stops it on the thread pool (<c>shutdown</c>, then its release),
    /// recording the stop as the folder's pending release, so the folder's next host and the server's exit wait for it. The
    /// caller holds <c>_lock</c>.
    /// </summary>
    /// <returns>The stop.</returns>
    private Task QueueStopLocked(string folder, HeadlessHost host, string reason)
    {
        var stop = Task.Run(() => host.StopClaimedAsync(reason));
        TrackLocked(folder, host, stop);
        return stop;
    }

    /// <summary>
    /// Takes the host out of the pool, unless another has replaced it there, and records <paramref name="release"/> as the
    /// folder's pending release, joined with any still under way. The caller holds <c>_lock</c>.
    /// </summary>
    private void TrackLocked(string folder, HeadlessHost host, Task release)
    {
        if (_hosts.TryGetValue(folder, out HeadlessHost? held) && ReferenceEquals(held, host))
        {
            _hosts.Remove(folder);
        }

        bool pending = _releases.TryGetValue(folder, out Task? earlier) && !earlier.IsCompleted;
        _releases[folder] = pending ? Task.WhenAll(earlier!, release) : release;
    }

    /// <summary>
    /// Waits for the folder's pending release, at most <see cref="HeadlessHost.ReleaseLimit"/> of wall time on the registry's
    /// clock; one that runs past it is logged, and the new host starts anyway. A release that failed does not fail the start.
    /// </summary>
    private async Task AwaitReleaseAsync(string folder, CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_lock)
        {
            pending = _releases.GetValueOrDefault(folder);
        }

        if (pending is null || pending.IsCompleted)
        {
            return;
        }

        await pending
            .WaitAsync(HeadlessHost.ReleaseLimit, registry.Clock.Time, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        if (!pending.IsCompleted)
        {
            Log.HeadlessHostReleaseSlow(registry.Logger, folder, HeadlessHost.ReleaseLimit.TotalSeconds);
        }
    }

    /// <summary>Drops the host when its connection closes or its process exits while it is idle; a busy one is its request's to end.</summary>
    private async Task DropWhenGoneAsync(string folder, HeadlessHost host)
    {
        await host.Gone;
        if (!host.TryEndIdle())
        {
            return;
        }

        Log.HeadlessHostDropped(registry.Logger, folder, host.ProcessId);
        await Retire(folder, host);
    }
}
