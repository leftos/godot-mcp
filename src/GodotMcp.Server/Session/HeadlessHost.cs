using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server.Session;

/// <summary>What a warm host's process is started with: its project folder, the listener's port, its token and its log.</summary>
internal sealed record HostLaunch(string ProjectDir, int Port, string Token, string LogPath);

/// <summary>One request to a warm host: what the errors call it, the operation and its parameters, and its ceiling.</summary>
internal sealed record HostRequest(string What, string Operation, JsonObject Parameters, TimeSpan Ceiling);

/// <summary>
/// A warm host's process: <see cref="GodotHostProcess"/> for a real Godot, a fake one in the tests. Its output goes to its
/// log, whose lines it counts; its tree's CPU is read for the stall watch.
/// </summary>
internal interface IHostProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    /// <summary>The exit code; read only once <see cref="HasExited"/>.</summary>
    int ExitCode { get; }

    /// <summary>The lines of output written to the log so far.</summary>
    long OutputLines { get; }

    /// <summary>Whether <see cref="CpuTicks"/> covers the whole tree; false turns the stall watch off.</summary>
    bool CpuMeasurable { get; }

    /// <summary>The CPU the process tree has used so far, in 100 ns units.</summary>
    long CpuTicks { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the exit at most <paramref name="timeout"/>; true when it exited, and once disposed.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Waits for the output to end after the exit, so the log holds all of it.</summary>
    Task FinishOutputAsync();

    /// <summary>
    /// Keeps a handle on the Godot process that said hello, <paramref name="gameProcessId"/>: on Windows the console
    /// wrapper's child, which <see cref="Kill"/> then also waits for.
    /// </summary>
    void KeepGameHandle(int gameProcessId);

    /// <summary>
    /// Kills the whole process tree and waits until Windows lets go of the process and of the one kept by
    /// <see cref="KeepGameHandle"/>; after an exit it only waits.
    /// </summary>
    void Kill();
}

/// <summary>A warm host's Godot, started and kept through <see cref="ToolProcess.Start"/>.</summary>
internal sealed class GodotHostProcess(ToolProcess.Running run, ILogger logger) : IHostProcess
{
    private int _disposed;
    private Process? _game;

    public int Id => run.Process.Id;

    /// <summary>True once it has exited, and once disposed.</summary>
    public bool HasExited => IsDisposed || run.Process.HasExited;

    /// <summary>The exit code; -1 once disposed.</summary>
    public int ExitCode
    {
        get
        {
            try
            {
                return IsDisposed ? -1 : run.Process.ExitCode;
            }
            catch (InvalidOperationException) when (IsDisposed)
            {
                // Disposed by a release on another thread between the check and the read.
                return -1;
            }
        }
    }

    public long OutputLines => run.OutputLines;

    public bool CpuMeasurable => run.Tree.Measurable;

    public long CpuTicks => run.Tree.Ticks;

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// <c>godot --headless --path &lt;project&gt; --script &lt;host.gd&gt;</c> in the project folder, with
    /// <see cref="GodotCommandLine.OffVariable"/> set as for every headless run and the host's port and token, its output
    /// written to its log, which it starts afresh.
    /// </summary>
    /// <exception cref="SessionException">Godot or the host script was not found, or Godot could not be started.</exception>
    public static IHostProcess Launch(HostLaunch launch, ILogger logger) =>
        Start(launch, Installation.FindGodot(), HeadlessHost.FindHostScript(), logger);

