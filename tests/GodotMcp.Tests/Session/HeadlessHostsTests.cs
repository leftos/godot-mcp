using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The pool of warm headless hosts on a fake clock, each host a fake process that dials the real listener over loopback:
/// reuse, invalidation by the fingerprint, C# projects kept cold, a host that exits mid-request, passes its ceiling, stalls,
/// is cancelled, refuses or answers with no object, one that never says hello, exits before it or right after it, a folder
/// that became C#, a slow release, the stops, and a host's log current on disk while it runs.
/// </summary>
public sealed class HeadlessHostsTests : IDisposable
{
    private const string Operation = "validate";
    private const int ServerPid = 1_001;
    private const int OtherServerPid = 1_002;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _registry;
    private readonly ConcurrentQueue<FakeHost> _launched = new();
    private readonly RecordingLogger _log = new();
    private FakeHostMode _launchMode = FakeHostMode.Answer;

    public HeadlessHostsTests()
    {
        _clock = new LoadClock(_time, new FakeLoadSource(_time, processors: 4));
        _listener = new BridgeListener(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        _registry = new SessionRegistry(_listener, _log)
        {
            OverrideFolders = new OverrideFolders(_temp.Combine("override-folders.txt"), TextWriter.Null),
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            HostLauncher = Launch,
            ServerProcessId = ServerPid,
        };
    }

    private HeadlessHosts Hosts => _registry.HeadlessHosts;

    public void Dispose()
    {
        _registry.Dispose();
        _listener.Dispose();
        _clock.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task TwoRequestsOnOneFolderAreAnsweredByOneHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");

        JsonObject first = await RunAsync(game, cancellation);
        JsonObject second = await RunAsync(game, cancellation);

        FakeHost host = Assert.Single(_launched);
        Assert.Equal(host.Id, PidIn(first));
        Assert.Equal(host.Id, PidIn(second));
        Assert.Equal(host.Id, Hosts.ProcessIdOf(game));
        Assert.Equal(2, host.HeadlessRequests);
    }

    [Fact]
    public async Task AFileChangedOutsideStartsANewHostAndStopsTheOld()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);

