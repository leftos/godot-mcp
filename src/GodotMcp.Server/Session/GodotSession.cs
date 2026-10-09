using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>Whether a session is a Godot run the server launched or a game attach_project reached.</summary>
internal enum SessionKind
{
    Run,
    Attach,
}

/// <summary>
/// What a session is created for: its name, its project folder, its kind, its pad setting, whether it runs quiet and whether
/// the bridge mutes the game.
/// </summary>
internal sealed record SessionSpec(string Name, string ProjectDir, SessionKind Kind, bool ShutOutRealGamepads, bool Quiet)
{
    /// <summary>Whether the session asked the bridge to mute the game's Master bus; a quiet game is silent either way.</summary>
    public bool Mute { get; init; }

    /// <summary>The dormant game an attach joins, by its process id; null for an attach that waits for a launch, and for a run.</summary>
    public int? JoinPid { get; init; }
}

/// <summary>
/// One named session in the <see cref="SessionRegistry"/>: a Godot run launched with the bridge injected, or a game
/// attach_project reached. It talks to its bridge, stops its run, and releases the injected override.cfg to the registry
/// on every way out (stop, a failed launch, the game exiting), which removes it once no other live session uses the folder.
/// </summary>
internal sealed partial class GodotSession(SessionSpec spec, SessionRegistry registry) : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What a runtime tool says when no session answers.</summary>
    public const string NoneRunning = "No Godot session is running; start one with run_project or attach_project.";

    private static readonly TimeSpan ShutdownReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a stop waits for the console wrapper to follow a game that quit before ending it.</summary>
    private static readonly TimeSpan WrapperExitWait = TimeSpan.FromSeconds(1);

    // SilentUnderDebugger's text quotes this.
    private static readonly TimeSpan DebuggerPingTimeout = TimeSpan.FromMilliseconds(500);
    private const int FailureStderrLines = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = registry.Logger;
    private volatile bool _pending = true;
    private GodotRun? _run;

    public string Name { get; } = spec.Name;

    public string ProjectDir { get; } = spec.ProjectDir;

    public SessionKind Kind { get; } = spec.Kind;

    public bool ShutOutRealGamepads { get; } = spec.ShutOutRealGamepads;

    /// <summary>Whether the session is quiet: a run started quiet, or an attach whose game parks its window.</summary>
    public bool Quiet { get; } = spec.Quiet;

    /// <summary>Whether the session asked the bridge to mute the game's Master bus, apart from quiet.</summary>
    public bool Mute { get; } = spec.Mute;

    /// <summary>
    /// The process the server started, once it has; null for an attached game. On Windows that is the Godot_console.exe
    /// wrapper, not the game: <see cref="GameProcessId"/> is the game's.
    /// </summary>
    public int? ProcessId { get; private set; }

    /// <summary>The game's own process id, as its bridge's hello reported it; null before the handshake or from an older bridge.</summary>
    public int? GameProcessId { get; private set; }

    /// <summary>What the run was launched with, once it has launched; null for an attached game.</summary>
    internal LaunchRequest? LastLaunch { get; private set; }

    /// <summary>Where the session logs.</summary>
    public ILogger Logger => _logger;

    /// <summary>The exit code of the process the server started, once it has exited; null while it runs and for an attached game.</summary>
    public int? RunExitCode => _run?.ExitCode;

    /// <summary>Plays one input call at a time on this session; calls to other sessions play alongside.</summary>
    public SemaphoreSlim InputGate { get; } = new(1, 1);

    /// <summary>The errors and warnings the game's bridge has reported, from the run or the attached game.</summary>
    public ErrorFeed Errors { get; } = new();

    /// <summary>The snapshots snapshot_subtree took of the current game; emptied when the run stops, exits or restarts.</summary>
    public SnapshotStore Snapshots { get; } = new();

    /// <summary>Whether the session is starting, its run is running, or its attached game is still connected.</summary>
    public bool IsLive => _pending || HasGame;

    /// <summary>Whether the session's game is running: its run has started and not exited, or its attached game is still connected.</summary>
    public bool HasGame => Kind == SessionKind.Run ? _run is { IsRunning: true } : _attached is { IsOpen: true };

    /// <summary>Whether this is an attach still waiting for its game to dial in.</summary>
    public bool IsWaitingForGame => _pending && Kind == SessionKind.Attach;

    /// <summary>Starts the run; a launch that fails before Godot starts leaves the registry without this session.</summary>
    /// <exception cref="SessionException">
    /// Godot or the bridge is missing, the project has its own override.cfg, or the bridge never connected.
    /// </exception>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            GodotCommandLine.RefuseUnrecordable(request);
            _ = GodotCommandLine.RequestedWindowSize(request.EngineArgs);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return (await StartRunAsync(request, previous: null, cancellationToken)).Started;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            AbandonStart(forget: _run is null);
            throw;
        }
        finally
        {
            _pending = false;
        }
    }

    /// <summary>
    /// Pings the game first: one silent for 2 s is stuck and is killed at once, without the shutdown command. One that answers
    /// is asked to quit and killed if it has not within 3 s, or 30 s while it records. Either way the override file is
    /// released, after a recording's clips are cut. An attached game is ended the same way (see <see cref="StopAttachedAsync"/>),
    /// and its session is dropped.
    /// </summary>
    /// <exception cref="SessionException">No run was ever launched, or the attached session has no game.</exception>
    public async Task<StopResult> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Kind == SessionKind.Attach)
            {
                return await StopAttachedAsync();
            }

            GodotRun run =
                _run ?? throw new SessionException("No Godot session has been started, so there is nothing to stop. Start one with run_project.");
            bool alreadyExited = !run.IsRunning;
            RunEnd ended = await EndRunAsync(run);
            int? gameExitCode = await run.ReleaseGameAsync();
            Snapshots.Clear();
            registry.Captures.End(Name, CaptureStore.EndedByStop);
            RecordingResult? recording = await FinishRecordingAsync();
            bool removed = registry.ReleaseFolder(this);
            return new StopResult(Name, run.ProjectDir, run.ExitCode, ended.Killed, removed)
            {
                AlreadyExited = alreadyExited,
                GameExitCode = gameExitCode,
                KillReason = ended.KillReason,
                LeftRunning = ended.LeftRunning.Count == 0 ? null : ended.LeftRunning,
                QuitMs = ended.QuitMs,
                Recording = recording,
                Warning = ended.Warning,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends a command to the live run's or attached game's bridge. When a debugger is attached to the game, a ping with a
    /// <see cref="DebuggerPingTimeout"/> goes first, so a game the debugger holds paused fails the call at once instead of at its timeout.
    /// </summary>
    /// <remarks>
    /// <paramref name="timeout"/> and <paramref name="release"/> are load-adjusted, as <see cref="BridgeConnection.SendAsync"/>
    /// takes them.
    /// </remarks>
    /// <exception cref="SessionException">
    /// The run is not live, the attached game's connection has ended, or the game is paused under a debugger.
    /// </exception>
    public async Task<JsonNode?> SendAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? release = null
    )
    {
        BridgeConnection connection = FindLiveConnection();
        if (DebuggedProcessId is int debugged)
        {
            await RefuseIfPausedAsync(connection, debugged, cancellationToken);
        }

        return await connection.SendAsync(command, parameters, timeout, cancellationToken, release);
    }

    /// <summary>
    /// Pings the game's bridge, in wall time, without the debugger check <see cref="SendAsync"/> makes first: the hang probe's
    /// own ping.
    /// </summary>
    /// <exception cref="SessionException">The run is not live, or the attached game's connection has ended.</exception>
    public Task<JsonNode?> PingAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        FindLiveConnection().SendRawAsync("ping", null, timeout, cancellationToken);

    /// <summary>The game's own process id when a debugger is attached to it; null when none is, or while that id is unknown.</summary>
    internal int? DebuggedProcessId => GameProcessId is int game && registry.IsDebuggerAttached(game) ? game : null;

    /// <summary>What the hang probe reports for a game that left its 2 s ping unanswered while a debugger is attached.</summary>
    internal static string PausedUnderDebugger(int processId) =>
        $"The game (pid {processId}) is paused under a debugger: continue it in the debugger before driving the game.";

    /// <summary>What a call fails with when the game leaves the short ping unanswered while a debugger is attached.</summary>
    internal static string SilentUnderDebugger(int processId) =>
        $"The game (pid {processId}) did not answer within 0.5 s while a debugger is attached: it is most likely paused at a breakpoint. "
        + "Continue it in the debugger, or retry if it was only busy.";

    /// <summary>The run's newest stderr lines, oldest first; null for an attached game, which has no captured output.</summary>
    public IReadOnlyList<string>? LastStderrLines(int count)
    {
        if (Kind == SessionKind.Attach)
        {
            return null;
        }

        return _run is { } run ? run.Stderr.Tail(count) : [];
    }

    /// <exception cref="SessionException">The session is attached, so there is no captured output.</exception>
    public DebugOutput GetDebugOutput(int limit, long? before)
    {
        if (Kind == SessionKind.Attach)
        {
            throw new SessionException(
                $"The session for {ProjectDir} is attached: attached sessions have no captured output; the game's own console or log has it."
            );
        }

        GodotRun? run = _run;
        if (run is null)
        {
            return new DebugOutput { Session = Name, ProjectPath = ProjectDir };
        }

        OutputPage stdout = run.Stdout.Page(limit, before);
        OutputPage stderr = run.Stderr.Page(limit, before);
        return new DebugOutput
        {
            Session = Name,
            ProjectPath = run.ProjectDir,
            Running = run.IsRunning,
            ExitCode = run.ExitCode,
            Stdout = stdout.Lines,
            Stderr = stderr.Lines,
            StdoutFirstLine = stdout.FirstLine,
            StderrFirstLine = stderr.FirstLine,
            StdoutTotalLines = run.Stdout.TotalLines,
            StderrTotalLines = run.Stderr.TotalLines,
        };
    }

    /// <summary>
    /// The server's own exit: a live game it launched is stopped as stop_project stops it (a ping, the shutdown command, then
    /// the grace, and a kill for a game silent or still running after it), without waiting on the gate. An attached game is
    /// left running and only its attach file removed. The registry bounds the wait, kills a game still running at its cap
    /// (<see cref="KillAtShutdownAsync"/>) and removes the override files.
    /// </summary>
    /// <returns>
    /// Whether the session was handled; false while a launch or restart is still in flight (the registry waits for those to
    /// settle first, up to its cap), which a later shutdown pass takes again once it has its run.
    /// </returns>
    public async Task<bool> ShutdownAsync()
    {
        if (Kind == SessionKind.Attach)
        {
            AtShutdown(RemoveHandoffFile);
            return true;
        }

        if (_pending || _run is not { } run)
        {
            return false;
        }

        if (run.IsRunning)
        {
            await StopRunningAsync(run);
        }

        return true;
    }

    /// <summary>
    /// Kills a game this session launched that is still running when the server's exit stops waiting for it to quit, with
    /// everything it started, and waits up to <see cref="KillWait"/> for them to exit: the run's process and the game's own,
    /// each that still runs (on Windows the game can outlive the console wrapper that is the run's process).
    /// </summary>
    public async Task KillAtShutdownAsync()
    {
        if (Kind != SessionKind.Run || _run is not { } run)
        {
            return;
        }

        Process[] running = [.. new[] { run.Process, run.Game }.OfType<Process>().Where(IsRunningAtShutdown)];
        foreach (Process process in running)
        {
            AtShutdown(() => process.Kill(entireProcessTree: true));
        }

        bool[] gone = await Task.WhenAll(running.Select(GoneAfterKillAsync));
        if (gone.Contains(false))
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            await Console.Error.WriteLineAsync(
                $"godot-mcp: the game of {ProjectDir} was still running {KillWait.TotalSeconds:0} s after its kill at shutdown."
            );
        }
    }

    /// <summary>Whether a process of the run still runs; one never started or already let go of does not.</summary>
    private static bool IsRunningAtShutdown(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Never started, or disposed as the run let go of it (ObjectDisposedException is one).
            return false;
        }
    }

    /// <summary>Waits up to <see cref="KillWait"/> for a killed process to exit; one the run let go of meanwhile counts as gone.</summary>
    private static async Task<bool> GoneAfterKillAsync(Process process)
    {
        try
        {
            return await ProcessExit.WaitUntilGoneAsync(process, KillWait);
        }
        catch (InvalidOperationException)
        {
            // Disposed while the wait ran (ObjectDisposedException is one): the run has let go of it, which it does once it exits.
            return true;
        }
    }

    /// <summary>Runs a step of the server's exit, reporting a failure to stderr and going on.</summary>
    private void AtShutdown(Action cleanup)
    {
        try
        {
            cleanup();
        }
        // Process.Kill with its tree throws AggregateException when part of the tree cannot be killed.
        catch (Exception e)
            when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or AggregateException)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine($"godot-mcp: cleanup of {ProjectDir} at shutdown failed: {e.Message}");
        }
    }

    /// <summary>Lets go of a session that is no longer live, before another takes its name: its connection and process.</summary>
    public async Task RetireAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_attached is not null)
            {
                await _attached.DisposeAsync();
                _attached = null;
            }

            DisposeAttachedGame();
            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        InputGate.Dispose();
    }

    /// <summary>Fails the call when the game leaves a ping unanswered for <see cref="DebuggerPingTimeout"/> while a debugger is attached.</summary>
    /// <exception cref="SessionException">The ping went unanswered: the debugger holds the game paused.</exception>
    private static async Task RefuseIfPausedAsync(BridgeConnection connection, int processId, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendRawAsync("ping", null, DebuggerPingTimeout, cancellationToken);
        }
        catch (TimeoutException e)
        {
            throw new SessionException(SilentUnderDebugger(processId), e);
        }
    }

    /// <summary>
    /// How <see cref="EndRunAsync"/> ended a run: whether the game had to be killed and why, the processes it left running
    /// when it quit, how long a quit took, and the warning when a debugger was attached to it.
    /// </summary>
    private readonly record struct RunEnd(bool Killed, string? Warning, string? KillReason, IReadOnlyList<string> LeftRunning, int? QuitMs);

    /// <summary>
    /// The warning stop_project and restart_project carry when they end a game still <paramref name="running"/> that a debugger
    /// is attached to; null otherwise.
    /// </summary>
    private string? WarnIfDebugged(bool running) =>
        running && DebuggedProcessId is int debugged
            ? $"A debugger was attached to the game (pid {debugged}); its debug session ended with the game."
            : null;

    private BridgeConnection FindLiveConnection()
    {
        if (_attached is { } attached)
        {
            return attached.IsOpen
                ? attached
                : throw new SessionException(
                    $"The attached game on {ProjectDir} has closed its connection (it may have quit). stop_project or detach_project ends "
                        + "the session; then attach_project again, which joins the game if it is still running on an armed folder."
                );
        }

        return _run is { IsRunning: true, Connection: { IsOpen: true } live } ? live : throw new SessionException(NoneRunning);
    }

    /// <summary>Releases the override file after a failed start and, when nothing started, drops the session from the registry.</summary>
    /// <returns>Whether the override file was removed; a failed removal is logged, so the start's own error reaches the caller.</returns>
    private bool AbandonStart(bool forget)
    {
        if (forget)
        {
            registry.Forget(this);
        }

        try
        {
            return registry.ReleaseFolder(this);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.CleanupFailed(_logger, e, OverrideFile.FileName, ProjectDir);
            return false;
        }
    }

    internal static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Adds a captured frame from this session's bridge to its capture in the registry's store.</summary>
    private void ReceiveCaptured(JsonObject frame) => registry.Captures.Receive(Name, frame);

    /// <summary>
    /// Under the folder's prep lock, which every launch takes, so no game starts on the folder while a prep builds or
    /// imports there: looks Godot up, builds a stale C# assembly and runs a due import (unless the request says never),
    /// stops the run a restart replaces, writes the override and starts Godot. The prep runs before anything is stopped or
    /// written, so a failed prep leaves nothing to undo and a restart's old game running.
    /// </summary>
    /// <param name="request">What to launch.</param>
    /// <param name="previous">The run a restart replaces, stopped after the prep and continued by the new run's output; null for a launch.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock and the prep.</param>
    /// <exception cref="SessionException">
    /// Godot was not found, the prep failed, the project has its own override.cfg, or Godot could not start.
    /// </exception>
    private async Task<(GodotRun Run, PrepResult Prep, string Token, RunEnd? Previous, string Godot)> PrepareAndStartAsync(
        LaunchRequest request,
        GodotRun? previous,
        CancellationToken cancellationToken
    )
    {
        SemaphoreSlim folderLock = registry.PrepLock(ProjectDir);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            string godotPath = Installation.FindGodot();
            PrepContext context = new(
                ProjectDir,
                _logger,
                () => registry.RunningSessionNames(ProjectDir, this),
                () => registry.HeadlessHosts.StopFolder(ProjectDir, "import")
            );
            PrepResult prep = request.Prepare ? await ProjectPrep.RunAsync(context, cancellationToken) : PrepResult.Skipped;
            string bridgeScript = Installation.FindBridgeScript();
            // A shutdown that began during the prep refuses the start before a restart's old game is stopped.
            registry.RefuseStartAtShutdown();
            RunEnd? previousEnd = null;
            if (previous is not null)
            {
                // A restart cancelled by now must not stop a healthy game that the handshake wait would then kill.
                cancellationToken.ThrowIfCancellationRequested();
                previousEnd = await EndRunAsync(previous);
                Snapshots.Clear();
                registry.Captures.End(Name, CaptureStore.EndedByRestart);
                StopOutputCapture(previous);
                await FinishRecordingAsync();
            }

            registry.WriteOverride(this, bridgeScript);
            GitExclude.Ensure(ProjectDir, OverrideFile.FileName, _logger);
            string token = CreateToken();
            string? moviePath = PrepareMoviePath(request);
            BridgeEndpoint bridge = new(registry.Listener.Port, token);
            ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(godotPath, request, bridge, moviePath);
            GodotRun run = new(ProjectDir, CreateRunProcess(startInfo, request.Quiet), previous);
            StartProcess(run, previous is null ? null : ProcessId);
            _run = run;
            _recording = moviePath is null ? null : new Recording(moviePath) { DropIdle = request.DropIdle };
            ProcessId = run.Process.Id;
            return (run, prep, token, previousEnd, godotPath);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <returns>The launch, and how the run it replaced ended; that end is null for a launch.</returns>
    private async Task<(LaunchResult Started, RunEnd? Previous)> StartRunAsync(
        LaunchRequest request,
        GodotRun? previous,
        CancellationToken cancellationToken
    )
    {
        registry.RefuseStartAtShutdown();
        (GodotRun run, PrepResult prep, string token, RunEnd? previousEnd, string godot) = await PrepareAndStartAsync(
            request,
            previous,
            cancellationToken
        );
        int processId = run.Process.Id;
        BridgeConnection connection = await WaitForHandshakeAsync(run, new HandshakeExpectation(token, ProjectDir), cancellationToken);
        connection.OnErrors(Errors.Receive);
        connection.OnCaptured(ReceiveCaptured);
        run.Connection = connection;
        GameProcessId = connection.GameProcessId;
        if (GameProcessId is int gameProcessId)
        {
            run.KeepGameHandle(gameProcessId, _logger);
        }

        await WelcomeAsync(connection);

        Log.RunStarted(_logger, processId, run.ProjectDir);
        LastLaunch = request;
        LaunchResult started = new(Name, run.ProjectDir, processId, request.Quiet, prep, godot)
        {
            Recording = _recording is { } recording ? new RecordingResult { Path = recording.Path } : null,
            Window = connection.Window,
            Warning = GodotCommandLine.DescribeWindowMismatch(GodotCommandLine.RequestedWindowSize(request.EngineArgs), connection.Window),
        };
        return (started, previousEnd);
    }

    /// <summary>
    /// The run's process, not yet running. A quiet run on Windows is created suspended on the server's hidden desktop, so
    /// none of its windows ever shows on the user's; every other run starts through <see cref="Process.Start()"/>.
    /// </summary>
    /// <exception cref="SessionException">The hidden desktop or the suspended process could not be created.</exception>
    private static IRunProcess CreateRunProcess(ProcessStartInfo startInfo, bool quiet)
    {
        if (GodotCommandLine.UsesHiddenDesktop(quiet))
        {
            return DesktopProcess.CreateSuspended(startInfo, HiddenDesktop.Path);
        }

        return new StartInfoProcess(new Process { StartInfo = startInfo, EnableRaisingEvents = true });
    }

    /// <summary>
    /// Starts the run's process and its output capture. On a restart (<paramref name="previousProcessId"/> set) a marker line
    /// goes into both carried-over streams once the new process has its id and before any of its output is read.
    /// </summary>
    private void StartProcess(GodotRun run, int? previousProcessId)
    {
        run.Process.Exited += (_, _) => _ = OnRunExitedAsync(run);
        run.Launcher.Start();
        if (previousProcessId is { } previous)
        {
            string marker = $"[godot-mcp] restarted: the previous game (pid {previous}) was stopped; output below is from pid {run.Process.Id}.";
            run.Stdout.Add(marker);
            run.Stderr.Add(marker);
        }

        run.Launcher.BeginRead(run.Stdout.Add, run.Stderr.Add);
    }

    private async Task<BridgeConnection> WaitForHandshakeAsync(GodotRun run, HandshakeExpectation expected, CancellationToken cancellationToken)
    {
        using LoadDeadline deadline = registry.LaunchClock.Start(HandshakeTimeout, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<BridgeConnection> accept = registry.Listener.AcceptBridgeAsync(expected, timeout.Token);
        Task exited = run.Process.WaitForExitAsync(timeout.Token);
        await Task.WhenAny(accept, exited);
        if (accept.IsCompletedSuccessfully)
        {
            return await accept;
        }

        await timeout.CancelAsync();
        await ObserveAbandonedAsync(accept, exited);
        bool exitedEarly = !run.IsRunning;
        if (!exitedEarly)
        {
            await KillAsync(run);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw DescribeFailedLaunch(run, exitedEarly, deadline);
    }

    /// <summary>
    /// Tells a launched game's bridge its hello was accepted, so it quits once this connection is lost; a bridge whose hello
    /// is refused (a child game that inherited the run's token) is never welcomed and runs on. An unanswered welcome is logged.
    /// </summary>
    private async Task WelcomeAsync(BridgeConnection connection)
    {
        try
        {
            await connection.SendRawAsync("welcome", null, ShutdownReplyTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The frame was written, so the bridge marks the connection once it reads it.
            Log.WelcomeTimedOut(_logger, ProjectDir, ShutdownReplyTimeout.TotalSeconds);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.WelcomeUnanswered(_logger, e, ProjectDir);
        }
    }

    private async Task ObserveAbandonedAsync(Task<BridgeConnection> accept, Task exited)
    {
        try
        {
            await Task.WhenAll(accept, exited);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Log.HandshakeAbandoned(_logger, e);
        }

        if (accept.IsCompletedSuccessfully)
        {
            await accept.Result.DisposeAsync();
        }
    }

    private static SessionException DescribeFailedLaunch(GodotRun run, bool exitedEarly, LoadDeadline deadline)
    {
        string backstop = deadline.Reason == DeadlineReason.Backstop ? deadline.BackstopClause() : string.Empty;
        string what = exitedEarly
            ? $"Godot exited with code {run.ExitCode} before the bridge connected"
            : $"the bridge did not connect within {HandshakeTimeout.TotalSeconds:0} s{backstop}, so Godot was stopped";
        string stderr = string.Join('\n', run.Stderr.Tail(FailureStderrLines));
        return new SessionException(
            $"Launching {run.ProjectDir} failed: {what}. Check that the project runs on its own "
                + $"(godot --path <project>) and that nothing blocks connections to 127.0.0.1. Last stderr lines:\n{stderr}"
        );
    }

    /// <summary>
    /// Whether the game's bridge left a ping unanswered for <see cref="HangProbe.PingTimeout"/>: its main thread is stuck, so a
    /// shutdown command would go unread too. A game without a connection, or whose connection ends meanwhile, is not silent:
    /// it gets the usual shutdown and grace.
    /// </summary>
    private async Task<bool> IsSilentAsync(BridgeConnection? bridge)
    {
        if (bridge is not { IsOpen: true } connection)
        {
            return false;
        }

        try
        {
            await connection.SendRawAsync("ping", null, HangProbe.PingTimeout, CancellationToken.None);
            return false;
        }
        catch (TimeoutException)
        {
            Log.StopFoundGameStuck(_logger, ProjectDir, HangProbe.PingTimeout.TotalSeconds);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.StopPingFailed(_logger, e, ProjectDir);
            return false;
        }
    }

    private async Task<QuitRequest> AskToQuitAsync(BridgeConnection? bridge)
    {
        if (bridge is not { IsOpen: true } connection)
        {
            return QuitRequest.NotSent;
        }

        try
        {
            await connection.SendRawAsync("shutdown", null, ShutdownReplyTimeout, CancellationToken.None);
            return QuitRequest.Acknowledged;
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
            Log.ShutdownNotAcknowledged(_logger, e);
            return QuitRequest.Unanswered;
        }
    }

    /// <summary>
    /// Stops a running game: a silent one is killed at once; one that answers is asked to quit, and killed if it has not
    /// exited within the grace. The grace watches the game's own process when the run has a handle on it, since on Windows
    /// the console wrapper also waits for every process the game started; a game that quit is then followed by
    /// <see cref="EndWrapperAsync"/>.
    /// </summary>
    private Task<RunEnd> StopRunningAsync(GodotRun run) =>
        StopGameAsync(run.Connection, run.Game ?? run.Process, () => KillAsync(run), () => EndWrapperAsync(run));

    /// <summary>
    /// Stops a game through its bridge <paramref name="connection"/>: a silent one is ended with <paramref name="kill"/> at once;
    /// one that answers is asked to quit, and killed if <paramref name="watched"/> has not exited within the grace, counted in
    /// load-adjusted time on the registry's clock with its backstop, and the kill says what the game was doing. After a quit,
    /// <paramref name="afterQuit"/> ends what the game left behind and returns it, as leftRunning lists it.
    /// </summary>
    private async Task<RunEnd> StopGameAsync(
        BridgeConnection? connection,
        Process watched,
        Func<Task> kill,
        Func<Task<IReadOnlyList<string>>> afterQuit
    )
    {
        if (await IsSilentAsync(connection))
        {
            await kill();
            return new RunEnd(Killed: true, Warning: null, GameKillReason.Silent, LeftRunning: [], QuitMs: null);
        }

        Task<long>? closedAt = connection is null ? null : ClosedAtAsync(connection.Closed);
        long askedAt = Stopwatch.GetTimestamp();
        QuitRequest request = await AskToQuitAsync(connection);
        long answeredAt = request == QuitRequest.Acknowledged ? Stopwatch.GetTimestamp() : askedAt;
        using LoadDeadline grace = registry.Clock.Start(CurrentExitGrace);
        if (!await ProcessExit.WaitUntilGoneAsync(watched, grace))
        {
            var spent = GraceSpent.Of(grace);
            string reason = await DescribeGraceKillAsync(request, spent, watched, () => ClosedSince(closedAt, answeredAt));
            // The state is sampled for a second, and a game that exits meanwhile quit on its own: it is not killed.
            if (!watched.WaitForExit(0))
            {
                Log.ExitGraceExpired(_logger, ProjectDir, spent.Text);
                await kill();
                return new RunEnd(Killed: true, Warning: null, reason, LeftRunning: [], QuitMs: null);
            }
        }

        int quitMs = (int)Math.Round(Stopwatch.GetElapsedTime(askedAt).TotalMilliseconds);
        return new RunEnd(Killed: false, Warning: null, KillReason: null, await afterQuit(), quitMs);
    }

    /// <summary>When the bridge's connection closed, as a <see cref="Stopwatch"/> timestamp.</summary>
    private static async Task<long> ClosedAtAsync(Task closed)
    {
        await closed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>How long after <paramref name="since"/> the connection closed; null while it is still open.</summary>
    private static TimeSpan? ClosedSince(Task<long>? closedAt, long since)
    {
        if (closedAt is not { IsCompletedSuccessfully: true })
        {
            return null;
        }

        TimeSpan after = Stopwatch.GetElapsedTime(since, closedAt.Result);
        return after < TimeSpan.Zero ? TimeSpan.Zero : after;
    }

    /// <summary>
    /// The killReason for a game still running when the grace ended, read before the kill: for a game asked to quit, the
    /// game's process state and last stderr lines, then whether the bridge's connection was still open.
    /// </summary>
    private async Task<string> DescribeGraceKillAsync(QuitRequest request, GraceSpent grace, Process watched, Func<TimeSpan?> closedAfter)
    {
        if (request == QuitRequest.NotSent)
        {
            return GameKillReason.NotSent(grace);
        }

        string state = await registry.DescribeGameProcess(watched.Id);
        IReadOnlyList<string>? stderr = LastStderrLines(HangProbe.StderrLineCount);
        return GameKillReason.AfterGrace(new GraceKill(request, grace, closedAfter(), state, stderr));
    }

    /// <summary>
    /// After the game quit: waits up to <see cref="WrapperExitWait"/> for the console wrapper to follow it, as it does unless
    /// the game left processes running. A wrapper still running then is ended, which closes its job and ends those processes.
    /// </summary>
    /// <returns>The processes the game left running, as leftRunning lists them; empty when the wrapper exited by itself.</returns>
    private async Task<IReadOnlyList<string>> EndWrapperAsync(GodotRun run)
    {
        if (run.Game is not { } game || !run.IsRunning || await ProcessExit.WaitUntilGoneAsync(run.Process, WrapperExitWait))
        {
            return [];
        }

        IReadOnlyList<string> leftRunning = LeftBehind.Find(game, _logger);
        Log.GameLeftProcessesRunning(_logger, run.ProjectDir, leftRunning.Count == 0 ? "no process it could list" : string.Join(", ", leftRunning));
        await TerminateAsync(run.Process);
        return leftRunning;
    }

    /// <summary>
    /// Ends a run the way stop_project does, without releasing the folder: a silent game is killed at once, one that answers
    /// is asked to quit and killed after the grace; then its connection is closed.
    /// </summary>
    /// <returns>
    /// Whether the game had to be killed and why, the processes it left running when it quit, and the warning when a debugger
    /// was attached to it as it was ended.
    /// </returns>
    private async Task<RunEnd> EndRunAsync(GodotRun run)
    {
        string? warning = WarnIfDebugged(run.IsRunning);
        RunEnd ended = run.IsRunning
            ? await StopRunningAsync(run)
            : new RunEnd(Killed: false, Warning: null, KillReason: null, LeftRunning: [], QuitMs: null);
        if (run.Connection is not null)
        {
            await run.Connection.DisposeAsync();
            run.Connection = null;
        }

        return ended with
        {
            Warning = warning,
        };
    }

    /// <summary>
    /// Stops reading a replaced run's output, so a game the kill could not end, or output still in flight, never lands in the
    /// buffers the new run continues. A stream that is not being read is logged and skipped.
    /// </summary>
    private void StopOutputCapture(GodotRun run)
    {
        try
        {
            run.Launcher.CancelRead();
        }
        catch (InvalidOperationException e)
        {
            Log.OutputCaptureStopFailed(_logger, e, run.ProjectDir);
        }
    }

    private async Task KillAsync(GodotRun run)
    {
        if (ReferenceEquals(run, _run) && _recording is { } recording)
        {
            recording.Killed = true;
        }

        run.MarkKilled();
        await TerminateAsync(run.Process);
    }

    /// <summary>
    /// Kills <paramref name="process"/> (a run's process, or an attached game's own) and everything it started, and waits up
    /// to <see cref="KillWait"/> for it to exit.
    /// </summary>
    private async Task TerminateAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.KillFailed(_logger, e, ProjectDir);
        }

        if (!await ProcessExit.WaitUntilGoneAsync(process, KillWait))
        {
            Log.StillRunningAfterKill(_logger, ProjectDir, KillWait.TotalSeconds);
        }
    }

    /// <summary>
    /// Releases the folder once the session's current run has exited. The exit of a run a restart has replaced only logs:
    /// the folder and its override belong to the newer run. Internal so the unit tests can raise an exit.
    /// </summary>
    internal async Task OnRunExitedAsync(GodotRun run)
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // The server is shutting down, and the registry's shutdown has already removed the override file.
            return;
        }

        try
        {
            if (!ReferenceEquals(run, _run))
            {
                // With no run of its own the session never owned this one: a quiet start that failed ended it.
                if (_run is not null)
                {
                    Log.ReplacedRunExited(_logger, run.ProjectDir);
                }

                return;
            }

            Snapshots.Clear();
            registry.Captures.End(Name, CaptureStore.EndedByExit);
            await FinishRecordingAsync();
            if (registry.ReleaseFolder(this))
            {
                Log.OverrideRemovedAfterExit(_logger, run.ProjectDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.OverrideRemovalFailed(_logger, e, run.ProjectDir);
        }
        finally
        {
            _gate.Release();
        }
    }
}
