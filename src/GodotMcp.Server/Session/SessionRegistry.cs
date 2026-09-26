using System.Globalization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The server's sessions by name (case-insensitive): runs and attaches share one name space, a live name is refused and a
/// name whose session has ended is replaced. Several sessions may share a project folder; they share its one marked
/// override.cfg, which is removed when the last live session on the folder ends.
/// </summary>
internal sealed partial class SessionRegistry(BridgeListener listener, ILogger<GodotSession> logger) : IDisposable
{
    /// <summary>What a launch or an attach says about a session name it refuses.</summary>
    public const string NameRule = "a session name is 1 to 64 characters of letters, digits, '.', '_' and '-'";

    /// <summary>How long a preview may take in all: its prep, its launch and its capture.</summary>
    public static readonly TimeSpan PreviewLimit = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();

    // The number of the last preview session named, under _lock; each preview's name carries the next.
    private int _previews;
    private readonly Dictionary<string, GodotSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // One per folder a prep has run on, kept for the server's lifetime. Never disposed: a prep still in flight at shutdown
    // releases its lock after the registry is gone, and a SemaphoreSlim whose wait handle is never asked for holds no handle.
    private readonly Dictionary<string, SemaphoreSlim> _prepLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    internal BridgeListener Listener => listener;

    internal ILogger Logger => logger;

    /// <summary>Launches a run under <paramref name="session"/>, or under the project folder's name when it is null.</summary>
    /// <exception cref="SessionException">The name is invalid or live, the project is missing, or the launch failed.</exception>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, string? session, CancellationToken cancellationToken)
    {
        string projectDir = NormaliseProjectDir(request.ProjectPath);
        SessionSpec spec = new(NameFor(session, projectDir), projectDir, SessionKind.Run, request.ShutOutRealGamepads, request.Quiet);
        GodotSession created = await ReserveAsync(spec);
        return await created.LaunchAsync(request with { ProjectPath = projectDir }, cancellationToken);
    }

