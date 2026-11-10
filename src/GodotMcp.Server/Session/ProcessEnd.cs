using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Server.Session;

/// <summary>
/// The one end of a process session's start: its job ended and closed, then a wait until Windows has signalled the program and
/// every process that was in the job, on a deadline of <see cref="RunKill.Budget"/> of load-adjusted time, then its output
/// drained. Windows signals a process only after closing its handles (its current directory among them), so a session whose end
/// has completed holds no file of its folder, but for the pids its warning names. The end runs once, whoever starts it first: the
/// program's own exit, a stop, a ready refusal, the server's exit or the session letting go of it; every later start joins it.
/// It owns the handles it waits on, so disposing the session's <see cref="System.Diagnostics.Process"/> mid-wait does not pass it.
/// </summary>
/// <param name="run">The started program, its job and its output.</param>
/// <param name="clock">The clock the wait's deadline runs on.</param>
/// <param name="projectDir">The project folder the logs name.</param>
/// <exception cref="System.ComponentModel.Win32Exception">The program's handle could not be duplicated.</exception>
internal sealed class ProcessEnd(ToolProcess.Running run, LoadClock clock, string projectDir)
{
    private readonly ToolProcess.Running _run = run;
    private readonly SafeProcessHandle _main = OwnWork.DuplicateHandleOf(run.Process);
    private readonly int _mainPid = run.Process.Id;
    private readonly LoadClock _clock = clock;
    private readonly string _projectDir = projectDir;
    private readonly Lock _lock = new();
    private Task<string?>? _end;

    /// <summary>Whether the end has completed: started, waited out and drained. It never blocks.</summary>
    public bool IsCompleted => Volatile.Read(ref _end) is { IsCompleted: true };

    /// <summary>
    /// Starts the end, or joins the one already started. The job is ended before this returns, so a caller may dispose the
    /// session's process right after; the wait and the drain go on in the task.
    /// </summary>
    /// <returns>
    /// Completes once the end has: with the warning naming the processes still not gone when the deadline expired, or null when
    /// all were gone. A fault is logged and completes it with null.
    /// </returns>
    public Task<string?> Start()
    {
        lock (_lock)
        {
            if (_end is null)
            {
                Volatile.Write(ref _end, EndAsync());
            }

            return _end;
        }
    }

    private async Task<string?> EndAsync()
    {
        List<(int Pid, SafeProcessHandle Handle)> kept = [(_mainPid, _main)];
        try
        {
            OwnWork.EndedJob ended = _run.Tree.EndJob(_run.Logger, _projectDir);
            kept.AddRange(ended.Kept);
            string? warning = await WaitForTreeAsync(kept, ended.Unopened);
            await _run.FinishOutputAsync();
            return warning;
        }
        catch (Exception e)
        {
            Log.ProcessEndFailed(_run.Logger, e, _projectDir);
            return null;
        }
        finally
        {
            foreach ((_, SafeProcessHandle handle) in kept)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits for the kept processes to be gone on a fresh deadline; the warning naming those that are not, and the
    /// <paramref name="unopened"/> ones, which could not be waited for, as still running; else null.
    /// </summary>
    private async Task<string?> WaitForTreeAsync(IReadOnlyList<(int Pid, SafeProcessHandle Handle)> kept, IReadOnlyList<int> unopened)
    {
        using LoadDeadline deadline = _clock.Start(RunKill.Budget);
        IReadOnlyList<int> stillRunning = await ProcessExit.WaitUntilGoneAsync(kept, deadline);
        int[] pids = [.. stillRunning.Concat(unopened).Distinct()];
        if (pids.Length == 0)
        {
            return null;
        }

        string after = GraceSpent.Of(deadline).TextFor("end wait");
        string warning = Warning(pids, after);
        Log.ProcessEndStillRunning(_run.Logger, _projectDir, warning);
        return warning;
    }

    /// <summary>The warning naming the processes still running after the end's wait, as a stop's kill wait names a game.</summary>
    internal static string Warning(IReadOnlyList<int> pids, string after)
    {
        string listed = string.Join(", ", pids.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
        return pids.Count == 1
            ? $"The ended process (pid {listed}) was still running after {after}; the files it holds stay in use until it is gone."
            : $"The ended processes (pids {listed}) were still running after {after}; the files they hold stay in use until they are gone.";
    }
}
