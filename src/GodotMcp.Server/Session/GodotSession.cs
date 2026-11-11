using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Whether a session is a Godot run the server launched, a game attach_project reached, a program run_process started, or a
/// Godot editor run_editor opened with the bridge inside it.
/// </summary>
internal enum SessionKind
{
    Run,
    Attach,
    Process,
    Editor,
}

/// <summary>
/// What a session is created for: its name, its project folder, its kind, its pad setting, whether it runs quiet, whether the
/// bridge mutes the game, and the project settings its folder's override.cfg carries.
/// </summary>
internal sealed record SessionSpec(string Name, string ProjectDir, SessionKind Kind, bool ShutOutRealGamepads, bool Quiet)
{
    /// <summary>Whether the session asked the bridge to mute the game's Master bus; a quiet game is silent either way.</summary>
    public bool Mute { get; init; }

    /// <summary>
    /// The run's project settings block, which the folder's override.cfg carries after the bridge's lines; empty when it sets
    /// none (<see cref="ProjectSettingsText.Block"/>). Sessions sharing the folder agree on it, as they do on the pad and quiet
    /// settings; an attach, an arm, a preview taken without one and a scratch run carry none.
    /// </summary>
    public string ProjectSettings { get; init; } = "";

    /// <summary>The dormant game an attach joins, by its process id; null for an attach that waits for a launch, and for a run.</summary>
    public int? JoinPid { get; init; }

    /// <summary>
    /// Whether the session runs one run_scratches scene: it always runs quiet with the real pads live and unmuted, and neither
    /// binds nor is bound by the folder's other sessions' and arm's quiet and pad settings.
    /// </summary>
    public bool Scratch { get; init; }
}

