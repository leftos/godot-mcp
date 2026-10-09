using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The server's sessions by name (case-insensitive): runs and attaches share one name space, a live name is refused and a
/// name whose session has ended is replaced. Several sessions may share a project folder; they share its one marked
/// override.cfg, which is removed when the last live session on the folder ends and no other server's live session uses it.
/// </summary>
internal sealed partial class SessionRegistry(BridgeListener listener, ILogger<GodotSession> logger) : IDisposable
{
    /// <summary>What a launch or an attach says about a session name it refuses.</summary>
    public const string NameRule = "a session name is 1 to 64 characters of letters, digits, '.', '_' and '-'";

    /// <summary>How long a preview may take in all: its prep, its launch and its capture.</summary>
    public static readonly TimeSpan PreviewLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long, in wall time, the server's exit waits for its games to quit before killing the rest; it cuts a recording
    /// run's 30 s grace short.
    /// </summary>
    internal static readonly TimeSpan ShutdownCap = TimeSpan.FromSeconds(10);

    /// <summary>How often the server's exit looks again whether the launches and restarts in flight have settled.</summary>
    private static readonly TimeSpan StartSettlePoll = TimeSpan.FromMilliseconds(50);

    private readonly Lock _lock = new();

    // Every Shutdown pass's stop of the games so far, under _lock; a later pass waits for it as well as its own.
    private Task _gamesStopped = Task.CompletedTask;

    // The sessions a Shutdown pass has taken, under _lock; a later pass stops only the others, so no game is asked twice.
    private readonly HashSet<GodotSession> _shutDown = [];

    // Whether a Shutdown pass has begun, under _lock; from then on no run starts (RefuseStartAtShutdown).
    private bool _shuttingDown;

    /// <summary>What a launch, restart, preview or scratch run fails with once the server's exit has begun.</summary>
    internal const string ShuttingDown = "The godot-mcp server is shutting down; no game can start now.";

    // The number of the last preview session named, under _lock; each preview's name carries the next.
    private int _previews;
    private readonly Dictionary<string, GodotSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scene each scratch session name was last taken for (its folder and res:// path), and the session that took it.</summary>
    private readonly Dictionary<string, (string Owner, GodotSession Session)> _scratchScenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LoadClock? _launchClock;
    private HeadlessHosts? _headlessHosts;

    // One per folder a prep has run on, kept for the server's lifetime. Never disposed: a prep still in flight at shutdown
    // releases its lock after the registry is gone, and a SemaphoreSlim whose wait handle is never asked for holds no handle.
    private readonly Dictionary<string, SemaphoreSlim> _prepLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    // The folders this server has armed, by normalised path, under _lock.
    private readonly Dictionary<string, ArmSettings> _armed = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    internal BridgeListener Listener => listener;

    /// <summary>The clock every ceiling the sessions enforce runs on: the listener's.</summary>
    internal LoadClock Clock => Listener.Clock;

    /// <summary>
    /// The clock a launch's handshake and an attach's wait for its game run on: <see cref="Clock"/> unless set. A harness that
    /// keeps <see cref="Clock"/> on wall time, so short ceilings keep their text, sets it to a load-adjusted clock, so a game
    /// slow to start on a busy machine is waited for.
    /// </summary>
    internal LoadClock LaunchClock
    {
        get => _launchClock ?? Clock;
        init => _launchClock = value;
    }

    internal ILogger Logger => logger;

    /// <summary>Starts a warm headless host's process: <see cref="GodotHostProcess.Launch"/> unless set, as a test sets a fake one.</summary>
    internal Func<HostLaunch, IHostProcess> HostLauncher { get; init; } = launch => GodotHostProcess.Launch(launch, logger);

    /// <summary>
    /// The server's process id, which names its warm hosts' logs (<see cref="HeadlessHost.LogPathOf"/>): this process's unless
    /// set, as a test sets one per registry to stand in for two servers.
    /// </summary>
    internal int ServerProcessId { get; init; } = Environment.ProcessId;

    /// <summary>The warm headless hosts, one per GDScript-only project folder a headless tool has run on.</summary>
    internal HeadlessHosts HeadlessHosts => LazyInitializer.EnsureInitialized(ref _headlessHosts, () => new HeadlessHosts(this));

    /// <summary>
    /// The machine-wide list every folder is recorded in before its override.cfg is written: <see cref="OverrideFolders.Default"/>
    /// unless set, as a test sets it to a list of its own.
    /// </summary>
    internal OverrideFolders OverrideFolders { get; init; } = OverrideFolders.Default;

    /// <summary>The dormant games on armed folders: <see cref="DormantGames.Default"/> unless set, as a test sets a fake process boundary.</summary>
    internal DormantGames Dormant { get; init; } = DormantGames.Default;

    /// <summary>The sessions' input captures, by session name, kept past the session they came from.</summary>
    internal CaptureStore Captures { get; } = new();

    /// <summary>
    /// Whether a debugger is attached to a game's process, by its id: <see cref="DebuggerPresence.IsAttached"/> logging to the
    /// registry's logger, or a test's fake.
    /// </summary>
    internal Func<int, bool> IsDebuggerAttached { get; set; } = processId => DebuggerPresence.IsAttached(processId, logger);

    /// <summary>How a stop describes a game's process state before killing it; a test replaces it.</summary>
    internal Func<int, Task<string>> DescribeGameProcess { get; set; } =
        processId => HangProbe.DescribeProcessAsync(processId, null, CancellationToken.None);

    /// <summary>
    /// Launches a run under <paramref name="session"/>, or when it is null under the project folder's name, numbered when a live
    /// session on another folder holds it (<see cref="DefaultName"/>).
    /// </summary>
    /// <exception cref="SessionException">The name is invalid or live, the project is missing, or the launch failed.</exception>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, string? session, CancellationToken cancellationToken)
    {
        string projectDir = NormaliseProjectDir(request.ProjectPath);
        SessionSpec spec = new(NameFor(session, projectDir), projectDir, SessionKind.Run, request.ShutOutRealGamepads, request.Quiet)
        {
            Mute = request.Mute,
        };
        GodotSession created = await ReserveAsync(spec, defaultName: session is null, completeUnderLock: static ready => ready);
        return await created.LaunchAsync(request with { ProjectPath = projectDir }, cancellationToken);
    }

    /// <summary>
    /// Attaches under the request's session, or when it is null under the project folder's name, numbered as a launch's
    /// is (<see cref="DefaultName"/>). A quiet attach writes the quiet override and tells the bridge to park its window, and
    /// shares the folder rules of a quiet run. The game is a dormant one on the folder when one is chosen
    /// (<see cref="ChooseDormantGame"/>, under the registry's lock, so two joins at once never choose one game), else one
    /// launched after the call.
    /// </summary>
    /// <exception cref="SessionException">
    /// The name is invalid or live, the project is missing, the dormant game asked for is not there, several could be meant or
    /// another join already waits for it, or no game connected in time.
    /// </exception>
    public async Task<AttachResult> AttachAsync(AttachRequest request, CancellationToken cancellationToken)
    {
        string projectDir = NormaliseProjectDir(request.ProjectPath);
        string bridgeScript = Installation.FindBridgeScript();
        ArmSettings settings = AttachSettings(projectDir, request);
        SessionSpec spec = new(NameFor(request.Session, projectDir), projectDir, SessionKind.Attach, settings.ShutOutRealGamepads, settings.Quiet)
        {
            Mute = settings.Mute,
        };
        GodotSession created = await ReserveAsync(
            spec,
            defaultName: request.Session is null,
            completeUnderLock: attach => attach with { JoinPid = ChooseDormantGame(projectDir, request.Pid) }
        );
        return await created.AttachAsync(bridgeScript, request.Wait, cancellationToken);
    }

    /// <summary>
    /// Arms the folder: writes its override.cfg (recorded in <see cref="OverrideFolders"/>, unless live sessions on it already
    /// have it) and its armed.json under the folder's prep lock, so every game started on it until <see cref="Disarm"/> or the
    /// server's exit carries a dormant bridge. Arming a folder again with the same settings changes nothing.
    /// </summary>
    /// <exception cref="SessionException">
    /// The project is missing or has its own override.cfg, the folder is armed with other settings, its live sessions run
    /// with other settings, or another live server's override.cfg or armed.json on it holds another bridge or other settings.
    /// </exception>
    public async Task<ArmState> ArmAsync(string projectPath, ArmSettings settings, CancellationToken cancellationToken)
    {
        string projectDir = NormaliseProjectDir(projectPath);
        string bridgeScript = Installation.FindBridgeScript();
        SemaphoreSlim folderLock = PrepLock(projectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsArmedAlready(projectDir, settings))
            {
                ArmFolder(projectDir, bridgeScript, settings);
            }
        }
        finally
        {
            folderLock.Release();
        }

        return DescribeArm(projectDir, settings);
    }