        File.WriteAllText(Path.Combine(game, "enemy.gd"), "extends Node\n");
        JsonObject second = await RunAsync(game, cancellation);

        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        Assert.Equal(launched[1].Id, PidIn(second));
        Assert.True(launched[0].ShutdownAsked, "the stale host was asked to shut down");
        Assert.True(launched[0].Disposed, "and let go of");
    }

    [Fact]
    public async Task ACSharpProjectRunsColdWithoutAHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        File.WriteAllText(Path.Combine(game, "Game.csproj"), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />");

        HeadlessHost? host = await Hosts.AcquireAsync(game, What(game), cancellation);

        Assert.Null(host);
        Assert.Empty(_launched);
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task AHostThatExitsMidRequestFailsItWithTheLogFromItsMarkerAndTheNextRequestStartsANewOne()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost first = Assert.Single(_launched);
        first.Mode = FakeHostMode.ExitOnRequest;

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => RunAsync(game, cancellation));
        int? afterFailure = Hosts.ProcessIdOf(game);
        JsonObject next = await RunAsync(game, cancellation);

        string log = HeadlessHost.LogPathOf(game, ServerPid);
        Assert.Equal(
            $"{What(game)} ended (Godot exited 3) before it answered. Its log from the request on, {log}:\n"
                + $"[godot-mcp] request 2 {Operation}\nboom",
            failed.Message
        );
        Assert.Null(afterFailure);
        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        Assert.Equal(launched[1].Id, PidIn(next));
    }

    [Fact]
    public async Task ARequestPastItsCeilingKillsAndDropsTheHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.Hang;

        Task<JsonObject> run = RunAsync(game, cancellation);
        await host.Hung.Task.WaitAsync(Wait, cancellation);
        Advance(Ceiling + TimeSpan.FromSeconds(1));
        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => run.WaitAsync(Wait, cancellation));

        Assert.StartsWith($"{What(game)} did not finish within 60 s of load-adjusted time (wall ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            $", so it was stopped with its whole process tree. Its log: {HeadlessHost.LogPathOf(game, ServerPid)}",
            failed.Message,
            StringComparison.Ordinal
        );
        Assert.True(host.Killed, "the host was killed");
        Assert.Equal(host.Id, host.KeptGameProcessId);
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ARequestThatStallsKillsAndDropsTheHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.Hang;
        host.CpuMeasurable = true;

        Task<JsonObject> run = RunAsync(Hosts, game, TimeSpan.FromSeconds(600), cancellation);
        await host.Hung.Task.WaitAsync(Wait, cancellation);
        await AdvanceUntilAsync(run, TimeSpan.FromSeconds(300), cancellation);
        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => run.WaitAsync(Wait, cancellation));

        Assert.Equal(
            $"{What(game)} did not finish (stalled: no output and no CPU for 120 s), so it was stopped with its whole process tree. "
                + $"Its log: {HeadlessHost.LogPathOf(game, ServerPid)}",
            failed.Message
        );
        Assert.True(host.Killed, "the host was killed");
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ACancelledCallKillsAndDropsItsHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.Hang;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        Task<JsonObject> run = RunAsync(Hosts, game, Ceiling, call.Token);
        await host.Hung.Task.WaitAsync(Wait, cancellation);
        await call.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait, cancellation));
        Assert.True(host.Killed, "the host was killed");
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task AHostThatNeverSaysHelloIsKilledAndTheCallFailsWithItsLog()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _launchMode = FakeHostMode.Silent;

        Task<JsonObject> run = RunAsync(game, cancellation);
        // The launch runs synchronously up to the wait for its hello.
        FakeHost host = Assert.Single(_launched);
        await host.HelloAwaited.Task.WaitAsync(Wait, cancellation);
        Advance(GodotSession.HandshakeTimeout + TimeSpan.FromSeconds(1));
        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => run.WaitAsync(Wait, cancellation));

        Assert.Equal(
            $"{What(game)} did not start: its warm headless host did not connect within 15 s, so it was stopped with its whole "
                + $"process tree. The last lines of its log, {HeadlessHost.LogPathOf(game, ServerPid)}:\nloading, and never dialling",
            failed.Message
        );
        Assert.True(host.Killed, "the host was killed");
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task AHostThatExitsBeforeItsHelloFailsTheCallWithItsExitCodeAndLog()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _launchMode = FakeHostMode.ExitAtStart;

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => RunAsync(game, cancellation));

        Assert.Equal(
            $"{What(game)} ended (Godot exited 7) before its warm headless host connected. The last lines of its log, "
                + $"{HeadlessHost.LogPathOf(game, ServerPid)}:\ncrashed at start",
            failed.Message
        );
        Assert.True(Assert.Single(_launched).Disposed, "the host was let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ARefusedRequestFailsWithTheRefusalAndKeepsTheHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.Refuse;

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => RunAsync(game, cancellation));

        Assert.Equal($"{What(game)} was refused by its warm headless host: The bridge refused 'headless': no such op", failed.Message);
        Assert.False(host.Killed, "the host was killed");
        Assert.Equal(host.Id, Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task AResultThatIsNotAnObjectFailsTheRequest()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.NotAnObject;

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => RunAsync(game, cancellation));

        Assert.Equal($"{What(game)} answered with a result that is not a JSON object: \"done\"", failed.Message);
    }

    [Fact]
    public async Task AReleasePastItsLimitIsLoggedAndTheNewHostStartsAnyway()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost first = Assert.Single(_launched);
        first.HoldReleaseWithLogClosed();

        first.End(5);
        await first.ReleaseStarted.Task.WaitAsync(Wait, cancellation);
        Task<JsonObject> next = RunAsync(game, cancellation);
        Advance(HeadlessHost.ReleaseLimit + TimeSpan.FromSeconds(1));
        JsonObject reply = await next.WaitAsync(Wait, cancellation);
        first.FinishRelease();

        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        Assert.False(launched[1].EarlierHostsReleasedAtLaunch, "the new host started while the old one's release still ran");
        Assert.Equal(launched[1].Id, PidIn(reply));
        Assert.Contains(
            $"The previous warm headless host for {game} was still being let go of after {HeadlessHost.ReleaseLimit.TotalSeconds} s; "
                + "the next one starts anyway.",
            _log.Messages
        );
    }

    [Fact]
    public async Task AHostGoneBetweenItsHelloAndItsFirstRequestFailsTheCallAsAStartFailure()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _launchMode = FakeHostMode.ExitAfterHello;

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => RunAsync(game, cancellation));

        Assert.Equal(
            $"{What(game)} ended (Godot exited 9) before its warm headless host took its first request. The last lines of its log, "
                + $"{HeadlessHost.LogPathOf(game, ServerPid)}:\ndied after its hello",
            failed.Message
        );
        FakeHost host = Assert.Single(_launched);
        Assert.Equal(0, host.HeadlessRequests);
        Assert.True(host.Disposed, "the host was let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task AFolderThatBecameCSharpStopsItsHostAndRunsCold()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        File.WriteAllText(Path.Combine(game, "Game.csproj"), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />");

        HeadlessHost? acquired = await Hosts.AcquireAsync(game, What(game), cancellation);

        Assert.Null(acquired);
        Assert.True(host.ShutdownAsked, "the old host was asked to shut down");
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
        Assert.Single(_launched);
        Assert.Contains($"Stopped the warm headless host for {game} (pid {host.Id}): the project became a C# project.", _log.Messages);
    }

    [Fact]
    public async Task AHostsLogHoldsEachLineOnDiskWhileTheHostRuns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        string log = HeadlessHost.LogPathOf(game, ServerPid);
        string marker = $"[godot-mcp] request 1 {Operation}";
        // A stand-in for Godot started as a host's process is: it prints the marker, then runs on as a host waiting for its reply.
        string[] arguments = ["-NoProfile", "-Command", $"Write-Output '{marker}'; Start-Sleep 60"];
        using IHostProcess host = GodotHostProcess.Open(new HostLaunch(game, 1, "token", log), "pwsh", arguments, NullLogger.Instance);
        try
        {
            var waited = Stopwatch.StartNew();
            while (!ReadLog(log).Contains(marker) && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation);
            }

            Assert.Contains(marker, ReadLog(log));
            Assert.False(host.HasExited, "the marker reached the log only once the process had ended");
        }
        finally
        {
            host.Kill();
        }
    }

    [Fact]
    public async Task StopFolderStopsItsHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);

        Hosts.StopFolder(game, "import");

        Assert.True(host.ShutdownAsked, "the host was asked to shut down");
        Assert.True(host.Disposed, "and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ShutdownStopsEveryHostAndWaitsForEach()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string first = Game("first");
        string second = Game("second");
        await RunAsync(first, cancellation);
        await RunAsync(second, cancellation);

        Hosts.Shutdown();

        Assert.Equal(2, _launched.Count);
        Assert.All(_launched, host => Assert.True(host.ShutdownAsked && host.Killed && host.Disposed, $"host {host.Id} was stopped and waited for"));
        Assert.Null(Hosts.ProcessIdOf(first));
        Assert.Null(Hosts.ProcessIdOf(second));
    }

    [Fact]
    public async Task TwoServersOnOneFolderEachKeepTheirOwnHostWritingItsOwnLog()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        using BridgeListener otherListener = new(NullLogger<BridgeListener>.Instance) { Clock = _clock };
        using SessionRegistry other = new(otherListener, NullLogger<GodotSession>.Instance)
        {
            OverrideFolders = new OverrideFolders(_temp.Combine("other-override-folders.txt"), TextWriter.Null),
            Dormant = new DormantGames(_ => new ProcessStart(Exists: true, StartTime: null), TextWriter.Null),
            HostLauncher = Launch,
            ServerProcessId = OtherServerPid,
        };

        JsonObject[] replies =
        [
            await RunAsync(game, cancellation),
            await RunAsync(other.HeadlessHosts, game, Ceiling, cancellation),
            await RunAsync(game, cancellation),
            await RunAsync(other.HeadlessHosts, game, Ceiling, cancellation),
        ];

        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        (FakeHost ours, FakeHost theirs) = (launched[0], launched[1]);
        Assert.Equal([ours.Id, theirs.Id, ours.Id, theirs.Id], replies.Select(PidIn));
        Assert.Equal(HeadlessHost.LogPathOf(game, ServerPid), ours.LogPath);
        Assert.Equal(HeadlessHost.LogPathOf(game, OtherServerPid), theirs.LogPath);
        Assert.NotEqual(ours.LogPath, theirs.LogPath);
        Assert.All(
            launched,
            host =>
            {
                string[] lines = ReadLog(host.LogPath);
                Assert.Equal(2, lines.Length);
                Assert.All(lines, line => Assert.Matches($@"^\[godot-mcp\] request \d+ {Operation}$", line));
            }
        );
    }

    [Fact]
    public async Task ARequestArrivingWhileAnIdleHostThatEndedIsReleasedStartsItsNewHostOnlyOnceTheReleaseFinished()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost first = Assert.Single(_launched);
        first.HoldRelease();

        first.End(5);
        await first.ReleaseStarted.Task.WaitAsync(Wait, cancellation);
        Task<JsonObject> next = RunAsync(game, cancellation);
        // Wall time for the request to reach the start of its host, which it must not while the release is held.
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation);
        int launchedDuringRelease = _launched.Count;
        first.FinishRelease();
        JsonObject reply = await next.WaitAsync(Wait, cancellation);

        Assert.Equal(1, launchedDuringRelease);
        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        Assert.True(launched[1].EarlierHostsReleasedAtLaunch, "the new host started after the old one's release had closed its log");
        Assert.Equal(launched[1].Id, PidIn(reply));
        Assert.Equal([$"[godot-mcp] request 1 {Operation}"], ReadLog(launched[1].LogPath));
    }

    [Fact]
    public void AHostWhoseLogIsHeldByAnotherWriterFailsToStartNamingItsLog()
    {
        string game = Game("game");
        string log = HeadlessHost.LogPathOf(game, ServerPid);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using FileStream held = new(log, FileMode.Create, FileAccess.Write, FileShare.Read);
        // Never run: the log is opened before the process starts.
        string godot = _temp.Combine("Godot_console.exe");

        SessionException failed = Assert.Throws<SessionException>(() =>
            GodotHostProcess.Start(new HostLaunch(game, 1, "token", log), godot, "host.gd", NullLogger.Instance)
        );

        Assert.StartsWith($"Godot could not be started from {godot}: its log {log} could not be opened: ", failed.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(failed.InnerException, exactMatch: false);
    }

    private static string What(string game) => $"The headless {Operation} run on {game}";

    private static int PidIn(JsonObject reply) => reply["result"]!["pid"]!.GetValue<int>();

    /// <summary>A host's log, read while its writer may still hold it.</summary>
    private static string[] ReadLog(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private FakeHost Launch(HostLaunch launch)
    {
        bool released = _launched.All(earlier => earlier.Disposed);
        FakeHost host = new(launch) { EarlierHostsReleasedAtLaunch = released, Mode = _launchMode };
        _launched.Enqueue(host);
        return host;
    }

    private string Game(string name)
    {
        string game = ProjectPaths.Normalise(_temp.Combine(name));
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "project.godot"), "config_version=5\n");
        return game;
    }

    private static async Task<JsonObject> RunAsync(HeadlessHosts hosts, string game, TimeSpan ceiling, CancellationToken cancellationToken)
    {
        HeadlessHost host =
            await hosts.AcquireAsync(game, What(game), cancellationToken) ?? throw new InvalidOperationException("the folder ran cold");
        HostRequest request = new(What(game), Operation, new JsonObject { ["targets"] = new JsonArray() }, ceiling);
        return await hosts.RunAsync(host, request, cancellationToken);
    }

    private Task<JsonObject> RunAsync(string game, CancellationToken cancellationToken) => RunAsync(Hosts, game, Ceiling, cancellationToken);

    /// <summary>Moves the fake time on by <paramref name="span"/> in steps, so the clock's timers fire as they fall due.</summary>
    private void Advance(TimeSpan span)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += Step)
        {
            _time.Advance(Step);
        }
    }

    /// <summary>
    /// Moves the fake time on a second at a time until <paramref name="task"/> ends or <paramref name="limit"/> has passed,
    /// pausing in real time after each second so a watch that re-arms its timer after each check keeps up.
    /// </summary>
    private async Task AdvanceUntilAsync(Task task, TimeSpan limit, CancellationToken cancellationToken)
    {
        var second = TimeSpan.FromSeconds(1);
        for (TimeSpan passed = TimeSpan.Zero; passed < limit && !task.IsCompleted; passed += second)
        {
            _time.Advance(second);
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private enum FakeHostMode
    {
        Answer,
        ExitOnRequest,
        Hang,
        Refuse,
        NotAnObject,

        /// <summary>Never dials the listener.</summary>
        Silent,

        /// <summary>Exits with 7 before it dials.</summary>
        ExitAtStart,

        /// <summary>Exits with 9 once the server has taken its hello, before its first request.</summary>
        ExitAfterHello,
    }

    /// <summary>Every message logged through the registry's logger, formatted.</summary>
    private sealed class RecordingLogger : ILogger<GodotSession>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }

    /// <summary>
    /// A warm host's process without Godot: dials the listener with its hello once the host waits on it (a real Godot takes
    /// longer to start than the server takes to listen for it), answers each headless request with its own pid after
    /// writing its marker to the log, and exits on shutdown or a kill. It holds its log from its launch to its disposal as a
    /// real host does, started afresh and shared for reading only, so a second host opening the same log while this one
    /// holds it fails.
    /// </summary>
    private sealed class FakeHost(HostLaunch launch) : IHostProcess
    {
        private static int _nextId = 40_000;
        private readonly HostLaunch _launch = launch;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _logLock = new();
        private readonly StreamWriter _log = OpenLog(launch.LogPath);
        private TaskCompletionSource? _releaseGate;
        private FakeBridge? _bridge;
        private int _dialled;
        private int _exitCode;
        private long _lines;
        private int _headless;
        private volatile FakeHostMode _mode;
        private volatile bool _cpuMeasurable;

        public int Id { get; } = Interlocked.Increment(ref _nextId);

        public string LogPath => _launch.LogPath;

        public FakeHostMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        public TaskCompletionSource Hung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the server waits for this host's hello (or its exit), when it dials unless it is silent.</summary>
        public TaskCompletionSource HelloAwaited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The process id the server kept a handle on from the hello; null before it did.</summary>
        public int? KeptGameProcessId { get; private set; }

        public int HeadlessRequests => Volatile.Read(ref _headless);

        public bool ShutdownAsked { get; private set; }

        public bool Killed { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Whether every host launched before this one had been let go of when this one was launched.</summary>
        public bool EarlierHostsReleasedAtLaunch { get; init; }

        /// <summary>Completes when the server starts waiting for the rest of this host's output, part of its release.</summary>
        public TaskCompletionSource ReleaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exited.Task.IsCompleted;

        public int ExitCode => _exitCode;

        public long OutputLines => Interlocked.Read(ref _lines);

        /// <summary>False by default, which turns the stall watch off; its tree's CPU never moves.</summary>
        public bool CpuMeasurable
        {
            get => _cpuMeasurable;
            set => _cpuMeasurable = value;
        }

        public long CpuTicks => 0;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _dialled, 1) == 0)
            {
                Begin();
            }

            return _exited.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Records the pid; a host in <see cref="FakeHostMode.ExitAfterHello"/> exits here, after its hello was taken.</summary>
        public void KeepGameHandle(int gameProcessId)
        {
            KeptGameProcessId = gameProcessId;
            if (Mode == FakeHostMode.ExitAfterHello)
            {
                WriteLog("died after its hello");
                Exit(9);
            }
        }

        public bool WaitForExit(TimeSpan timeout) => _exited.Task.Wait(timeout);

        /// <summary>Completes at once, or once <see cref="FinishRelease"/> is called after <see cref="HoldRelease"/>.</summary>
        public Task FinishOutputAsync()
        {
            ReleaseStarted.TrySetResult();
            return _releaseGate?.Task ?? Task.CompletedTask;
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public void Dispose()
        {
            lock (_logLock)
            {
                Disposed = true;
                _log.Dispose();
            }
        }

        /// <summary>Makes the server's wait for the rest of the output, and so its release, last until <see cref="FinishRelease"/>.</summary>
        public void HoldRelease() => _releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary><see cref="HoldRelease"/> with the log closed at once, so a new host may open it while the release is held.</summary>
        public void HoldReleaseWithLogClosed()
        {
            HoldRelease();
            lock (_logLock)
            {
                _log.Dispose();
            }
        }

        public void FinishRelease() => _releaseGate?.TrySetResult();

        /// <summary>Exits with <paramref name="code"/> on its own, as a crash does.</summary>
        public void End(int code) => Exit(code);

        /// <summary>A reply frame to <paramref name="request"/> with <paramref name="ok"/> and one field besides.</summary>
        private static JsonObject Reply(JsonObject request, bool ok, string field, string value) =>
            new()
            {
                ["id"] = request["id"]?.DeepClone(),
                ["ok"] = ok,
                [field] = value,
            };

        private static StreamWriter OpenLog(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }

        private void Exit(int code)
        {
            if (_exited.Task.IsCompleted)
            {
                return;
            }

            _exitCode = code;
            _exited.TrySetResult();
            _bridge?.Dispose();
        }

        private void WriteLog(params string[] lines)
        {
            lock (_logLock)
            {
                foreach (string line in lines)
                {
                    _log.WriteLine(line);
                }

                Interlocked.Add(ref _lines, lines.Length);
            }
        }

        /// <summary>What the host does once the server waits on it: dials, stays silent, or exits before its hello.</summary>
        private void Begin()
        {
            switch (Mode)
            {
                case FakeHostMode.Silent:
                    WriteLog("loading, and never dialling");
                    break;
                case FakeHostMode.ExitAtStart:
                    WriteLog("crashed at start");
                    Exit(7);
                    break;
                default:
                    _ = ServeAsync();
                    break;
            }

            HelloAwaited.TrySetResult();
        }

        private async Task ServeAsync()
        {
            try
            {
                _bridge = await FakeBridge.DialAsync(_launch.Port, _launch.Token, _launch.ProjectDir, Id, CancellationToken.None);
                while (!_exited.Task.IsCompleted)
                {
                    JsonObject request = await _bridge.ReadRequestAsync(CancellationToken.None);
                    await AnswerAsync(_bridge, request);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Killed, or the server closed the connection.
            }
        }

        private async Task AnswerAsync(FakeBridge bridge, JsonObject request)
        {
            string? command = HandshakeExpectation.ReadString(request, "command");
            if (command == "shutdown")
            {
                ShutdownAsked = true;
                await bridge.ReplyAsync(request, [], CancellationToken.None);
                Exit(0);
                return;
            }

            Interlocked.Increment(ref _headless);
            string op = HandshakeExpectation.ReadString(request["params"]!.AsObject(), "op") ?? string.Empty;
            string marker = $"[godot-mcp] request {request["id"]} {op}";
            switch (Mode)
            {
                case FakeHostMode.ExitOnRequest:
                    WriteLog(marker, "boom");
                    Exit(3);
                    return;
                case FakeHostMode.Hang:
                    WriteLog(marker);
                    Hung.TrySetResult();
                    return;
                case FakeHostMode.Refuse:
                    WriteLog(marker);
                    await bridge.WriteAsync(Reply(request, ok: false, "error", "no such op"), CancellationToken.None);
                    return;
                case FakeHostMode.NotAnObject:
                    WriteLog(marker);
                    await bridge.WriteAsync(Reply(request, ok: true, "result", "done"), CancellationToken.None);
                    return;
            }

            WriteLog(marker);
            JsonObject result = new()
            {
                ["ok"] = true,
                ["result"] = new JsonObject { ["pid"] = Id },
                ["engineErrors"] = new JsonArray(),
            };
            await bridge.ReplyAsync(request, result, CancellationToken.None);
        }
    }
}
