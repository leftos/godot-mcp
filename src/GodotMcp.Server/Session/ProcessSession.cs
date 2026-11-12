using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using GodotMcp.Server.Tools;

namespace GodotMcp.Server.Session;

/// <summary>
/// The process side of a session: a program a project declares in its godot-mcp.json <c>processes</c> section, started by
/// run_process and kept beside the games until stop_project. It has no bridge and no game; its stdout and stderr fill two
/// ring buffers, its output also goes to <c>.godot/godot-mcp/process-&lt;session&gt;.log</c>, and its tree lives in a job
/// with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so closing the job ends it and everything it started.
/// </summary>
internal sealed partial class GodotSession
{
    private readonly OutputBuffer _processStdout = new(GodotRun.OutputCapacity);
    private readonly OutputBuffer _processStderr = new(GodotRun.OutputCapacity);
    private ProcessRun? _process;
    private ProcessLaunch? _launch;

    /// <summary>
    /// One start of a process session: the started program and its one end, kept together so a restart replaces both at once
    /// while the replaced start's exit watcher still holds its own.
    /// </summary>
    private sealed record ProcessRun(ToolProcess.Running Running, ProcessEnd End)
    {
        private int _endedByStop;

        /// <summary>Whether a stop ended this start's program, so a ready wait it cut short says so rather than "exited".</summary>
        public bool EndedByStop => Volatile.Read(ref _endedByStop) == 1;

        /// <summary>Marks the program as ended by a stop, before the stop starts its end.</summary>
        public void MarkEndedByStop() => Volatile.Write(ref _endedByStop, 1);
    }

    /// <summary>
    /// The launch a process session keeps, so a restart runs the same prep again, starts the same command and waits on the
    /// same ready line; the prep is null when the profile declares none. run_process hands one to the first start.
    /// </summary>
    internal sealed record ProcessLaunch(
        string Key,
        ProcessProfile Profile,
        IReadOnlyList<string> Command,
        ReadyProfile? Ready,
        Func<CancellationToken, Task<ProcessPrepResult>>? Prep
    );

    /// <summary>
    /// The program a process session runs, as the refusals name it: its file name without the extension, e.g. "dotnet"; null
    /// until the session starts.
    /// </summary>
    internal string? ProcessProgram { get; private set; }

    /// <summary>Whether the session has started its process once, so its stored launch can be started again.</summary>
    internal bool HasProcessLaunch => _launch is not null;

    /// <summary>
    /// Whether the process session is live: it has started and its end has not completed, so some process of its job may not
    /// yet be gone or its output not yet drained; false for a session that has not started. It never blocks.
    /// </summary>
    private bool ProcessRunning => _process is { } run && !run.End.IsCompleted;

    /// <summary>
    /// Whether the program itself has exited, by its exit code: what its exit code, "exited before ready" and a stop's
    /// <c>alreadyExited</c> say. False for a session that has not started.
    /// </summary>
    private bool ProgramExited
    {
        get
        {
            try
            {
                return _process is { } run && run.Running.Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // The process was disposed as the session let go of it: it runs no more.
                return true;
            }
        }
    }

    /// <summary>
    /// What every runtime tool but the output wait, restart_project and the session tools says when it addresses a process
    /// session.
    /// </summary>
    internal static string ProcessRefusal(string session, string program) =>
        $"Session '{session}' is a process ({program}), not a Godot game: only wait_for with an output condition, "
        + "get_debug_output, restart_project, stop_project and list_sessions reach it.";

    /// <summary>What restart_project says on a process session that never started.</summary>
    internal const string NothingToRestart = "No process has been started in this session, so there is nothing to restart.";

