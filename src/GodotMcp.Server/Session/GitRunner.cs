using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>What a git command did.</summary>
/// <param name="ExitCode">git's exit code; -1 when it could not start or was stopped at its ceiling.</param>
/// <param name="Output">Its stdout, untrimmed; empty when it could not be read to its end.</param>
/// <param name="Error">Its stderr, or why git did not run to its end or its output could not be read.</param>
/// <param name="OutputRead">Whether both streams were read to their end.</param>
internal sealed record GitResult(int ExitCode, string Output, string Error, bool OutputRead)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Every non-empty line of stderr, then of stdout.</summary>
    public IReadOnlyList<string> Lines => (Error + "\n" + Output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The first of <see cref="Lines"/>: git's own account of a failure.</summary>
    public string Message => Lines.Count > 0 ? Lines[0] : $"git exited with status {ExitCode}";

    internal static GitResult Failed(string reason) => new(-1, "", reason, OutputRead: false);
}

/// <summary>
/// Runs git for the server, on its own closed stdin and isolated from any git hook the server runs under. A git that has
/// not finished within its ceiling of load-adjusted time is stopped with its process tree and counts as a failure; a git
/// run with no ceiling (a commit, whose hooks must never be killed halfway) runs to its end. Once git has exited, its
/// output is read for at most <see cref="OutputGrace"/>: a process a hook left behind can hold the pipes open.
/// </summary>
internal static class GitRunner
{
    /// <summary>How long git may run, in load-adjusted time, when it runs with a ceiling.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>How long git's output is read for after git has exited.</summary>
    public static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private const int PollMilliseconds = 250;
    private const int KillWaitMilliseconds = 5000;

    // Set when the server itself runs under a git hook; they would point git at another repository than the project's.
    private static readonly string[] InheritedGitVariables = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR"];

    /// <summary>
    /// Runs git in <paramref name="workingDirectory"/> with <see cref="Ceiling"/>; its stdout, untrimmed, or null when git
    /// fails, cannot start, passes its ceiling or its output cannot be read.
    /// </summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <param name="arguments">git's arguments.</param>
    public static string? Run(string workingDirectory, ILogger logger, params string[] arguments)
    {
        GitResult result = Run("git", workingDirectory, logger, clock: null, Ceiling, arguments);
        return result.Succeeded && result.OutputRead ? result.Output : null;
    }

    /// <summary>Runs git in <paramref name="workingDirectory"/> and returns its exit code, stdout and stderr.</summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="logger">Where a missing or stopped git is reported.</param>
    /// <param name="ceiling">How long git may run in load-adjusted time, or null to let it run to its end.</param>
    /// <param name="arguments">git's arguments.</param>
    public static GitResult RunForResult(string workingDirectory, ILogger logger, TimeSpan? ceiling, IReadOnlyList<string> arguments) =>
        Run("git", workingDirectory, logger, clock: null, ceiling, arguments);

    /// <summary>Runs <paramref name="fileName"/> as git: the seam tests use to stand in for a git that hangs.</summary>
    /// <param name="fileName">The program to run.</param>
    /// <param name="workingDirectory">Where it runs.</param>
    /// <param name="logger">Where a missing or stopped program is reported.</param>
    /// <param name="clock">The clock the ceiling runs on; null is <see cref="LoadClock.Shared"/>.</param>
    /// <param name="ceiling">How long it may run in load-adjusted time, or null to let it run to its end.</param>
    /// <param name="arguments">Its arguments.</param>
    internal static GitResult Run(
        string fileName,
        string workingDirectory,
        ILogger logger,
        LoadClock? clock,
        TimeSpan? ceiling,
        IReadOnlyList<string> arguments
    )
    {
        try
        {
            using Process process = new() { StartInfo = CreateStartInfo(fileName, workingDirectory, arguments) };
            using LoadDeadline? deadline = ceiling is { } limit ? (clock ?? LoadClock.Shared).Start(limit) : null;
            ChildProcesses.Start(process);
            process.StandardInput.Close();
            // Both streams are drained so a chatty git never blocks on a full pipe; outside a repository stderr only says so.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            while (!process.WaitForExit(PollMilliseconds))
            {
                if (deadline is { Expired: true })
                {
                    Stop(process, workingDirectory, logger, deadline);
                    return GitResult.Failed($"git {string.Join(' ', arguments)} passed its ceiling and was stopped");
                }
            }

            return Collect(process.ExitCode, output, error, workingDirectory, logger);
        }
        catch (Win32Exception e)
        {
            Log.GitUnavailable(logger, e, workingDirectory);
            return GitResult.Failed($"git could not run: {e.Message}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string workingDirectory, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

        return startInfo;
    }

    // Once git has exited its streams get OutputGrace to end; a failed read counts as ended (Task.WaitAny, unlike
    // Task.Wait, does not throw for a faulted task). Output that did not end, or failed, is reported and left out.
    private static GitResult Collect(int exitCode, Task<string> output, Task<string> error, string workingDirectory, ILogger logger)
    {
        Task reads = Task.WhenAll(output, error);
        if (Task.WaitAny([reads], OutputGrace) != 0 || reads.IsFaulted)
        {
            Log.GitOutputUnreadable(logger, reads.Exception, workingDirectory);
            return new GitResult(exitCode, "", $"git's output could not be read to its end within {OutputGrace.TotalSeconds:0} s of its exit", false);
        }

        return new GitResult(exitCode, output.Result, error.Result, true);
    }

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