    /// <summary>Attaches under <paramref name="session"/>, or under the project folder's name when it is null.</summary>
    /// <exception cref="SessionException">The name is invalid or live, the project is missing, or no game connected in time.</exception>
    public async Task<AttachResult> AttachAsync(
        string projectPath,
        string? session,
        TimeSpan wait,
        bool shutOutRealGamepads,
        CancellationToken cancellationToken
    )
    {
        string projectDir = NormaliseProjectDir(projectPath);
        string bridgeScript = Installation.FindBridgeScript();
        SessionSpec spec = new(NameFor(session, projectDir), projectDir, SessionKind.Attach, shutOutRealGamepads, Quiet: false);
        GodotSession created = await ReserveAsync(spec);
        return await created.AttachAsync(bridgeScript, wait, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="capture"/> on a preview: the project started on the request's scene under a session of its own
    /// (<see cref="PreviewName"/>), which is stopped and forgotten however the call ends. The session takes the settings of
    /// the live sessions on the folder, whose override.cfg it shares, so it is never refused for differing from them; its run
    /// is started as the request says. The prep, the launch and the capture share <see cref="PreviewLimit"/>.
    /// </summary>
    /// <exception cref="SessionException">
    /// The project is missing, the prep failed or passed the limit, the launch failed, or the whole call passed the limit.
    /// </exception>
    public async Task<T> PreviewAsync<T>(
        LaunchRequest request,
        Func<GodotSession, PrepResult, CancellationToken, Task<T>> capture,
        CancellationToken cancellationToken
    )
    {
        string projectDir = NormaliseProjectDir(request.ProjectPath);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(PreviewLimit);
        GodotSession session = ReservePreview(projectDir);
        try
        {
            PrepResult prep = request.Prepare ? await PrepareForPreviewAsync(session, limit.Token, cancellationToken) : PrepResult.Skipped;
            await session.LaunchAsync(request with { ProjectPath = projectDir, Prepare = false }, limit.Token);
            return await capture(session, prep, limit.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(
                $"preview_scene did not show {request.Scene} within {PreviewLimit.TotalSeconds:0} s (prep, launch and capture together), "
                    + "so its game was stopped. run_project on the scene shows what holds it up.",
                e
            );
        }
        finally
        {
            await EndPreviewAsync(session);
        }
    }

    /// <exception cref="SessionException">No session answers to the name, or the session is attached.</exception>
    public Task<StopResult> StopAsync(string? session, CancellationToken cancellationToken)
    {
        GodotSession target =
            TryResolve(session)
            ?? throw new SessionException("No Godot session has been started, so there is nothing to stop. Start one with run_project.");
        return target.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Restarts the named or only run with the request it was launched with. The session is marked starting under the
    /// registry's lock, so its name is never free during the restart, after the folder's live sessions are checked again:
    /// one may have started on it with another setting since the session's game exited.
    /// </summary>
    /// <exception cref="SessionException">
    /// No session answers, the session is attached, still starting or never launched, the folder's live sessions rule it
    /// out, the prep failed, or the new game did not start.
    /// </exception>
    public Task<RestartResult> RestartAsync(string? session, bool prepare, CancellationToken cancellationToken)
    {
        GodotSession target;
        lock (_lock)
        {
            target = FindSession(session) ?? throw new SessionException(GodotSession.NoneRunning);
            CheckCanRestart(target);
            target.BeginRestart();
        }

        return target.RestartAsync(prepare, cancellationToken);
    }

    /// <exception cref="SessionException">No session answers to the name, or the session is not an attached one.</exception>
    public Task<DetachResult> DetachAsync(string? session, CancellationToken cancellationToken)
    {
        GodotSession target =
            TryResolve(session) ?? throw new SessionException("No session is attached, so there is nothing to detach. attach_project starts one.");
        return target.DetachAsync(cancellationToken);
    }

    /// <summary>The named or only session's output; empty while there is no session at all.</summary>
    /// <exception cref="SessionException">No session answers to the name, or the session is attached.</exception>
    public DebugOutput GetDebugOutput(string? session, int limit, long? before) =>
        TryResolve(session)?.GetDebugOutput(limit, before) ?? new DebugOutput();

    /// <summary>The session a tool addresses: the named one, else the only live one, else the only one there is.</summary>
    /// <exception cref="SessionException">No session answers to the name, there is none at all, or several could be meant.</exception>
    public GodotSession Resolve(string? session) => TryResolve(session) ?? throw new SessionException(GodotSession.NoneRunning);

    /// <summary>Every session, ordered by name.</summary>
    public IReadOnlyList<SessionInfo> List()
    {
        lock (_lock)
        {
            return [.. Ordered().Select(Describe)];
        }
    }

    /// <summary>
    /// The last-resort cleanup for the server's own exit: every session's own cleanup, then the override file of every
    /// folder a session used, since none of them outlives the server.
    /// </summary>
    public void Shutdown()
    {
        GodotSession[] sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
        }

        foreach (GodotSession session in sessions)
        {
            session.Shutdown();
        }

        foreach (string projectDir in sessions.Select(session => session.ProjectDir).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RemoveOverrideAtShutdown(projectDir);
        }
    }

    public void Dispose()
    {
        Shutdown();
        lock (_lock)
        {
            foreach (GodotSession session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }
    }

    /// <summary>The name a session gets: <paramref name="session"/> when given, else the project folder's name.</summary>
    /// <exception cref="SessionException">The given name breaks <see cref="NameRule"/>.</exception>
    internal static string NameFor(string? session, string projectPath)
    {
        CheckName(session);
        if (session is not null)
        {
            return session;
        }

        string projectDir = ProjectPaths.Normalise(projectPath);
        string folder = Path.GetFileName(projectDir);
        return folder.Length > 0 ? folder : projectDir;
    }

    /// <summary>The name of a folder's <paramref name="number"/>th preview session: <c>&lt;folder name&gt;.preview-&lt;n&gt;</c>.</summary>
    internal static string PreviewName(string folderName, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"{folderName}.preview-{number}");

    /// <summary>
    /// Registers a pending preview session on the folder under the next preview name no session holds. It takes the pad and
    /// quiet settings of a live session on the folder (quiet and no shut-out when there is none), so the folder's rule that
    /// its sessions agree on them, which their one override.cfg needs, holds without refusing the preview.
    /// </summary>
    internal GodotSession ReservePreview(string projectDir)
    {
        lock (_lock)
        {
            string folderName = NameFor(null, projectDir);
            string name;
            do
            {
                _previews++;
                name = PreviewName(folderName, _previews);
            } while (_sessions.ContainsKey(name));

            GodotSession? onFolder = _sessions.Values.FirstOrDefault(other => other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir));
            SessionSpec spec = new(name, projectDir, SessionKind.Run, onFolder?.ShutOutRealGamepads ?? false, onFolder?.Quiet ?? true);
            GodotSession created = new(spec, this);
            _sessions[name] = created;
            return created;
        }
    }

    /// <summary>Writes the marked override.cfg for a starting session, unless live sessions on its folder already have it.</summary>
    /// <exception cref="SessionException">The project has its own override.cfg.</exception>
    internal void WriteOverride(GodotSession session, string bridgeScript)
    {
        lock (_lock)
        {
            string path = OverrideFile.PathIn(session.ProjectDir);
            if (HasOtherLiveSession(session) && File.Exists(path) && OverrideFile.IsOurs(path))
            {
                return;
            }

            OverrideFile.Write(session.ProjectDir, bridgeScript, session.ShutOutRealGamepads, session.Quiet);
        }
    }

    /// <summary>Removes the session's override.cfg unless another live session uses the folder; returns whether it removed one.</summary>
    internal bool ReleaseFolder(GodotSession session)
    {
        lock (_lock)
        {
            return !HasOtherLiveSession(session) && OverrideFile.Remove(session.ProjectDir);
        }
    }

    /// <summary>The lock that lets one prep at a time build or import in a project folder.</summary>
    internal SemaphoreSlim PrepLock(string projectDir)
    {
        string folder = ProjectPaths.Normalise(projectDir);
        lock (_lock)
        {
            if (!_prepLocks.TryGetValue(folder, out SemaphoreSlim? folderLock))
            {
                folderLock = new SemaphoreSlim(1, 1);
                _prepLocks[folder] = folderLock;
            }

            return folderLock;
        }
    }

    /// <summary>
    /// The names of the sessions whose game runs on the folder (a started run, or an attached game still connected),
    /// ordered, leaving out <paramref name="except"/>. A session still starting is not counted: it waits on the folder's
    /// prep lock, so it starts its game only after a prep holding the lock has finished.
    /// </summary>
    internal IReadOnlyList<string> RunningSessionNames(string projectDir, GodotSession? except)
    {
        lock (_lock)
        {
            return
            [
                .. Ordered()
                    .Where(other => !ReferenceEquals(other, except) && other.HasGame && ProjectPaths.AreSame(other.ProjectDir, projectDir))
                    .Select(other => other.Name),
            ];
        }
    }

    /// <summary>
    /// The names of the live sessions on the folder (running, attached, or still launching or waiting to attach), ordered:
    /// every one of them has or will have the bridge's override.cfg in the folder.
    /// </summary>
    internal IReadOnlyList<string> LiveSessionNames(string projectDir)
    {
        lock (_lock)
        {
            return [.. Ordered().Where(other => other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir)).Select(other => other.Name)];
        }
    }

    /// <summary>Drops the session from the registry, if it still holds its name.</summary>
    internal void Forget(GodotSession session)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(session.Name, out GodotSession? held) && ReferenceEquals(held, session))
            {
                _sessions.Remove(session.Name);
            }
        }
    }

    private GodotSession? TryResolve(string? session)
    {
        lock (_lock)
        {
            return FindSession(session);
        }
    }

    /// <summary>The named session, else the only live one, else the only one there is; the caller holds the lock.</summary>
    private GodotSession? FindSession(string? session)
    {
        if (session is not null)
        {
            return _sessions.GetValueOrDefault(session) ?? throw new SessionException($"No session named '{session}'. Sessions: {DescribeAll()}.");
        }

        GodotSession[] live = [.. _sessions.Values.Where(candidate => candidate.IsLive)];
        if (live.Length == 1)
        {
            return live[0];
        }

        return live.Length == 0 && _sessions.Count <= 1
            ? _sessions.Values.FirstOrDefault()
            : throw new SessionException($"Several sessions exist ({DescribeAll()}); pass session to choose one.");
    }

    /// <summary>Registers a new pending session under the spec's name, then lets go of the ended session it replaces.</summary>
    /// <exception cref="SessionException">The name is live, or the folder's live sessions rule the new one out.</exception>
    private async Task<GodotSession> ReserveAsync(SessionSpec spec)
    {
        GodotSession created;
        GodotSession? replaced;
        lock (_lock)
        {
            replaced = _sessions.GetValueOrDefault(spec.Name);
            CheckCanStart(spec, replaced);
            created = new GodotSession(spec, this);
            _sessions[spec.Name] = created;
        }

        if (replaced is not null)
        {
            try
            {
                await RetireAsync(replaced);
            }
            catch
            {
                Forget(created);
                created.Dispose();
                throw;
            }
        }

        return created;
    }

    /// <summary>Refuses a session whose setting differs from the live ones' on its folder: they share one override.cfg.</summary>
    private static void CheckSameSetting(SessionSpec spec, GodotSession[] onFolder, string option, Func<GodotSession, bool> setting, bool wanted)
    {
        if (onFolder.FirstOrDefault(other => setting(other) != wanted) is { } differing)
        {
            string value = setting(differing) ? "true" : "false";
            throw new SessionException(
                $"Sessions on {spec.ProjectDir} run with {option}={value}; start this one with the same value, or stop them first."
            );
        }
    }

    private void CheckCanStart(SessionSpec spec, GodotSession? holder)
    {
        if (holder is { IsLive: true })
        {
            throw new SessionException(
                $"A session named '{holder.Name}' is live on {holder.ProjectDir}; stop_project or detach_project it, or pass another session name."
            );
        }

        GodotSession[] onFolder = CheckFolder(spec, except: null);
        if (spec.Kind == SessionKind.Attach && onFolder.Any(other => other.IsWaitingForGame))
        {
            throw new SessionException($"Another attach on {spec.ProjectDir} is still waiting for its game; wait for it or let it time out first.");
        }
    }

    /// <summary>Refuses an attached session, one still starting or never launched, and one the folder's live sessions rule out.</summary>
    private void CheckCanRestart(GodotSession target)
    {
        if (target.Kind == SessionKind.Attach)
        {
            throw new SessionException(
                $"session '{target.Name}' is attached, not started by run_project, so it cannot be restarted; detach_project, then start "
                    + "the game again yourself."
            );
        }

        if (target.IsStarting)
        {
            throw new SessionException($"session '{target.Name}' is still starting or restarting; wait for its call to return, or stop_project it.");
        }

        if (target.LastLaunch is null)
        {
            throw new SessionException(GodotSession.NoneRunning);
        }

        CheckFolder(new SessionSpec(target.Name, target.ProjectDir, target.Kind, target.ShutOutRealGamepads, target.Quiet), except: target);
    }

    /// <summary>The live sessions on the spec's folder but <paramref name="except"/>, once they are checked to share its settings.</summary>
    private GodotSession[] CheckFolder(SessionSpec spec, GodotSession? except)
    {
        GodotSession[] onFolder =
        [
            .. _sessions.Values.Where(other =>
                !ReferenceEquals(other, except) && other.IsLive && ProjectPaths.AreSame(other.ProjectDir, spec.ProjectDir)
            ),
        ];
        CheckSameSetting(spec, onFolder, "shutOutRealGamepads", session => session.ShutOutRealGamepads, spec.ShutOutRealGamepads);
        CheckSameSetting(spec, onFolder, "quiet", session => session.Quiet, spec.Quiet);
        return onFolder;
    }

    /// <summary>
    /// A preview's prep under the folder's prep lock, as a launch's is. Passing <paramref name="limit"/> stops it, with its
    /// whole process tree, and refuses the preview with where the logs are and how to prepare the project beforehand.
    /// </summary>
    /// <exception cref="SessionException">The prep failed, or it passed the preview's limit.</exception>
    private async Task<PrepResult> PrepareForPreviewAsync(GodotSession session, CancellationToken limit, CancellationToken caller)
    {
        string projectDir = session.ProjectDir;
        SemaphoreSlim folderLock = PrepLock(projectDir);
        try
        {
            await folderLock.WaitAsync(limit);
            try
            {
                PrepContext context = new(projectDir, logger, () => RunningSessionNames(projectDir, session));
                return await ProjectPrep.RunAsync(context, limit);
            }
            finally
            {
                folderLock.Release();
            }
        }
        catch (OperationCanceledException e) when (!caller.IsCancellationRequested)
        {
            throw new SessionException(
                $"The prep of {projectDir} (its C# build or Godot import) did not finish within preview_scene's "
                    + $"{PreviewLimit.TotalSeconds:0} s, so it was stopped with its whole process tree and the scene was not shown. "
                    + $"Its logs are in {ProjectPrep.LogFolder(projectDir)}. Run run_project or validate first to build and import, "
                    + "or pass prepare: never.",
                e
            );
        }
    }

    /// <summary>Stops a preview's game if it runs, then forgets the session and lets go of it as a replaced session is.</summary>
    private async Task EndPreviewAsync(GodotSession session)
    {
        if (session.HasGame)
        {
            try
            {
                await session.StopAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.OverrideRemovalFailed(logger, e, session.ProjectDir);
            }
        }

        Forget(session);
        await RetireAsync(session);
    }

    private async Task RetireAsync(GodotSession replaced)
    {
        await replaced.RetireAsync();
        try
        {
            ReleaseFolder(replaced);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.OverrideRemovalFailed(logger, e, replaced.ProjectDir);
        }

        replaced.Dispose();
    }

    private bool HasOtherLiveSession(GodotSession session) =>
        _sessions.Values.Any(other => !ReferenceEquals(other, session) && other.IsLive && ProjectPaths.AreSame(other.ProjectDir, session.ProjectDir));

    private IEnumerable<GodotSession> Ordered() => _sessions.Values.OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase);

    private string DescribeAll()
    {
        string[] entries = [.. Ordered().Select(session => $"{session.Name} ({(session.IsLive ? "live" : "stopped")})")];
        return entries.Length == 0 ? "none" : string.Join(", ", entries);
    }

    private static SessionInfo Describe(GodotSession session) =>
        new(session.Name, session.ProjectDir, session.Kind == SessionKind.Run ? "run" : "attach", session.IsLive, session.ProcessId)
        {
            Recording = session.RecordingState,
        };

    private static void CheckName(string? session)
    {
        if (session is not null && !ValidName().IsMatch(session))
        {
            throw new SessionException($"session '{session}' is not a valid name: {NameRule}.");
        }
    }

    /// <exception cref="SessionException">The path is empty or holds no project.godot.</exception>
    internal static string NormaliseProjectDir(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new SessionException("projectPath is empty. Pass the folder that holds the project's project.godot.");
        }

        string projectDir = ProjectPaths.Normalise(projectPath);
        return File.Exists(Path.Combine(projectDir, "project.godot"))
            ? projectDir
            : throw new SessionException($"{projectDir} holds no project.godot. Pass the folder that holds the project's project.godot.");
    }

    private static void RemoveOverrideAtShutdown(string projectDir)
    {
        try
        {
            OverrideFile.Remove(projectDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine($"godot-mcp: cleanup of {projectDir} at shutdown failed: {e.Message}");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex ValidName();
}

/// <summary>One entry of list_sessions; <see cref="Recording"/> is set for a session whose latest run records.</summary>
internal sealed record SessionInfo(string Name, string ProjectPath, string Kind, bool Live, int? ProcessId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? Recording { get; init; }
}
