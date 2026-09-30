using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// One tool process to run to its end: the file, its arguments and working directory, where its combined output goes and
/// how long it may take. <see cref="SetVariables"/> are added to the server's environment and
/// <see cref="RemovedVariables"/> taken out of it.
/// </summary>
internal sealed record ToolProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string LogPath, TimeSpan Ceiling)
{
    public IReadOnlyDictionary<string, string> SetVariables { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> RemovedVariables { get; init; } = [];

    /// <summary>Appends to the log instead of replacing it.</summary>
    public bool AppendToLog { get; init; }

    /// <summary>
    /// Puts each line on disk as it is written, so the log is current while the process runs: a warm host's, which agents
    /// read during its life. Off, a build's or an import's log is buffered and complete once the process has ended.
    /// </summary>
    public bool FlushEachLine { get; init; }

    /// <summary>How long neither the output nor the tree's CPU may stand still before the tree is killed as stalled.</summary>
    public TimeSpan StallLimit { get; init; } = ToolProcess.DefaultStallLimit;

    /// <summary>The clock <see cref="ToolProcessRequest.Ceiling"/> runs on; null is <see cref="LoadClock.Shared"/>.</summary>
    public LoadClock? Clock { get; init; }

    /// <summary>Puts the process tree in a job of its own to measure its CPU; false stands in, in tests, for a tree that cannot join one.</summary>
    internal bool MeasureTree { get; init; } = true;
}

/// <summary>What killed a tool process and its tree, if anything did.</summary>
internal enum KillReason
{
    None,

    /// <summary>Its ceiling of load-adjusted time ran out.</summary>
    Ceiling,

    /// <summary>Neither its output nor its tree's CPU moved for its stall limit.</summary>
    Stall,

    /// <summary>It ran <see cref="LoadClock.BackstopFactor"/> times its ceiling in wall time.</summary>
    Backstop,
}

/// <summary>
/// How a tool process ended: its exit code (-1 when a kill did not end it in time), how long it ran in wall time, and what
/// killed it and its children, with <see cref="KillDetail"/> saying why in words.
/// </summary>
internal sealed record ToolProcessResult(int ExitCode, TimeSpan Elapsed, KillReason Killed, string? KillDetail = null)
{
    public bool WasKilled => Killed != KillReason.None;

    /// <summary>
    /// <see cref="KillDetail"/> as it follows "did not finish": a ceiling kill's clause as it is, whose own parentheses hold the
    /// wall time and the machine's free share, and any other kill's detail in parentheses.
    /// </summary>
    public string KillPhrase => ToolProcess.KillPhrase(Killed, KillDetail);
}

/// <summary>
/// Runs a tool process (a build, an import, a headless Godot) with its own stdin, closed at once, and its stdout and stderr
/// written line by line to one log file. The process is killed with its whole process tree when it outlives its ceiling of
/// load-adjusted time, runs <see cref="LoadClock.BackstopFactor"/> times the ceiling in wall time, or stalls: neither
/// writes a line nor uses CPU for its stall limit.
/// </summary>
internal static class ToolProcess
{
    /// <summary>The stall limit of every request that sets none.</summary>
    public static readonly TimeSpan DefaultStallLimit = TimeSpan.FromSeconds(120);

    /// <summary>How often the ceiling and the stall are checked.</summary>
    private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(1);

    /// <summary>How long the output is still read after the process ends, in case a child it left holds the pipes open.</summary>
    internal static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long a killed process is waited for.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="request">What to run.</param>
    /// <param name="logger">Where a failed kill is reported.</param>
    /// <param name="cancellationToken">Kills the process tree and ends the call.</param>
    /// <exception cref="Win32Exception">The file could not be started.</exception>
    /// <exception cref="OperationCanceledException">The call was cancelled; the process tree was killed first.</exception>
    public static async Task<ToolProcessResult> RunAsync(ToolProcessRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        using Running run = Start(request, logger);
        (KillReason killed, string? detail) = await WaitOrKillAsync(run, cancellationToken);
        TimeSpan elapsed = run.Elapsed;
        await run.FinishOutputAsync();
        return new ToolProcessResult(run.Process.HasExited ? run.Process.ExitCode : -1, elapsed, killed, detail);
    }