    /// <summary><see cref="Launch"/> with Godot and the host script found.</summary>
    /// <exception cref="SessionException">Godot could not be started, or its log could not be opened for writing.</exception>
    internal static IHostProcess Start(HostLaunch launch, string godot, string script, ILogger logger)
    {
        string[] arguments = ["--headless", "--path", launch.ProjectDir, "--script", Path.GetFullPath(script).Replace('\\', '/')];
        return Open(launch, godot, arguments, logger);
    }

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> as a host's process, in the project folder with the
    /// host's variables set, its output written to its log line by line, each line put on disk as it is written, so the log is
    /// current while the host runs.
    /// </summary>
    /// <exception cref="SessionException">The file could not be started, or its log could not be opened for writing.</exception>
    internal static IHostProcess Open(HostLaunch launch, string fileName, IReadOnlyList<string> arguments, ILogger logger)
    {
        ToolProcessRequest request = new(fileName, arguments, launch.ProjectDir, launch.LogPath, Timeout.InfiniteTimeSpan)
        {
            SetVariables = new Dictionary<string, string>
            {
                [GodotCommandLine.OffVariable] = "1",
                [HeadlessHost.PortVariable] = launch.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [HeadlessHost.TokenVariable] = launch.Token,
            },
            FlushEachLine = true,
        };
        try
        {
            return new GodotHostProcess(ToolProcess.Start(request, logger), logger);
        }
        catch (Win32Exception e)
        {
            throw new SessionException($"Godot could not be started from {fileName}: {e.Message}.", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SessionException($"Godot could not be started from {fileName}: its log {launch.LogPath} could not be opened: {e.Message}", e);
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => run.Process.WaitForExitAsync(cancellationToken);

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return IsDisposed || run.Process.WaitForExit(timeout);
        }
        catch (InvalidOperationException) when (IsDisposed)
        {
            // Disposed by a release on another thread during the wait; the release killed it first.
            return true;
        }
    }

    public Task FinishOutputAsync() => run.FinishOutputAsync();

    public void KeepGameHandle(int gameProcessId)
    {
        Process? game = null;
        try
        {
            game = Process.GetProcessById(gameProcessId);
            // Reading Handle opens the process handle and keeps it on the object, so the wait after a kill has one to wait on.
            _ = game.Handle;
            _game = game;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            game?.Dispose();
            Log.HeadlessHostGameHandleFailed(logger, e, gameProcessId, run.Request.WorkingDirectory);
        }
    }

    public void Kill()
    {
        run.Kill();
        // The console wrapper's child holds the project folder until its own handle is signalled, tens of ms after the wrapper's.
        if (_game is { } game && !game.WaitForExit(ToolProcess.KillWait))
        {
            Log.ToolStillRunningAfterKill(logger, $"The warm headless host's Godot (pid {game.Id})", ToolProcess.KillWait.TotalSeconds);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            run.Dispose();
            _game?.Dispose();
        }
    }
}

/// <summary>
/// One warm headless host: a <c>godot --headless --script headless/host.gd</c> process on one project folder, dialled in
/// over the bridge's wire, answering <c>headless</c> requests one at a time. A request that passes its ceiling or stalls
/// kills the host; one the host exits during fails with the log from its request's marker. Either way the host has ended,
/// and its pool drops it.
/// </summary>
internal sealed class HeadlessHost
{
    public const string PortVariable = "GODOT_MCP_HOST_PORT";
    public const string TokenVariable = "GODOT_MCP_HOST_TOKEN";

    private const string MarkerPrefix = "[godot-mcp] request ";

    /// <summary>What the tool-process log lines call a host.</summary>
    private const string HostName = "The warm headless host";
    private const int LogTailLines = 20;
    private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(1);

    /// <summary>How long a stop waits, in wall time, for the host to answer its shutdown and exit before it is killed.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a host whose connection closed during a request is given to exit before it is killed.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private readonly IHostProcess _process;
    private readonly BridgeConnection _connection;
    private readonly LoadClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Lazy<Task> _release;
    private bool _busy;
    private bool _ended;
    private int _requests;
    private DateTimeOffset _lastReply;
    private ITimer? _idleTimer;
    private TimeSpan _idleLimit;

    private HeadlessHost(HostLaunch launch, IHostProcess process, BridgeConnection connection, LoadClock clock, ILogger logger)
    {
        ProjectDir = launch.ProjectDir;
        LogPath = launch.LogPath;
        ProcessId = process.Id;
        _process = process;
        _connection = connection;
        _clock = clock;
        _logger = logger;
        StartedAt = clock.Time.GetUtcNow();
        _lastReply = StartedAt;
        _release = new Lazy<Task>(() => Task.Run(ReleaseOnceAsync));
        Gone = Task.WhenAny(connection.Closed, process.WaitForExitAsync(CancellationToken.None));
    }

    /// <summary>
    /// The longest a release takes, in wall time: the kill's waits for the process and for the Godot that said hello, and the
    /// output's drain grace, with a margin for closing the connection.
    /// </summary>
    public static TimeSpan ReleaseLimit { get; } = (2 * ToolProcess.KillWait) + ToolProcess.OutputDrainGrace + TimeSpan.FromSeconds(5);

    public string ProjectDir { get; }

    /// <summary>The host's log: <see cref="LogPathOf"/> for its folder and its server.</summary>
    public string LogPath { get; }

    public int ProcessId { get; }