    /// <summary>
    /// The first start of a session run_process reserved: the launch's prep, then <see cref="StartProcess"/>. The session, starting
    /// since its reservation, stops starting once this has returned or failed; a start that fails lets go of the session
    /// (<see cref="AbandonFirstStartAsync"/>).
    /// </summary>
    /// <exception cref="SessionException">The prep failed, or the start did (see <see cref="StartProcess"/>).</exception>
    internal async Task<(ProcessPrepResult? Prep, int ProcessId, string Log, ReadyResult? Ready)> StartFirstProcessAsync(
        ProcessLaunch launch,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ProcessPrepResult? prepared = await RunPrepAsync(launch, cancellationToken);
            (int processId, string log, ReadyResult? ready) = await StartProcess(
                launch.Key,
                launch.Profile,
                launch.Command,
                launch.Ready,
                cancellationToken,
                launch.Prep
            );
            return (prepared, processId, log, ready);
        }
        catch
        {
            await AbandonFirstStartAsync();
            throw;
        }
        finally
        {
            _pending = false;
        }
    }

    /// <summary>
    /// Lets go of a session whose first start failed: drops it from the registry, so no later call finds it, then takes its gate,
    /// so a stop that holds the gate or waits on it finishes first, and lets go of the program under it. The gates are never
    /// disposed here, so a stop that found the session before it was dropped still takes the gate and is told there is nothing
    /// to stop.
    /// </summary>
    private async Task AbandonFirstStartAsync()
    {
        registry.Forget(this);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            DisposeProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts the process a run_process call declares: <paramref name="command"/> from the project folder, in the profile's
    /// cwd with its environment, with no ceiling or stall kill, its output buffered and logged, and its tree in a job that
    /// ends it when the job closes. When the call declares a ready line, the start waits for it before returning; a process
    /// that cannot join the job, exits first or misses the line refuses the start and is ended.
    /// </summary>
    /// <param name="key">The process's name as the project's godot-mcp.json declares it.</param>
    /// <param name="profile">The process's declaration.</param>
    /// <param name="command">The program resolved and its arguments, as the process is started.</param>
    /// <param name="ready">The line to wait for before returning; null for a start that returns at once.</param>
    /// <param name="cancellationToken">Refuses the start when the call was cancelled.</param>
    /// <param name="prep">
    /// The prep the profile declares, which the caller runs before this start and which a restart runs again first; null when
    /// it declares none.
    /// </param>
    /// <exception cref="SessionException">
    /// The program could not be started, the process exited before ready, or its ready timeout passed.
    /// </exception>
    internal async Task<(int ProcessId, string Log, ReadyResult? Ready)> StartProcess(
        string key,
        ProcessProfile profile,
        IReadOnlyList<string> command,
        ReadyProfile? ready,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<ProcessPrepResult>>? prep = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The ready wait may scan only what the new process prints; the buffers carry a restart's earlier lines and numbering.
        long stdoutStart = _processStdout.TotalLines;
        long stderrStart = _processStderr.TotalLines;
        string log;
        ProcessRun run;
        try
        {
            log = Path.Combine(ProjectPrep.LogFolder(ProjectDir), $"process-{Name}.log");
            ToolProcessRequest request = new(command[0], [.. command.Skip(1)], profile.Cwd ?? ProjectDir, log, Timeout.InfiniteTimeSpan)
            {
                SetVariables = profile.Env,
                FlushEachLine = true,
                StallLimit = TimeSpan.MaxValue,
                KillOnClose = true,
                StandardOutputLine = _processStdout.Add,
                StandardErrorLine = _processStderr.Add,
            };
            // A shutdown that began during the prep or a restart's stop refuses the start before any program starts.
            registry.RefuseStartAtShutdown();
            run = StartRun(request);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new SessionException($"The process could not be started: {e.Message}", e);
        }

        // The session owns the process before anything can watch it, so a failure from here leaves a session stop_project can end.
        _process = run;
        ProcessLaunch launch = new(key, profile, [.. command], ready, prep);
        _launch = launch;
        int processId = run.Running.Process.Id;
        ProcessId = processId;
        ProcessProgram = ProgramName(command[0]);
        _ = EndWhenExitedAsync(run);
        ReadyResult? met = ready is null ? null : await WaitForReadyAsync(launch, stdoutStart, stderrStart, cancellationToken);
        return (processId, log, met);
    }

    /// <summary>Starts the program with its end; a program whose end cannot be made is let go of, which closes its job.</summary>
    private ProcessRun StartRun(ToolProcessRequest request)
    {
        ToolProcess.Running process = ToolProcess.Start(request, Logger);
        try
        {
            return new ProcessRun(process, new ProcessEnd(process, registry.LaunchClock, ProjectDir));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits for the started process to print a line the launch's ready matches, scanning only the lines above the marks taken
    /// before it started so a restart never matches the replaced process's line, stdout before stderr. A process that exits
    /// first, a pattern that takes over a second to match and a timeout each end the process's tree and refuse the start.
    /// </summary>
    private async Task<ReadyResult> WaitForReadyAsync(ProcessLaunch launch, long stdoutStart, long stderrStart, CancellationToken cancellationToken)
    {
        ReadyProfile ready = launch.Ready!;
        long started = Stopwatch.GetTimestamp();
        using LoadDeadline deadline = LoadClock.Shared.Start(TimeSpan.FromMilliseconds(ready.TimeoutMs), cancellationToken);
        while (true)
        {
            (OutputPage stdout, OutputPage stderr, _, _) = GetProcessRawOutput();
            (OutputMatch? match, string? error) = OutputWait.Scan(stdout, stderr, "any", ready.Regex, stdoutStart, stderrStart);
            if (error is not null)
            {
                throw await RefuseReadyAsync(() =>
                    new SessionException($"The ready pattern '{ready.Output}' took over 1 s to match a line; simplify the pattern.")
                );
            }

            if (match is { } found)
            {
                return new ReadyResult(
                    OutputBuffer.Cut(found.Line, OutputBuffer.MaxLineLength),
                    found.Stream,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
                );
            }

            if (registry.ShutdownBegun)
            {
                throw await RefuseReadyAsync(() => new SessionException(SessionRegistry.ShuttingDown));
            }

            if (ProgramExited)
            {
                throw await RefuseReadyAsync(() => ExitRefusal(launch));
            }

            if (deadline.Expired)
            {
                throw await RefuseReadyAsync(() => ReadyTimedOut(launch));
            }

            await PollReadyAsync(launch, deadline);
        }
    }

    /// <summary>
    /// One poll's pause of the ready wait: a deadline that expires in it refuses the start, and a cancelled call ends the
    /// process, waiting for its end, before the cancellation goes on.
    /// </summary>
    private async Task PollReadyAsync(ProcessLaunch launch, LoadDeadline deadline)
    {
        try
        {
            await Task.Delay(OutputWait.Poll, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.Expired)
        {
            throw await RefuseReadyAsync(() => ReadyTimedOut(launch));
        }
        catch (OperationCanceledException)
        {
            await EndProcessAsync();
            throw;
        }
    }

    /// <summary>Ends the session's process and waits for its end; the warning naming what was still running, or null.</summary>
    private async Task<string?> EndProcessAsync() => _process is { } run ? await run.End.Start() : null;

    /// <summary>
    /// Ends a process that failed its ready wait and waits for its end, then returns the refusal, with the end's warning in a
    /// paragraph of its own, never read as one more stderr line, when a process was still running past it.
    /// </summary>
    private async Task<SessionException> RefuseReadyAsync(Func<SessionException> refusal)
    {
        string? warning = await EndProcessAsync();
        SessionException refused = refusal();
        return warning is null ? refused : new SessionException($"{refused.Message}\n\n{warning}");
    }

    /// <summary>
    /// The refusal of a ready wait whose program exited: one a stop ended says so, one the server's shutdown ended says the
    /// server is shutting down, and one that exited on its own gives its exit code and last stderr lines.
    /// </summary>
    private SessionException ExitRefusal(ProcessLaunch launch)
    {
        if (_process is { EndedByStop: true })
        {
            return new SessionException($"The process '{launch.Key}' was stopped by stop_project before a line matched '{launch.Ready!.Output}'.");
        }

        return registry.ShutdownBegun ? new SessionException(SessionRegistry.ShuttingDown) : ExitedBeforeReady(launch);
    }

    /// <summary>The refusal of a process that exited before its ready line: its own exit code and its last stderr lines.</summary>
    private SessionException ExitedBeforeReady(ProcessLaunch launch)
    {
        int code = _process is { } run ? ExitCodeOf(run.Running.Process) ?? 0 : 0;
        string lines = Lines(_processStderr);
        return new SessionException(
            $"The process '{launch.Key}' exited (code {code}) before a line matched '{launch.Ready!.Output}'; " + $"its last stderr lines:\n{lines}"
        );
    }

    /// <summary>
    /// The refusal of a process that printed no ready line in time: the pattern, the load-adjusted seconds and both
    /// streams' last lines.
    /// </summary>
    private SessionException ReadyTimedOut(ProcessLaunch launch)
    {
        ReadyProfile ready = launch.Ready!;
        string seconds = TimeSpan.FromMilliseconds(ready.TimeoutMs).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return new SessionException(
            $"The process '{launch.Key}' printed no line matching '{ready.Output}' within {seconds} s of load-adjusted time, so it was "
                + $"stopped; its last output lines:\nstdout:\n{Lines(_processStdout)}\nstderr:\n{Lines(_processStderr)}"
        );
    }

    /// <summary>A stream's last 20 held lines, or "(none)", as a ready refusal quotes them.</summary>
    private static string Lines(OutputBuffer buffer)
    {
        List<string> lines = buffer.Tail(HangProbe.StderrLineCount);
        return lines.Count == 0 ? "(none)" : string.Join('\n', lines);
    }

    /// <summary>
    /// Ends a process session at once: ends its job, which ends the process and everything it started, and waits for that end
    /// (or joins the one its own exit started), then reports its exit code only when it had already exited, and a warning naming
    /// the processes still running when the end's wait ran out.
    /// </summary>
    /// <exception cref="SessionException">No process was started in the session.</exception>
    private async Task<ProcessStopResult> StopProcessAsync()
    {
        ProcessRun run = _process ?? throw new SessionException("No process has been started in this session, so there is nothing to stop.");
        bool alreadyExited = ProgramExited;
        int? exitCode = alreadyExited ? ExitCodeOf(run.Running.Process) : null;
        run.MarkEndedByStop();
        string? warning = await run.End.Start();
        return new ProcessStopResult(Name, ProjectDir, "process", exitCode, Killed: !alreadyExited, AlreadyExited: alreadyExited)
        {
            Warning = warning,
        };
    }

    /// <summary>
    /// Under the session's gate: stops the process it runs (or the one that already ended), runs the launch's prep again,
    /// starts its stored command, waiting on the same ready line, and lets go of the old handle once the new one is in place. A
    /// start that fails leaves the old handle, so the session stays stoppable; a prep or ready refusal leaves it stopped.
    /// </summary>
    /// <exception cref="SessionException">The session never started a process, or the new process failed its ready wait.</exception>
    internal async Task<ProcessRestartResult> RestartProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ProcessLaunch launch = _launch ?? throw new SessionException(NothingToRestart);
                int previousProcessId = ProcessId ?? throw new SessionException(NothingToRestart);
                ToolProcess.Running previous = _process?.Running ?? throw new SessionException(NothingToRestart);
                // A restart cancelled by now must not stop a healthy program.
                cancellationToken.ThrowIfCancellationRequested();
                ProcessStopResult stopped = await StopProcessAsync();
                previous.CloseLog();
                try
                {
                    ProcessPrepResult? prepared = await RunPrepAsync(launch, cancellationToken);
                    (int processId, string log, ReadyResult? ready) = await StartProcess(
                        launch.Key,
                        launch.Profile,
                        launch.Command,
                        launch.Ready,
                        cancellationToken,
                        launch.Prep
                    );
                    return new ProcessRestartResult(
                        Name,
                        ProjectDir,
                        launch.Key,
                        processId,
                        previousProcessId,
                        stopped.ExitCode,
                        launch.Command,
                        log,
                        ready
                    )
                    {
                        Prep = prepared,
                        Warning = stopped.Warning,
                    };
                }
                finally
                {
                    // A start that failed before assigning leaves the old handle in place, so the session stays stoppable.
                    if (!ReferenceEquals(_process?.Running, previous))
                    {
                        previous.Dispose();
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _pending = false;
        }
    }

    /// <summary>A launch's prep run again before its restart, under the folder's prep lock; null when the profile declares none.</summary>
    private static async Task<ProcessPrepResult?> RunPrepAsync(ProcessLaunch launch, CancellationToken cancellationToken) =>
        launch.Prep is null ? null : await launch.Prep(cancellationToken);

    /// <summary>
    /// Ends a process session at the server's exit and waits for its end, which logs a warning naming any process still running
    /// past its wait.
    /// </summary>
    private async Task ShutdownProcessAsync() => await EndProcessAsync();

    /// <summary>The process session's output pages, as a run's <see cref="GetDebugOutput"/> reads its run's.</summary>
    private DebugOutput GetProcessDebugOutput(int limit, long? before)
    {
        OutputPage stdout = _processStdout.Page(limit, before);
        OutputPage stderr = _processStderr.Page(limit, before);
        return new DebugOutput
        {
            Session = Name,
            ProjectPath = ProjectDir,
            Running = ProcessRunning,
            ExitCode = _process is { } run && ProgramExited ? ExitCodeOf(run.Running.Process) : null,
            Stdout = stdout.Lines,
            Stderr = stderr.Lines,
            StdoutFirstLine = stdout.FirstLine,
            StderrFirstLine = stderr.FirstLine,
            StdoutTotalLines = _processStdout.TotalLines,
            StderrTotalLines = _processStderr.TotalLines,
        };
    }

    /// <summary>The process session's uncut lines and their totals, as an output wait reads them.</summary>
    private (OutputPage Stdout, OutputPage Stderr, long StdoutTotalLines, long StderrTotalLines) GetProcessRawOutput()
    {
        // Read the totals before the pages, as a run's GetRawOutput does.
        long stdoutTotal = _processStdout.TotalLines;
        long stderrTotal = _processStderr.TotalLines;
        return (_processStdout.Raw(), _processStderr.Raw(), stdoutTotal, stderrTotal);
    }

    /// <summary>
    /// Lets go of the process session's process and job, starting its end first, so its tree ends; the end goes on without the
    /// caller, which may hold the registry's lock, and logs a process still running past its wait.
    /// </summary>
    private void DisposeProcess()
    {
        ProcessRun? run = _process;
        _process = null;
        if (run is null)
        {
            return;
        }

        _ = run.End.Start();
        run.Running.Dispose();
    }

    /// <summary>Starts the end when the program exits on its own, so the processes it started end with it.</summary>
    private static async Task EndWhenExitedAsync(ProcessRun run)
    {
        try
        {
            await run.Running.Process.WaitForExitAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            // The process was never started or was disposed as the session let go of it, which started its end.
        }
        finally
        {
            _ = run.End.Start();
        }
    }

    /// <summary>A program's name without its folder or extension, as a refusal names it.</summary>
    internal static string ProgramName(string program) => Path.GetFileNameWithoutExtension(program);
}
