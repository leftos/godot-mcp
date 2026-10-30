using System.Collections.Concurrent;
using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A launch whose output says Godot could not create a window fails at once, quoting the line its process printed, instead of
/// waiting out the handshake; the OpenGL 3.3 warning before a working ANGLE fallback is not that failure. Each launch's process
/// is a fake over a real child process, so a run has a pid and an exit while the test feeds the lines a reader thread would.
/// </summary>
[Collection(GodotPathCollection)]
public sealed class LaunchRefusesNoGlTests : IAsyncDisposable
{
    /// <summary>The test classes that set GODOT_PATH for their launches, run one at a time so none restores it under another.</summary>
    internal const string GodotPathCollection = "GODOT_PATH";

    private const string AngleError = "ERROR: Failed to create ANGLE OpenGL window.";

    private const string OpenGlWarning = "WARNING: Your video card drivers seem not to support the required OpenGL 3.3 version, switching to ANGLE.";

    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;
    private readonly ConcurrentQueue<FakeRunProcess> _launched = new();
    private readonly string? _configuredGodot;

    public LaunchRefusesNoGlTests()
    {
        // Both clocks are frozen: a wait that only ends on the handshake's timeout never ends, so a refusal that comes back is
        // the output's and not the deadline's.
        _clock = new LoadClock(_time, new NoLoadSource());
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance)
        {
            LaunchClock = _clock,
            OverrideFolders = new OverrideFolders(_temp.Combine("override-folders.txt"), TextWriter.Null) { Clock = _clock },
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            RunProcessFactory = Launch,
        };
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
        while (_sessions.List(includeStopped: true).Any(session => session.Live))
        {
            Assert.True(DateTime.UtcNow < guard, "a stopped run's exit was not taken within its hang guard");
            await Task.Delay(20);
        }

        _sessions.Dispose();
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

    /// <summary>The run's process factory: a fake over a real child process, its start info kept for the bridge's token.</summary>
    private FakeRunProcess Launch(ProcessStartInfo startInfo, bool _)
    {
        var run = new FakeRunProcess(startInfo);
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

    /// <summary>
    /// A fake run process: a real started child process, so the run has a pid and an exit, whose output the test passes on
    /// through the handlers <see cref="BeginRead"/> was given rather than read from the child.
    /// </summary>
    private sealed class FakeRunProcess : IRunProcess, IDisposable
    {
        private Action<string>? _stdout;
        private Action<string>? _stderr;
        private Action? _ended;
        private int _endedOnce;

        public FakeRunProcess(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            Process = RegistryHarness.StartStandInGame();
            Process.EnableRaisingEvents = true;
            // As a killed run's streams end and call the handler's `ended`, which closes its output file.
            Process.Exited += (_, _) => EndOutput();
        }

        /// <summary>The start info the launch built, whose environment carries the bridge's port and token.</summary>
        public ProcessStartInfo StartInfo { get; }

        public Process Process { get; }

        /// <summary>The token the run's bridge must prove itself with.</summary>
        public string Token => StartInfo.Environment[GodotCommandLine.TokenVariable]!;

        public void Start()
        {
            // The child is already running; the run only needs its pid and its exit.
        }

        public void BeginRead(Action<string> stdout, Action<string> stderr, Action ended)
        {
            _stdout = stdout;
            _stderr = stderr;
            _ended = ended;
        }

        public void CancelRead()
        {
            _stdout = null;
            _stderr = null;
        }

        /// <summary>Passes a line on as the child's stdout reader would.</summary>
        public void WriteStdout(string line) => _stdout?.Invoke(line);

        /// <summary>Passes a line on as the child's stderr reader would.</summary>
        public void WriteStderr(string line) => _stderr?.Invoke(line);

        /// <summary>Kills the child, waits for it to be gone, and lets the run's output end as a stopped process's does.</summary>
        public async Task EndAsync(CancellationToken cancellationToken)
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }

            await ExitedAsync(cancellationToken);
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

        private void EndOutput()
        {
            if (Interlocked.Increment(ref _endedOnce) == 1)
            {
                _ended?.Invoke();
            }
        }
    }
}