    /// <summary>When the host said hello, in UTC on the registry's clock.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the host last answered a request, in UTC on the registry's clock; <see cref="StartedAt"/> before its first.</summary>
    public DateTimeOffset LastReply
    {
        get
        {
            lock (_lock)
            {
                return _lastReply;
            }
        }
    }

    /// <summary>The folder's fingerprint taken after the host's last reply; null before its first.</summary>
    public ProjectFingerprint? Fingerprint { get; set; }

    /// <summary>Whether the host has been stopped, killed or dropped; an ended host serves no more requests.</summary>
    public bool HasEnded
    {
        get
        {
            lock (_lock)
            {
                return _ended;
            }
        }
    }

    /// <summary>Whether the connection is open and the process running.</summary>
    public bool IsAlive => _connection.IsOpen && !_process.HasExited;

    /// <summary>Completes once the connection closes or the process exits, whichever is first.</summary>
    public Task Gone { get; }

    /// <summary>
    /// The log of a host the server with process id <paramref name="serverProcessId"/> starts on the folder:
    /// <c>.godot/godot-mcp/headless-host-&lt;server pid&gt;.log</c>, started afresh by each host, so it holds one host's life
    /// and two servers on one folder never share it.
    /// </summary>
    public static string LogPathOf(string projectDir, int serverProcessId) =>
        Path.Combine(
            ProjectPrep.LogFolder(projectDir),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"headless-host-{serverProcessId}.log")
        );

    /// <summary><c>host.gd</c>, beside the headless operations script wherever that is found.</summary>
    /// <exception cref="SessionException">The operations script or the host script is missing.</exception>
    public static string FindHostScript()
    {
        string host = Path.Combine(Path.GetDirectoryName(Installation.FindHeadlessScript())!, "host.gd");
        return File.Exists(host) ? host : throw new SessionException($"The headless host script was not found at {host}.");
    }

