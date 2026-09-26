using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>Whether a session is a Godot run the server launched or a game attach_project reached.</summary>
internal enum SessionKind
{
    Run,
    Attach,
}

/// <summary>What a session is created for: its name, its project folder, its kind and its pad setting.</summary>
internal sealed record SessionSpec(string Name, string ProjectDir, SessionKind Kind, bool ShutOutRealGamepads);

/// <summary>
/// One named session in the <see cref="SessionRegistry"/>: a Godot run launched with the bridge injected, or a game
/// attach_project reached. It talks to its bridge, stops its run, and releases the injected override.cfg to the registry
/// on every way out (stop, a failed launch, the game exiting), which removes it once no other live session uses the folder.
/// </summary>
internal sealed partial class GodotSession(SessionSpec spec, SessionRegistry registry) : IDisposable
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What a runtime tool says when no session answers.</summary>
    public const string NoneRunning = "No Godot session is running; start one with run_project or attach_project.";

    private static readonly TimeSpan ShutdownReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);
    private const int FailureStderrLines = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = registry.Logger;
    private volatile bool _pending = true;
    private GodotRun? _run;

    public string Name { get; } = spec.Name;

    public string ProjectDir { get; } = spec.ProjectDir;

    public SessionKind Kind { get; } = spec.Kind;

    public bool ShutOutRealGamepads { get; } = spec.ShutOutRealGamepads;

    /// <summary>The launched game's process id, once it has started; null for an attached game.</summary>
    public int? ProcessId { get; private set; }

    /// <summary>Plays one input call at a time on this session; calls to other sessions play alongside.</summary>
    public SemaphoreSlim InputGate { get; } = new(1, 1);

    /// <summary>Whether the session is starting, its run is running, or its attached game is still connected.</summary>
    public bool IsLive => _pending || (Kind == SessionKind.Run ? _run is { IsRunning: true } : _attached is { IsOpen: true });

    /// <summary>Whether this is an attach still waiting for its game to dial in.</summary>
    public bool IsWaitingForGame => _pending && Kind == SessionKind.Attach;

    /// <summary>Starts the run; a launch that fails before Godot starts leaves the registry without this session.</summary>
    /// <exception cref="SessionException">Godot or the bridge is missing, the project has its own override.cfg, or the bridge never connected.</exception>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await StartRunAsync(request, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            AbandonStart(forget: _run is null);
            throw;
        }
        finally
        {
            _pending = false;
        }
    }

    /// <summary>Asks the game to quit, kills it if it has not within 3 s, and releases the override file.</summary>
    /// <exception cref="SessionException">No run was ever launched, or the session is attached.</exception>
    public async Task<StopResult> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Kind == SessionKind.Attach)
            {
                throw new SessionException(
                    $"The session for {ProjectDir} is attached to a game godot-mcp did not start: use detach_project; "
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

            bool removed = registry.ReleaseFolder(this);
            return new StopResult(Name, run.ProjectDir, run.ExitCode, killed, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a command to the live run's or attached game's bridge.</summary>
    /// <exception cref="SessionException">The run is not live, or the attached game's connection has ended.</exception>
    public Task<JsonNode?> SendAsync(string command, JsonObject? parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        FindLiveConnection().SendAsync(command, parameters, timeout, cancellationToken);

    /// <summary>How many stderr lines the run has produced: a mark to pass to <see cref="GetStderrSince"/>.</summary>
    public long MarkStderr() => _run?.Stderr.TotalLines ?? 0;

    /// <summary>The run's stderr lines produced after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<string> GetStderrSince(long mark) => _run?.Stderr.Since(mark) ?? [];

    /// <exception cref="SessionException">The session is attached, so there is no captured output.</exception>
    public DebugOutput GetDebugOutput(int limit)
    {
        if (Kind == SessionKind.Attach)
        {
            throw new SessionException(
                $"The session for {ProjectDir} is attached: attached sessions have no captured output; the game's own console or log has it."
            );
        }

        GodotRun? run = _run;
        if (run is null)
        {
            return new DebugOutput { Session = Name, ProjectPath = ProjectDir };
        }

        return new DebugOutput
        {
            Session = Name,
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
    /// The last-resort cleanup for the server's own exit: kills a live game it launched and removes an attach file, without
    /// waiting on the gate or the bridge. An attached game is left running; the registry removes the override files.
    /// </summary>
    public void Shutdown()
    {
        try
        {
            if (Kind == SessionKind.Attach)
            {
                AttachFile.Remove(ProjectDir);
            }
            else if (_run is { IsRunning: true } run)
            {
                run.Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Logging may already be torn down while the process exits, so this goes straight to stderr.
            Console.Error.WriteLine($"godot-mcp: cleanup of {ProjectDir} at shutdown failed: {e.Message}");
        }
    }

    /// <summary>Lets go of a session that is no longer live, before another takes its name: its connection and process.</summary>
    public async Task RetireAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_attached is not null)
            {
                await _attached.DisposeAsync();
                _attached = null;
            }

            if (_run is not null)
            {
                await _run.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        InputGate.Dispose();
    }

    private BridgeConnection FindLiveConnection()
    {
        if (_attached is { } attached)
        {
            return attached.IsOpen
                ? attached
                : throw new SessionException(
                    $"The attached game on {ProjectDir} has closed its connection (it may have quit). " + "detach_project, then attach_project again."
                );
        }

        return _run is { IsRunning: true, Connection: { IsOpen: true } live } ? live : throw new SessionException(NoneRunning);
    }

    /// <summary>Releases the override file after a failed start and, when nothing started, drops the session from the registry.</summary>
    /// <returns>Whether the override file was removed; a failed removal is logged, so the start's own error reaches the caller.</returns>
    private bool AbandonStart(bool forget)
    {
        if (forget)
        {
            registry.Forget(this);
        }

        try
        {
            return registry.ReleaseFolder(this);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.CleanupFailed(_logger, e, OverrideFile.FileName, ProjectDir);
            return false;
        }
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private async Task<LaunchResult> StartRunAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        string godotPath = Installation.FindGodot();
        registry.WriteOverride(this, Installation.FindBridgeScript());
        GitExclude.Ensure(ProjectDir, OverrideFile.FileName, _logger);
        string token = CreateToken();
        ProcessStartInfo startInfo = GodotCommandLine.CreateStartInfo(godotPath, request, new BridgeEndpoint(registry.Listener.Port, token));
        GodotRun run = new(ProjectDir, new Process { StartInfo = startInfo, EnableRaisingEvents = true });
        StartProcess(run);
        _run = run;
        int processId = run.Process.Id;
        ProcessId = processId;
        run.Connection = await WaitForHandshakeAsync(run, new HandshakeExpectation(token, ProjectDir), cancellationToken);
        Log.RunStarted(_logger, processId, run.ProjectDir);
        return new LaunchResult(Name, run.ProjectDir, processId, request.Background);
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
        Task<BridgeConnection> accept = registry.Listener.AcceptBridgeAsync(expected, timeout.Token);
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
            Log.HandshakeAbandoned(_logger, e);
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
                Log.ShutdownNotAcknowledged(_logger, e);
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
            Log.ExitGraceExpired(_logger, run.ProjectDir, ExitGrace.TotalSeconds);
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
            Log.KillFailed(_logger, e, run.ProjectDir);
        }

        using CancellationTokenSource wait = new(KillWait);
        try
        {
            await run.Process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            Log.StillRunningAfterKill(_logger, run.ProjectDir, KillWait.TotalSeconds);
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
            // The server is shutting down, and the registry's shutdown has already removed the override file.
            return;
        }

        try
        {
            if (registry.ReleaseFolder(this))
            {
                Log.OverrideRemovedAfterExit(_logger, run.ProjectDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.OverrideRemovalFailed(_logger, e, run.ProjectDir);
        }
        finally
        {
            _gate.Release();
        }
    }
}