/// <summary>
/// One named session in the <see cref="SessionRegistry"/>: a Godot run launched with the bridge injected, a game
/// attach_project reached, or a program run_process started beside the game. It talks to its bridge, stops its run, and
/// releases the injected override.cfg to the registry on every way out (stop, a failed launch, the game exiting), which
/// removes it once no other live session uses the folder. A process session has no bridge: its output fills two ring
/// buffers and its tree lives in a kill-on-close job (see ProcessSession.cs).
/// </summary>
internal sealed partial class GodotSession(SessionSpec spec, SessionRegistry registry) : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// An editor session's handshake timeout, load-adjusted: its host adds the bridge only once the editor's first scan is done,
    /// which takes seconds on a project opened before and longer after an engine upgrade.
    /// </summary>
    public static readonly TimeSpan EditorHandshakeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The grace an editor's close request gets, load-adjusted: the editor's own exit path saves its layout and unloads its plugins.
    /// </summary>
    internal static readonly TimeSpan EditorExitGrace = TimeSpan.FromSeconds(10);

    /// <summary>What a runtime tool says when no session answers.</summary>
    public const string NoneRunning = "No Godot session is running; start one with run_project or attach_project.";

    private static readonly TimeSpan ShutdownReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The most a game that quit is given for its bridge connection to close before it is disposed.</summary>
    private static readonly TimeSpan BridgeCloseGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a stop waits for the console wrapper to follow a game that quit before ending it.</summary>
    private static readonly TimeSpan WrapperExitWait = TimeSpan.FromSeconds(1);

    // SilentUnderDebugger's text quotes this.
    private static readonly TimeSpan DebuggerPingTimeout = TimeSpan.FromMilliseconds(500);
    private const int FailureStderrLines = 20;

    // The clock's handshake: five pings 7 ms apart from one start, so they land at different points of the 60 fps frame, each
    // answered or abandoned within ClockPingTimeout of wall time; the design's ~40 ms for all five.
    private static readonly TimeSpan ClockPingGap = TimeSpan.FromMilliseconds(7);
    private static readonly TimeSpan ClockPingTimeout = TimeSpan.FromMilliseconds(500);
    internal const int ClockPingCount = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile TaskCompletionSource _exitHandled = NewExitHandled();
    private readonly ILogger _logger = registry.Logger;
    private volatile bool _pending = true;

    /// <summary>
    /// The scratch user data folder this session made for its runs (<see cref="ScratchUserData.RootFor"/>), kept across a restart
    /// and deleted when the session's run ends; null when it has none.
    /// </summary>
    private string? _userDataRoot;

    /// <summary>The game's user data folder inside <see cref="_userDataRoot"/>, as the launch result reports it; null with none.</summary>
    private string? _userDataDir;
    private volatile SessionClock _clock = new();
    private GodotRun? _run;

    public string Name { get; } = spec.Name;

    public string ProjectDir { get; } = spec.ProjectDir;

    public SessionKind Kind { get; } = spec.Kind;

    public bool ShutOutRealGamepads { get; } = spec.ShutOutRealGamepads;

    /// <summary>Whether the session is quiet: a run started quiet, or an attach whose game parks its window.</summary>
    public bool Quiet { get; } = spec.Quiet;

    /// <summary>Whether the session asked the bridge to mute the game's Master bus, apart from quiet.</summary>
    public bool Mute { get; } = spec.Mute;

    /// <summary>The run's project settings block, which the folder's override.cfg carries after the bridge's lines; empty for none.</summary>
    public string ProjectSettings { get; } = spec.ProjectSettings;

    /// <inheritdoc cref="SessionSpec.Scratch"/>
    public bool Scratch { get; } = spec.Scratch;

    /// <summary>
    /// The process the server started, once it has; null for an attached game. On Windows that is the Godot_console.exe
    /// wrapper, not the game: <see cref="GameProcessId"/> is the game's. For a process session it is the program itself.
    /// </summary>
    public int? ProcessId { get; private set; }

    /// <summary>The game's own process id, as its bridge's hello reported it; null before the handshake or from an older bridge.</summary>
    public int? GameProcessId { get; private set; }

    /// <summary>
    /// The game's creation time, read from the handle the server keeps on it from the hello; null without a kept handle or when
    /// it could not be read. It tells the game apart from a later process that took its pid.
    /// </summary>
    public DateTime? GameStartTime { get; private set; }

    /// <summary>
    /// The game's main window handle, as its bridge's hello reported it; null before the handshake, from an older bridge, and
    /// for a headless game.
    /// </summary>
    public long? WindowHandle { get; private set; }

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

    /// <summary>
    /// The session's clock offset from the drive clock, replaced at each handshake: the hello's upper bound and the five
    /// handshake pings', then each pong a later clock ping brings.
    /// </summary>
    internal SessionClock Clock => _clock;

    /// <summary>Completes when the handler of the current run's exit has finished, its folder released or kept; for tests.</summary>
    internal Task ExitHandled => _exitHandled.Task;

    /// <summary>The run the server started for the session, the one whose exit <see cref="OnRunExitedAsync"/> handles; for tests.</summary>
    internal GodotRun? CurrentRun => _run;

    /// <summary>Whether the session is starting, its run is running, or its attached game is still connected.</summary>
    public bool IsLive => _pending || HasGame;

    /// <summary>
    /// Whether the session's process is running: its run has started and not exited, its attached game is still connected, or
    /// its process session's program is still running.
    /// </summary>
    public bool HasGame =>
        Kind switch
        {
            SessionKind.Process => ProcessRunning,
            SessionKind.Run or SessionKind.Editor => _run is { IsRunning: true },
            SessionKind.Attach => _attached is { IsOpen: true },
            _ => throw new InvalidOperationException($"Unknown session kind {Kind}."),
        };

    /// <summary>
    /// Whether a session of <paramref name="kind"/> injects the bridge through the folder's override.cfg: a run and an attach do;
    /// an editor ignores override.cfg (4.7.2 <c>main/main.cpp</c> L2107) and gets the bridge from its host, and a process has none.
    /// </summary>
    internal static bool WritesOverride(SessionKind kind) => kind is SessionKind.Run or SessionKind.Attach;

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
    /// is asked to quit and killed if it has not within 3 s, or 30 s while it records. A real-time recording's capture is ended
    /// first, so its clip ends at the stop. Either way the override file is released, after a recording's clips are cut or its
    /// real-time clip finished. An attached game is ended the same way (see <see cref="StopAttachedAsync"/>),
    /// and its session is dropped.
    /// </summary>
    /// <remarks>An editor session with unsaved scenes is refused, as without discard.</remarks>
    public Task<object> StopAsync(CancellationToken cancellationToken) => StopAsync(discard: false, cancellationToken);

    /// <inheritdoc cref="StopAsync(CancellationToken)" path="/summary"/>
    /// <param name="discard">
    /// For an editor session: kill it without its close request, losing its unsaved scenes; without it, a stop of an editor with
    /// unsaved scenes is refused before anything is stopped. Ignored by every other kind.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the session's gate.</param>
    /// <exception cref="SessionException">
    /// No run was ever launched, the attached session has no game, or the editor has unsaved scenes and <paramref name="discard"/>
    /// is false.
    /// </exception>
    public async Task<object> StopAsync(bool discard, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Kind == SessionKind.Process)
            {
                return await StopProcessAsync();
            }

            if (Kind == SessionKind.Attach)
            {
                return await StopAttachedAsync();
            }

            GodotRun run =
                _run ?? throw new SessionException("No Godot session has been started, so there is nothing to stop. Start one with run_project.");
            bool discardEditor = await RefuseUnsavedScenesAsync(run, discard);
            bool alreadyExited = !run.IsRunning;
            RunEnd ended;
            try
            {
                await EndRealtimeCaptureAsync();
                ended = await EndRunAsync(run, discardEditor);
            }
            finally
            {
                await CloseOutputLogAsync(run);
            }

            int? gameExitCode = await run.ReleaseGameAsync();
            Snapshots.Clear();
            registry.Captures.End(Name, CaptureStore.EndedByStop);
            RecordingResult? recording = await FinishRecordingAsync(run) ?? await TakeRealtimeResultAsync();
            bool removed;
            try
            {
                removed = registry.ReleaseFolder(this);
            }
            finally
            {
                // The scratch folder does not depend on override.cfg, so a failed release of that file leaves it no less ended.
                DeleteScratchUserData();
            }

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
    public Task<JsonNode?> SendAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? release = null
    ) => SendCappedAsync(command, parameters, timeout, release is { } after ? new Release(after, null) : null, cancellationToken);

    /// <summary>
    /// <see cref="SendAsync"/> with a wall cap on the release, as <see cref="BridgeConnection.SendCappedAsync"/> takes it.
    /// </summary>
    /// <exception cref="SessionException">As <see cref="SendAsync"/> throws it.</exception>
    public async Task<JsonNode?> SendCappedAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        Release? release,
        CancellationToken cancellationToken
    )
    {
        BridgeConnection connection = FindLiveConnection();
        if (DebuggedProcessId is int debugged)
        {
            await RefuseIfPausedAsync(connection, debugged, cancellationToken);
        }

        return await connection.SendCappedAsync(command, parameters, timeout, release, cancellationToken);
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
        if (Kind == SessionKind.Process)
        {
            return _processStderr.Tail(count);
        }

        if (Kind == SessionKind.Attach)
        {
            return null;
        }

        return _run is { } run ? run.Stderr.Tail(count) : [];
    }

    /// <summary>
    /// Writes one line the server adds, such as a scratch step's status, into the run's whole-output file only, never into its
    /// stdout or stderr rings, so nothing that reads the game's output (debug output, scratch patterns, a step's lines) sees it;
    /// does nothing for a session with no run or no open file.
    /// </summary>
    public void WriteToOutputLog(string line)
    {
        if (_run is { } run)
        {
            run.OutputLog?.Write(line);
        }
    }

    /// <exception cref="SessionException">The session is attached, so there is no captured output.</exception>
    public DebugOutput GetDebugOutput(int limit, long? before)
    {
        if (Kind == SessionKind.Process)
        {
            return GetProcessDebugOutput(limit, before);
        }

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
            Log = run.OutputLog?.Path,
        };
    }

    /// <summary>
    /// Completes once the output of the session's run has been read to its end, so no line it printed can still be in flight;
    /// completed at once for a session with no run to read, as a process session has.
    /// </summary>
    public Task OutputDrained => _run?.OutputDrained ?? Task.CompletedTask;

    /// <summary>The held lines of each stream uncut, with each stream's line count: what an output wait reads, so a line
    /// longer than <see cref="OutputBuffer.MaxLineLength"/> is still matched whole. An attached session has no captured output.</summary>
    /// <exception cref="SessionException">The session is attached.</exception>
    public (OutputPage Stdout, OutputPage Stderr, long StdoutTotalLines, long StderrTotalLines) GetRawOutput()
    {
        if (Kind == SessionKind.Process)
        {
            return GetProcessRawOutput();
        }

        if (Kind == SessionKind.Attach)
        {
            throw new SessionException(
                $"The session for {ProjectDir} is attached: attached sessions have no captured output; the game's own console or log has it."
            );
        }

        GodotRun? run = _run;
        if (run is null)
        {
            return (new OutputPage(null, []), new OutputPage(null, []), 0L, 0L);
        }

        // Read the totals before the snapshots: a line arriving between them would otherwise be in neither page nor above
        // a mark read later, and a wait without after would miss it.
        long stdoutTotal = run.Stdout.TotalLines;
        long stderrTotal = run.Stderr.TotalLines;
        return (run.Stdout.Raw(), run.Stderr.Raw(), stdoutTotal, stderrTotal);
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
        if (Kind == SessionKind.Process)
        {
            await ShutdownProcessAsync();
            return true;
        }

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
    /// <remarks>
    /// The session's scratch user data folder is deleted after the kills, so no game still holds a file in it; this is the server
    /// exit's delete, since the registry's shutdown reaches every session that launched through here.
    /// </remarks>
    public async Task KillAtShutdownAsync()
    {
        if (Kind is SessionKind.Run or SessionKind.Editor && _run is { } run)
        {
            await KillRunAtShutdownAsync(run);
        }

        if (TakeScratchUserDataRoot() is { } root)
        {
            AtShutdown(() => ScratchUserData.Delete(root));
        }
    }

    /// <summary>The kills of <see cref="KillAtShutdownAsync"/>: each process of the run still running, then the wait for them.</summary>
    private async Task KillRunAtShutdownAsync(GodotRun run)
    {
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

            DisposeProcess();
            DisposeAttachedGame();
            // Under the gate and before the run is let go of, so a late exit handler of this session finds no folder to delete,
            // whatever session takes the name next.
            DeleteScratchUserData();
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
        DisposeProcess();
        _run?.OutputLog?.Dispose();
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
    /// when it quit, how long a quit took, and its warnings, joined: a debugger attached to it, and what the kill left still
    /// running after its kill wait.
    /// </summary>
    private readonly record struct RunEnd(bool Killed, string? Warning, string? KillReason, IReadOnlyList<string> LeftRunning, int? QuitMs);

    /// <summary>
    /// A run <see cref="PrepareAndStartAsync"/> started: the run, its prep, the wait for its bridge's hello (registered before
    /// Godot started, and disposed by whoever ends the start) and the Godot it runs, plus, for a restart, how the run it
    /// replaced ended and how that run's recording ended.
    /// </summary>
    private sealed record PreparedRun
    {
        public required GodotRun Run { get; init; }

        public required PrepResult Prep { get; init; }

        public required HandshakeWait Handshake { get; init; }

        public required string Godot { get; init; }

        public required RunEnd? Previous { get; init; }

        public required RecordingResult? PreviousRecording { get; init; }
    }

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

    /// <summary>Whether the live run's bridge connection still awaits the reply to request <paramref name="id"/>; false with no live run.</summary>
    internal bool IsRequestPending(long id) => _run is { IsRunning: true, Connection: { IsOpen: true } live } && live.IsPending(id);

    /// <summary>Releases the override file after a failed start and, when nothing started, drops the session from the registry.</summary>
    /// <returns>Whether the override file was removed; a failed removal is logged, so the start's own error reaches the caller.</returns>
    private bool AbandonStart(bool forget)
    {
        if (forget)
        {
            registry.Forget(this);
        }

        DeleteScratchUserData();
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

    /// <summary>
    /// The exit code of Godot's crash at boot on Windows: 0xC06D007F, a delay-load "procedure not found" raised by dinput8,
    /// whose start-up asks GetProcAddress through an import Godot has already hooked but whose real address it has not yet
    /// saved, before Main::setup, the project or any script. A start on a registry with
    /// <see cref="SessionRegistry.RetryBootCrashOnce"/> retries it once.
    /// </summary>
    internal const int BootCrashExitCode = unchecked((int)0xC06D007F);

    /// <summary>Whether a Godot run exited on its own with <see cref="BootCrashExitCode"/>.</summary>
    internal static bool CrashedAtBoot(ToolProcessResult ran) => !ran.WasKilled && ran.ExitCode == BootCrashExitCode;

    internal static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What the hello of this session's run must carry; an editor's must also say it runs in the editor.</summary>
    private HandshakeExpectation ExpectHello(string token) => new(token, ProjectDir) { Editor = Kind == SessionKind.Editor };

    /// <summary>
    /// The launch's handshake timeout: the registry's, or for an editor session the longer of it and
    /// <see cref="EditorHandshakeTimeout"/>.
    /// </summary>
    private TimeSpan CurrentHandshakeTimeout =>
        Kind == SessionKind.Editor
            ? TimeSpan.FromTicks(Math.Max(EditorHandshakeTimeout.Ticks, registry.HandshakeTimeout.Ticks))
            : registry.HandshakeTimeout;

    /// <summary>What refuses the stop of an editor that has unsaved scenes, naming them.</summary>
    internal static string UnsavedScenesRefusal(IEnumerable<string> scenes) =>
        $"The editor has unsaved scenes: {string.Join(", ", scenes)}. Save them in the editor, or pass discard: true to close without saving.";

    /// <summary>
    /// For an editor session stopped without <paramref name="discard"/>: asks its running editor's bridge for its unsaved scenes
    /// and refuses the stop when there are any, since the editor's close request would only show its save dialog and the grace
    /// would end in a kill. An editor that does not answer is left to the stop, which pings it and kills it when it is silent.
    /// Nothing for any other kind.
    /// </summary>
    /// <returns>Whether the stop discards an editor's unsaved scenes: an editor session stopped with discard.</returns>
    /// <exception cref="SessionException">The editor has unsaved scenes and <paramref name="discard"/> is false.</exception>
    private async Task<bool> RefuseUnsavedScenesAsync(GodotRun run, bool discard)
    {
        if (Kind != SessionKind.Editor || discard)
        {
            return Kind == SessionKind.Editor;
        }

        string[] scenes = run.IsRunning && run.Connection is { IsOpen: true } connection ? await AskUnsavedScenesAsync(connection) : [];
        return scenes.Length > 0 ? throw new SessionException(UnsavedScenesRefusal(scenes)) : false;
    }

    /// <summary>The editor's unsaved scenes, as its bridge's editor_unsaved lists them; none when it does not answer, which is logged.</summary>
    private async Task<string[]> AskUnsavedScenesAsync(BridgeConnection connection)
    {
        try
        {
            JsonNode? reply = await connection.SendRawAsync("editor_unsaved", null, ShutdownReplyTimeout, CancellationToken.None);
            return reply?["scenes"] is JsonArray listed ? [.. listed.Select(scene => scene?.GetValue<string>() ?? string.Empty)] : [];
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
            Log.ShutdownNotAcknowledged(_logger, e);
            return [];
        }
    }

    /// <summary>Adds a captured frame from this session's bridge to its capture in the registry's store.</summary>
    private void ReceiveCaptured(JsonObject frame) => registry.Captures.Receive(Name, frame);

    /// <summary>
    /// Notes the frames a call ran in on <paramref name="recording"/>, the Movie Maker recording of the run whose connection
    /// read the reply, until its clips are cut, a reply read after the game exited included; with none, they are dropped.
    /// </summary>
    private static void ReceiveReplyFrames(Recording? recording, long read, long replied)
    {
        if (recording is { Outcome: null })
        {
            recording.AddCall(read, replied);
        }
    }

    /// <summary>
    /// Keeps <paramref name="recording"/>, the Movie Maker recording of the run whose bridge ended with a call in flight, from
    /// its last known frame to the end of the movie, since no reply will note that call's frames; with no recording, or one
    /// already cut, nothing.
    /// </summary>
    private static void KeepRecordingToEnd(Recording? recording)
    {
        if (recording is { Outcome: null })
        {
            recording.KeepToEnd();
        }
    }

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
    private async Task<PreparedRun> PrepareAndStartAsync(LaunchRequest request, GodotRun? previous, CancellationToken cancellationToken)
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
            )
            {
                RetryBootCrashOnce = registry.RetryBootCrashOnce,
            };
            PrepResult prep = request.Prepare ? await ProjectPrep.RunAsync(context, cancellationToken) : PrepResult.Skipped;
            string bridgeScript = Installation.FindBridgeScript();
            // A shutdown that began during the prep refuses the start before a restart's old game is stopped.
            registry.RefuseStartAtShutdown();
            RunEnd? previousEnd = null;
            RecordingResult? previousRecording = null;
            if (previous is not null)
            {
                // A restart cancelled by now must not stop a healthy game that the handshake wait would then kill.
                cancellationToken.ThrowIfCancellationRequested();
                await EndRealtimeCaptureAsync();
                previousEnd = await EndRunAsync(previous, discard: false);
                Snapshots.Clear();
                registry.Captures.End(Name, CaptureStore.EndedByRestart);
                StopOutputCapture(previous);
                previousRecording = await FinishRecordingAsync(previous) ?? await TakeRealtimeResultAsync();
            }

            if (WritesOverride(Kind))
            {
                registry.WriteOverride(this, bridgeScript);
                GitExclude.Ensure(ProjectDir, OverrideFile.FileName, _logger);
            }

            MakeScratchUserData(request, previous);
            Func<int, string>? marker = previous is not null && ProcessId is int replaced ? started => RestartMarker(replaced, started) : null;
            (GodotRun run, HandshakeWait handshake) = await StartGodotAsync(request, godotPath, previous, marker, cancellationToken);
            return new PreparedRun
            {
                Run = run,
                Prep = prep,
                Handshake = handshake,
                Previous = previousEnd,
                PreviousRecording = previousRecording,
                Godot = godotPath,
            };
        }
        finally
        {
            folderLock.Release();
        }
    }

    /// <summary>
    /// Creates and starts Godot for <paramref name="request"/> with a fresh bridge token, and makes the new run the session's.
    /// </summary>
    /// <param name="request">What to launch.</param>
    /// <param name="godotPath">The Godot executable.</param>
    /// <param name="carried">The run whose output buffers and output file the new run continues; null for a first launch.</param>
    /// <param name="marker">
    /// The marker line the carried output gets once the new process has its pid, from that pid; null for no marker.
    /// </param>
    /// <param name="cancellationToken">The caller's token; cancelling it withdraws the handshake wait.</param>
    /// <returns>The started run and the wait for its bridge's hello, which the caller disposes.</returns>
    /// <remarks>
    /// A start that fails before the run is the session's withdraws the wait, then kills whatever the run started, with its
    /// tree, and disposes the run (<see cref="EndUnhandedRunAsync"/>), so no Godot outlives a launch that failed to hand it over.
    /// </remarks>
    private async Task<(GodotRun Run, HandshakeWait Handshake)> StartGodotAsync(
        LaunchRequest request,
        string godotPath,
        GodotRun? carried,
        Func<int, string>? marker,
        CancellationToken cancellationToken
    )
    {
        string token = CreateToken();
        string? moviePath = PrepareMoviePath(request);
        BridgeEndpoint bridge = new(registry.Listener.Port, token);
        LaunchRequest launched = request.ScratchUserData is null ? request : request with { UserDataRoot = _userDataRoot };
        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(godotPath, launched, bridge, moviePath);
        GodotRun run = new(ProjectDir, CreateRunProcess(startInfo, request.Quiet), carried);
        if (request.OutputLogPath is { } logPath)
        {
            run.OutputLogPath = logPath;
        }

        // The wait is registered before Godot starts, since its bridge may dial at once and the listener refuses a hello whose
        // token no session awaits; its deadline starts here, so the prep never counts.
        HandshakeWait handshake = new(registry.LaunchClock, registry.Listener, ExpectHello(token), CurrentHandshakeTimeout, cancellationToken);
        try
        {
            StartProcess(run, marker);
        }
        catch
        {
            handshake.Dispose();
            await EndUnhandedRunAsync(run);
            throw;
        }

        MarkGameStarted();
        _exitHandled = NewExitHandled();
        _run = run;
        _recording = moviePath is null ? null : new Recording(moviePath) { DropIdle = request.DropIdle };
        ProcessId = run.Process.Id;
        return (run, handshake);
    }

    /// <summary>
    /// Ends a run whose start failed before the session took it: kills its process, with everything it started, when it runs,
    /// waits for it as a stop's kill does (<see cref="KillAsync"/>), and disposes the run. A process that never started is left
    /// alone. Its exit's handler finds a run that is not the session's (<see cref="OnRunExitedAsync"/>), so the session's
    /// current run, its folder and its exit's handling are untouched.
    /// </summary>
    private async Task EndUnhandedRunAsync(GodotRun run)
    {
        _ = await KillAsync(run);
        await run.DisposeAsync();
    }

    /// <summary>
    /// Whether a run whose handshake failed is retried: the registry retries a boot crash
    /// (<see cref="SessionRegistry.RetryBootCrashOnce"/>) and the run exited with <see cref="BootCrashExitCode"/>.
    /// </summary>
    private bool IsBootCrashToRetry(GodotRun run) => registry.RetryBootCrashOnce && !run.IsRunning && run.ExitCode == BootCrashExitCode;

    /// <summary>
    /// Makes and seeds the session's scratch user data folder for a request that asks for one. A restart reuses the folder its
    /// replaced run had, so what the game saved survives it; a launch, or a restart after the folder went with the game's exit,
    /// makes it afresh. Set before it is made, so a failed start deletes what was made (<see cref="AbandonStart"/>).
    /// </summary>
    /// <exception cref="SessionException">The folder could not be made or the seed not copied.</exception>
    private void MakeScratchUserData(LaunchRequest request, GodotRun? previous)
    {
        if (request.ScratchUserData is not { } scratch || (previous is not null && _userDataRoot is not null))
        {
            return;
        }

        string root = ScratchUserData.RootFor(ProjectDir, Name);
        _userDataRoot = root;
        try
        {
            _userDataDir = ScratchUserData.Create(ProjectDir, root, scratch.SeedFolder, ProjectSettings, request.Record);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SessionException($"Could not make the scratch user data folder {root}: {e.Message}", e);
        }
    }

    /// <summary>Takes the session's scratch user data folder off it, so exactly one caller deletes it; null when it has none.</summary>
    private string? TakeScratchUserDataRoot()
    {
        _userDataDir = null;
        return Interlocked.Exchange(ref _userDataRoot, null);
    }

    /// <summary>
    /// Deletes the session's scratch user data folder, once its game has exited: at stop_project, at the exit of the session's
    /// current run, and after a failed start. Never on a restart, whose replaced run's exit finds a newer run in its place.
    /// A failed delete is logged and the folder left.
    /// </summary>
    private void DeleteScratchUserData()
    {
        if (TakeScratchUserDataRoot() is not { } root)
        {
            return;
        }

        try
        {
            ScratchUserData.Delete(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogScratchUserDataDeleteFailed(_logger, e, root);
        }
    }

    /// <summary>
    /// The exit handler's delete of the scratch folder: only for the session's <paramref name="current"/> run, and only once the
    /// game itself is gone. On Windows the run's process is a console wrapper the game can outlive; a game still running keeps
    /// its folder for stop_project, the session's retirement or the server's exit, which end the game first.
    /// </summary>
    private void DeleteScratchUserDataAfterExit(bool current, GodotRun run)
    {
        if (current && !(run.Game is { } game && IsRunningAtShutdown(game)))
        {
            DeleteScratchUserData();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the scratch user data folder {Root}")]
    private static partial void LogScratchUserDataDeleteFailed(ILogger logger, Exception exception, string root);

    /// <summary>The line a restart writes into the output it carries over, once the new game has its pid.</summary>
    private static string RestartMarker(int replaced, int started) =>
        $"[godot-mcp] restarted: the previous game (pid {replaced}) was stopped; output below is from pid {started}.";

    /// <summary>The line a retry after a boot crash writes into the output it carries over, once the new game has its pid.</summary>
    private static string BootCrashMarker(int crashed, int started) =>
        $"[godot-mcp] Godot (pid {crashed}) crashed at boot with 0xC06D007F; retried, output below is from pid {started}";

    /// <summary>
    /// Starts Godot once more for a run that crashed at boot: the crashed run's output capture stopped as a restart stops its
    /// old run's, then a fresh process for the same request under the folder's prep lock, with no second prep, whose output
    /// carries on the crashed run's buffers and file after a marker line. The crashed run is let go of once the new one is the
    /// session's, and also when the wait for the lock is cancelled or the new start fails.
    /// </summary>
    /// <exception cref="SessionException">The server's exit has begun, or Godot could not start.</exception>
    /// <exception cref="OperationCanceledException">The wait for the folder's prep lock was cancelled.</exception>
    private async Task<PreparedRun> RelaunchAfterBootCrashAsync(LaunchRequest request, PreparedRun crashed, CancellationToken cancellationToken)
    {
        StopOutputCapture(crashed.Run);
        int crashedProcessId = crashed.Run.Process.Id;
        SemaphoreSlim folderLock = registry.PrepLock(ProjectDir);
        try
        {
            await folderLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await crashed.Run.DisposeAsync();
            throw;
        }

        PreparedRun relaunched;
        try
        {
            registry.RefuseStartAtShutdown();
            LogBootCrashRetried(_logger, "the launch");
            (GodotRun run, HandshakeWait handshake) = await StartGodotAsync(
                request,
                crashed.Godot,
                crashed.Run,
                started => BootCrashMarker(crashedProcessId, started),
                cancellationToken
            );
            relaunched = crashed with { Run = run, Handshake = handshake };
        }
        catch
        {
            await crashed.Run.DisposeAsync();
            throw;
        }
        finally
        {
            folderLock.Release();
        }

        try
        {
            await crashed.Run.DisposeAsync();
        }
        catch
        {
            relaunched.Handshake.Dispose();
            throw;
        }

        return relaunched;
    }

    /// <summary>Says a start whose Godot crashed at boot runs once more; <paramref name="start"/> names it ("the launch").</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Godot crashed at boot with 0xC06D007F; retrying {Start} once")]
    internal static partial void LogBootCrashRetried(ILogger logger, string start);

    /// <returns>
    /// The launch, how the run it replaced ended and how that run's recording ended; both are null for a launch, and the
    /// recording also for a replaced run that recorded nothing.
    /// </returns>
    private async Task<(LaunchResult Started, RunEnd? Previous, RecordingResult? PreviousRecording)> StartRunAsync(
        LaunchRequest request,
        GodotRun? previous,
        CancellationToken cancellationToken
    )
    {
        registry.RefuseStartAtShutdown();
        PreparedRun prepared = await PrepareAndStartAsync(request, previous, cancellationToken);
        BridgeConnection connection;
        try
        {
            connection = await WaitForHandshakeAsync(prepared.Run, prepared.Handshake, cancellationToken);
        }
        catch (SessionException) when (IsBootCrashToRetry(prepared.Run))
        {
            prepared = await RelaunchAfterBootCrashAsync(request, prepared, cancellationToken);
            connection = await WaitForHandshakeAsync(prepared.Run, prepared.Handshake, cancellationToken);
        }

        GodotRun run = prepared.Run;
        int processId = run.Process.Id;
        // The clock is replaced before the connection is published, so no call converts a new run's stamp on the old run's clock.
        await StartClockAsync(connection);
        connection.OnErrors(Errors.Receive);
        connection.OnCaptured(ReceiveCaptured);
        // Bound to this run's own recording, so a late reply or end of this connection never reaches a later run's movie.
        Recording? runRecording = _recording;
        connection.OnReplyFrames((read, replied) => ReceiveReplyFrames(runRecording, read, replied));
        connection.OnEndedWithCallsInFlight(() => KeepRecordingToEnd(runRecording));
        run.Connection = connection;
        GameProcessId = connection.GameProcessId;
        GameStartTime = null;
        WindowHandle = connection.WindowHandle;
        if (GameProcessId is int gameProcessId)
        {
            run.KeepGameHandle(gameProcessId, _logger);
            GameStartTime = StartTimeOf(run.Game);
        }

        await WelcomeAsync(connection);

        Log.RunStarted(_logger, processId, run.ProjectDir);
        LastLaunch = request;
        string? windowWarning = GodotCommandLine.DescribeWindowMismatch(GodotCommandLine.RequestedWindowSize(request.EngineArgs), connection.Window);
        (string? userDataDir, string? userDataWarning) = ScratchUserDataResult(request, connection);
        LaunchResult started = new(Name, run.ProjectDir, processId, request.Quiet, prepared.Prep, prepared.Godot)
        {
            Recording = _recording is { } recording ? new RecordingResult { Mode = Recording.MovieMode, Path = recording.Path } : null,
            Window = connection.Window,
            Warning = JoinWarnings(windowWarning, userDataWarning),
            Log = run.OutputLog?.Path,
            UserDataDir = userDataDir,
        };
        return (started, prepared.Previous, prepared.PreviousRecording);
    }

    /// <summary>
    /// A scratch run's user data folder and its warning, as <see cref="ScratchUserData.Reconcile"/> sets them from the folder the
    /// server made and the one the game's hello reported; neither for a run without scratch user data.
    /// </summary>
    private (string? UserDataDir, string? Warning) ScratchUserDataResult(LaunchRequest request, BridgeConnection connection) =>
        request.ScratchUserData is not null && _userDataDir is { } serverDir
            ? ScratchUserData.Reconcile(serverDir, connection.HelloUserDataDir)
            : (null, null);

    /// <summary>Two warnings joined with a space, either alone when the other is null; null when both are.</summary>
    private static string? JoinWarnings(string? first, string? second) => first is null || second is null ? first ?? second : $"{first} {second}";

    /// <summary>The creation time of a game the server holds a handle on; null without one, or when it cannot be read.</summary>
    private static DateTime? StartTimeOf(Process? game)
    {
        try
        {
            return game?.StartTime;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // The handle is still kept; only a pid check against the game's creation time is lost.
            return null;
        }
    }

    /// <summary>
    /// The run's process, not yet running. A quiet run on Windows is created suspended on the server's hidden desktop, so
    /// none of its windows ever shows on the user's; every other run starts through <see cref="Process.Start()"/>. A registry
    /// given a <see cref="SessionRegistry.RunProcessFactory"/> has it create the process instead, so a test can feed a launch's
    /// output without Godot.
    /// </summary>
    /// <exception cref="SessionException">The hidden desktop or the suspended process could not be created.</exception>
    private IRunProcess CreateRunProcess(ProcessStartInfo startInfo, bool quiet)
    {
        if (registry.RunProcessFactory is { } factory)
        {
            return factory(startInfo, quiet);
        }

        if (GodotCommandLine.UsesHiddenDesktop(quiet))
        {
            return DesktopProcess.CreateSuspended(startInfo, HiddenDesktop.Path);
        }

        return new StartInfoProcess(new Process { StartInfo = startInfo, EnableRaisingEvents = true });
    }

    /// <summary>
    /// Starts the run's process and its output capture. A run with no log file yet opens one, fresh for a launch and in append
    /// mode for a restart or a retry after a boot crash, whose predecessor's file it writes on into; a file that cannot be opened
    /// is logged and the run goes on without one. With a <paramref name="marker"/> (a restart's, a retry's) its line, made from
    /// the new process's id, goes into both carried-over streams and the file once, before any of the new output is read. The
    /// file closes when both streams have ended, so a stopped session holds nothing in the project folder.
    /// </summary>
    private void StartProcess(GodotRun run, Func<int, string>? marker)
    {
        // The runtime raises Exited inside the process's own lock, and inline in whatever HasExited or WaitForExit call observed
        // the exit, under that caller's locks. The handler is queued, so it never runs under either and takes them in no order.
        run.Process.Exited += (_, _) => _ = Task.Run(() => OnRunExitedAsync(run));
        run.Launcher.Start();
        try
        {
            BeginOutput(run, marker);
        }
        catch
        {
            // The output file closes here, as the output's end would have closed it; the caller ends the started process.
            run.OutputEnded();
            throw;
        }
    }

    /// <summary>
    /// The output half of <see cref="StartProcess"/>, once the process runs: opens the output file, writes the marker line, and
    /// begins passing the process's output on.
    /// </summary>
    private void BeginOutput(GodotRun run, Func<int, string>? marker)
    {
        run.OutputLog ??= OpenOutputLog(run);
        if (marker is not null)
        {
            string line = marker(run.Process.Id);
            run.Stdout.Add(line);
            run.Stderr.Add(line);
            run.OutputLog?.Write(line);
        }

        Action<string> stdout = WithOutputLog(run, run.Stdout.Add);
        Action<string> stderr = WithOutputLog(run, run.Stderr.Add);
        if (Kind == SessionKind.Editor)
        {
            stdout = new EditorOutputNoise(stdout).Add;
            stderr = new EditorOutputNoise(stderr).Add;
        }

        run.Launcher.BeginRead(stdout, stderr, run.OutputEnded);
    }

    /// <summary>
    /// Opens the run's output log, fresh or on into the file a restart carried over, and records its path on the run. The file
    /// counts as opened only once it is: a failure is logged, and the run goes on without one.
    /// </summary>
    private RunOutputLog? OpenOutputLog(GodotRun run)
    {
        bool append = run.OutputLogPath is not null;
        string path = run.OutputLogPath ?? Path.Combine(ProjectPrep.LogFolder(run.ProjectDir), $"run-{Name}.log");
        try
        {
            var log = RunOutputLog.Open(path, append, _logger);
            run.OutputLogPath = path;
            return log;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.RunLogUnwritable(_logger, e, path, run.ProjectDir);
            return null;
        }
    }

    /// <summary>
    /// A stream's line handler: into the run's ring, through the watch for a line that says Godot could not create a window,
    /// then into its whole-output file when it has one.
    /// </summary>
    private static Action<string> WithOutputLog(GodotRun run, Action<string> add) =>
        line =>
        {
            add(line);
            run.WatchOutput(line);
            run.OutputLog?.Write(line);
        };

    /// <summary>
    /// Waits for a stopped run's output file to close once its readers have ended, for the drain grace
    /// <see cref="ToolProcess.OutputDrainGrace"/> gives a tool's own log, and then closes it anyway: the lines still in the pipes
    /// are dropped, as <see cref="ToolProcess.LogSink"/> drops those still arriving after its grace. The session holds nothing in
    /// the project folder once a stop returns.
    /// </summary>
    private static async Task CloseOutputLogAsync(GodotRun run)
    {
        if (run.OutputLog is not { } log)
        {
            return;
        }

        try
        {
            await log.Closed.WaitAsync(ToolProcess.OutputDrainGrace);
        }
        catch (TimeoutException)
        {
            log.Dispose();
        }
    }

    private async Task<BridgeConnection> WaitForHandshakeAsync(GodotRun run, HandshakeWait handshake, CancellationToken cancellationToken)
    {
        using HandshakeWait wait = handshake;
        Task exited = run.Process.WaitForExitAsync(wait.Token);
        Task<string> noGl = run.NoGlWindowLine;
        // A bridge that connected wins over a line that arrived beside it: a game that dialled in has its window.
        await Task.WhenAny(wait.Accept, exited, noGl);
        if (wait.Accept.IsCompletedSuccessfully)
        {
            return await wait.TakeAccept();
        }

        await wait.CancelAsync();
        await ObserveAbandonedAsync(wait.TakeAccept(), exited);
        bool exitedEarly = !run.IsRunning;
        if (noGl.IsCompletedSuccessfully)
        {
            throw await RefuseNoGlAsync(run, noGl.Result, cancellationToken);
        }

        if (!exitedEarly)
        {
            await KillAsync(run);
        }

        // The failed run stays the session's, so its file is closed here rather than left pinning the project folder.
        await CloseOutputLogAsync(run);
        cancellationToken.ThrowIfCancellationRequested();
        throw DescribeFailedLaunch(run, exitedEarly, wait.Deadline);
    }

    /// <summary>
    /// Stops a run whose output said Godot could not create a window and returns that refusal: what the wait does to a run
    /// that timed out, without the timeout it would have spent first. Godot is alive behind a modal alert, so waiting for the
    /// bridge cannot help it.
    /// </summary>
    private async Task<SessionException> RefuseNoGlAsync(GodotRun run, string line, CancellationToken cancellationToken)
    {
        await KillAsync(run);
        await CloseOutputLogAsync(run);
        cancellationToken.ThrowIfCancellationRequested();
        return DescribeNoGl(run, line);
    }

    /// <summary>
    /// The refusal of a launch whose own output said Godot could not create a window: the line that said it, and the run's
    /// last stderr lines. It leaves out the timeout's advice about 127.0.0.1, which no connection had anything to do with.
    /// </summary>
    private static SessionException DescribeNoGl(GodotRun run, string line)
    {
        string stderr = string.Join('\n', run.Stderr.Tail(FailureStderrLines));
        return new SessionException(
            $"Launching {run.ProjectDir} failed: Godot cannot create a window on this machine; its output says \"{line}\" "
                + "(no usable OpenGL: no GPU driver, or a session with no desktop), so Godot was stopped without waiting for "
                + $"the bridge. Run it in a logged-on console session or on a machine with a GPU. Last stderr lines:\n{stderr}"
        );
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

    /// <summary>
    /// Replaces the session's clock from the connection just handshaken and sends its five handshake pings, started 7 ms apart
    /// from one start and awaited together, so every later stamp converts. A ping that fails or times out leaves the bounds as
    /// they are and never fails the launch or the attach.
    /// </summary>
    private async Task StartClockAsync(BridgeConnection connection)
    {
        SessionClock clock = new();
        if (connection.HelloTicksUsec is long hello)
        {
            clock.AddHello(hello, connection.HelloReceivedMs);
        }

        _clock = clock;
        long started = Stopwatch.GetTimestamp();
        List<Task> pings = [];
        for (int index = 0; index < ClockPingCount; index++)
        {
            pings.Add(PingClockAsync(connection, clock, started, index));
        }

        await Task.WhenAll(pings);
    }

    /// <summary>
    /// Sends one more clock ping when the session's bounds are wider than 2 ms or missing, so a stamp converted next is as
    /// precise as a pong can make it; returns at once while they are tight. A ping that times out or fails, one that
    /// <paramref name="cancellationToken"/> cancels, and a session with no live connection, leave the bounds as they are, so
    /// a result already in hand is never lost to the ping. Returns the clock it read on entry and fed, the one to convert on.
    /// </summary>
    internal async Task<SessionClock> RefreshClockAsync(CancellationToken cancellationToken)
    {
        SessionClock clock = _clock;
        if (!clock.IsWide)
        {
            return clock;
        }

        BridgeConnection connection;
        try
        {
            connection = FindLiveConnection();
        }
        catch (SessionException)
        {
            // No live connection, so nothing to ping: the stamp converts on the bounds the clock already has.
            return clock;
        }

        try
        {
            await AddClockPingAsync(connection, clock, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up during the ping, after its result arrived: the stamp converts on the bounds already held, and
            // the caller's own cancellation handling decides what becomes of the result.
        }

        return clock;
    }

    /// <summary>One handshake ping, sent once its gap from the handshake's start has passed, feeding the clock its pong.</summary>
    private static async Task PingClockAsync(BridgeConnection connection, SessionClock clock, long started, int index)
    {
        TimeSpan due = ClockPingGap * index;
        TimeSpan spent = Stopwatch.GetElapsedTime(started);
        if (due > spent)
        {
            await Task.Delay(due - spent);
        }

        await AddClockPingAsync(connection, clock, CancellationToken.None);
    }

    /// <summary>
    /// Sends one clock ping on the connection's raw path and adds its pong's bounds to <paramref name="clock"/>; a ping that
    /// times out or fails leaves the bounds as they are.
    /// </summary>
    private static async Task AddClockPingAsync(BridgeConnection connection, SessionClock clock, CancellationToken cancellationToken)
    {
        try
        {
            if (await connection.ClockPingAsync(ClockPingTimeout, cancellationToken) is { } sample)
            {
                clock.Add(sample.TicksUsec, sample.SentMs, sample.ReceivedMs);
            }
        }
        catch (TimeoutException)
        {
            // A ping the game does not answer leaves the bounds to the pongs that do; neither the handshake nor a stamp fails for it.
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // The connection ended or the bridge refused the ping: the clock keeps the bounds it already has.
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
            : $"the bridge did not connect within {LoadDeadline.Seconds(deadline.Budget)} s{backstop}, so Godot was stopped";
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
    /// load-adjusted time on the registry's clock with its backstop, and the kill says what the game was doing. A game that
    /// acknowledged the quit and closed its bridge by the end of the exit grace gets the teardown grace past it
    /// (<see cref="WaitOutTeardownAsync"/>). The kill's own warning, when what it killed was still running after its wait, is
    /// the end's. After a quit, <paramref name="afterQuit"/> ends what the game left behind and returns it, as leftRunning lists it.
    /// </summary>
    private async Task<RunEnd> StopGameAsync(
        BridgeConnection? connection,
        Process watched,
        Func<Task<string?>> kill,
        Func<Task<IReadOnlyList<string>>> afterQuit
    )
    {
        if (await IsSilentAsync(connection))
        {
            string? stillRunning = await kill();
            return new RunEnd(Killed: true, stillRunning, GameKillReason.Silent, LeftRunning: [], QuitMs: null);
        }

        Task<long>? closedAt = connection is null ? null : ClosedAtAsync(connection.Closed);
        long askedAt = Stopwatch.GetTimestamp();
        QuitRequest request = await AskToQuitAsync(connection);
        long answeredAt = request == QuitRequest.Acknowledged ? Stopwatch.GetTimestamp() : askedAt;
        using LoadDeadline grace = registry.Clock.Start(CurrentExitGrace);
        if (
            !await ProcessExit.WaitUntilGoneAsync(watched, grace)
            && await WaitOutTeardownAsync(request, connection, watched, GraceSpent.Of(grace)) is { } spent
        )
        {
            string reason = await DescribeGraceKillAsync(request, spent, watched, () => ClosedSince(closedAt, answeredAt));
            // The state is sampled for a second, and a game that exits meanwhile quit on its own: it is not killed. HasExited reads
            // the exit code, set before the handle is signalled, so a game whose code is set but whose signal lags counts as exited.
            if (!watched.HasExited)
            {
                Log.ExitGraceExpired(_logger, ProjectDir, spent.Text);
                string? stillRunning = await kill();
                return new RunEnd(Killed: true, stillRunning, reason, LeftRunning: [], QuitMs: null);
            }
        }

        int quitMs = (int)Math.Round(Stopwatch.GetElapsedTime(askedAt).TotalMilliseconds);
        return new RunEnd(Killed: false, Warning: null, KillReason: null, await afterQuit(), quitMs);
    }

    /// <summary>
    /// After an exit grace that ended with the game still running: a game that acknowledged the quit and whose bridge connection
    /// has closed is in the engine's own teardown, so it gets <see cref="SessionRegistry.TeardownGrace"/> more, unless the run is
    /// recording, whose grace was the one spent.
    /// </summary>
    /// <param name="request">What became of the quit request.</param>
    /// <param name="connection">The game's bridge connection, whose own close is tested; null without one.</param>
    /// <param name="watched">The game's process.</param>
    /// <param name="spent">The exit grace as it ended.</param>
    /// <returns>Null when the game was gone within the teardown grace; else the grace spent in all, both phases summed.</returns>
    private async Task<GraceSpent?> WaitOutTeardownAsync(QuitRequest request, BridgeConnection? connection, Process watched, GraceSpent spent)
    {
        if (request != QuitRequest.Acknowledged || connection is not { Closed.IsCompleted: true } || _recording is { Outcome: null })
        {
            return spent;
        }

        using LoadDeadline teardown = registry.Clock.Start(registry.TeardownGrace);
        return await ProcessExit.WaitUntilGoneAsync(watched, teardown) ? null : spent.Plus(GraceSpent.Of(teardown));
    }

    /// <summary>
    /// Gives a game that quit (<paramref name="quit"/>: it ran and was not killed), while a watchFinal frame is awaited from it
    /// (<see cref="BridgeConnection.AwaitsWatchFinal"/>), up to <see cref="BridgeCloseGrace"/> on <paramref name="clock"/>,
    /// load-adjusted, for its bridge connection to close, so the read loop hands on the frame the bridge sent in its exit work
    /// before the connection is disposed; returns at once otherwise. The system closes a gone game's socket, so a wait normally
    /// returns at once too.
    /// </summary>
    internal static async Task AwaitBridgeCloseAsync(BridgeConnection connection, bool quit, LoadClock clock, ILogger logger, string projectDir)
    {
        if (!quit || !connection.AwaitsWatchFinal)
        {
            return;
        }

        using LoadDeadline grace = clock.Start(BridgeCloseGrace);
        try
        {
            await connection.Closed.WaitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            Log.BridgeCloseGraceExpired(logger, projectDir, BridgeCloseGrace.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Sets the handler for the live run's next watchFinal frame (<see cref="BridgeConnection.OnWatchFinal"/>), the watch's
    /// timeline the bridge sends as the game quits; false when the run has no open connection.
    /// </summary>
    internal bool OnWatchFinal(Action<JsonObject> handler)
    {
        if (_run is not { IsRunning: true, Connection: { IsOpen: true } live })
        {
            return false;
        }

        live.OnWatchFinal(handler);
        return true;
    }

    /// <summary>The attached game's bridge connection's read loop, which completes when the connection closes; null without one.</summary>
    internal Task? AttachedBridgeClosed => _attached?.Closed;

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
        // The game quit, so the wrapper still running is no kill the stop reports: its wait is only logged.
        _ = await KillAndWaitAsync(run.Process, game: null);
        return leftRunning;
    }

    /// <summary>
    /// Ends a run the way stop_project does, without releasing the folder: a silent game is killed at once, one that answers
    /// is asked to quit and killed after the grace; then its connection is closed.
    /// </summary>
    /// <returns>
    /// Whether the game had to be killed and why, the processes it left running when it quit, and the warnings when a debugger
    /// was attached to it as it was ended and when what the stop killed was still running after its kill wait, in that order.
    /// </returns>
    private async Task<RunEnd> EndRunAsync(GodotRun run, bool discard)
    {
        bool wasRunning = run.IsRunning;
        string? warning = WarnIfDebugged(wasRunning);
        RunEnd ended = (wasRunning, discard) switch
        {
            (false, _) => new RunEnd(Killed: false, Warning: null, KillReason: null, LeftRunning: [], QuitMs: null),
            (true, true) => new RunEnd(Killed: true, await KillAsync(run), GameKillReason.Discarded, LeftRunning: [], QuitMs: null),
            (true, false) => await StopRunningAsync(run),
        };
        if (run.Connection is not null)
        {
            await AwaitBridgeCloseAsync(run.Connection, quit: wasRunning && !ended.Killed, registry.Clock, _logger, ProjectDir);
            await run.Connection.DisposeAsync();
            run.Connection = null;
        }

        return ended with
        {
            Warning = JoinWarnings(warning, ended.Warning),
        };
    }

    /// <summary>
    /// Stops reading a replaced run's output, so a game the kill could not end, or output still in flight, never lands in the
    /// buffers the new run continues, and closes its output file: no line reaches the file after the cancel that the rings would
    /// have kept. A stream that is not being read is logged and skipped.
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

        run.OutputLog?.Dispose();
    }

    /// <summary>
    /// Kills a run: its game's own process when the run holds a handle on it, and its console wrapper, each with everything it
    /// started, and waits for both (<see cref="KillAndWaitAsync"/>).
    /// </summary>
    /// <returns>The warning when either was still running after the kill wait; null when both are gone.</returns>
    private Task<string?> KillAsync(GodotRun run)
    {
        if (ReferenceEquals(run, _run) && _recording is { } recording)
        {
            recording.Killed = true;
        }

        run.MarkKilled();
        return KillAndWaitAsync(run.Process, run.Game);
    }

    /// <summary>
    /// Kills <paramref name="game"/> and <paramref name="wrapper"/>, each that is given, and waits for both on one deadline of
    /// load-adjusted time on the registry's launch clock (<see cref="RunKill.KillAndWaitAsync"/>).
    /// </summary>
    private Task<string?> KillAndWaitAsync(Process? wrapper, Process? game) =>
        RunKill.KillAndWaitAsync(wrapper, game, registry.LaunchClock, _logger, ProjectDir);

    /// <summary>A fresh source for <see cref="ExitHandled"/>, one per run, whose awaiters never run inside the exit's handler.</summary>
    private static TaskCompletionSource NewExitHandled() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        TaskCompletionSource? handled = null;
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

            handled = _exitHandled;
            Snapshots.Clear();
            registry.Captures.End(Name, CaptureStore.EndedByExit);
            await FinishRecordingAsync(run);
            await KeepRealtimeResultAsync();
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
            // Set only for the session's current run, so a replaced run's exit never reaches the folder.
            try
            {
                DeleteScratchUserDataAfterExit(current: handled is not null, run);
                ReleaseGateAfterExit();
            }
            finally
            {
                // Completed whatever the cleanup met, so whoever awaits the exit's handling is never left waiting.
                handled?.TrySetResult();
            }
        }

        // A grandchild still holding the game's pipes keeps its output from ending, so the file is closed after the drain grace
        // without holding up the exit. The output's own end closes it first when it comes, and a second close does nothing.
        _ = CloseOutputLogAsync(run);
    }

    /// <summary>
    /// Lets go of the gate an exit's handler took. The handler is queued, so it can take the gate after a replace by name has
    /// retired the session and let the gate go before disposing it: a gate disposed meanwhile has nothing left to let go of.
    /// </summary>
    private void ReleaseGateAfterExit()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The session was disposed while the handler held the gate; nothing else waits on it.
        }
    }
}
