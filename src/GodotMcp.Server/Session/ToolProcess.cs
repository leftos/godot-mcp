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
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long a killed process is waited for.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="request">What to run.</param>
    /// <param name="logger">Where a failed kill is reported.</param>
    /// <param name="cancellationToken">Kills the process tree and ends the call.</param>
    /// <exception cref="Win32Exception">The file could not be started.</exception>
    /// <exception cref="OperationCanceledException">The call was cancelled; the process tree was killed first.</exception>
    public static async Task<ToolProcessResult> RunAsync(ToolProcessRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.LogPath))!);
        using LogSink sink = new(new StreamWriter(request.LogPath, request.AppendToLog, Utf8NoBom));
        using Process process = new() { StartInfo = CreateStartInfo(request) };
        var stopwatch = Stopwatch.StartNew();
        ChildProcesses.Start(process);
        using var tree = OwnWork.TreeCpu.Adopt(process, logger, request.MeasureTree);
        // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
        process.StandardInput.Close();
        var drain = Task.WhenAll(sink.CopyAsync(process.StandardOutput), sink.CopyAsync(process.StandardError));
        (KillReason killed, string? detail) = await WaitOrKillAsync(new Watched(process, sink, tree, request, logger), cancellationToken);
        TimeSpan elapsed = stopwatch.Elapsed;
        await FinishDrainAsync(drain, sink);
        return new ToolProcessResult(process.HasExited ? process.ExitCode : -1, elapsed, killed, detail);
    }

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
    private static async Task<(KillReason Reason, string? Detail)> WaitOrKillAsync(Watched run, CancellationToken cancellationToken)
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
                await KillAsync(run);
                cancellationToken.ThrowIfCancellationRequested();
            }

            (KillReason reason, string? detail) = Check(run, deadline, stall);
            if (reason != KillReason.None)
            {
                await KillAsync(run);
                return (reason, detail);
            }
        }
    }

    /// <returns>The first limit the process has passed, and why in words; <see cref="KillReason.None"/> when it has passed none.</returns>
    private static (KillReason Reason, string? Detail) Check(Watched run, LoadDeadline deadline, StallWatch stall)
    {
        switch (deadline.Reason)
        {
            case DeadlineReason.Ceiling:
                return (KillReason.Ceiling, deadline.CeilingClause());
            case DeadlineReason.Backstop:
                return (
                    KillReason.Backstop,
                    $"ran {LoadDeadline.Seconds(deadline.Wall)} s of wall time, {LoadClock.BackstopFactor} x its "
                        + $"{LoadDeadline.Seconds(deadline.Budget)} s ceiling (load-adjusted {LoadDeadline.Seconds(deadline.Adjusted)} s, "
                        + $"machine {LoadDeadline.Percent(deadline.MeanFree)}% free on average)"
                );
        }

        // Without the tree's CPU, a child working silently (a compile in an MSBuild worker) would read as stalled.
        if (!run.Tree.Measurable || !stall.Observe(run.Sink.Lines, run.Tree.Ticks))
        {
            return (KillReason.None, null);
        }

        TimeSpan limit = run.Request.StallLimit;
        Log.ToolStalled(run.Logger, run.Request.FileName, limit.TotalSeconds);
        return (KillReason.Stall, $"stalled: no output and no CPU for {LoadDeadline.Seconds(limit)} s");
    }

    private static async Task KillAsync(Watched run)
    {
        KillTree(run.Process, run.Request.FileName, run.Logger);
        await WaitAfterKillAsync(run.Process, run.Request.FileName, run.Logger);
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
        using CancellationTokenSource wait = new(KillWait);
        try
        {
            await process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
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

    /// <summary>A started tool process, the log its output goes to and its tree's CPU, with what it was asked to run.</summary>
    private sealed record Watched(Process Process, LogSink Sink, OwnWork.TreeCpu Tree, ToolProcessRequest Request, ILogger Logger);

    /// <summary>
    /// Writes both streams' lines to one log, one line at a time, and counts them; lines arriving after it is closed are
    /// dropped.
    /// </summary>
    private sealed class LogSink(StreamWriter writer) : IDisposable
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