    /// <summary>Starts a host on the folder and waits for its hello, within the launch's handshake time on the launch clock.</summary>
    /// <param name="registry">The listener it dials, its clocks, its logger and how its process starts.</param>
    /// <param name="projectDir">The normalised project folder.</param>
    /// <param name="what">"The headless &lt;op&gt; run on &lt;project&gt;", which a failure to start begins with.</param>
    /// <param name="cancellationToken">Withdraws the wait; the process is killed first.</param>
    /// <exception cref="SessionException">Godot could not be started, exited, or did not say hello in time.</exception>
    public static async Task<HeadlessHost> StartAsync(SessionRegistry registry, string projectDir, string what, CancellationToken cancellationToken)
    {
        string token = GodotSession.CreateToken();
        HostLaunch launch = new(projectDir, registry.Listener.Port, token, LogPathOf(projectDir, registry.ServerProcessId));
        IHostProcess process = registry.HostLauncher(launch);
        try
        {
            BridgeConnection connection = await AwaitHelloAsync(registry, process, launch, what, cancellationToken);
            if (connection.GameProcessId is int game)
            {
                process.KeepGameHandle(game);
            }

            Log.HeadlessHostStarted(registry.Logger, projectDir, process.Id);
            if (!process.CpuMeasurable)
            {
                Log.StallGuardOff(registry.Logger, HostName);
            }

            return new HeadlessHost(launch, process, connection, registry.Clock, registry.Logger);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Marks the host busy with a request; false when it has ended, or its connection or process is gone.</summary>
    public bool TryBeginRequest()
    {
        lock (_lock)
        {
            if (_ended || !IsAlive)
            {
                return false;
            }

            _busy = true;
            _requests++;
            return true;
        }
    }

    /// <summary>Marks the host idle after its request, notes the reply's time and re-arms the idle timer from it.</summary>
    public void EndRequest()
    {
        lock (_lock)
        {
            _busy = false;
            _lastReply = _clock.Time.GetUtcNow();
            _idleTimer?.Change(_idleLimit, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Calls <paramref name="onIdle"/> once <paramref name="limit"/> has passed on the registry's clock since the host's last
    /// reply (its start before its first), re-armed by each reply. It may fire while a request runs; the callback decides.
    /// Nothing for a host that has ended; the release disposes the timer.
    /// </summary>
    public void WatchIdle(TimeSpan limit, Action onIdle)
    {
        lock (_lock)
        {
            if (_ended)
            {
                return;
            }

            _idleLimit = limit;
            _idleTimer = _clock.Time.CreateTimer(_ => onIdle(), null, limit, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>What list_sessions shows of the host at <paramref name="now"/>: idle seconds since its last reply, 0 while busy.</summary>
    public HeadlessHostInfo Describe(DateTimeOffset now)
    {
        lock (_lock)
        {
            int idleSeconds = _busy ? 0 : (int)Math.Max(0, (now - _lastReply).TotalSeconds);
            return new HeadlessHostInfo(ProjectDir, ProcessId, StartedAt, _requests, idleSeconds);
        }
    }

    /// <summary>
    /// Marks an idle host ended, so no request can begin on it, for the caller to stop with <see cref="StopClaimed"/>; false
    /// when a request runs on it or it has already ended.
    /// </summary>
    public bool TryClaimIdle()
    {
        lock (_lock)
        {
            if (_busy || _ended)
            {
                return false;
            }

            _ended = true;
            return true;
        }
    }

    /// <summary>
    /// <see cref="TryClaimIdle"/> for the idle timer: only a host idle for its whole idle limit since its last reply is claimed.
    /// One that answered since the timer fired (or whose timer ran early) is kept, its timer re-armed for the rest of the limit.
    /// </summary>
    public bool TryClaimExpired()
    {
        lock (_lock)
        {
            if (_busy || _ended)
            {
                return false;
            }

            TimeSpan idle = _clock.Time.GetUtcNow() - _lastReply;
            if (idle < _idleLimit)
            {
                _idleTimer?.Change(_idleLimit - idle, Timeout.InfiniteTimeSpan);
                return false;
            }

            _ended = true;
            return true;
        }
    }

    /// <summary>Marks the host ended, whether or not a request runs on it; false when it had already ended.</summary>
    public bool TryMarkEnded()
    {
        lock (_lock)
        {
            if (_ended)
            {
                return false;
            }

            _ended = true;
            return true;
        }
    }

    /// <summary>
    /// Stops a host the caller has marked ended (<see cref="TryClaimIdle"/>, <see cref="TryClaimExpired"/>,
    /// <see cref="TryMarkEnded"/>) as <see cref="Stop"/> does, and completes once it is released, blocking no thread meanwhile.
    /// </summary>
    public Task StopClaimedAsync(string reason) => QuitAsync(reason, CancellationToken.None);

    /// <summary>Ends an idle host; false when a request is running on it or it has already ended.</summary>
    public bool TryEndIdle()
    {
        lock (_lock)
        {
            if (_busy || _ended)
            {
                return false;
            }

            _ended = true;
            return true;
        }
    }

    /// <summary>
    /// Sends one request and returns the host's result object, <c>{ok, result?, error?, engineErrors}</c>, watching its ceiling
    /// on the connection's clock and its stall on the host tree's CPU and output while it runs.
    /// </summary>
    /// <exception cref="SessionException">
    /// The request passed its ceiling or stalled (the host is killed), the host ended before it answered, or it answered with
    /// no result object.
    /// </exception>
    /// <exception cref="OperationCanceledException">The call was cancelled; the host is killed first.</exception>
    public async Task<JsonObject> RequestAsync(HostRequest request, CancellationToken cancellationToken)
    {
        long linesAtStart = _process.OutputLines;
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource done = new();
        Task watch = WatchForStallAsync(stalled, done.Token);
        Exception failure;
        try
        {
            JsonObject body = new() { ["op"] = request.Operation, ["params"] = request.Parameters };
            JsonNode? result = await _connection.SendAsync("headless", body, request.Ceiling, stalled.Token);
            return result as JsonObject
                ?? throw new SessionException($"{request.What} answered with a result that is not a JSON object: {result?.ToJsonString() ?? "null"}");
        }
        catch (Exception e) when (e is LoadTimeoutException or OperationCanceledException or IOException or InvalidOperationException)
        {
            failure = e;
        }
        finally
        {
            // The watch reads the process, so it ends before a failure lets go of the host.
            await done.CancelAsync();
            await watch;
        }

        throw await FailAsync(request, failure, linesAtStart, cancellationToken);
    }

    /// <summary>
    /// Stops the host: <c>shutdown</c>, and <see cref="ShutdownWait"/> of wall time in all to answer and exit, then its tree is
    /// killed and waited for. Nothing when it has already ended.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller stopped waiting; the host is still killed and let go of, without it.</exception>
    public void Stop(string reason, CancellationToken cancellationToken)
    {
        if (TryMarkEnded())
        {
            QuitAsync(reason, cancellationToken).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Ends the host once it is marked ended: <c>shutdown</c>, then the release, which kills whatever is left and is waited for.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller stopped waiting; the release runs on without it.</exception>
    private async Task QuitAsync(string reason, CancellationToken cancellationToken)
    {
        Task release;
        try
        {
            await AskToQuitAsync(cancellationToken);
        }
        finally
        {
            // Started even when the caller stopped waiting: the release kills the host and runs on without it.
            release = ReleaseAsync();
            Log.HeadlessHostStopped(_logger, ProjectDir, ProcessId, reason);
        }

        await release.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Ends a host that said hello and was gone before its first request, and says so as a start failure: its exit code and
    /// the last lines of its log.
    /// </summary>
    public async Task<SessionException> EndAtStartAsync(string what)
    {
        await WaitForExitAsync();
        int code = _process.HasExited ? _process.ExitCode : -1;
        await EndAsync("it ended before its first request");
        string tail = string.Join('\n', LogLines(LogPath, _process.OutputLines).TakeLast(LogTailLines));
        return new SessionException(
            $"{what} ended (Godot exited {code}) before its warm headless host took its first request. The last lines of its log, {LogPath}:\n{tail}"
        );
    }

    /// <summary>
    /// Lets go of an ended host: kills its tree and waits until it is gone (a no-op after its exit), closes the connection,
    /// waits for the rest of its output and closes its log. The first call starts it; every call completes with that one, so
    /// its log is closed once any call has completed.
    /// </summary>
    public Task ReleaseAsync() => _release.Value;

    private static async Task<BridgeConnection> AwaitHelloAsync(
        SessionRegistry registry,
        IHostProcess process,
        HostLaunch launch,
        string what,
        CancellationToken cancellationToken
    )
    {
        using LoadDeadline deadline = registry.LaunchClock.Start(GodotSession.HandshakeTimeout, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<BridgeConnection> accept = registry.Listener.AcceptBridgeAsync(new HandshakeExpectation(launch.Token, launch.ProjectDir), timeout.Token);
        Task exited = process.WaitForExitAsync(timeout.Token);
        await Task.WhenAny(accept, exited);
        if (accept.IsCompletedSuccessfully)
        {
            await timeout.CancelAsync();
            await exited.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return await accept;
        }

        await timeout.CancelAsync();
        await ObserveAbandonedAsync(accept, exited);
        bool exitedEarly = process.HasExited;
        int code = exitedEarly ? process.ExitCode : -1;
        process.Kill();
        await process.FinishOutputAsync();
        long lines = process.OutputLines;
        process.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        string log = launch.LogPath;
        string tail = string.Join('\n', LogLines(log, lines).TakeLast(LogTailLines));
        string backstop = deadline.Reason == DeadlineReason.Backstop ? deadline.BackstopClause() : string.Empty;
        string failed = exitedEarly
            ? $"{what} ended (Godot exited {code}) before its warm headless host connected"
            : $"{what} did not start: its warm headless host did not connect within {GodotSession.HandshakeTimeout.TotalSeconds:0} s{backstop}, "
                + "so it was stopped with its whole process tree";
        throw new SessionException($"{failed}. The last lines of its log, {log}:\n{tail}");
    }

    /// <summary>Waits out an abandoned hello wait; a bridge that connected after all is closed.</summary>
    private static async Task ObserveAbandonedAsync(Task<BridgeConnection> accept, Task exited)
    {
        await Task.WhenAll(accept, exited).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (accept.IsCompletedSuccessfully)
        {
            await accept.Result.DisposeAsync();
        }
    }

    /// <summary>
    /// The last <paramref name="count"/> lines of the log: the ones the host process wrote since it had written
    /// <c>OutputLines - count</c>, since the host holds its own log alone and writes to its end. Read after the process let go of it.
    /// </summary>
    private static List<string> LogLines(string log, long count) =>
        count <= 0 || !File.Exists(log) ? [] : [.. File.ReadLines(log).TakeLast((int)Math.Min(count, int.MaxValue))];

    /// <summary>Ends the host after a request failed, and says why the request failed; a cancelled call gets its own exception back.</summary>
    private async Task<Exception> FailAsync(HostRequest request, Exception failure, long linesAtStart, CancellationToken cancellationToken)
    {
        switch (failure)
        {
            // A connection closed under the request, by the host's exit or by a release on another thread.
            case IOException or ObjectDisposedException:
                return await EndedAsync(request, linesAtStart);
            case InvalidOperationException refused:
                return new SessionException($"{request.What} was refused by its warm headless host: {refused.Message}", refused);
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                await EndAsync("its request was cancelled");
                return failure;
        }

        (KillReason reason, string detail) = failure is LoadTimeoutException timedOut
            ? ToolProcess.DeadlineKill(timedOut.Deadline)
            : (KillReason.Stall, $"stalled: no output and no CPU for {LoadDeadline.Seconds(ToolProcess.DefaultStallLimit)} s");
        string phrase = ToolProcess.KillPhrase(reason, detail);
        await EndAsync($"its {request.Operation} request did not finish {phrase}");
        return new SessionException(
            $"{request.What} did not finish {phrase}, so it was stopped with its whole process tree. Its log: {LogPath}",
            failure
        );
    }

    /// <summary>The failure of a request the host exited during: its exit code and the log from the request's marker on.</summary>
    private async Task<SessionException> EndedAsync(HostRequest request, long linesAtStart)
    {
        await WaitForExitAsync();
        int code = _process.HasExited ? _process.ExitCode : -1;
        await EndAsync($"it ended during its {request.Operation} request");
        List<string> lines = LogLines(LogPath, _process.OutputLines - linesAtStart);
        int marker = lines.FindLastIndex(line => line.StartsWith(MarkerPrefix, StringComparison.Ordinal));
        IEnumerable<string> quoted = marker < 0 ? lines.TakeLast(LogTailLines) : [lines[marker], .. lines.Skip(marker + 1).TakeLast(LogTailLines)];
        return new SessionException(
            $"{request.What} ended (Godot exited {code}) before it answered. Its log from the request on, {LogPath}:\n" + string.Join('\n', quoted)
        );
    }

    /// <summary>
    /// Gives a host whose connection closed <see cref="ExitWait"/> to exit; nothing for one that has exited, or whose process a
    /// release on another thread (the server's exit) has already let go of.
    /// </summary>
    private async Task WaitForExitAsync()
    {
        if (!_process.HasExited)
        {
            await Task.Run(() => _process.WaitForExit(ExitWait));
        }
    }

    /// <summary>Marks the host ended and lets go of it off the caller's thread.</summary>
    private async Task EndAsync(string reason)
    {
        lock (_lock)
        {
            _ended = true;
        }

        await ReleaseAsync();
        Log.HeadlessHostStopped(_logger, ProjectDir, ProcessId, reason);
    }

    private async Task ReleaseOnceAsync()
    {
        ITimer? idleTimer;
        lock (_lock)
        {
            idleTimer = _idleTimer;
            _idleTimer = null;
        }

        if (idleTimer is not null)
        {
            await idleTimer.DisposeAsync();
        }

        _process.Kill();
        await _connection.DisposeAsync();
        await _process.FinishOutputAsync();
        _process.Dispose();
    }

    /// <summary>Sends <c>shutdown</c> and waits for the exit, <see cref="ShutdownWait"/> in all; an unanswered one is logged.</summary>
    /// <exception cref="OperationCanceledException">The caller stopped waiting.</exception>
    private async Task AskToQuitAsync(CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _connection.SendRawAsync("shutdown", null, ShutdownWait, cancellationToken);
            TimeSpan left = ShutdownWait - waited.Elapsed;
            TimeSpan wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            // The synchronous wait returns only once Windows lets go of the process (DEVELOPMENT.md footgun), so it runs off-thread.
            await Task.Run(() => _process.WaitForExit(wait), CancellationToken.None).WaitAsync(cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log.HeadlessHostShutdownUnanswered(_logger, ProjectDir, e.Message);
        }
    }

    /// <summary>
    /// Cancels <paramref name="request"/> once neither the host's output nor its tree's CPU has moved for the stall limit,
    /// checking every second on the connection's clock until <paramref name="done"/>.
    /// </summary>
    private async Task WatchForStallAsync(CancellationTokenSource request, CancellationToken done)
    {
        if (!_process.CpuMeasurable)
        {
            return;
        }

        StallWatch stall = new(_clock.Time, ToolProcess.DefaultStallLimit);
        while (!done.IsCancellationRequested)
        {
            await Task.Delay(CheckPeriod, _clock.Time, done).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!done.IsCancellationRequested && stall.Observe(_process.OutputLines, _process.CpuTicks))
            {
                Log.ToolStalled(_logger, HostName, ToolProcess.DefaultStallLimit.TotalSeconds);
                await request.CancelAsync();
                return;
            }
        }
    }
}
