using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// Runs git for the server, on its own closed stdin and isolated from any git hook the server runs under. A git that has
/// not finished within <see cref="Ceiling"/> of load-adjusted time is stopped with its process tree and counts as a failure.
/// </summary>
internal static class GitRunner
{
    /// <summary>How long git may run, in load-adjusted time.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private const int PollMilliseconds = 250;
    private const int KillWaitMilliseconds = 5000;

    // Set when the server itself runs under a git hook; they would point git at another repository than the project's.
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>
    /// Runs git in <paramref name="workingDirectory"/>; its stdout, untrimmed, or null when git fails, cannot start or passes
    /// its ceiling.
    /// </summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <param name="arguments">git's arguments.</param>
    public static string? Run(string workingDirectory, ILogger logger, params string[] arguments) =>
        Run("git", workingDirectory, logger, clock: null, arguments);

    /// <summary>Runs <paramref name="fileName"/> as git: the seam tests use to stand in for a git that hangs.</summary>
    /// <param name="fileName">The program to run.</param>
    /// <param name="workingDirectory">Where it runs.</param>
    /// <param name="logger">Where a missing or stopped program is reported.</param>
    /// <param name="clock">The clock <see cref="Ceiling"/> runs on; null is <see cref="LoadClock.Shared"/>.</param>
    /// <param name="arguments">Its arguments.</param>
    internal static string? Run(string fileName, string workingDirectory, ILogger logger, LoadClock? clock, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string name in InheritedGitVariables)
        {
            startInfo.Environment.Remove(name);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            using LoadDeadline deadline = (clock ?? LoadClock.Shared).Start(Ceiling);
            ChildProcesses.Start(process);
            process.StandardInput.Close();
            // Both streams are drained so a chatty git never blocks on a full pipe; outside a repository stderr only says so.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task reads = Task.WhenAll(output, process.StandardError.ReadToEndAsync());
            while (!Finished(process, reads))
            {
                if (deadline.Expired)
                {
                    Stop(process, workingDirectory, logger, deadline);
                    return null;
                }
            }

            if (reads.IsFaulted)
            {
                Log.GitOutputUnreadable(logger, reads.Exception, workingDirectory);
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Win32Exception e)
        {
            Log.GitUnavailable(logger, e, workingDirectory);
            return null;
        }
    }

    /// <summary>
    /// Whether git has exited and both its streams have ended, a failed read counting as ended; waits up to a poll interval
    /// for each. Task.WaitAny, unlike Task.Wait, does not throw for a faulted task.
    /// </summary>
    private static bool Finished(Process process, Task reads) =>
        process.WaitForExit(PollMilliseconds) && Task.WaitAny([reads], PollMilliseconds) == 0;

    private static void Stop(Process process, string workingDirectory, ILogger logger, LoadDeadline deadline)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(KillWaitMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // git exited between the last poll and the kill: there is nothing left to stop.
        }
        catch (Exception e) when (e is Win32Exception or AggregateException)
        {
            Log.ToolKillFailed(logger, e, "git");
        }

        string clause =
            deadline.Reason == DeadlineReason.Backstop
                ? $"in {LoadDeadline.Seconds(deadline.Wall)} s{deadline.BackstopClause()}"
                : deadline.CeilingClause();
        Log.GitTimedOut(logger, workingDirectory, clause);
    }
}
