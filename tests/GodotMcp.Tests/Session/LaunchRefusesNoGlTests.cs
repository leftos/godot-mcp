using System.Collections.Concurrent;
using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A launch whose output says Godot could not create a window fails at once, quoting the line its process printed, instead of
/// waiting out the handshake; the OpenGL 3.3 warning before a working ANGLE fallback is not that failure. A launch whose Godot
/// crashed at boot with 0xC06D007F is retried once on a registry that says so (the itest harness's), and refused as before
/// everywhere else. Each launch's process is a fake over a real child process, so a run has a pid and an exit while the test
/// feeds the lines a reader thread would.
/// </summary>
[Collection(GodotPathCollection)]
public sealed class LaunchRefusesNoGlTests : IAsyncDisposable
{
    /// <summary>The test classes that set GODOT_PATH for their launches, run one at a time so none restores it under another.</summary>
    internal const string GodotPathCollection = "GODOT_PATH";

    private const string AngleError = "ERROR: Failed to create ANGLE OpenGL window.";

    private const string OpenGlWarning = "WARNING: Your video card drivers seem not to support the required OpenGL 3.3 version, switching to ANGLE.";

    /// <summary>The exit code of Godot's crash at boot in the GPU driver's start-up: 0xC06D007F, a delay-load "procedure not found".</summary>
    private const int BootCrash = -1066598273;

    private const string BootCrashRetried = "Godot crashed at boot with 0xC06D007F; retrying the launch once (itest harness)";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;
    private readonly SessionRegistry _retrying;
    private readonly RecordingLogger _retryingLog = new();
    private readonly ConcurrentQueue<FakeRunProcess> _launched = new();

    // The exit codes the next launches' processes exit with, in order, each at once or once the test releases it (held); a
    // launch with none left gets a stand-in game.
    private readonly ConcurrentQueue<(int Code, bool Held)> _crashes = new();
    private readonly string? _configuredGodot;

