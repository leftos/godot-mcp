using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>
    /// The program a process session runs, as the refusals name it: its file name without the extension, e.g. "dotnet"; null
    /// until the session starts.
    /// </summary>
    internal string? ProcessProgram { get; private set; }

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

    /// <summary>What every runtime tool but the output wait says when it addresses a process session.</summary>
    internal static string ProcessRefusal(string session, string program) =>
        $"Session '{session}' is a process ({program}), not a Godot game: only wait_for with an output condition, "
        + "get_debug_output, stop_project and list_sessions reach it.";

    /// <summary>What restart_project says on a process session.</summary>
    internal static string RestartRefusal(string session, string program) =>
        $"Session '{session}' is a process ({program}): stop it with stop_project and start it again with run_process; "
        + "restart_project restarts Godot runs.";

    /// <summary>
    /// Starts the process a run_process call declares: <paramref name="command"/> from the project folder, in the profile's
    /// cwd with its environment, with no ceiling or stall kill, its output buffered and logged, and its tree in a job that
    /// ends it when the job closes. A process that cannot join the job refuses the start.
    /// </summary>
    /// <param name="profile">The process's declaration.</param>
    /// <param name="command">The program resolved and its arguments, as the process is started.</param>
    /// <param name="cancellationToken">Refuses the start when the call was cancelled.</param>
    /// <exception cref="SessionException">The session was already stopped.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started, or the kill-on-close job made.</exception>
    /// <exception cref="IOException">The log could not be opened.</exception>
    internal (int ProcessId, string Log) StartProcess(ProcessProfile profile, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string log = Path.Combine(ProjectPrep.LogFolder(ProjectDir), $"process-{Name}.log");
        ToolProcessRequest request = new(command[0], [.. command.Skip(1)], profile.Cwd ?? ProjectDir, log, Timeout.InfiniteTimeSpan)
        {
            SetVariables = profile.Env,
            FlushEachLine = true,
            StallLimit = TimeSpan.MaxValue,
            KillOnClose = true,
            StandardOutputLine = _processStdout.Add,
            StandardErrorLine = _processStderr.Add,
        };
        ToolProcess.Running process = ToolProcess.Start(request, Logger);
        // The session owns the process before anything can watch it, so a failure from here leaves a session stop_project can end.
        _process = process;
        ProcessId = process.Process.Id;
        ProcessProgram = ProgramName(command[0]);
        _pending = false;
        _ = CloseJobWhenExitedAsync(process);
        return (process.Process.Id, log);
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
