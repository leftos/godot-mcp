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

/// <summary>What a session is created for: its name, its project folder, its kind, its pad setting and whether it runs quiet.</summary>
internal sealed record SessionSpec(string Name, string ProjectDir, SessionKind Kind, bool ShutOutRealGamepads, bool Quiet);

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
    /// released, after a recording's clips are cut.
    /// </summary>
    /// <exception cref="SessionException">No run was ever launched, or the session is attached.</exception>
    public async Task<StopResult> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Kind == SessionKind.Attach)
            {
                throw new SessionException(
                    $"The session for {ProjectDir} is attached to a game godot-mcp did not start: use detach_project; "
                        + "stop_project only stops games run_project started."
                );
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
    /// The last-resort cleanup for the server's own exit: kills a live game it launched and removes an attach file, without
    /// waiting on the gate or the bridge. An attached game is left running; the registry removes the override files.
    /// </summary>
    public void Shutdown()
    {
        try
        {
            if (Kind == SessionKind.Attach)
            {
                AttachFile.Remove(ProjectDir);
            }
            else if (_run is { IsRunning: true } run)
            {
                run.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
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
    /// when it quit, and the warning when a debugger was attached to it.
    /// </summary>
    private readonly record struct RunEnd(bool Killed, string? Warning, string? KillReason, IReadOnlyList<string> LeftRunning);

    /// <summary>The warning stop_project and restart_project carry when they end a running game a debugger is attached to; null otherwise.</summary>
    private string? WarnIfDebugged(GodotRun run) =>
        run.IsRunning && DebuggedProcessId is int debugged
            ? $"A debugger was attached to the game (pid {debugged}); its debug session ended with the game."
            : null;

    private BridgeConnection FindLiveConnection()
    {
        if (_attached is { } attached)
        {
            return attached.IsOpen
                ? attached
                : throw new SessionException(
                    $"The attached game on {ProjectDir} has closed its connection (it may have quit). " + "detach_project, then attach_project again."
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

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

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
    private async Task<(GodotRun Run, PrepResult Prep, string Token, string? ReplacedWarning, string Godot)> PrepareAndStartAsync(
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
            PrepContext context = new(ProjectDir, _logger, () => registry.RunningSessionNames(ProjectDir, this));
            PrepResult prep = request.Prepare ? await ProjectPrep.RunAsync(context, cancellationToken) : PrepResult.Skipped;
            string bridgeScript = Installation.FindBridgeScript();
            string? replacedWarning = null;
            if (previous is not null)
            {
                // A restart cancelled by now must not stop a healthy game that the handshake wait would then kill.
                cancellationToken.ThrowIfCancellationRequested();
                replacedWarning = (await EndRunAsync(previous)).Warning;
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
            return (run, prep, token, replacedWarning, godotPath);
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <returns>The launch, and the warning when a debugger was attached to the game <paramref name="previous"/> ran.</returns>
    private async Task<(LaunchResult Started, string? ReplacedWarning)> StartRunAsync(
        LaunchRequest request,
        GodotRun? previous,
        CancellationToken cancellationToken
    )
    {
        (GodotRun run, PrepResult prep, string token, string? replacedWarning, string godot) = await PrepareAndStartAsync(
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

        Log.RunStarted(_logger, processId, run.ProjectDir);
        LastLaunch = request;
        LaunchResult started = new(Name, run.ProjectDir, processId, request.Quiet, prep, godot)
        {
            Recording = _recording is { } recording ? new RecordingResult { Path = recording.Path } : null,
        };
        return (started, replacedWarning);
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
            return DesktopProcess.CreateSuspended(startInfo, HiddenDesktop.Name);
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
    /// Whether the run's bridge left a ping unanswered for <see cref="HangProbe.PingTimeout"/>: its main thread is stuck, so a
    /// shutdown command would go unread too. A run without a connection, or whose connection ends meanwhile, is not silent:
    /// it gets the usual shutdown and grace.
    /// </summary>
    private async Task<bool> IsSilentAsync(GodotRun run)
    {
        if (run.Connection is not { IsOpen: true } connection)
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
            Log.StopFoundGameStuck(_logger, run.ProjectDir, HangProbe.PingTimeout.TotalSeconds);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.StopPingFailed(_logger, e, run.ProjectDir);
            return false;
        }
    }

    private async Task<QuitRequest> AskToQuitAsync(GodotRun run)
    {
        if (run.Connection is not { IsOpen: true } connection)
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
    private async Task<RunEnd> StopRunningAsync(GodotRun run)
    {
        if (await IsSilentAsync(run))
        {
            await KillAsync(run);
            return new RunEnd(Killed: true, Warning: null, GameKillReason.Silent, LeftRunning: []);
        }

        QuitRequest request = await AskToQuitAsync(run);
        TimeSpan exitGrace = CurrentExitGrace;
        if (!await ProcessExit.WaitUntilGoneAsync(run.Game ?? run.Process, exitGrace))
        {
            Log.ExitGraceExpired(_logger, run.ProjectDir, exitGrace.TotalSeconds);
            await KillAsync(run);
            return new RunEnd(Killed: true, Warning: null, GameKillReason.AfterGrace(request, exitGrace), LeftRunning: []);
        }

        return new RunEnd(Killed: false, Warning: null, KillReason: null, await EndWrapperAsync(run));
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
        await TerminateAsync(run);
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
        string? warning = WarnIfDebugged(run);
        RunEnd ended = run.IsRunning ? await StopRunningAsync(run) : new RunEnd(Killed: false, Warning: null, KillReason: null, LeftRunning: []);
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
        await TerminateAsync(run);
    }

    /// <summary>Kills the run's process and everything it started, and waits up to <see cref="KillWait"/> for it to exit.</summary>
    private async Task TerminateAsync(GodotRun run)
    {
        try
        {
            run.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.KillFailed(_logger, e, run.ProjectDir);
        }

        if (!await ProcessExit.WaitUntilGoneAsync(run.Process, KillWait))
        {
            Log.StillRunningAfterKill(_logger, run.ProjectDir, KillWait.TotalSeconds);
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
