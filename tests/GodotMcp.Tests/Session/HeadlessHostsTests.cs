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
/// that became C#, a slow release, the stops, a start that finishes after the server's shutdown, and a host's log current
/// on disk while it runs; the idle limit and its reset,
/// the cap's eviction of the least recently used idle host and never a busy one, a stale reply, the listing, and
/// stop_project on a folder with only a host.
/// </summary>
public sealed class HeadlessHostsTests : IDisposable
{
    private const string Operation = "validate";

    /// <summary>The message a call gets once the server is shutting down.</summary>
    private const string ShuttingDown = "The server is shutting down, so no headless host starts now.";

    private const int ServerPid = 1_001;
    private const int OtherServerPid = 1_002;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>A ceiling no idle-limit or cap test's advance of the fake clock reaches.</summary>
    private static readonly TimeSpan LongCeiling = TimeSpan.FromHours(1);

    /// <summary>Real time for a stop the fake clock set off, on another thread, to have begun if it was going to.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _registry;
    private readonly ConcurrentQueue<FakeHost> _launched = new();
    private readonly RecordingLogger _log = new();
    private FakeHostMode _launchMode = FakeHostMode.Answer;

    // When set, hosts launched from then on dial only once it completes, so their starts stay under way together.
    private TaskCompletionSource? _dialGate;

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
    public async Task AStartThatFinishesAfterShutdownStopsItsHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _dialGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HeadlessHost?> acquire = Hosts.AcquireAsync(game, What(game), cancellation);
        // The launch runs synchronously up to the wait for its hello, which the gate holds back.
        FakeHost host = Assert.Single(_launched);
        Hosts.Shutdown();
        _dialGate.SetResult();

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => acquire.WaitAsync(Wait, cancellation));

        Assert.Equal(ShuttingDown, failed.Message);
        Assert.Null(Hosts.ProcessIdOf(game));
        Assert.True(host.ShutdownAsked && host.Disposed, "the host was stopped and let go of");
    }

    [Fact]
    public async Task AcquireAfterShutdownIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");

        Hosts.Shutdown();
        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => Hosts.AcquireAsync(game, What(game), cancellation));

        Assert.Equal(ShuttingDown, failed.Message);
        Assert.Empty(_launched);
        Assert.Null(Hosts.ProcessIdOf(game));
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

    [Fact]
    public async Task IdleLimitStopsAnIdleHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);

        Advance(HeadlessHosts.IdleLimit - TimeSpan.FromSeconds(1));
        await Task.Delay(Settle, cancellation);
        bool stoppedEarly = host.ShutdownAsked;
        Advance(TimeSpan.FromSeconds(2));
        await RegistryHarness.WaitUntilAsync(() => host.Disposed && Hosts.ProcessIdOf(game) is null);

        Assert.False(stoppedEarly, "the host was stopped before its idle limit");
        Assert.True(host.ShutdownAsked, "the idle host was asked to shut down");
        Assert.Contains($"Stopped the warm headless host for {game} (pid {host.Id}): idle.", _log.Messages);
    }

    [Fact]
    public async Task ARequestBeforeTheLimitResetsTheIdleTimer()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);

        Advance(HeadlessHosts.IdleLimit - TimeSpan.FromSeconds(30));
        await RunAsync(game, cancellation);
        // Past the first reply's limit, well within the second's.
        Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(Settle, cancellation);
        bool stoppedEarly = host.ShutdownAsked;
        int? stillHeld = Hosts.ProcessIdOf(game);
        Advance(HeadlessHosts.IdleLimit);
        await RegistryHarness.WaitUntilAsync(() => host.Disposed && Hosts.ProcessIdOf(game) is null);

        Assert.False(stoppedEarly, "the host was stopped by the limit its first reply set");
        Assert.Equal(host.Id, stillHeld);
        Assert.Single(_launched);
        Assert.True(host.ShutdownAsked, "the host was stopped at the limit its second reply set");
    }

    [Fact]
    public async Task ABusyHostIsNeverIdleStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.Mode = FakeHostMode.Hang;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        Task<JsonObject> run = RunAsync(Hosts, game, LongCeiling, call.Token);
        await host.Hung.Task.WaitAsync(Wait, cancellation);
        Advance(HeadlessHosts.IdleLimit + TimeSpan.FromMinutes(1));
        await Task.Delay(Settle, cancellation);
        bool stopped = host.ShutdownAsked || host.Killed;
        int? held = Hosts.ProcessIdOf(game);
        await call.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait, cancellation));

        Assert.False(stopped, "the busy host was stopped at its idle limit");
        Assert.Equal(host.Id, held);
    }

    [Fact]
    public async Task AFifthFolderEvictsTheLeastRecentlyUsedIdleHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] games = [.. Enumerable.Range(1, 5).Select(number => Game($"game{number}"))];
        foreach (string game in games[..4])
        {
            await RunAsync(game, cancellation);
            Advance(TimeSpan.FromSeconds(1));
        }

        // The first folder used again, so the second is now the least recently used.
        await RunAsync(games[0], cancellation);
        Advance(TimeSpan.FromSeconds(1));
        JsonObject fifth = await RunAsync(games[4], cancellation);
        int? evictedAfterStart = Hosts.ProcessIdOf(games[1]);
        FakeHost[] launched = [.. _launched];
        await RegistryHarness.WaitUntilAsync(() => launched[1].Disposed);

        Assert.Equal(5, launched.Length);
        Assert.Equal(launched[4].Id, PidIn(fifth));
        Assert.True(launched[1].ShutdownAsked, "the least recently used host was asked to shut down");
        Assert.Null(evictedAfterStart);
        foreach (int kept in (int[])[0, 2, 3, 4])
        {
            Assert.False(launched[kept].ShutdownAsked, $"host {kept + 1} was stopped");
            Assert.Equal(launched[kept].Id, Hosts.ProcessIdOf(games[kept]));
        }

        Assert.Contains($"Stopped the warm headless host for {games[1]} (pid {launched[1].Id}): cap.", _log.Messages);
    }

    [Fact]
    public async Task TheCapNeverEvictsABusyHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] games = [.. Enumerable.Range(1, 5).Select(number => Game($"game{number}"))];
        foreach (string game in games[..4])
        {
            await RunAsync(game, cancellation);
            Advance(TimeSpan.FromSeconds(1));
        }

        FakeHost[] first = [.. _launched];
        first[0].Mode = FakeHostMode.Hang;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<JsonObject> busy = RunAsync(Hosts, games[0], LongCeiling, call.Token);
        await first[0].Hung.Task.WaitAsync(Wait, cancellation);

        await RunAsync(games[4], cancellation);
        await RegistryHarness.WaitUntilAsync(() => first[1].Disposed);
        bool busyStopped = first[0].ShutdownAsked || first[0].Killed;
        await call.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => busy.WaitAsync(Wait, cancellation));

        Assert.False(busyStopped, "the busy host, the least recently used, was stopped for the cap");
        Assert.True(first[1].ShutdownAsked, "the least recently used idle host was stopped instead");
        Assert.Null(Hosts.ProcessIdOf(games[1]));
    }

    [Fact]
    public async Task AStaleReplyStopsTheHostAndTheNextRequestStartsANewOne()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _launchMode = FakeHostMode.Stale;

        JsonObject staleReply = await RunAsync(game, cancellation);
        int? afterStale = Hosts.ProcessIdOf(game);
        _launchMode = FakeHostMode.Answer;
        JsonObject next = await RunAsync(game, cancellation);

        FakeHost[] launched = [.. _launched];
        Assert.Equal(2, launched.Length);
        Assert.Equal(launched[0].Id, PidIn(staleReply));
        Assert.False(staleReply.ContainsKey("stale"), "the flag reached the caller");
        Assert.Null(afterStale);
        Assert.True(launched[0].ShutdownAsked && launched[0].Disposed, "the stale host was stopped and let go of");
        Assert.Equal(launched[1].Id, PidIn(next));
        Assert.Contains(
            $"Stopped the warm headless host for {game} (pid {launched[0].Id}): a resource its request named stayed cached after its reply.",
            _log.Messages
        );
    }

    [Fact]
    public async Task ListReportsHostsByPathWithIdleSeconds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        IReadOnlyList<HeadlessHostInfo> beforeAny = _registry.ListHeadlessHosts();
        string beta = Game("beta");
        string alpha = Game("alpha");
        string gamma = Game("gamma");

        DateTimeOffset betaStarted = _time.GetUtcNow();
        await RunAsync(beta, cancellation);
        Advance(TimeSpan.FromSeconds(10));
        DateTimeOffset alphaStarted = _time.GetUtcNow();
        await RunAsync(alpha, cancellation);
        await RunAsync(alpha, cancellation);
        Advance(TimeSpan.FromSeconds(5));
        DateTimeOffset gammaStarted = _time.GetUtcNow();
        _launchMode = FakeHostMode.Hang;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<JsonObject> busy = RunAsync(Hosts, gamma, LongCeiling, call.Token);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 3);
        FakeHost[] launched = [.. _launched];
        await launched[2].Hung.Task.WaitAsync(Wait, cancellation);
        IReadOnlyList<HeadlessHostInfo> listed = _registry.ListHeadlessHosts();
        await call.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => busy.WaitAsync(Wait, cancellation));

        Assert.Empty(beforeAny);
        Assert.Equal(
            [
                new HeadlessHostInfo(alpha, launched[1].Id, alphaStarted, Requests: 2, IdleSeconds: 5),
                new HeadlessHostInfo(beta, launched[0].Id, betaStarted, Requests: 1, IdleSeconds: 15),
                new HeadlessHostInfo(gamma, launched[2].Id, gammaStarted, Requests: 1, IdleSeconds: 0),
            ],
            listed
        );
    }

    [Fact]
    public async Task StopWithProjectPathStopsAHostOnlyFolder()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);

        object stopped = await _registry.StopFolderAsync(game, null, cancellation);

        Assert.Equal(new HostStopResult(game, HeadlessHostStopped: true), stopped);
        Assert.True(host.ShutdownAsked && host.Disposed, "the host was stopped and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
        Assert.Contains($"Stopped the warm headless host for {game} (pid {host.Id}): stop_project.", _log.Messages);
    }

    [Fact]
    public async Task TwoStartsAtThreeLiveHostsStopOneSoNoMoreThanFourLive()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] games = await ThreeHostsAndTwoNewFoldersAsync(cancellation);

        // Both hosts dial only once both starts are under way, so neither is in the pool when the other reserves its place.
        _dialGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject> fourth = RunAsync(games[3], cancellation);
        Task<JsonObject> fifth = RunAsync(games[4], cancellation);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 5);
        _dialGate.SetResult();
        await Task.WhenAll(fourth, fifth).WaitAsync(Wait, cancellation);
        FakeHost[] launched = [.. _launched];
        await RegistryHarness.WaitUntilAsync(() => launched[0].Disposed);

        Assert.Equal(5, launched.Length);
        Assert.True(launched[0].ShutdownAsked, "the least recently used host was stopped for the second start");
        Assert.Equal((int?[])[null, launched[1].Id, launched[2].Id, launched[3].Id, launched[4].Id], games.Select(Hosts.ProcessIdOf));
    }

    [Fact]
    public async Task TwoStartsAtFourIdleHostsStopTheTwoLeastRecentlyUsed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] games = [.. Enumerable.Range(1, 6).Select(number => Game($"game{number}"))];
        foreach (string game in games[..4])
        {
            await RunAsync(game, cancellation);
            Advance(TimeSpan.FromSeconds(1));
        }

        _dialGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject> fifth = RunAsync(games[4], cancellation);
        Task<JsonObject> sixth = RunAsync(games[5], cancellation);
        await RegistryHarness.WaitUntilAsync(() => _launched.Count == 6);
        _dialGate.SetResult();
        await Task.WhenAll(fifth, sixth).WaitAsync(Wait, cancellation);
        FakeHost[] launched = [.. _launched];
        await RegistryHarness.WaitUntilAsync(() => launched[0].Disposed && launched[1].Disposed);

        Assert.Equal(6, launched.Length);
        Assert.Equal((int?[])[null, null, launched[2].Id, launched[3].Id, launched[4].Id, launched[5].Id], games.Select(Hosts.ProcessIdOf));
        Assert.All(launched[2..], host => Assert.False(host.ShutdownAsked, $"host {host.Id} was stopped"));
    }

    [Fact]
    public async Task ShutdownWaitsForAnIdleStopUnderWay()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.HoldRelease();

        Advance(HeadlessHosts.IdleLimit + TimeSpan.FromSeconds(1));
        await host.ReleaseStarted.Task.WaitAsync(Wait, cancellation);
        var shutdown = Task.Run(Hosts.Shutdown, cancellation);
        await Task.Delay(Settle, cancellation);
        bool returnedDuringRelease = shutdown.IsCompleted;
        host.FinishRelease();
        await shutdown.WaitAsync(Wait, cancellation);

        Assert.False(returnedDuringRelease, "the shutdown returned while the idle stop's release was under way");
        Assert.True(host.Disposed, "the host was let go of");
    }

    [Fact]
    public async Task AReplyAfterTheIdleTimerFiredKeepsTheHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ConcurrentQueue<Action> heldChecks = new();
        Hosts.IdleDispatch = heldChecks.Enqueue;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);

        Advance(HeadlessHosts.IdleLimit + TimeSpan.FromSeconds(1));
        int checksAtLimit = heldChecks.Count;
        // The request ends after the timer fired and before its check ran.
        await RunAsync(game, cancellation);
        RunHeld(heldChecks);
        await Task.Delay(Settle, cancellation);
        bool stoppedAfterReply = host.ShutdownAsked;
        int? held = Hosts.ProcessIdOf(game);
        Advance(HeadlessHosts.IdleLimit);
        RunHeld(heldChecks);
        await RegistryHarness.WaitUntilAsync(() => host.Disposed);

        Assert.Equal(1, checksAtLimit);
        Assert.False(stoppedAfterReply, "the host was stopped right after its reply");
        Assert.Equal(host.Id, held);
        Assert.True(host.ShutdownAsked, "the host was stopped once idle for the whole limit after its reply");
    }

    [Fact]
    public async Task ASessionStopAlsoStopsTheFoldersIdleHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        FakeBridge bridge = await AttachGameAsync(game, "server", cancellation);
        bridge.Dispose();
        await RegistryHarness.WaitUntilAsync(() => !_registry.Resolve("server").IsLive);

        StopResult stopped = await _registry.StopAsync("server", cancellation);

        Assert.True(stopped.HeadlessHostStopped, "headlessHostStopped");
        Assert.True(host.ShutdownAsked && host.Disposed, "the host was stopped and let go of");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ASessionStopLeavesABusyHostRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        FakeBridge bridge = await AttachGameAsync(game, "server", cancellation);
        bridge.Dispose();
        await RegistryHarness.WaitUntilAsync(() => !_registry.Resolve("server").IsLive);
        host.Mode = FakeHostMode.Hang;
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<JsonObject> busy = RunAsync(Hosts, game, LongCeiling, call.Token);
        await host.Hung.Task.WaitAsync(Wait, cancellation);
        // Held as a headless call through the runner holds it for its whole request, so a stop waiting on it never returns.
        SemaphoreSlim folderLock = _registry.PrepLock(game);
        await folderLock.WaitAsync(cancellation);
        StopResult stopped;
        try
        {
            stopped = await _registry.StopAsync("server", cancellation).WaitAsync(Wait, cancellation);
        }
        finally
        {
            folderLock.Release();
        }

        bool hostStopped = host.ShutdownAsked || host.Killed;
        await call.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => busy.WaitAsync(Wait, cancellation));

        Assert.False(stopped.HeadlessHostStopped, "headlessHostStopped");
        Assert.False(hostStopped, "the busy host was stopped");
    }

    [Fact]
    public async Task StopWithProjectPathStopsTheLiveSessionAndTheHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        FakeBridge bridge = await AttachGameAsync(game, "server", cancellation);

        Task<object> stop = _registry.StopFolderAsync(game, null, cancellation);
        using (bridge)
        {
            await bridge.AnswerOneAsync("quit", cancellation);
        }

        StopResult stopped = Assert.IsType<StopResult>(await stop.WaitAsync(Wait, cancellation));
        Assert.Equal("server", stopped.Session);
        Assert.True(stopped.HeadlessHostStopped, "headlessHostStopped");
        Assert.True(host.ShutdownAsked && host.Disposed, "the host was stopped and let go of");
        Assert.Empty(_registry.List(includeStopped: false));
    }

    [Fact]
    public async Task AStaleFlagIsTakenOutOfAFailedResultToo()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        _launchMode = FakeHostMode.StaleFailure;

        JsonObject failed = await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        await RegistryHarness.WaitUntilAsync(() => host.Disposed);

        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.False(failed.ContainsKey("stale"), "the flag reached the caller");
        Assert.True(host.ShutdownAsked, "the stale host was stopped");
        Assert.Null(Hosts.ProcessIdOf(game));
    }

    [Fact]
    public async Task ACapEvictionStopsTheHostOffTheCallersPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string[] games = [.. Enumerable.Range(1, 5).Select(number => Game($"game{number}"))];
        foreach (string game in games[..4])
        {
            await RunAsync(game, cancellation);
            Advance(TimeSpan.FromSeconds(1));
        }

        FakeHost evicted = _launched.First();
        evicted.HoldRelease();

        // An eviction run on the caller's path would hold the call until the release is let go, so the wait below times out.
        JsonObject fifth;
        bool disposedWhileHeld;
        try
        {
            fifth = await Task.Run(() => RunAsync(games[4], cancellation), cancellation).WaitAsync(Wait, cancellation);
            await evicted.ReleaseStarted.Task.WaitAsync(Wait, cancellation);
            disposedWhileHeld = evicted.Disposed;
        }
        finally
        {
            evicted.FinishRelease();
        }

        await RegistryHarness.WaitUntilAsync(() => evicted.Disposed);

        Assert.Equal(_launched.Last().Id, PidIn(fifth));
        Assert.True(evicted.ShutdownAsked, "the evicted host was asked to shut down");
        Assert.False(disposedWhileHeld, "the evicted host was let go of before its release was");
    }

    [Fact]
    public async Task AStaleStopRunsOffTheCallersPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string game = Game("game");
        await RunAsync(game, cancellation);
        FakeHost host = Assert.Single(_launched);
        host.HoldRelease();
        host.Mode = FakeHostMode.Stale;

        // A stale stop run on the caller's path would hold the reply until the release is let go, so the wait below times out.
        JsonObject reply;
        bool disposedWhileHeld;
        try
        {
            reply = await Task.Run(() => RunAsync(game, cancellation), cancellation).WaitAsync(Wait, cancellation);
            await host.ReleaseStarted.Task.WaitAsync(Wait, cancellation);
            disposedWhileHeld = host.Disposed;
        }
        finally
        {
            host.FinishRelease();
        }

        await RegistryHarness.WaitUntilAsync(() => host.Disposed);

        Assert.Equal(host.Id, PidIn(reply));
        Assert.True(host.ShutdownAsked, "the stale host was asked to shut down");
        Assert.False(disposedWhileHeld, "the stale host was let go of before its release was");
    }

    private static string What(string game) => $"The headless {Operation} run on {game}";

    private static void RunHeld(ConcurrentQueue<Action> checks)
    {
        while (checks.TryDequeue(out Action? check))
        {
            check();
        }
    }

    /// <summary>Five folders, the first three with a host each, used a second apart in order.</summary>
    private async Task<string[]> ThreeHostsAndTwoNewFoldersAsync(CancellationToken cancellation)
    {
        string[] games = [.. Enumerable.Range(1, 5).Select(number => Game($"game{number}"))];
        foreach (string game in games[..3])
        {
            await RunAsync(game, cancellation);
            Advance(TimeSpan.FromSeconds(1));
        }

        return games;
    }

    /// <summary>Attaches a session named <paramref name="name"/> on the folder to a fake game with no process id.</summary>
    private async Task<FakeBridge> AttachGameAsync(string game, string name, CancellationToken cancellation)
    {
        string attachFile = AttachFile.PathIn(game);
        Task<AttachResult> attach = _registry.AttachAsync(new AttachRequest(game, name, Wait, false, false, null), cancellation);
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile));
        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, token, game, null, cancellation);
        await attach.WaitAsync(Wait, cancellation);
        return bridge;
    }

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
        FakeHost host = new(launch)
        {
            EarlierHostsReleasedAtLaunch = released,
            Mode = _launchMode,
            DialGate = _dialGate?.Task,
        };
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

        /// <summary>Answers as <see cref="Answer"/> does, with <c>stale: true</c> in its result object.</summary>
        Stale,

        /// <summary>Answers with a failed operation (<c>ok: false</c>) and <c>stale: true</c> in its result object.</summary>
        StaleFailure,
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

        /// <summary>When set, the host dials only once it completes.</summary>
        public Task? DialGate { get; init; }

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
                if (DialGate is { } gate)
                {
                    await gate;
                }

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
                ["ok"] = Mode != FakeHostMode.StaleFailure,
                ["result"] = new JsonObject { ["pid"] = Id },
                ["engineErrors"] = new JsonArray(),
            };
            if (Mode is FakeHostMode.Stale or FakeHostMode.StaleFailure)
            {
                result["stale"] = true;
            }

            await bridge.ReplyAsync(request, result, CancellationToken.None);
        }
    }
}