    /// <summary>
    /// Disarms a folder this server armed: takes this server off its armed.json's owners, then releases its override.cfg unless
    /// a live session of this server uses the folder. Attached sessions stay attached.
    /// </summary>
    /// <exception cref="SessionException">The project is missing, or this server has not armed the folder.</exception>
    public DisarmResult Disarm(string projectPath)
    {
        string projectDir = NormaliseProjectDir(projectPath);
        return OverrideFolders.Hold(
            $"disarming {projectDir}",
            () =>
            {
                lock (_lock)
                {
                    if (!_armed.Remove(projectDir))
                    {
                        throw new SessionException($"{projectDir} is not armed; arm_project arms it.");
                    }

                    ArmFile.Release(projectDir);
                    bool removed = !HasLiveSessionOn(projectDir, except: null) && OverrideFile.Release(projectDir);
                    return new DisarmResult(projectDir, removed);
                }
            }
        );
    }

    /// <summary>The folders this server has armed, ordered by path, each with its settings and its dormant games.</summary>
    public IReadOnlyList<ArmState> ListArmed()
    {
        KeyValuePair<string, ArmSettings>[] armed;
        lock (_lock)
        {
            armed = [.. _armed.OrderBy(folder => folder.Key, StringComparer.OrdinalIgnoreCase)];
        }

        return [.. armed.Select(folder => DescribeArm(folder.Key, folder.Value))];
    }

