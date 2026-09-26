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
}

/// <summary>
/// How a tool process ended: its exit code (-1 when a kill at the ceiling did not end it in time), how long it ran, and
/// whether its ceiling killed it and its children.
/// </summary>
internal sealed record ToolProcessResult(int ExitCode, TimeSpan Elapsed, bool KilledByCeiling);

/// <summary>
/// Runs a tool process (a build, an import, a headless Godot) with its own stdin, closed at once, and its stdout and stderr
/// written line by line to one log file; a process that outlives its ceiling is killed with its whole process tree.
/// </summary>
internal static class ToolProcess
{
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
        var clock = Stopwatch.StartNew();
        process.Start();
        // Never inherit the server's stdin, the MCP pipe (see GodotCommandLine.CreateStartInfo).
        process.StandardInput.Close();
        var drain = Task.WhenAll(sink.CopyAsync(process.StandardOutput), sink.CopyAsync(process.StandardError));
        bool killed = await WaitOrKillAsync(process, request, logger, cancellationToken);
        TimeSpan elapsed = clock.Elapsed;
        await FinishDrainAsync(drain, sink);
        return new ToolProcessResult(process.HasExited ? process.ExitCode : -1, elapsed, killed);
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

    /// <returns>Whether the ceiling was reached and the process tree killed.</returns>
    private static async Task<bool> WaitOrKillAsync(Process process, ToolProcessRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(request.Ceiling);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            KillTree(process, request.FileName, logger);
            await WaitAfterKillAsync(process, request.FileName, logger);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
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

    /// <summary>Writes both streams' lines to one log, one line at a time; lines arriving after it is closed are dropped.</summary>
    private sealed class LogSink(StreamWriter writer) : IDisposable
    {
        private readonly Lock _lock = new();
        private bool _closed;

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
                }
            }
        }
    }
}