    /// <summary>
    /// Starts the process and keeps it: its stdin closed, its output going to its log, its tree in a job of its own. Nothing
    /// watches its ceiling or a stall; the caller owns the handle, and <see cref="Running.Kill"/> or its exit ends it.
    /// </summary>
    /// <exception cref="Win32Exception">The file could not be started.</exception>
    public static Running Start(ToolProcessRequest request, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.LogPath))!);
        FileStreamOptions logOptions = new()
        {
            Mode = request.AppendToLog ? FileMode.Append : FileMode.Create,
            Access = FileAccess.Write,
            // Anyone may read the log while the process writes it; a second writer is refused.
            Share = FileShare.Read,
        };
        LogSink sink = new(new StreamWriter(request.LogPath, Utf8NoBom, logOptions) { AutoFlush = request.FlushEachLine });
        Process process = new() { StartInfo = CreateStartInfo(request) };
        OwnWork.TreeCpu? tree = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChildProcesses.Start(process);
            tree = OwnWork.TreeCpu.Adopt(process, logger, request.MeasureTree);
            // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
            process.StandardInput.Close();
            var drain = Task.WhenAll(sink.CopyAsync(process.StandardOutput), sink.CopyAsync(process.StandardError));
            return new Running(new Started(process, sink, tree, stopwatch, drain), request, logger);
        }
        catch
        {
            tree?.Dispose();
            process.Dispose();
            sink.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The kill a <see cref="LoadDeadline"/> that expired calls for, and why in words: <see cref="KillReason.Ceiling"/> with its
    /// ceiling clause, or <see cref="KillReason.Backstop"/> with the wall time it ran.
    /// </summary>
    internal static (KillReason Reason, string Detail) DeadlineKill(LoadDeadline deadline) =>
        deadline.Reason == DeadlineReason.Backstop
            ? (
                KillReason.Backstop,
                $"ran {LoadDeadline.Seconds(deadline.Wall)} s of wall time, {LoadClock.BackstopFactor} x its "
                    + $"{LoadDeadline.Seconds(deadline.Budget)} s ceiling (load-adjusted {LoadDeadline.Seconds(deadline.Adjusted)} s, "
                    + $"machine free {LoadDeadline.Percent(deadline.MeanFree)}% on average)"
            )
            : (KillReason.Ceiling, deadline.CeilingClause());

    /// <summary>
    /// A kill's <paramref name="detail"/> as it follows "did not finish": a ceiling kill's clause as it is, whose own
    /// parentheses hold the wall time and the machine's free share, and any other kill's detail in parentheses.
    /// </summary>
    internal static string KillPhrase(KillReason reason, string? detail) => reason == KillReason.Ceiling ? detail ?? string.Empty : $"({detail})";

    private static ProcessStartInfo CreateStartInfo(ToolProcessRequest request)
    {
        ProcessStartInfo startInfo = new(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string name in request.RemovedVariables)
        {
            startInfo.Environment.Remove(name);
        }

        foreach ((string name, string value) in request.SetVariables)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    /// <summary>Waits for the exit, checking the ceiling and the stall every <see cref="CheckPeriod"/>.</summary>
    /// <returns>What killed the process tree, and why in words; <see cref="KillReason.None"/> when the process exited.</returns>
    private static async Task<(KillReason Reason, string? Detail)> WaitOrKillAsync(Running run, CancellationToken cancellationToken)
    {
        LoadClock clock = run.Request.Clock ?? LoadClock.Shared;
        using LoadDeadline deadline = clock.Start(run.Request.Ceiling, cancellationToken);
        StallWatch stall = new(clock.Time, run.Request.StallLimit);
        if (!run.Tree.Measurable)
        {
            Log.StallGuardOff(run.Logger, run.Request.FileName);
        }

        Task exit = run.Process.WaitForExitAsync(CancellationToken.None);
        while (true)
        {
            await Task.WhenAny(exit, Task.Delay(CheckPeriod, clock.Time, cancellationToken));
            if (exit.IsCompleted)
            {
                return (KillReason.None, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await run.KillAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            (KillReason reason, string? detail) = Check(run, deadline, stall);
            if (reason != KillReason.None)
            {
                await run.KillAsync();
                return (reason, detail);
            }
        }
    }

    /// <returns>The first limit the process has passed, and why in words; <see cref="KillReason.None"/> when it has passed none.</returns>
    private static (KillReason Reason, string? Detail) Check(Running run, LoadDeadline deadline, StallWatch stall)
    {
        if (deadline.Expired)
        {
            return DeadlineKill(deadline);
        }

        // Without the tree's CPU, a child working silently (a compile in an MSBuild worker) would read as stalled.
        if (!run.Tree.Measurable || !stall.Observe(run.OutputLines, run.Tree.Ticks))
        {
            return (KillReason.None, null);
        }

        TimeSpan limit = run.Request.StallLimit;
        Log.ToolStalled(run.Logger, run.Request.FileName, limit.TotalSeconds);
        return (KillReason.Stall, $"stalled: no output and no CPU for {LoadDeadline.Seconds(limit)} s");
    }

    private static void KillTree(Process process, string fileName, ILogger logger)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the ceiling and the kill; there is nothing left to kill.
        }
        catch (Exception e) when (e is Win32Exception or AggregateException)
        {
            Log.ToolKillFailed(logger, e, fileName);
        }
    }

    private static async Task WaitAfterKillAsync(Process process, string fileName, ILogger logger)
    {
        if (!await ProcessExit.WaitUntilGoneAsync(process, KillWait))
        {
            Log.ToolStillRunningAfterKill(logger, fileName, KillWait.TotalSeconds);
        }
    }

    private static async Task FinishDrainAsync(Task drain, LogSink sink)
    {
        try
        {
            await drain.WaitAsync(OutputDrainGrace);
        }
        catch (TimeoutException)
        {
            sink.Close($"[godot-mcp] the process exited but its output stayed open for {OutputDrainGrace.TotalSeconds:0} s; the rest is not logged.");
        }
    }

    /// <summary>What <see cref="Start"/> set going: the process, its log, its tree's CPU, its clock and the reading of its output.</summary>
    internal sealed record Started(Process Process, LogSink Sink, OwnWork.TreeCpu Tree, Stopwatch Stopwatch, Task Drain);

    /// <summary>
    /// A started tool process, the log its output goes to and its tree's CPU, with what it was asked to run. Disposing it
    /// lets go of the tree's job, the process handle and the log, in that order; it never kills the process.
    /// </summary>
    internal sealed class Running(Started started, ToolProcessRequest request, ILogger logger) : IDisposable
    {
        public Process Process => started.Process;

        public OwnWork.TreeCpu Tree => started.Tree;

        public ToolProcessRequest Request => request;

        public ILogger Logger => logger;

        /// <summary>The lines of output written to the log so far.</summary>
        public long OutputLines => started.Sink.Lines;

        /// <summary>The wall time since the process started.</summary>
        public TimeSpan Elapsed => started.Stopwatch.Elapsed;

        /// <summary>Waits for the output to end after the exit, at most <see cref="OutputDrainGrace"/>, saying in the log when it did not.</summary>
        public Task FinishOutputAsync() => FinishDrainAsync(started.Drain, started.Sink);

        /// <summary>Kills the whole process tree and waits until Windows lets go of the process, at most <see cref="KillWait"/>.</summary>
        public void Kill()
        {
            KillTree(Process, request.FileName, logger);
            if (!Process.WaitForExit(KillWait))
            {
                Log.ToolStillRunningAfterKill(logger, request.FileName, KillWait.TotalSeconds);
            }
        }

        /// <summary><see cref="Kill"/> with the wait off the caller's thread.</summary>
        public async Task KillAsync()
        {
            KillTree(Process, request.FileName, logger);
            await WaitAfterKillAsync(Process, request.FileName, logger);
        }

        public void Dispose()
        {
            started.Tree.Dispose();
            started.Process.Dispose();
            started.Sink.Dispose();
        }
    }

    /// <summary>
    /// Writes both streams' lines to one log, one line at a time, and counts them; lines arriving after it is closed are
    /// dropped.
    /// </summary>
    internal sealed class LogSink(StreamWriter writer) : IDisposable
    {
        private readonly Lock _lock = new();
        private bool _closed;
        private long _lines;

        /// <summary>The lines written so far.</summary>
        public long Lines => Interlocked.Read(ref _lines);

        public async Task CopyAsync(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    Write(line);
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or IOException)
            {
                // Only after the drain grace, when a child the process left still holds the pipe: on Windows the pipes are
                // synchronous, so disposing the process does not cancel the read already blocked on it. That read waits until
                // the child writes or exits; the next one then fails on the disposed stream, here. Close has already said in
                // the log that the rest of the output is missing, and lines arriving now are dropped.
            }
        }

        public void Close(string lastLine)
        {
            Write(lastLine);
            lock (_lock)
            {
                _closed = true;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _closed = true;
                writer.Dispose();
            }
        }

        private void Write(string line)
        {
            lock (_lock)
            {
                if (!_closed)
                {
                    writer.WriteLine(line);
                    Interlocked.Increment(ref _lines);
                }
            }
        }
    }
}
