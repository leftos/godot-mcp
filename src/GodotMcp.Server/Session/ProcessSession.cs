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
    private ToolProcess.Running? _process;
    private ProcessLaunch? _launch;

    /// <summary>
    /// The launch a process session keeps, so a restart runs the same prep again, starts the same command and waits on the
    /// same ready line; the prep is null when the profile declares none.
    /// </summary>
    private sealed record ProcessLaunch(
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

    /// <summary>Whether the process session's program is running; false for a session that has not started or has stopped.</summary>
    private bool ProcessRunning
    {
        get
        {
            try
            {
                return _process is { } process && !process.Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
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
        ToolProcess.Running process;
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
            process = ToolProcess.Start(request, Logger);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new SessionException($"The process could not be started: {e.Message}", e);
        }

        // The session owns the process before anything can watch it, so a failure from here leaves a session stop_project can end.
        _process = process;
        ProcessLaunch launch = new(key, profile, [.. command], ready, prep);
        _launch = launch;
        ProcessId = process.Process.Id;
        ProcessProgram = ProgramName(command[0]);
        _ = CloseJobWhenExitedAsync(process);
        ReadyResult? met = ready is null ? null : await WaitForReadyAsync(launch, stdoutStart, stderrStart, cancellationToken);
        _pending = false;
        return (process.Process.Id, log, met);
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

            if (!ProcessRunning)
            {
                throw await RefuseReadyAsync(() => ExitedBeforeReady(launch));
            }

            if (deadline.Expired)
            {
                throw await RefuseReadyAsync(() => ReadyTimedOut(launch));
            }

            try
            {
                await Task.Delay(OutputWait.Poll, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.Expired)
            {
                throw await RefuseReadyAsync(() => ReadyTimedOut(launch));
            }
        }
    }

    /// <summary>Ends a process that failed its ready wait: closes its job, waits for it and finishes its output, then returns the refusal.</summary>
    private async Task<SessionException> RefuseReadyAsync(Func<SessionException> refusal)
    {
        if (_process is { } process)
        {
            process.Tree.CloseJob();
            await WaitGoneAsync(process.Process);
            await process.FinishOutputAsync();
        }

        return refusal();
    }

    /// <summary>The refusal of a process that exited before its ready line: its own exit code and its last stderr lines.</summary>
    private SessionException ExitedBeforeReady(ProcessLaunch launch)
    {
        int code = _process is { } process ? ExitCodeOf(process.Process) ?? 0 : 0;
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
    /// Ends a process session at once: closes its job, which ends the process and everything it started, and reports its
    /// exit code only when it had already exited.
    /// </summary>
    /// <exception cref="SessionException">No process was started in the session.</exception>
    private async Task<ProcessStopResult> StopProcessAsync()
    {
        ToolProcess.Running process =
            _process ?? throw new SessionException("No process has been started in this session, so there is nothing to stop.");
        bool alreadyExited = !ProcessRunning;
        int? exitCode = alreadyExited ? ExitCodeOf(process.Process) : null;
        process.Tree.CloseJob();
        await WaitGoneAsync(process.Process);
        await process.FinishOutputAsync();
        return new ProcessStopResult(Name, ProjectDir, "process", exitCode, Killed: !alreadyExited, AlreadyExited: alreadyExited);
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
                ToolProcess.Running previous = _process ?? throw new SessionException(NothingToRestart);
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
                    };
                }
                finally
                {
                    // A start that failed before assigning leaves the old handle in place, so the session stays stoppable.
                    if (!ReferenceEquals(_process, previous))
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

    /// <summary>Ends a process session at the server's exit: closes its job and waits for the process to go.</summary>
    private async Task ShutdownProcessAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        process.Tree.CloseJob();
        await WaitGoneAsync(process.Process);
    }

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
            ExitCode = ProcessRunning || _process is null ? null : ExitCodeOf(_process.Process),
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

    /// <summary>Lets go of the process session's process and job, closing the job first so its tree ends.</summary>
    private void DisposeProcess()
    {
        ToolProcess.Running? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        process.Tree.CloseJob();
        process.Dispose();
    }

    /// <summary>Closes the job when the process ends on its own, so the processes it started end with it.</summary>
    private static async Task CloseJobWhenExitedAsync(ToolProcess.Running process)
    {
        try
        {
            await process.Process.WaitForExitAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            // The process was never started or was disposed as the session let go of it; the job still closes below.
        }
        finally
        {
            process.Tree.CloseJob();
        }
    }

    /// <summary>Waits for a process to go, at most <see cref="KillWait"/>; the job's close has already asked it to.</summary>
    private static async Task WaitGoneAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(KillWait);
        }
        catch (TimeoutException)
        {
            // The job was closed; the process handle's exit signal may lag behind the tree's end.
        }
        catch (InvalidOperationException)
        {
            // The process was never started or was already disposed.
        }
    }

    /// <summary>A program's name without its folder or extension, as a refusal names it.</summary>
    internal static string ProgramName(string program) => Path.GetFileNameWithoutExtension(program);
}
