using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// The warm headless hosts, at most one per GDScript-only project folder, keyed by its normalised path. A C# project gets
/// none: its headless requests run one process each. Requests on a folder reach its host under the folder's prep lock,
/// which the caller holds; a host found stale (the folder's fingerprint changed since its last reply) or gone is replaced.
/// A host that ends while idle is dropped at once, so it holds its folder no longer. A host taken out of the pool leaves its
/// release behind as the folder's pending one, and no new host starts on the folder until it has finished, since the new
/// host writes the same log.
/// </summary>
internal sealed class HeadlessHosts(SessionRegistry registry)
{
    /// <summary>How two normalised folders compare as keys: without case on Windows, whose file system ignores it.</summary>
    private static readonly StringComparer FolderComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, HeadlessHost> _hosts = new(FolderComparer);

    // The release of each folder's last host taken out of the pool, under _lock; a finished one stays until replaced.
    private readonly Dictionary<string, Task> _releases = new(FolderComparer);

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
    /// <exception cref="SessionException">A new host could not be started, or ended before its first request.</exception>
    public async Task<HeadlessHost?> AcquireAsync(string projectDir, string what, CancellationToken cancellationToken)
    {
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
                _ = Retire(folder, current);
            }
        }

        return await StartAsync(folder, what, cancellationToken);
    }

    /// <summary>
    /// Runs one request on a host <see cref="AcquireAsync"/> gave; after the reply the folder's fingerprint is retaken, so the
    /// host's own writes never count as a change. A host the request ended is dropped.
    /// </summary>
    /// <exception cref="SessionException">The request failed; see <see cref="HeadlessHost.RequestAsync"/>.</exception>
    public async Task<JsonObject> RunAsync(HeadlessHost host, HostRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await host.RequestAsync(request, cancellationToken);
        }
        finally
        {
            if (host.HasEnded)
            {
                _ = Retire(host.ProjectDir, host);
            }
            else
            {
                host.Fingerprint = ProjectFingerprint.Take(host.ProjectDir);
                host.EndRequest();
            }
        }
    }

    /// <summary>Stops the folder's host, if it has one; the caller holds the folder's prep lock, so none of its requests runs.</summary>
    public void StopFolder(string projectDir, string reason) => StopHost(ProjectPaths.Normalise(projectDir), reason, CancellationToken.None);

    /// <summary>
    /// Stops every host and waits for each process to be gone, then for every release still under way, at most
    /// <see cref="HeadlessHost.ReleaseLimit"/> of wall time; one that runs past it is logged. For the server's exit.
    /// </summary>
    public void Shutdown()
    {
        HeadlessHost[] hosts;
        lock (_lock)
        {
            hosts = [.. _hosts.Values];
            _hosts.Clear();
        }

        foreach (HeadlessHost host in hosts)
        {
            host.Stop("the server is shutting down", CancellationToken.None);
        }

        KeyValuePair<string, Task>[] pending;
        lock (_lock)
        {
            pending = [.. _releases.Where(release => !release.Value.IsCompleted)];
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

    private HeadlessHost? Find(string folder)
    {
        lock (_lock)
        {
            return _hosts.GetValueOrDefault(folder);
        }
    }

    /// <summary>Takes the folder's host out of the pool, if it has one, stops it and releases it.</summary>
    /// <exception cref="OperationCanceledException">The caller stopped waiting for the stop; the host is still released.</exception>
    private void StopHost(string folder, string reason, CancellationToken cancellationToken)
    {
        HeadlessHost? host;
        lock (_lock)
        {
            if (!_hosts.Remove(folder, out host))
            {
                return;
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
    }

    /// <summary>
    /// Starts the folder's new host once its last release has finished, and marks it busy; one that is gone before its first
    /// request is a start failure.
    /// </summary>
    /// <exception cref="SessionException">The host could not be started, or ended before its first request.</exception>
    private async Task<HeadlessHost> StartAsync(string folder, string what, CancellationToken cancellationToken)
    {
        await AwaitReleaseAsync(folder, cancellationToken);
        HeadlessHost started = await HeadlessHost.StartAsync(registry, folder, what, cancellationToken);
        if (!started.TryBeginRequest())
        {
            SessionException failed = await started.EndAtStartAsync(what);
            _ = Retire(folder, started);
            throw failed;
        }

        lock (_lock)
        {
            _hosts[folder] = started;
        }

        _ = DropWhenGoneAsync(folder, started);
        return started;
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
            if (_hosts.TryGetValue(folder, out HeadlessHost? held) && ReferenceEquals(held, host))
            {
                _hosts.Remove(folder);
            }

            Task release = host.ReleaseAsync();
            bool pending = _releases.TryGetValue(folder, out Task? earlier) && !earlier.IsCompleted;
            _releases[folder] = pending ? Task.WhenAll(earlier!, release) : release;
            return release;
        }
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
