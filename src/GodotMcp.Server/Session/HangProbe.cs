using System.ComponentModel;
using System.Diagnostics;

namespace GodotMcp.Server.Session;

/// <summary>
/// What <see cref="HangProbe"/> found after a request timed out: whether the game answered a ping and, when it did not, its
/// process's state and, for a run, its last stderr lines (null for an attached game, which has no captured output).
/// </summary>
internal sealed record HangReport(bool Answered, string ProcessState, IReadOnlyList<string>? StderrLines)
{
    /// <summary>The main thread's state in one word, for the log: running or stuck.</summary>
    public string Outcome => Answered ? "running" : "stuck";

    /// <summary>The timed-out tool's error message.</summary>
    /// <param name="tool">The tool whose request timed out.</param>
    /// <param name="timeout">The request's timeout.</param>
    /// <param name="hint">What the caller can do about a busy game, starting with "; ", or empty.</param>
    public string Describe(string tool, TimeSpan timeout, string hint)
    {
        string timedOut = $"'{tool}' timed out after {timeout.TotalMilliseconds:0} ms";
        if (Answered)
        {
            return $"{timedOut}, but the game answered a ping, so its main thread is running{hint}.";
        }

        string stuck =
            $"{timedOut} and the game did not answer a ping within {HangProbe.PingTimeout.TotalSeconds:0} s: its main thread is stuck.\n{ProcessState}";
        if (StderrLines is null)
        {
            return stuck;
        }

        string lines = StderrLines.Count == 0 ? "(none)" : string.Join('\n', StderrLines);
        return $"{stuck}\nLast stderr lines:\n{lines}";
    }
}

/// <summary>
/// Tells a busy game from a stuck one after a request timed out. The bridge answers from the main thread's <c>_process</c>,
/// so a ping that goes unanswered means the main thread is not getting back to its loop; then the probe samples the game's
/// process for a second: whether it has exited, the CPU it used, its thread count and its main thread's state.
/// </summary>
internal static class HangProbe
{
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    public const int StderrLineCount = 20;
    private static readonly TimeSpan CpuSample = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pings the session's game and, when the ping goes unanswered, describes the game's process: the one the hello named, else
    /// the process the server started.
    /// </summary>
    public static async Task<HangReport> RunAsync(GodotSession session, CancellationToken cancellationToken)
    {
        if (await AnswersPingAsync(session, cancellationToken))
        {
            return new HangReport(true, string.Empty, null);
        }

        int? processId = session.GameProcessId ?? session.ProcessId;
        string state = processId is int id
            ? await DescribeProcessAsync(id, session.RunExitCode, cancellationToken)
            : "The game's process id is unknown: its bridge's hello carried none.";
        return new HangReport(false, state, session.LastStderrLines(StderrLineCount));
    }

    /// <summary>Whether the game replied to a ping in time; a refusal is a reply too, so only silence or a lost connection is false.</summary>
    private static async Task<bool> AnswersPingAsync(GodotSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.SendAsync("ping", null, PingTimeout, cancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or SessionException)
        {
            return false;
        }
    }

    private static async Task<string> DescribeProcessAsync(int processId, int? runExitCode, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws this for a process that is not running.
            return Exited(processId, runExitCode);
        }

        using (process)
        {
            try
            {
                return await SampleAsync(process, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return $"Process {processId}: its state could not be read ({e.Message}).";
            }
        }
    }

    private static async Task<string> SampleAsync(Process process, CancellationToken cancellationToken)
    {
        TimeSpan before = process.TotalProcessorTime;
        await Task.Delay(CpuSample, cancellationToken);
        process.Refresh();
        if (process.HasExited)
        {
            return Exited(process.Id, process.ExitCode);
        }

        long cpuMs = (long)(process.TotalProcessorTime - before).TotalMilliseconds;
        return $"Process {process.Id}: {cpuMs} ms CPU over {CpuSample.TotalSeconds:0} s, {process.Threads.Count} threads, "
            + $"main thread {DescribeMainThread(process)}.";
    }

    /// <summary>The earliest-started thread's state, with its wait reason when it waits: <c>Wait/ExecutionDelay</c>, <c>Running</c>.</summary>
    private static string DescribeMainThread(Process process)
    {
        ProcessThread? main = process.Threads.Cast<ProcessThread>().MinBy(StartTimeOrLatest);
        if (main is null)
        {
            return "unknown (no threads)";
        }

        System.Diagnostics.ThreadState state = main.ThreadState;
        return state == System.Diagnostics.ThreadState.Wait ? $"{state}/{main.WaitReason}" : state.ToString();
    }

    /// <summary>A thread's start time; one that has ended, or cannot be read, sorts last.</summary>
    private static DateTime StartTimeOrLatest(ProcessThread thread)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            return DateTime.MaxValue;
        }

        try
        {
            return thread.StartTime;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return DateTime.MaxValue;
        }
    }

    private static string Exited(int processId, int? exitCode) =>
        exitCode is int code ? $"Process {processId} has exited with code {code}." : $"Process {processId} has exited.";
}