    public LaunchRefusesNoGlTests()
    {
        // Both clocks are frozen: a wait that only ends on the handshake's timeout never ends, so a refusal that comes back is
        // the output's and not the deadline's.
        _clock = new LoadClock(_time, new NoLoadSource());
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        _sessions = NewRegistry("plain", NullLogger<GodotSession>.Instance, retryBootCrashOnce: false);
        _retrying = NewRegistry("retrying", _retryingLog, retryBootCrashOnce: true);
        // The launch only has to find a file: the factory replaces the process it would start.
        _configuredGodot = Environment.GetEnvironmentVariable(Installation.GodotPathVariable);
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, typeof(LaunchRefusesNoGlTests).Assembly.Location);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Installation.GodotPathVariable, _configuredGodot);
        foreach (FakeRunProcess run in _launched)
        {
            await run.EndAsync(CancellationToken.None);
        }

        DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
        while (_sessions.List(includeStopped: true).Concat(_retrying.List(includeStopped: true)).Any(session => session.Live))
        {
            Assert.True(DateTime.UtcNow < guard, "a stopped run's exit was not taken within its hang guard");
            await Task.Delay(20);
        }

        _sessions.Dispose();
        _retrying.Dispose();
        _listener.Dispose();
        _clock.Dispose();
        foreach (FakeRunProcess run in _launched)
        {
            run.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task ALaunchWhoseStderrSaysAngleCouldNotMakeAWindowIsRefusedBeforeTheTimeout()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        FakeRunProcess run = await StartedAsync(launch);

        run.WriteStderr(OpenGlWarning);
        run.WriteStderr(AngleError);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => launch.WaitAsync(RegistryHarness.HangGuard, cancellation));

        Assert.Contains("Failed to create ANGLE OpenGL window", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no usable OpenGL", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not connect within", refused.Message, StringComparison.Ordinal);
        await run.ExitedAsync(cancellation);
    }

    [Fact]
    public async Task ALaunchWhoseStdoutSaysAngleCouldNotMakeAWindowIsRefusedBeforeTheTimeout()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        FakeRunProcess run = await StartedAsync(launch);

        run.WriteStdout(OpenGlWarning);
        run.WriteStdout(AngleError);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => launch.WaitAsync(RegistryHarness.HangGuard, cancellation));

        Assert.Contains("Failed to create ANGLE OpenGL window", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no usable OpenGL", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not connect within", refused.Message, StringComparison.Ordinal);
        await run.ExitedAsync(cancellation);
    }

    [Fact]
    public async Task TheOpenGlWarningAloneAndABridgeThatConnectsStillLaunches()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _sessions.LaunchAsync(Request(project), "server", cancellation);
        FakeRunProcess run = await StartedAsync(launch);

        run.WriteStderr(OpenGlWarning);
        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, run.Token, project, cancellation);

        LaunchResult started = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);

        Assert.Equal(project, started.ProjectPath);
        Assert.False(run.Process.HasExited, "the OpenGL 3.3 warning is not a refusal");
    }

    [Fact]
    public async Task ABootCrashIsRetriedOnceWhenTheRegistrySaysSoAndTheSecondRunLaunches()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueCrash(BootCrash);
        Task<LaunchResult> launch = _retrying.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 2, launch);
        FakeRunProcess second = _launched.Last();

        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, second.Token, project, cancellation);
        LaunchResult started = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);

        FakeRunProcess first = _launched.First();
        string marker = BootCrashMarker(first.Pid!.Value, second.Pid!.Value);
        Assert.Equal(second.Process.Id, started.ProcessId);
        Assert.Single(_retryingLog.Warnings, warning => warning == BootCrashRetried);
        Assert.Throws<InvalidOperationException>(() => first.Process.HasExited);
        Assert.Contains(marker, _retrying.GetDebugOutput("server", 100, null).Stdout);
        Assert.Contains(marker, ReadShared(started.Log!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterARetryTheCrashedRunsExitLeavesTheOverrideAndTheSessionLive()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueCrash(BootCrash);
        Task<LaunchResult> launch = _retrying.LaunchAsync(Request(project), "server", cancellation);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 2, launch);
        using FakeBridge game = await FakeBridge.DialAsync(_listener.Port, _launched.Last().Token, project, cancellation);
        await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);

        await RegistryHarness.WaitUntilAsync(() =>
            _retryingLog.Messages.Any(entry =>
                entry.Level == LogLevel.Information && entry.Text.StartsWith("A replaced Godot run on", StringComparison.Ordinal)
            )
        );

        Assert.True(File.Exists(Path.Combine(project, OverrideFile.FileName)), "the crashed run's exit removed the new run's override.cfg");
        Assert.True(_retrying.List(includeStopped: true).Single(session => session.Name == "server").Live);
    }

    [Fact]
    public async Task ABootCrashRetriedDuringARestartStopsTheOldGameOnceAndCarriesItsOutputAndLog()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        Task<LaunchResult> launch = _retrying.LaunchAsync(Request(project), "server", cancellation);
        FakeRunProcess first = await StartedAsync(launch);
        using FakeBridge oldGame = await FakeBridge.DialAsync(_listener.Port, first.Token, project, cancellation);
        LaunchResult launched = await launch.WaitAsync(RegistryHarness.HangGuard, cancellation);
        first.WriteStdout("before the restart");
        QueueCrash(BootCrash);

        Task<object> restart = _retrying.RestartAsync("server", prepare: false, cancellation);
        int shutdowns = 0;
        while (shutdowns == 0)
        {
            string? command = await oldGame.AnswerOneAsync("quit", cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation);
            shutdowns += command == "shutdown" ? 1 : 0;
        }

        await first.EndAsync(cancellation);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 3, restart);
        FakeRunProcess crashed = _launched.ElementAt(1);
        FakeRunProcess third = _launched.Last();
        using FakeBridge newGame = await FakeBridge.DialAsync(_listener.Port, third.Token, project, cancellation);
        var restarted = (RestartResult)await restart.WaitAsync(RegistryHarness.HangGuard, cancellation);

        string[] carried =
        [
            "before the restart",
            $"[godot-mcp] restarted: the previous game (pid {first.Pid}) was stopped; output below is from pid {crashed.Pid}.",
            BootCrashMarker(crashed.Pid!.Value, third.Pid!.Value),
        ];
        IReadOnlyList<string> stdout = _retrying.GetDebugOutput("server", 100, null).Stdout;
        string file = ReadShared(restarted.Log!);
        Assert.Equal(first.Pid, restarted.PreviousProcessId);
        Assert.Equal(third.Pid, restarted.ProcessId);
        Assert.Null(restarted.PreviousKillReason);
        Assert.Equal(launched.Log, restarted.Log);
        Assert.All(carried, line => Assert.Contains(line, stdout));
        Assert.All(carried, line => Assert.Contains(line, file, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AShutdownBegunBeforeTheRetryRefusesIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueHeldCrash(BootCrash);
        Task<LaunchResult> launch = _retrying.LaunchAsync(Request(project), "server", cancellation);
        FakeRunProcess first = await CrashStartedAsync(launch);
        SemaphoreSlim folderLock = _retrying.PrepLock(project);
        await folderLock.WaitAsync(cancellation);
        Task shutdown;
        try
        {
            first.ReleaseCrash();
            // The retry has stopped the crashed run's output capture and waits for the folder's lock the test holds.
            await RegistryHarness.WaitUntilAsync(() => first.ReadCancelled, launch);
            shutdown = Task.Run(_retrying.Shutdown, cancellation);
            await RegistryHarness.WaitUntilAsync(IsShuttingDown, shutdown);
        }
        finally
        {
            folderLock.Release();
        }

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => launch.WaitAsync(RegistryHarness.HangGuard, cancellation));

        Assert.Equal(SessionRegistry.ShuttingDown, refused.Message);
        Assert.Single(_launched);
        Assert.DoesNotContain(BootCrashRetried, _retryingLog.Warnings);
        await shutdown.WaitAsync(RegistryHarness.HangGuard, cancellation);
    }

    [Fact]
    public async Task ACancelledWaitForTheRetrysLockDisposesTheCrashedRunWithoutLoggingARetry()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueHeldCrash(BootCrash);
        using var cancelLaunch = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<LaunchResult> launch = _retrying.LaunchAsync(Request(project), "server", cancelLaunch.Token);
        FakeRunProcess first = await CrashStartedAsync(launch);
        SemaphoreSlim folderLock = _retrying.PrepLock(project);
        await folderLock.WaitAsync(cancellation);
        try
        {
            first.ReleaseCrash();
            await RegistryHarness.WaitUntilAsync(() => first.ReadCancelled, launch);
            await cancelLaunch.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch.WaitAsync(RegistryHarness.HangGuard, cancellation));
        }
        finally
        {
            folderLock.Release();
        }

        Assert.Throws<InvalidOperationException>(() => first.Process.HasExited);
        Assert.Single(_launched);
        Assert.DoesNotContain(BootCrashRetried, _retryingLog.Warnings);
    }

    private static string BootCrashMarker(int crashed, int started) =>
        $"[godot-mcp] Godot (pid {crashed}) crashed at boot with 0xC06D007F; retried, output below is from pid {started}";

    /// <summary>A run's output file as it stands, read while the run may still hold it open.</summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Whether the retrying registry's exit has begun, so a start is refused.</summary>
    private bool IsShuttingDown()
    {
        try
        {
            _retrying.RefuseStartAtShutdown();
            return false;
        }
        catch (SessionException)
        {
            return true;
        }
    }

    private void QueueCrash(int code) => _crashes.Enqueue((code, Held: false));

    private void QueueHeldCrash(int code) => _crashes.Enqueue((code, Held: true));

    /// <summary>The launch's first fake process, once its crashing child has started.</summary>
    private async Task<FakeRunProcess> CrashStartedAsync(Task<LaunchResult> launch)
    {
        FakeRunProcess first = await StartedAsync(launch);
        await RegistryHarness.WaitUntilAsync(() => first.Pid is not null, launch);
        return first;
    }

    [Fact]
    public async Task ABootCrashIsRefusedAsBeforeWhenTheRegistryDoesNotRetry()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueCrash(BootCrash);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _sessions.LaunchAsync(Request(project), "server", cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation)
        );

        Assert.Contains($"Godot exited with code {BootCrash} before the bridge connected", refused.Message, StringComparison.Ordinal);
        Assert.Single(_launched);
    }

    [Fact]
    public async Task AnEarlyExitWithAnotherCodeIsRefusedWithoutARetry()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueCrash(3);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _retrying.LaunchAsync(Request(project), "server", cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation)
        );

        Assert.Contains("Godot exited with code 3 before the bridge connected", refused.Message, StringComparison.Ordinal);
        Assert.Single(_launched);
        Assert.DoesNotContain(BootCrashRetried, _retryingLog.Warnings);
    }

    [Fact]
    public async Task ASecondBootCrashIsRefusedAfterTheOneRetry()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Project("game");
        QueueCrash(BootCrash);
        QueueCrash(BootCrash);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _retrying.LaunchAsync(Request(project), "server", cancellation).WaitAsync(RegistryHarness.HangGuard, cancellation)
        );

        Assert.Contains($"Godot exited with code {BootCrash} before the bridge connected", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, _launched.Count);
        Assert.Single(_retryingLog.Warnings, warning => warning == BootCrashRetried);
    }

    private SessionRegistry NewRegistry(string name, ILogger<GodotSession> logger, bool retryBootCrashOnce) =>
        new(_listener, logger)
        {
            LaunchClock = _clock,
            OverrideFolders = new OverrideFolders(_temp.Combine($"{name}-override-folders.txt"), TextWriter.Null) { Clock = _clock },
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            RunProcessFactory = Launch,
            RetryBootCrashOnce = retryBootCrashOnce,
        };

    /// <summary>
    /// The run's process factory: a fake over a real child process, its start info kept for the bridge's token. The child exits
    /// with the next queued crash code, or is a stand-in game when none is queued.
    /// </summary>
    private FakeRunProcess Launch(ProcessStartInfo startInfo, bool _)
    {
        FakeRunProcess run = _crashes.TryDequeue(out (int Code, bool Held) crash)
            ? new FakeRunProcess(startInfo, crash.Code, crash.Held)
            : new FakeRunProcess(startInfo, crashExitCode: null, held: false);
        _launched.Enqueue(run);
        return run;
    }

    /// <summary>The one fake process the launch created, once it has one.</summary>
    private async Task<FakeRunProcess> StartedAsync(Task<LaunchResult> launch)
    {
        await RegistryHarness.WaitUntilAsync(() => !_launched.IsEmpty, launch);
        return _launched.First();
    }

    private string Project(string folder)
    {
        string projectDir = _temp.Combine(folder);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        return projectDir;
    }

    private static LaunchRequest Request(string project) => new(project, null, [], [], Quiet: false, ShutOutRealGamepads: false, Prepare: false);

    /// <summary>Every message logged through the registry's logger, with its level, formatted.</summary>
    private sealed class RecordingLogger : ILogger<GodotSession>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _messages = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Messages => [.. _messages];

        public IReadOnlyList<string> Warnings => [.. _messages.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Text)];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A fake run process: a real started child process, so the run has a pid and an exit, whose output the test passes on
    /// through the handlers <see cref="BeginRead"/> was given rather than read from the child. A crashing one's child exits with
    /// its exit code, at once or once <see cref="ReleaseCrash"/> is called; it starts in <see cref="Start"/>, and an exit
    /// before the handlers are in place still reaches the run, so its
    /// output's end reaches the run.
    /// </summary>
    private sealed class FakeRunProcess : IRunProcess, IDisposable
    {
        private readonly bool _crashes;
        private Action<string>? _stdout;
        private Action<string>? _stderr;
        private Action? _ended;
        private int _endedOnce;
        private volatile bool _readCancelled;

        // The child's pid once it has started; 0 until then.
        private volatile int _pid;

        /// <param name="startInfo">The start info the launch built.</param>
        /// <param name="crashExitCode">The code the child exits with; null for a stand-in game that runs until killed.</param>
        /// <param name="held">Whether a crashing child waits for <see cref="ReleaseCrash"/> before it exits.</param>
        public FakeRunProcess(ProcessStartInfo startInfo, int? crashExitCode, bool held)
        {
            StartInfo = startInfo;
            _crashes = crashExitCode is not null;
            Process = crashExitCode is int exitCode ? CrashingChild(exitCode, held) : RegistryHarness.StartStandInGame();
            if (!_crashes)
            {
                _pid = Process.Id;
            }

            Process.EnableRaisingEvents = true;
            // As a killed run's streams end and call the handler's `ended`, which closes its output file.
            Process.Exited += (_, _) => EndOutput();
        }

        /// <summary>The start info the launch built, whose environment carries the bridge's port and token.</summary>
        public ProcessStartInfo StartInfo { get; }

        public Process Process { get; }

        /// <summary>The token the run's bridge must prove itself with.</summary>
        public string Token => StartInfo.Environment[GodotCommandLine.TokenVariable]!;

        /// <summary>The child's pid, kept from its start so it reads after the run disposed the process; null until it starts.</summary>
        public int? Pid => _pid == 0 ? null : _pid;

        /// <summary>Whether the run stopped reading this process's output, as a restart or a retry does to the run it replaces.</summary>
        public bool ReadCancelled => _readCancelled;

        /// <summary>Lets a held crashing child exit with its code.</summary>
        public void ReleaseCrash() => Process.StandardInput.WriteLine("crash");

        public void Start()
        {
            // A stand-in game's child is already running; the run only needs its pid and its exit. A crashing child starts here,
            // as a real run's process does, so the run reads its pid before its output.
            if (_crashes)
            {
                Process.Start();
                _pid = Process.Id;
            }
        }

        public void BeginRead(Action<string> stdout, Action<string> stderr, Action ended)
        {
            _stdout = stdout;
            _stderr = stderr;
            _ended = ended;
            // A child that exited before the handlers were in place still ends the run's output, as its drained pipes would.
            if (_crashes && Process.HasExited)
            {
                EndOutput();
            }
        }

        public void CancelRead()
        {
            _stdout = null;
            _stderr = null;
            _readCancelled = true;
        }

        /// <summary>Passes a line on as the child's stdout reader would.</summary>
        public void WriteStdout(string line) => _stdout?.Invoke(line);

        /// <summary>Passes a line on as the child's stderr reader would.</summary>
        public void WriteStderr(string line) => _stderr?.Invoke(line);

        /// <summary>
        /// Kills the child, waits for it to be gone, and lets the run's output end as a stopped process's does. A child whose run
        /// disposed it (a retry's crashed run, a restart's replaced one) had exited first, so nothing is left to kill or wait on.
        /// </summary>
        public async Task EndAsync(CancellationToken cancellationToken)
        {
            bool disposed = false;
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                disposed = true;
            }

            if (!disposed)
            {
                await ExitedAsync(cancellationToken);
            }

            EndOutput();
        }

        /// <summary>
        /// Waits for the child to be gone, on the hang guard alone: a passing run never reaches it, and a process this waits
        /// for exits on its own or not at all, so no window of wall time is asserted.
        /// </summary>
        public async Task ExitedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Process.WaitForExitAsync(cancellationToken).WaitAsync(RegistryHarness.HangGuard, cancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail("a fake run's process did not exit within its hang guard, so the test hung");
            }
        }

        public void Dispose() => Process.Dispose();

        /// <summary>
        /// A child, not yet started, that exits with <paramref name="exitCode"/>: at once, or when held, once a line reaches its
        /// stdin (<c>set /p</c> reads it).
        /// </summary>
        private static Process CrashingChild(int exitCode, bool held) =>
            new()
            {
                StartInfo = new ProcessStartInfo("cmd.exe", held ? $"/c \"set /p _= & exit {exitCode}\"" : $"/c exit {exitCode}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = held,
                },
            };

        private void EndOutput()
        {
            // Counted only once the handler is in place, so an exit before BeginRead is passed on by BeginRead itself.
            if (_ended is { } ended && Interlocked.Increment(ref _endedOnce) == 1)
            {
                ended();
            }
        }
    }
}