    /// <summary>The warm headless hosts, ordered by folder; empty, without making the pool, when no headless tool has run.</summary>
    public IReadOnlyList<HeadlessHostInfo> ListHeadlessHosts() => Volatile.Read(ref _headlessHosts)?.List() ?? [];

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
        using LoadDeadline limit = Clock.Start(PreviewLimit, cancellationToken);
        GodotSession session = ReservePreview(projectDir);
        try
        {
            PrepResult prep = request.Prepare ? await PrepareForPreviewAsync(session, limit, cancellationToken) : PrepResult.Skipped;
            await session.LaunchAsync(request with { ProjectPath = projectDir, Prepare = false }, limit.Token);
            return await capture(session, prep, limit.Token);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionException(
                $"preview_scene did not show {request.Scene} within {PreviewLimit.TotalSeconds:0} s (prep, launch and capture together)"
                    + $"{BackstopClauseOf(limit)}, so its game was stopped. run_project on the scene shows what holds it up.",
                e
            );
        }
        finally
        {
            await EndPreviewAsync(session);
        }
    }

    /// <summary>
    /// Stops the named or only session, then the warm headless host on its folder if it is idle; a host running a request is
    /// left running, without waiting for it.
    /// </summary>
    /// <exception cref="SessionException">No session answers to the name, or it never reached a game.</exception>
    public async Task<StopResult> StopAsync(string? session, CancellationToken cancellationToken)
    {
        GodotSession target =
            TryResolve(session)
            ?? throw new SessionException("No Godot session has been started, so there is nothing to stop. Start one with run_project.");
        StopResult stopped = await target.StopAsync(cancellationToken);
        bool hostStopped = Volatile.Read(ref _headlessHosts) is { } hosts && await hosts.StopIfIdleAsync(target.ProjectDir, "stop_project");
        return stopped with { HeadlessHostStopped = hostStopped };
    }

    /// <summary>
    /// stop_project on a folder: stops the session running there (the one <paramref name="session"/> names, else the only
    /// live one on the folder) and the folder's warm headless host, waiting for a request running on it under the folder's
    /// prep lock, so the folder can be renamed or deleted.
    /// </summary>
    /// <returns>The session's <see cref="StopResult"/>, or a <see cref="HostStopResult"/> when only a host ran there.</returns>
    /// <exception cref="SessionException">
    /// The folder holds no project, the named session runs another folder, several live sessions run there, the session never
    /// reached a game, or neither a session nor a host runs there.
    /// </exception>
    public async Task<object> StopFolderAsync(string projectPath, string? session, CancellationToken cancellationToken)
    {
        string projectDir = NormaliseProjectDir(projectPath);
        GodotSession? target = FindSessionIn(projectDir, session);
        if (target is not null)
        {
            StopResult stopped = await target.StopAsync(cancellationToken);
            return stopped with { HeadlessHostStopped = await StopHeadlessHostAsync(projectDir, cancellationToken) };
        }

        return await StopHeadlessHostAsync(projectDir, cancellationToken)
            ? new HostStopResult(projectDir, HeadlessHostStopped: true)
            : throw new SessionException($"No Godot session or warm headless host runs in {projectDir}.");
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

    /// <summary>The live sessions, ordered by name; with <paramref name="includeStopped"/>, the stopped ones too.</summary>
    public IReadOnlyList<SessionInfo> List(bool includeStopped)
    {
        lock (_lock)
        {
            return [.. Ordered().Where(session => includeStopped || session.IsLive).Select(Describe)];
        }
    }

    /// <summary>
    /// The cleanup for the server's own exit: every launch and restart in flight waited for, then every session's own shutdown
    /// at once (<see cref="GodotSession.ShutdownAsync"/>: the games it launched asked to quit, sharing one grace), every game
    /// still running at <see cref="ShutdownCap"/> (which the wait for the starts shares) killed,
    /// then every warm headless host stopped and waited for, every armed folder disarmed, then this server's release of the
    /// override file of every folder a session used or was armed, since none of them outlives the server. It runs from the
    /// host's stop, the process's exit and <see cref="Dispose"/>: the first call stops the games, and a later one waits for
    /// that and stops only the sessions no earlier pass has covered. Once it has begun, no run starts
    /// (<see cref="RefuseStartAtShutdown"/>).
    /// </summary>
    public void Shutdown()
    {
        GodotSession[] sessions;
        string[] armed;
        Task gamesStopped;
        lock (_lock)
        {
            _shuttingDown = true;
            sessions = [.. _sessions.Values];
            armed = [.. _armed.Keys];
            _armed.Clear();
            GodotSession[] uncovered = [.. sessions.Where(_shutDown.Add)];
            Task earlier = _gamesStopped;
            gamesStopped = _gamesStopped =
                uncovered.Length == 0 ? earlier : Task.WhenAll(earlier, Task.Run(() => StopGamesAtShutdownAsync(uncovered)));
        }

        try
        {
            gamesStopped.Wait();
        }
        finally
        {
            try
            {
                Volatile.Read(ref _headlessHosts)?.Shutdown();
            }
            finally
            {
                OverrideFolders.Hold("the shutdown cleanup", () => ReleaseAtShutdown(sessions, armed));
            }
        }
    }

    /// <summary>
    /// Fails a run about to start once <see cref="Shutdown"/> has begun. Its check and the shutdown's flag share the registry's
    /// lock, so a start either is refused or passed the check first and is in flight, which a later pass takes.
    /// </summary>
    /// <exception cref="SessionException">The server's exit has begun.</exception>
    internal void RefuseStartAtShutdown()
    {
        lock (_lock)
        {
            if (_shuttingDown)
            {
                throw new SessionException(ShuttingDown);
            }
        }
    }

    /// <summary>
    /// Waits for every launch or restart in flight to settle, then stops every session's game at once and waits for them, the
    /// two sharing <see cref="ShutdownCap"/> of wall time; then kills every game still running of the sessions it took. A
    /// session still starting at the cap is handed back to a later pass and its game, once it has one, killed.
    /// </summary>
    private async Task StopGamesAtShutdownAsync(GodotSession[] sessions)
    {
        var cap = Task.Delay(ShutdownCap);
        await WaitForStartsAsync(sessions, cap);
        Task<bool>[] stops = [.. sessions.Select(session => Task.Run(() => ShutdownSessionAsync(session)))];
        Task<bool[]> all = Task.WhenAll(stops);
        if (await Task.WhenAny(all, cap) != all)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            await Console.Error.WriteLineAsync(
                $"godot-mcp: the games had not all quit {ShutdownCap.TotalSeconds:0} s into the shutdown; killing the rest."
            );
        }

        // A session handed back that is no longer starting never launched, so it has no game to kill.
        await Task.WhenAll(sessions.Where((session, index) => !IsHandedBack(stops[index]) || session.IsStarting).Select(KillSessionAtShutdownAsync));
    }

    /// <summary>Whether a session's stop at shutdown returned and handed the session back to a later pass.</summary>
    private static bool IsHandedBack(Task<bool> stop) => stop.IsCompletedSuccessfully && !stop.Result;

    /// <summary>
    /// Waits until no launch or restart of <paramref name="sessions"/> is in flight, or <paramref name="cap"/> has passed. No
    /// start begins once the shutdown has, and one still in its prep is refused when it ends, so they settle quickly.
    /// </summary>
    private static async Task WaitForStartsAsync(GodotSession[] sessions, Task cap)
    {
        while (!cap.IsCompleted && sessions.Any(session => session.Kind == SessionKind.Run && session.IsStarting))
        {
            await Task.Delay(StartSettlePoll);
        }
    }

    /// <summary>One session's <see cref="GodotSession.KillAtShutdownAsync"/>, a failure reported to stderr so the other kills go on.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "At the server's exit one session's failed kill, whatever it is, must not stop the others or the file release."
    )]
    private static async Task KillSessionAtShutdownAsync(GodotSession session)
    {
        try
        {
            await session.KillAtShutdownAsync();
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"godot-mcp: killing the game of {session.ProjectDir} at shutdown failed: {e.Message}");
        }
    }

    /// <summary>
    /// One session's <see cref="GodotSession.ShutdownAsync"/>, a failure reported to stderr so the others go on; a run still
    /// starting is taken off the covered set, so a later pass takes it.
    /// </summary>
    /// <returns>Whether this pass took the session; false when it handed it back. A failed stop counts as taken, so its game is killed.</returns>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "At the server's exit one session's failure, whatever it is, must not stop the others or the file release."
    )]
    private async Task<bool> ShutdownSessionAsync(GodotSession session)
    {
        try
        {
            if (await session.ShutdownAsync())
            {
                return true;
            }

            lock (_lock)
            {
                _shutDown.Remove(session);
            }

            return false;
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"godot-mcp: stopping the game of {session.ProjectDir} at shutdown failed: {e.Message}");
            return true;
        }
    }

    /// <summary>Takes this server off the armed.json of every armed folder, then off the override.cfg of every folder it used.</summary>
    private static void ReleaseAtShutdown(GodotSession[] sessions, string[] armed)
    {
        foreach (string projectDir in armed)
        {
            AtShutdown(projectDir, () => ArmFile.Release(projectDir));
        }

        foreach (string projectDir in sessions.Select(session => session.ProjectDir).Concat(armed).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AtShutdown(projectDir, () => OverrideFile.Release(projectDir));
        }
    }

    /// <summary>
    /// Shuts down (<see cref="Shutdown"/>), then disposes and drops every session not still starting. One still starting past
    /// the shutdown's cap keeps its gate, which its start still releases, and stays for the process exit's pass to take.
    /// </summary>
    public void Dispose()
    {
        Shutdown();
        lock (_lock)
        {
            foreach (GodotSession session in _sessions.Values.Where(session => !session.IsStarting).ToArray())
            {
                session.Dispose();
                _sessions.Remove(session.Name);
            }
        }
    }

    /// <summary>
    /// The name a session gets: <paramref name="session"/> when given, else the project folder's name with every
    /// character outside <see cref="NameRule"/>'s set replaced by '_' and the result cut to 64 characters.
    /// </summary>
    /// <exception cref="SessionException">The given name breaks <see cref="NameRule"/>; a given name is never changed.</exception>
    internal static string NameFor(string? session, string projectPath)
    {
        CheckName(session);
        if (session is not null)
        {
            return session;
        }

        string projectDir = ProjectPaths.Normalise(projectPath);
        string folder = Path.GetFileName(projectDir);
        return SanitiseFolderName(folder.Length > 0 ? folder : projectDir);
    }

    /// <summary>
    /// The name of a folder's <paramref name="number"/>th preview session: <c>&lt;folder name&gt;.preview-&lt;n&gt;</c>, with
    /// the folder part cut so the whole name fits <see cref="NameRule"/> and the suffix stays whole.
    /// </summary>
    internal static string PreviewName(string folderName, int number) =>
        WithSuffix(folderName, string.Create(CultureInfo.InvariantCulture, $".preview-{number}"));

    /// <summary>
    /// The name of the session a scratch scene plays in: <c>&lt;prefix&gt;.scratch-&lt;scene&gt;</c>, the scene's characters
    /// outside <see cref="NameRule"/>'s set replaced by '_', the prefix part cut so the whole name fits and the scene part cut
    /// only when it alone would not. The prefix is the project folder's name, or the run's <c>options.session</c>.
    /// </summary>
    internal static string ScratchName(string prefix, string scene)
    {
        string suffix = ".scratch-" + SanitiseFolderName(scene);
        return WithSuffix(prefix, suffix.Length > 64 ? suffix[..64] : suffix);
    }

    /// <summary>
    /// Whether a scratch scene may take a name: no session holds it, or a stopped session of the same scene (folder and res://
    /// path, <paramref name="holderScene"/>) does, which it replaces. <paramref name="holderLive"/> is null when no session
    /// holds the name.
    /// </summary>
    internal static bool MayTakeScratchName(bool? holderLive, string? holderScene, string scene) =>
        holderLive is null || (holderLive == false && string.Equals(holderScene, scene, StringComparison.Ordinal));

    /// <summary>
    /// Registers a pending session for a scratch scene under <see cref="ScratchName"/> of the run's prefix, or, when a live
    /// session or another scene's session holds that, under the first of its <see cref="NumberedName"/>s from 2 that the scene
    /// may take (<see cref="MayTakeScratchName"/>): two runs of one scene, two worktrees with one folder name, and scene names
    /// that clean to the same text or share their first 55 characters each get a session of their own. It takes the folder's
    /// live settings as a preview does (<see cref="FolderSettingsForPreview"/>), so it is never refused for differing from the
    /// sessions it shares the override.cfg with.
    /// </summary>
    internal async Task<GodotSession> ReserveScratchAsync(string projectDir, string scene, string resPath, string prefix)
    {
        string owner = ProjectPaths.Normalise(projectDir) + "|" + resPath;
        string baseName = ScratchName(prefix, scene);
        SessionSpec spec = new(baseName, projectDir, SessionKind.Run, ShutOutRealGamepads: false, Quiet: true);
        GodotSession created = await ReserveAsync(
            spec,
            defaultName: false,
            completeUnderLock: ready =>
            {
                string name = FreeScratchName(baseName, owner);
                ArmSettings settings = FolderSettingsForPreview(projectDir);
                return ready with { Name = name, Quiet = settings.Quiet, ShutOutRealGamepads = settings.ShutOutRealGamepads, Mute = settings.Mute };
            }
        );
        lock (_lock)
        {
            _scratchScenes[created.Name] = (owner, created);
        }

        return created;
    }

    /// <summary>
    /// The scratch name the scene takes: the base name, else its first numbered name it may take. A name's scene counts only
    /// while the session that took it for that scene still holds it, so a session started under it since is never replaced.
    /// The caller holds the lock.
    /// </summary>
    private string FreeScratchName(string baseName, string owner)
    {
        string name = baseName;
        for (int number = 2; ; number++)
        {
            GodotSession? holder = _sessions.GetValueOrDefault(name);
            string? holderScene =
                _scratchScenes.TryGetValue(name, out (string Owner, GodotSession Session) taken) && ReferenceEquals(taken.Session, holder)
                    ? taken.Owner
                    : null;
            if (MayTakeScratchName(holder?.IsLive, holderScene, owner))
            {
                return name;
            }

            name = NumberedName(baseName, number);
        }
    }

    /// <summary>
    /// A run_scratches prep, once for its scenes, under the folder's prep lock as a launch's is; the scenes then launch with
    /// prepare off.
    /// </summary>
    /// <exception cref="SessionException">The prep failed: a red build, or an import refused while a game runs on the folder.</exception>
    internal async Task<PrepResult> PrepareFolderAsync(string projectDir, CancellationToken cancellationToken)
    {
        SemaphoreSlim folderLock = PrepLock(projectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            PrepContext context = new(
                projectDir,
                logger,
                () => RunningSessionNames(projectDir, except: null),
                () => HeadlessHosts.StopFolder(projectDir, "import")
            );
            return await ProjectPrep.RunAsync(context, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>
    /// A folder's <paramref name="number"/>th default name, <c>&lt;folder name&gt;-&lt;n&gt;</c>, with the folder part cut so
    /// the whole name fits <see cref="NameRule"/> and the suffix stays whole.
    /// </summary>
    internal static string NumberedName(string folderName, int number) =>
        WithSuffix(folderName, string.Create(CultureInfo.InvariantCulture, $"-{number}"));

    private static string WithSuffix(string folderName, string suffix)
    {
        int keep = Math.Max(0, 64 - suffix.Length);
        return folderName.Length > keep ? folderName[..keep] + suffix : folderName + suffix;
    }

    /// <summary>
    /// Makes a folder-derived name fit <see cref="NameRule"/>: every character it excludes becomes '_', and the first 64
    /// characters are kept.
    /// </summary>
    private static string SanitiseFolderName(string folderName)
    {
        int length = Math.Min(folderName.Length, 64);
        Span<char> sanitised = stackalloc char[64];
        for (int index = 0; index < length; index++)
        {
            char character = folderName[index];
            sanitised[index] = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_';
        }

        return new string(sanitised[..length]);
    }

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

            ArmSettings settings = FolderSettingsForPreview(projectDir);
            SessionSpec spec = new(name, projectDir, SessionKind.Run, settings.ShutOutRealGamepads, settings.Quiet);
            GodotSession created = new(spec, this);
            _sessions[name] = created;
            return created;
        }
    }

    /// <summary>
    /// Writes the marked override.cfg for a starting session, with this server among its owners, unless the folder is held
    /// (another live session uses it, or it is armed) and already has it; the folder is recorded in <see cref="OverrideFolders"/> first.
    /// </summary>
    /// <exception cref="SessionException">
    /// The project has its own override.cfg, or another live server's marked one injects another bridge or holds other settings.
    /// </exception>
    internal void WriteOverride(GodotSession session, string bridgeScript) =>
        WriteOverrideUnlessHeld(
            session.ProjectDir,
            bridgeScript,
            new ArmSettings(session.Quiet, session.ShutOutRealGamepads, session.Mute),
            () => IsHeldByOther(session)
        );

    /// <summary>
    /// Takes this server off the owners of the session's override.cfg unless the folder is held: another of its live sessions
    /// uses it, or it is armed. The file is deleted once no live server owns it. Returns whether it deleted the file.
    /// </summary>
    internal bool ReleaseFolder(GodotSession session) =>
        OverrideFolders.Hold(
            $"releasing {session.ProjectDir}",
            () =>
            {
                lock (_lock)
                {
                    return !IsHeldByOther(session) && OverrideFile.Release(session.ProjectDir);
                }
            }
        );

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

    /// <summary>
    /// The session stop_project stops on the folder: the named one, which must run there, else the only live one there;
    /// null when none is live there.
    /// </summary>
    /// <exception cref="SessionException">No session answers to the name, it runs another folder, or several live ones run there.</exception>
    private GodotSession? FindSessionIn(string projectDir, string? session)
    {
        lock (_lock)
        {
            if (session is not null)
            {
                GodotSession named = FindSession(session)!;
                return ProjectPaths.AreSame(named.ProjectDir, projectDir)
                    ? named
                    : throw new SessionException($"session '{named.Name}' runs {named.ProjectDir}, not projectPath {projectDir}; pass one of them.");
            }

            GodotSession[] live = [.. Ordered().Where(other => other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir))];
            return live.Length <= 1
                ? live.FirstOrDefault()
                : throw new SessionException(
                    $"Several sessions run in {projectDir}: {string.Join(", ", live.Select(other => other.Name))}; pass session to choose one."
                );
        }
    }

    /// <summary>
    /// Stops the folder's warm headless host under the folder's prep lock, so it waits for a request running there; false,
    /// without making the pool, when the folder has none.
    /// </summary>
    private async Task<bool> StopHeadlessHostAsync(string projectDir, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _headlessHosts) is not { } hosts)
        {
            return false;
        }

        SemaphoreSlim folderLock = PrepLock(projectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            return hosts.StopFolder(projectDir, "stop_project");
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>The named session, else the only live one, else the only one there is; the caller holds the lock.</summary>
    private GodotSession? FindSession(string? session)
    {
        if (session is not null)
        {
            return _sessions.GetValueOrDefault(session)
                ?? throw new SessionException($"No session named '{session}'. Live sessions: {DescribeLive()}.{DescribeStopped()}");
        }

        GodotSession[] live = [.. _sessions.Values.Where(candidate => candidate.IsLive)];
        if (live.Length == 1)
        {
            return live[0];
        }

        return live.Length == 0 && _sessions.Count <= 1
            ? _sessions.Values.FirstOrDefault()
            : throw new SessionException(
                $"Several sessions exist (live: {DescribeLive()}); pass session to choose one. "
                    + "Pass the session run_project or attach_project returned on every call: "
                    + $"another agent's game can start at any time.{DescribeStopped()}"
            );
    }

    /// <summary>
    /// Registers a new pending session under the spec's name, or under <see cref="DefaultName"/> when
    /// <paramref name="defaultName"/> says the spec's name is the folder's, then lets go of the ended session it replaces.
    /// </summary>
    /// <param name="spec">The session to reserve.</param>
    /// <param name="defaultName">Whether the spec's name is the folder's, to be numbered when another folder's session holds it.</param>
    /// <param name="completeUnderLock">
    /// Completes the spec under the registry's lock, before the checks, as an attach chooses its dormant game there.
    /// </param>
    /// <exception cref="SessionException">
    /// The name is live, <paramref name="completeUnderLock"/> refused, or the folder's live sessions rule the new one out.
    /// </exception>
    private async Task<GodotSession> ReserveAsync(SessionSpec spec, bool defaultName, Func<SessionSpec, SessionSpec> completeUnderLock)
    {
        GodotSession created;
        GodotSession? replaced;
        lock (_lock)
        {
            spec = completeUnderLock(spec);
            if (defaultName)
            {
                spec = spec with { Name = DefaultName(spec.Name, spec.ProjectDir) };
            }

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

    /// <summary>
    /// The name a session started without one gets: the folder's name, unless a live session on another folder holds it (two
    /// worktrees of one repo share the leaf), then the first <see cref="NumberedName"/> from 2 that no live session holds. A
    /// live holder on the same folder keeps the folder's name, so the start is refused as before. The caller holds the lock.
    /// </summary>
    private string DefaultName(string folderName, string projectDir)
    {
        if (_sessions.GetValueOrDefault(folderName) is not { IsLive: true } holder || ProjectPaths.AreSame(holder.ProjectDir, projectDir))
        {
            return folderName;
        }

        for (int number = 2; ; number++)
        {
            string candidate = NumberedName(folderName, number);
            if (_sessions.GetValueOrDefault(candidate) is not { IsLive: true })
            {
                return candidate;
            }
        }
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
        if (spec.Kind == SessionKind.Attach && onFolder.FirstOrDefault(other => other.IsWaitingForGame && other.JoinPid == spec.JoinPid) is { } rival)
        {
            throw new SessionException(DescribeWaitingRival(spec, rival));
        }
    }

    /// <summary>
    /// Why an attach cannot wait beside <paramref name="rival"/>, which waits for the same game: a join of the same dormant game,
    /// or a second attach waiting for a launch, since the attach file is one per folder.
    /// </summary>
    private static string DescribeWaitingRival(SessionSpec spec, GodotSession rival) =>
        spec.JoinPid is int pid
            ? $"A join of the game (pid {pid}) on {spec.ProjectDir} is already waiting in session '{rival.Name}'; wait for it or let it time "
                + "out first."
            : $"Another attach on {spec.ProjectDir} is still waiting for its game; wait for it or let it time out first.";

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
        if (_armed.TryGetValue(spec.ProjectDir, out ArmSettings? arm))
        {
            CheckArmSetting(spec.ProjectDir, "shutOutRealGamepads", arm.ShutOutRealGamepads, spec.ShutOutRealGamepads);
            CheckArmSetting(spec.ProjectDir, "quiet", arm.Quiet, spec.Quiet);
        }

        return onFolder;
    }

    /// <summary>Refuses a session whose setting differs from the folder's arm: the arm's override.cfg is the one it would share.</summary>
    private static void CheckArmSetting(string projectDir, string option, bool armed, bool wanted)
    {
        if (armed != wanted)
        {
            throw new SessionException(
                $"{projectDir} is armed with {option}={(armed ? "true" : "false")}; start this session with the same value, or "
                    + "disarm_project first."
            );
        }
    }

    /// <summary>
    /// Whether the folder is already armed with <paramref name="wanted"/>, once it is checked that it is not armed with other
    /// settings and that its live sessions run with the same ones.
    /// </summary>
    /// <exception cref="SessionException">The folder is armed with other settings, or its live sessions run with other ones.</exception>
    private bool IsArmedAlready(string projectDir, ArmSettings wanted)
    {
        lock (_lock)
        {
            if (_armed.TryGetValue(projectDir, out ArmSettings? armed))
            {
                return armed == wanted
                    ? true
                    : throw new SessionException(
                        $"{projectDir} is already armed with quiet={Flag(armed.Quiet)}, shutOutRealGamepads={Flag(armed.ShutOutRealGamepads)} "
                            + $"and mute={Flag(armed.Mute)}; disarm_project first to arm it with other settings."
                    );
            }

            GodotSession[] onFolder = [.. _sessions.Values.Where(other => other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir))];
            CheckSessionsForArm(projectDir, onFolder, "shutOutRealGamepads", session => session.ShutOutRealGamepads, wanted.ShutOutRealGamepads);
            CheckSessionsForArm(projectDir, onFolder, "quiet", session => session.Quiet, wanted.Quiet);

            // Recorded with the check, under one lock, so a session reserved while the files are written meets the arm's settings.
            _armed[projectDir] = wanted;
            return false;
        }
    }

    /// <summary>
    /// An attach's pad, quiet and mute settings: each the request's when given, else the value of this server's arm on the
    /// folder, else false.
    /// </summary>
    private ArmSettings AttachSettings(string projectDir, AttachRequest request)
    {
        ArmSettings arm;
        lock (_lock)
        {
            arm = _armed.GetValueOrDefault(projectDir) ?? new ArmSettings(Quiet: false, ShutOutRealGamepads: false, Mute: false);
        }

        return new ArmSettings(
            Quiet: request.Quiet ?? arm.Quiet,
            ShutOutRealGamepads: request.ShutOutRealGamepads ?? arm.ShutOutRealGamepads,
            Mute: request.Mute ?? arm.Mute
        );
    }

    /// <summary>Refuses an arm whose setting differs from the live sessions' on its folder: they share one override.cfg.</summary>
    private static void CheckSessionsForArm(string projectDir, GodotSession[] onFolder, string option, Func<GodotSession, bool> setting, bool wanted)
    {
        if (onFolder.FirstOrDefault(other => setting(other) != wanted) is { } differing)
        {
            throw new SessionException(
                $"Sessions on {projectDir} run with {option}={Flag(setting(differing))}; arm it with the same value, or stop them first."
            );
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>
    /// Writes the folder's override.cfg (unless live sessions on it already have it), hides it from git and writes its
    /// armed.json, then records the arm; a failure after the override was written releases it again.
    /// </summary>
    private void ArmFolder(string projectDir, string bridgeScript, ArmSettings settings)
    {
        try
        {
            WriteOverrideUnlessHeld(projectDir, bridgeScript, settings, () => HasLiveSessionOn(projectDir, except: null));
            GitExclude.Ensure(projectDir, OverrideFile.FileName, logger);
            OverrideFolders.Hold($"arming {projectDir}", () => ArmFile.Write(projectDir, settings));
        }
        catch
        {
            lock (_lock)
            {
                _armed.Remove(projectDir);
            }

            ReleaseAfterFailedArm(projectDir);
            throw;
        }
    }

    /// <summary>Releases the override.cfg an arm wrote before it failed; a failure is logged, so the arm's own error reaches the caller.</summary>
    private void ReleaseAfterFailedArm(string projectDir)
    {
        try
        {
            OverrideFolders.Hold(
                $"releasing {projectDir}",
                () =>
                {
                    lock (_lock)
                    {
                        if (!HasLiveSessionOn(projectDir, except: null))
                        {
                            OverrideFile.Release(projectDir);
                        }
                    }
                }
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.CleanupFailed(logger, e, OverrideFile.FileName, projectDir);
        }
    }

    private ArmState DescribeArm(string projectDir, ArmSettings settings) =>
        new(projectDir, settings.Quiet, settings.ShutOutRealGamepads, settings.Mute, Dormant.List(projectDir));

    /// <summary>
    /// The dormant game an attach joins: <paramref name="pid"/> when given, which must be one of the folder's; else the only
    /// one; else none, and the attach waits for a game launched after it.
    /// </summary>
    /// <exception cref="SessionException">The given pid is not a dormant game on the folder, or several wait and none was given.</exception>
    private int? ChooseDormantGame(string projectDir, int? pid)
    {
        IReadOnlyList<DormantGame> dormant = Dormant.List(projectDir);
        if (pid is int wanted)
        {
            return dormant.Any(game => game.Pid == wanted) ? wanted : throw new SessionException(DescribeNotDormant(projectDir, wanted, dormant));
        }

        return dormant.Count switch
        {
            0 => null,
            1 => dormant[0].Pid,
            _ => throw new SessionException(
                $"{dormant.Count} dormant games wait on {projectDir}: {DescribeDormant(dormant)}. Pass options.pid to choose one."
            ),
        };
    }

    private static string DescribeNotDormant(string projectDir, int pid, IReadOnlyList<DormantGame> dormant)
    {
        string waiting =
            dormant.Count == 0 ? $"No dormant game waits on {projectDir}." : $"The dormant games on {projectDir} are: {DescribeDormant(dormant)}.";
        return $"No dormant game with pid {pid} waits on {projectDir}. {waiting} A game started before arm_project, or on a folder that is "
            + "not armed, has no bridge to join: relaunch it while the folder is armed.";
    }

    /// <summary>"pid N, started &lt;UTC ISO time&gt;" for each game, separated by "; ".</summary>
    private static string DescribeDormant(IEnumerable<DormantGame> dormant) =>
        string.Join(
            "; ",
            dormant.Select(game =>
                string.Create(CultureInfo.InvariantCulture, $"pid {game.Pid}, started {game.StartedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}")
            )
        );

    /// <summary>
    /// The pad and quiet settings a preview takes: those of a live session on the folder, else the folder's arm, else quiet with
    /// the real pads live.
    /// </summary>
    private ArmSettings FolderSettingsForPreview(string projectDir)
    {
        GodotSession? onFolder = _sessions.Values.FirstOrDefault(other => other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir));
        if (onFolder is not null)
        {
            return new ArmSettings(onFolder.Quiet, onFolder.ShutOutRealGamepads, onFolder.Mute);
        }

        return _armed.GetValueOrDefault(projectDir) ?? new ArmSettings(Quiet: true, ShutOutRealGamepads: false, Mute: false);
    }

    /// <summary>
    /// Writes the marked override.cfg with this server among its owners, unless <paramref name="held"/> says this server
    /// already holds the folder and the file is there; the folder is recorded in <see cref="OverrideFolders"/> first.
    /// </summary>
    /// <exception cref="SessionException">
    /// The project has its own override.cfg, or another live server's marked one injects another bridge or holds other settings.
    /// </exception>
    private void WriteOverrideUnlessHeld(string projectDir, string bridgeScript, ArmSettings settings, Func<bool> held) =>
        OverrideFolders.RecordWhile(
            projectDir,
            () =>
            {
                lock (_lock)
                {
                    string path = OverrideFile.PathIn(projectDir);
                    if (held() && File.Exists(path) && OverrideFile.IsOurs(path))
                    {
                        return;
                    }

                    OverrideFile.Write(projectDir, bridgeScript, settings.ShutOutRealGamepads, settings.Quiet);
                }
            }
        );

    /// <summary>
    /// A preview's prep under the folder's prep lock, as a launch's is. Passing <paramref name="limit"/> stops it, with its
    /// whole process tree, and refuses the preview with where the logs are and how to prepare the project beforehand.
    /// </summary>
    /// <exception cref="SessionException">The prep failed, or it passed the preview's limit.</exception>
    private async Task<PrepResult> PrepareForPreviewAsync(GodotSession session, LoadDeadline limit, CancellationToken caller)
    {
        string projectDir = session.ProjectDir;
        SemaphoreSlim folderLock = PrepLock(projectDir);
        try
        {
            await folderLock.WaitAsync(limit.Token);
            try
            {
                PrepContext context = new(
                    projectDir,
                    logger,
                    () => RunningSessionNames(projectDir, session),
                    () => HeadlessHosts.StopFolder(projectDir, "import")
                );
                return await ProjectPrep.RunAsync(context, limit.Token);
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
                    + $"{PreviewLimit.TotalSeconds:0} s{BackstopClauseOf(limit)}, so it was stopped with its whole process tree and the "
                    + "scene was not shown. "
                    + $"Its logs are in {ProjectPrep.LogFolder(projectDir)}. Run run_project or validate first to build and import, "
                    + "or pass prepare: never.",
                e
            );
        }
    }

    /// <summary>The backstop clause of a preview's limit when its backstop ended it; empty when its ceiling did.</summary>
    private static string BackstopClauseOf(LoadDeadline limit) => limit.Reason == DeadlineReason.Backstop ? limit.BackstopClause() : string.Empty;

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

    /// <summary>
    /// Whether the session's folder is held by something else of this server: another live session, or its arm. The caller
    /// holds the lock.
    /// </summary>
    private bool IsHeldByOther(GodotSession session) => HasLiveSessionOn(session.ProjectDir, session) || _armed.ContainsKey(session.ProjectDir);

    /// <summary>Whether a live session but <paramref name="except"/> uses the folder. The caller holds the lock.</summary>
    private bool HasLiveSessionOn(string projectDir, GodotSession? except) =>
        _sessions.Values.Any(other => !ReferenceEquals(other, except) && other.IsLive && ProjectPaths.AreSame(other.ProjectDir, projectDir));

    private IEnumerable<GodotSession> Ordered() => _sessions.Values.OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The live sessions' names, ordered and comma-separated, or "none".</summary>
    private string DescribeLive()
    {
        string[] names = [.. Ordered().Where(session => session.IsLive).Select(session => session.Name)];
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>" N stopped (…)", counting the stopped sessions and saying how to list them; empty when none is stopped.</summary>
    private string DescribeStopped()
    {
        int stopped = _sessions.Values.Count(session => !session.IsLive);
        return stopped == 0 ? string.Empty : $" {stopped} stopped (list_sessions with includeStopped: true lists them).";
    }

    private static SessionInfo Describe(GodotSession session) =>
        new(
            session.Name,
            session.ProjectDir,
            session.Kind == SessionKind.Run ? "run" : "attach",
            session.IsLive,
            session.ProcessId,
            session.GameProcessId
        )
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

    private static void AtShutdown(string projectDir, Func<bool> release)
    {
        try
        {
            release();
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

/// <summary>
/// One entry of list_sessions: <see cref="ProcessId"/> is the process the server started (the Godot_console.exe wrapper on
/// Windows; null for an attach), <see cref="GameProcessId"/> the game's own, from its bridge's hello (null until then);
/// <see cref="Recording"/> is set for a session whose latest run records.
/// </summary>
internal sealed record SessionInfo(string Name, string ProjectPath, string Kind, bool Live, int? ProcessId, int? GameProcessId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RecordingResult? Recording { get; init; }
}

/// <summary>
/// What attach_project asks for: the project, the new session's name (the folder's when null), how long to wait for the game,
/// its pad and quiet settings (the arm's when null on a folder this server armed, else false), and the dormant game to join
/// (the only one when null, if any).
/// </summary>
internal sealed record AttachRequest(string ProjectPath, string? Session, TimeSpan Wait, bool? ShutOutRealGamepads, bool? Quiet, int? Pid)
{
    /// <summary>
    /// Whether the bridge mutes the game's Master bus: the arm's when null on a folder this server armed, else false. Unlike
    /// quiet it may differ from the arm's and from the folder's other sessions: it is the game's alone.
    /// </summary>
    public bool? Mute { get; init; }
}
