using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A timed-out request told apart as a busy or a stuck main thread, and a game paused under a debugger failing a call at
/// once, through an attached session whose game is a <see cref="FakeBridge"/>. The stuck game's hello names this test process,
/// so the probe has a real process to sample; whether a debugger is attached is a fake the test sets.
/// </summary>
public sealed partial class HangProbeTests : IAsyncDisposable
{
    private const string Script = "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn 1\n";
    private const int TimeoutMs = 300;
    private const string Command = "get_ui_elements";
    private static readonly TimeSpan AttachWait = TimeSpan.FromSeconds(10);
    private readonly TempDirectory _temp = new();

    // Wall time: a build loading the machine would stretch the 300 ms ceiling into its backstop and change the message
    // these tests assert word for word; LoadClockTests cover the load itself.
    private readonly LoadClock _wallClock = new(TimeProvider.System, new NoLoadSource());
    private readonly BridgeListener _listener;
    private readonly SessionRegistry _sessions;
    private FakeBridge? _game;
    private bool _debuggerAttached;

    public HangProbeTests()
    {
        _listener = new(NullLogger<BridgeListener>.Instance) { Clock = _wallClock };
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance) { IsDebuggerAttached = _ => _debuggerAttached };
    }

    public ValueTask DisposeAsync()
    {
        _game?.Dispose();
        _sessions.Dispose();
        _listener.Dispose();
        _wallClock.Dispose();
        _temp.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AGameThatAnswersPingsButNotTheCommandIsBusy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);
        using var stopAnswering = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task answering = game.AnswerPingsOnlyAsync(stopAnswering.Token);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));
        await stopAnswering.CancelAsync();
        await answering;

        Assert.Equal(
            "'run_script' timed out after 300 ms, but the game answered a ping, so its main thread is running; "
                + "a script that needs longer can raise timeoutMs.",
            timedOut.Message
        );
    }

    [Fact]
    public async Task AGameThatAnswersNothingIsStuckAndItsHellosProcessIsDescribed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AttachAsync(Environment.ProcessId, cancellation);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));

        string[] lines = timedOut.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("'run_script' timed out after 300 ms and the game did not answer a ping within 2 s: its main thread is stuck.", lines[0]);
        Assert.Matches(ProcessLine(), lines[1]);
        Assert.StartsWith($"Process {Environment.ProcessId}: ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStuckGameWhoseHelloHasNoPidSaysItsProcessIsUnknown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AttachAsync(null, cancellation);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));

        Assert.EndsWith(
            "its main thread is stuck.\nThe game's process id is unknown: its bridge's hello carried none.",
            timedOut.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ACallToAGamePausedUnderADebuggerFailsFast()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AttachAsync(Environment.ProcessId, cancellation);
        _debuggerAttached = true;
        var elapsed = Stopwatch.StartNew();

        McpException refused = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(10_000, cancellation));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"the call failed after {elapsed.Elapsed}, not within about 1 s");
        Assert.Equal(
            $"The game (pid {Environment.ProcessId}) did not answer within 0.5 s while a debugger is attached: it is most likely paused at "
                + "a breakpoint. Continue it in the debugger, or retry if it was only busy.",
            refused.Message
        );
    }

    // The hello's pid is this test process, which still runs after the game's connection ends, as a pid another process reuses would.
    [Fact]
    public async Task AnExitedGameIsNeverReportedPaused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);
        _debuggerAttached = true;
        GodotSession session = _sessions.Resolve(null);
        game.Dispose();
        DateTime deadline = DateTime.UtcNow + AttachWait;
        while (session.HasGame)
        {
            Assert.True(DateTime.UtcNow < deadline, "the session still had its game 10 s after the connection closed");
            await Task.Delay(20, cancellation);
        }

        HangReport report = await HangProbe.RunAsync(session, cancellation);

        Assert.False(report.Answered);
        Assert.Null(report.DebuggedProcessId);
        Assert.Equal("stuck", report.Outcome);
    }

    [Fact]
    public async Task ACallWithADebuggerAttachedButRunningGoesThrough()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);
        _debuggerAttached = true;

        Task<JsonNode?> sent = _sessions.Resolve(null).SendAsync(Command, null, TimeSpan.FromSeconds(5), cancellation);
        string? first = await AnswerAsync(game, "first", cancellation);
        string? second = await AnswerAsync(game, "second", cancellation);
        JsonNode? result = await sent;

        Assert.Equal(["ping", Command], [first, second]);
        Assert.Equal("second", result?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task NoDebuggerMeansNoExtraPing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);

        Task<JsonNode?> sent = _sessions.Resolve(null).SendAsync(Command, null, TimeSpan.FromSeconds(5), cancellation);
        string? first = await AnswerAsync(game, "only", cancellation);
        JsonNode? result = await sent;

        Assert.Equal(Command, first);
        Assert.Equal("only", result?["name"]?.GetValue<string>());
    }

    // The debugger's ping before the call is answered, as by a game running when the call starts; then the game goes silent, as
    // one a breakpoint pauses mid-call does, so the call times out and the probe's own ping goes unanswered.
    [Fact]
    public async Task TheHangProbeReportsAPausedGameUnderADebugger()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);
        _debuggerAttached = true;

        Task<string> call = RunScriptAsync(TimeoutMs, cancellation);
        string? first = await AnswerAsync(game, "pong", cancellation);
        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => call);

        Assert.Equal("ping", first);
        Assert.Equal(
            $"'run_script' timed out after 300 ms. The game (pid {Environment.ProcessId}) is paused under a debugger: continue it in the "
                + "debugger before driving the game.",
            timedOut.Message
        );
    }

    [Fact]
    public void ABackstopTimeoutSaysSo()
    {
        FakeTimeProvider time = new();
        FakeLoadSource machine = new(time, processors: 4);
        using LoadClock clock = new(time, machine);
        machine.SetLoad(busyShare: 1, ownCores: 0);
        using LoadDeadline backstopped = clock.Start(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using LoadClock idleClock = new(time, new NoLoadSource());
        using LoadDeadline ceiling = idleClock.Start(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // A saturated machine runs the clock at 5%: 2 s of load-adjusted time would take far longer than the 10 s backstop.
        for (int step = 0; step < 105; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        HangReport busy = new(true, string.Empty, null, null);
        const string Running = ", but the game answered a ping, so its main thread is running; a hint.";

        Assert.Equal(DeadlineReason.Backstop, backstopped.Reason);
        Assert.StartsWith(
            "; that is 5 x its 2 s ceiling in wall time, the backstop (load-adjusted ",
            backstopped.BackstopClause(),
            StringComparison.Ordinal
        );
        Assert.Equal(
            $"'run_script' timed out after 2000 ms{backstopped.BackstopClause()}{Running}",
            busy.Describe("run_script", TimeSpan.FromSeconds(2), "; a hint", backstopped)
        );
        Assert.Equal(DeadlineReason.Ceiling, ceiling.Reason);
        Assert.Equal($"'run_script' timed out after 2000 ms{Running}", busy.Describe("run_script", TimeSpan.FromSeconds(2), "; a hint", ceiling));
    }

    [GeneratedRegex(@"^Process \d+: \d+ ms CPU over 1 s, \d+ threads, main thread \w+(/\w+)?\.$")]
    private static partial Regex ProcessLine();

    /// <summary>Answers the fake game's next request, failing the test instead of hanging when none comes within 10 s.</summary>
    private static async Task<string?> AnswerAsync(FakeBridge game, string name, CancellationToken cancellation)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(AttachWait);
        return await game.AnswerOneAsync(name, bounded.Token);
    }

    private Task<string> RunScriptAsync(CancellationToken cancellation) => RunScriptAsync(TimeoutMs, cancellation);

    private Task<string> RunScriptAsync(int timeoutMs, CancellationToken cancellation) =>
        new RuntimeTools(_sessions, TestCSharp.Unused()).RunScriptAsync(Script, timeoutMs, cancellationToken: cancellation);

    /// <summary>Attaches a session to a fake game whose hello carries <paramref name="processId"/>.</summary>
    private async Task<FakeBridge> AttachAsync(int? processId, CancellationToken cancellation)
    {
        string projectDir = _temp.Combine("game");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        string attachFile = AttachFile.PathIn(projectDir);
        Task<AttachResult> attach = _sessions.AttachAsync(projectDir, null, AttachWait, false, false, cancellation);
        DateTime deadline = DateTime.UtcNow + AttachWait;
        while (!File.Exists(attachFile))
        {
            Assert.True(DateTime.UtcNow < deadline, "the attach file did not appear within 10 s");
            await Task.Delay(20, cancellation);
        }

        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        _game = await FakeBridge.DialAsync(_listener.Port, token, projectDir, processId, cancellation);
        await attach;
        return _game;
    }
}
