using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>
/// The one Godot run the server manages at a time: launch with the bridge injected, talk to the bridge, stop, and remove
/// the injected override.cfg on every way out (stop, a failed launch, the game exiting, the server shutting down).
/// </summary>
internal sealed partial class GodotSession(BridgeListener listener, ILogger<GodotSession> logger) : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);
    private const int FailureStderrLines = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GodotRun? _run;

    /// <exception cref="SessionException">A run is live, the project or Godot is missing, or the bridge never connected.</exception>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RetirePreviousRunAsync();
            LaunchRequest validated = Validate(request);
            string godotPath = Installation.FindGodot();
            OverrideFile.Write(validated.ProjectPath, Installation.FindBridgeScript(), validated.ShutOutRealGamepads);
            try
            {
                return await StartRunAsync(godotPath, validated, cancellationToken);
            }
            catch
            {
                OverrideFile.Remove(validated.ProjectPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks the game to quit, kills it if it has not within 3 s, and removes the override file.</summary>
    /// <exception cref="SessionException">No run was ever launched, or the session is attached.</exception>
    public async Task<StopResult> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_attached is not null)
            {
                throw new SessionException(
                    $"The session for {_attached.ProjectDir} is attached to a game godot-mcp did not start: use detach_project; "
                        + "stop_project only stops games run_project started."
                );
            }

            GodotRun run =
                _run ?? throw new SessionException("No Godot session has been started, so there is nothing to stop. Start one with run_project.");
            bool killed = run.IsRunning && !await ShutDownGracefullyAsync(run);
            if (killed)
            {
                await KillAsync(run);
            }

            if (run.Connection is not null)
            {
                await run.Connection.DisposeAsync();
                run.Connection = null;
            }

            bool removed = OverrideFile.Remove(run.ProjectDir);
            return new StopResult(run.ProjectDir, run.ExitCode, killed, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a command to the live run's bridge.</summary>
    /// <exception cref="SessionException">No run is live, or the attached game's connection has ended.</exception>
    public Task<JsonNode?> SendAsync(string command, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        FindLiveConnection().SendAsync(command, parameters, timeout, cancellationToken);

    /// <summary>How many stderr lines the current or last run has produced: a mark to pass to <see cref="GetStderrSince"/>.</summary>
    public long MarkStderr() => _run?.Stderr.TotalLines ?? 0;

    /// <summary>The current or last run's stderr lines produced after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<string> GetStderrSince(long mark) => _run?.Stderr.Since(mark) ?? [];

    /// <exception cref="SessionException">The session is attached, so there is no captured output.</exception>
    public DebugOutput GetDebugOutput(int limit)
    {
        if (_attached is not null)
        {
            throw new SessionException(
                $"The session for {_attached.ProjectDir} is attached: attached sessions have no captured output; the game's own "
                    + "console or log has it."
            );
        }

        GodotRun? run = _run;
        if (run is null)
        {
            return new DebugOutput();
        }

        return new DebugOutput
        {
            ProjectPath = run.ProjectDir,
            Running = run.IsRunning,
            ExitCode = run.ExitCode,
            Stdout = run.Stdout.Tail(limit),
            Stderr = run.Stderr.Tail(limit),
            StdoutTotalLines = run.Stdout.TotalLines,
            StderrTotalLines = run.Stderr.TotalLines,
        };
    }

    /// <summary>
    /// The last-resort cleanup for the server's own exit: kills a live game it launched and removes the override file (and an
    /// attach file), without waiting on the gate or the bridge. An attached game is left running.
    /// </summary>
    public void Shutdown()
    {
        RemoveAttachFiles();
        GodotRun? run = _run;
        if (run is null)
        {
            return;
        }

        try
        {
            if (run.IsRunning)
            {
                run.Process.Kill(entireProcessTree: true);
            }

            OverrideFile.Remove(run.ProjectDir);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine($"godot-mcp: cleanup of {run.ProjectDir} at shutdown failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        Shutdown();
        _gate.Dispose();
    }

    private BridgeConnection FindLiveConnection()
    {
        if (_attached is { } attached)
        {
            return attached.Connection.IsOpen
                ? attached.Connection
                : throw new SessionException(
                    $"The attached game on {attached.ProjectDir} has closed its connection (it may have quit). "
                        + "detach_project, then attach_project again."
                );
        }

        return _run is { IsRunning: true, Connection: { IsOpen: true } live }
            ? live
            : throw new SessionException("No Godot session is running; start one with run_project or attach_project.");
    }

    private async Task RetirePreviousRunAsync()
    {
        await RetireAttachedAsync();
        if (_run is null)
        {
            return;
        }

        if (_run.IsRunning)
        {
            throw new SessionException($"A session is already running for {_run.ProjectDir}; stop_project first.");
        }

        OverrideFile.Remove(_run.ProjectDir);
        await _run.DisposeAsync();
        _run = null;
    }

    private static LaunchRequest Validate(LaunchRequest request) => request with { ProjectPath = NormaliseProjectDir(request.ProjectPath) };

    /// <exception cref="SessionException">The path is empty or holds no project.godot.</exception>
    private static string NormaliseProjectDir(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new SessionException("projectPath is empty. Pass the folder that holds the project's project.godot.");
        }

        string projectDir = ProjectPaths.Normalise(projectPath);
        return File.Exists(Path.Combine(projectDir, "project.godot"))
            ? projectDir
            : throw new SessionException($"{projectDir} holds no project.godot. Pass the folder that holds the project's project.godot.");
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private async Task<LaunchResult> StartRunAsync(string godotPath, LaunchRequest request, CancellationToken cancellationToken)
    {
        GitExclude.Ensure(request.ProjectPath, OverrideFile.FileName, logger);
        string token = CreateToken();
        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(godotPath, request, new BridgeEndpoint(listener.Port, token));
        GodotRun run = new(request.ProjectPath, new Process { StartInfo = startInfo, EnableRaisingEvents = true });
        StartProcess(run);
        _run = run;
        run.Connection = await WaitForHandshakeAsync(run, new HandshakeExpectation(token, request.ProjectPath), cancellationToken);
        int processId = run.Process.Id;
        Log.RunStarted(logger, processId, run.ProjectDir);
        return new LaunchResult(run.ProjectDir, processId, request.Background);
    }

    private void StartProcess(GodotRun run)
    {
        run.Process.OutputDataReceived += (_, e) => AddLine(run.Stdout, e.Data);
        run.Process.ErrorDataReceived += (_, e) => AddLine(run.Stderr, e.Data);
        run.Process.Exited += (_, _) => _ = OnRunExitedAsync(run);
        try
        {
            run.Process.Start();
        }
        catch (Win32Exception e)
        {
            run.Process.Dispose();
            throw new SessionException(
                $"Godot could not be started from {run.Process.StartInfo.FileName}: {e.Message}. "
                    + $"Set {Installation.GodotPathVariable} to the Godot 4.7 console executable.",
                e
            );
        }

        run.Process.StandardInput.Close();
        run.Process.BeginOutputReadLine();
        run.Process.BeginErrorReadLine();
    }

    private static void AddLine(OutputBuffer buffer, string? line)
    {
        if (line is not null)
        {
            buffer.Add(line);
        }
    }

    private async Task<BridgeConnection> WaitForHandshakeAsync(GodotRun run, HandshakeExpectation expected, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        Task<BridgeConnection> accept = listener.AcceptBridgeAsync(expected, timeout.Token);
        Task exited = run.Process.WaitForExitAsync(timeout.Token);
        await Task.WhenAny(accept, exited);
        if (accept.IsCompletedSuccessfully)
        {
            return await accept;
        }

        await timeout.CancelAsync();
        await ObserveAbandonedAsync(accept, exited);
        bool exitedEarly = !run.IsRunning;
        if (!exitedEarly)
        {
            await KillAsync(run);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw DescribeFailedLaunch(run, exitedEarly);
    }

    private async Task ObserveAbandonedAsync(Task<BridgeConnection> accept, Task exited)
    {
        try
        {
            await Task.WhenAll(accept, exited);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Log.HandshakeAbandoned(logger, e);
        }

        if (accept.IsCompletedSuccessfully)
        {
            await accept.Result.DisposeAsync();
        }
    }

    private static SessionException DescribeFailedLaunch(GodotRun run, bool exitedEarly)
    {
        string what = exitedEarly
            ? $"Godot exited with code {run.ExitCode} before the bridge connected"
            : $"the bridge did not connect within {HandshakeTimeout.TotalSeconds:0} s, so Godot was stopped";
        string stderr = string.Join('\n', run.Stderr.Tail(FailureStderrLines));
        return new SessionException(
            $"Launching {run.ProjectDir} failed: {what}. Check that the project runs on its own "
                + $"(godot --path <project>) and that nothing blocks connections to 127.0.0.1. Last stderr lines:\n{stderr}"
        );
    }

    private async Task<bool> ShutDownGracefullyAsync(GodotRun run)
    {
        if (run.Connection is { IsOpen: true } connection)
        {
            try
            {
                await connection.SendAsync("shutdown", null, ShutdownReplyTimeout, CancellationToken.None);
            }
            catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
            {
                Log.ShutdownNotAcknowledged(logger, e);
            }
        }

        using CancellationTokenSource grace = new(ExitGrace);
        try
        {
            await run.Process.WaitForExitAsync(grace.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.ExitGraceExpired(logger, run.ProjectDir, ExitGrace.TotalSeconds);
            return false;
        }
    }

    private async Task KillAsync(GodotRun run)
    {
        try
        {
            run.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.KillFailed(logger, e, run.ProjectDir);
        }

        using CancellationTokenSource wait = new(KillWait);
        try
        {
            await run.Process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            Log.StillRunningAfterKill(logger, run.ProjectDir, KillWait.TotalSeconds);
        }
    }

    private async Task OnRunExitedAsync(GodotRun run)
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // The server is shutting down, and Shutdown has already removed the override file.
            return;
        }

        try
        {
            if (ReferenceEquals(_run, run) && OverrideFile.Remove(run.ProjectDir))
            {
                Log.OverrideRemovedAfterExit(logger, run.ProjectDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.OverrideRemovalFailed(logger, e, run.ProjectDir);
        }
        finally
        {
            _gate.Release();
        }
    }
}
