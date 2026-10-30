using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Wire;

/// <summary>
/// The bridge's errors frames reaching the connection's handler, and a request's load-adjusted reply timeout, its release and
/// its cancel, over real loopback sockets; the connection's clock runs on a fake time and a fake machine of four processors.
/// </summary>
public sealed class BridgeConnectionTests : IDisposable
{
    private const string Token = "ERRORS";

    private const string Forgotten =
        "no longer awaited: the method keeps running on its node; restored Engine.time_scale to 1; restart_project stops it.";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);
    private static readonly string ProjectDir = Path.Combine(Path.GetTempPath(), "godot-mcp-connection", "game");
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLoadSource _machine;
    private readonly LoadClock _clock;
    private readonly BridgeListener _listener;

    public BridgeConnectionTests()
    {
        _machine = new FakeLoadSource(_time, processors: 4);
        _clock = new LoadClock(_time, _machine);
        _listener = new(NullLogger<BridgeListener>.Instance) { Clock = _clock };
    }

    public void Dispose()
    {
        _listener.Dispose();
        _clock.Dispose();
    }

    [Fact]
    public async Task AReleasedRequestSendsCancelAndTakesTheLateAnswer()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync(
            "frame",
            new JsonObject { ["action"] = "step" },
            TimeSpan.FromSeconds(15),
            cancellation,
            TimeSpan.FromSeconds(10)
        );
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(9.5));
        Task<JsonObject> next = ReadRequiredAsync(bridge, "the cancel", cancellation);
        await Task.Delay(Quiet, cancellation);
        bool cancelledEarly = next.IsCompleted;
        Run(TimeSpan.FromSeconds(0.7));
        JsonObject cancel = await next;
        await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = true }, cancellation);
        await bridge.ReplyAsync(request, new JsonObject { ["name"] = "stalled" }, cancellation);
        JsonNode reply = (await sent.WaitAsync(Wait, cancellation))!;

        Assert.False(cancelledEarly, "the cancel went before its 10 s release");
        Assert.Equal("cancel", cancel["command"]!.GetValue<string>());
        Assert.Equal($$"""{"request":{{request["id"]!.ToJsonString()}}}""", cancel["params"]!.ToJsonString());
        Assert.Equal("stalled", reply["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReleasedRequestWhoseTimersRanLateStillTakesTheAnswerToItsCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // call_method as CallStoppableAsync sends it: released at 1 s, its reply waited for 5 s more.
        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation, TimeSpan.FromSeconds(1));
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        // A server that got no CPU from the write until 7 s later: every timer of the request runs at once, past both limits.
        _time.Advance(TimeSpan.FromSeconds(7));
        JsonObject cancel = await ReadRequiredAsync(bridge, "the cancel", cancellation);
        // The bridge's order for a call_method it stops awaiting: the request's answer, then the cancel's.
        await bridge.WriteAsync(
            new JsonObject
            {
                ["id"] = request["id"]!.DeepClone(),
                ["ok"] = false,
                ["error"] = Forgotten,
            },
            cancellation
        );
        await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = true }, cancellation);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => sent.WaitAsync(Wait, cancellation));
        Assert.Equal($"The bridge refused 'call_method': {Forgotten}", refused.Message);
    }

    [Fact]
    public async Task AReleasedRequestWhoseTimersRanLateTimesOutWhenItsCancelGoesUnanswered()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation, TimeSpan.FromSeconds(1));
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        _time.Advance(TimeSpan.FromSeconds(7));
        await ReadRequiredAsync(bridge, "the cancel", cancellation);
        // A game that answers nothing, not even the cancel: the wait ends at the allowance after the cancel went out.
        await RunUntilAsync(sent, TimeSpan.FromSeconds(60), cancellation);
        LoadTimeoutException timedOut = await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(HangGuard, cancellation));

        Assert.Equal(
            $"The bridge did not answer 'call_method' (request {request["id"]?.GetValue<long>()}) within 6 s; a late reply will be dropped.",
            timedOut.Message
        );
        // The report covers the whole wait: the 7 s stall and the 5 s allowance after the cancel.
        Assert.Equal(DeadlineReason.Ceiling, timedOut.Deadline.Reason);
        Assert.True(timedOut.Deadline.Adjusted >= TimeSpan.FromSeconds(12), $"load-adjusted {timedOut.Deadline.Adjusted}");
        Assert.True(timedOut.Deadline.Wall >= TimeSpan.FromSeconds(12), $"wall {timedOut.Deadline.Wall}");
    }

    [Fact]
    public async Task AReleasedRequestOnASaturatedMachineEndsAtItsReplyBackstop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // Every core busy with other work: once sampled, the clock runs at its 5% floor, so both of the request's deadlines
        // end on their wall backstops, the release at 5 s and the reply at 5 x 6 s = 30 s.
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        using (_clock.Start(TimeSpan.FromSeconds(1), cancellation))
        {
            Run(TimeSpan.FromSeconds(1.1));
        }

        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation, TimeSpan.FromSeconds(1));
        await ReadRequiredAsync(bridge, "the request", cancellation);
        // Time stops just past the reply's backstop: a wait that went on past it would never end.
        Run(TimeSpan.FromSeconds(30.2));
        LoadTimeoutException timedOut = await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(HangGuard, cancellation));

        Assert.Equal(DeadlineReason.Backstop, timedOut.Deadline.Reason);
        Assert.Contains("within 6 s; that is 5 x its 6 s ceiling in wall time, the backstop", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatLandsAsTheReplyDeadlinePassesIsTaken()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation, TimeSpan.FromSeconds(1));
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        // The answer goes out in the same step of time that passes the release and the reply deadline.
        await bridge.ReplyAsync(request, new JsonObject { ["name"] = "answered" }, cancellation);
        _time.Advance(TimeSpan.FromSeconds(7));
        JsonNode? reply = await sent.WaitAsync(HangGuard, cancellation);

        Assert.Equal("answered", reply?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task AReleasedRequestOnARunningServerTimesOutAtItsReplyTimeout()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation, TimeSpan.FromSeconds(1));
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        // Time stepped as a running server's timers see it: the cancel goes at the 1 s release.
        Run(TimeSpan.FromSeconds(1.2));
        await ReadRequiredAsync(bridge, "the cancel", cancellation);
        await RunUntilAsync(sent, TimeSpan.FromSeconds(60), cancellation);
        LoadTimeoutException timedOut = await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(HangGuard, cancellation));

        Assert.Equal(
            $"The bridge did not answer 'call_method' (request {request["id"]?.GetValue<long>()}) within 6 s; a late reply will be dropped.",
            timedOut.Message
        );
        Assert.Equal(DeadlineReason.Ceiling, timedOut.Deadline.Reason);
        Assert.True(timedOut.Deadline.Adjusted >= TimeSpan.FromSeconds(6), $"load-adjusted {timedOut.Deadline.Adjusted}");
    }

    [Fact]
    public async Task AnAbandonedReleasedRequestSendsCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        Task<JsonNode?> sent = connection.SendAsync(
            "frame",
            new JsonObject { ["action"] = "step" },
            TimeSpan.FromSeconds(15),
            caller.Token,
            TimeSpan.FromSeconds(10)
        );
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sent.WaitAsync(Wait, cancellation));
        JsonObject cancel = await ReadRequiredAsync(bridge, "the cancel", cancellation);

        Assert.Equal("cancel", cancel["command"]!.GetValue<string>());
        Assert.Equal($$"""{"request":{{request["id"]!.ToJsonString()}}}""", cancel["params"]!.ToJsonString());
    }

    [Fact]
    public async Task AReleaseAndAWalkAwayTogetherSendExactlyOneCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // Which of the two paths runs first is up to the thread pool, so the race is run several times.
        for (int round = 1; round <= 10; round++)
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task<JsonNode?> sent = connection.SendAsync(
                "frame",
                new JsonObject { ["action"] = "step" },
                TimeSpan.FromSeconds(15),
                caller.Token,
                TimeSpan.FromSeconds(10)
            );
            JsonObject request = await ReadRequiredAsync(bridge, $"round {round}'s request", cancellation);
            Run(TimeSpan.FromSeconds(9.9));
            _time.Advance(TimeSpan.FromSeconds(0.2));
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sent.WaitAsync(Wait, cancellation));
            JsonObject cancel = await ReadRequiredAsync(bridge, $"round {round}'s cancel", cancellation);
            await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = true }, cancellation);
            JsonObject? second = await ReadWithinAsync(bridge, Quiet, cancellation);

            Assert.Equal($$"""{"request":{{request["id"]!.ToJsonString()}}}""", cancel["params"]!.ToJsonString());
            Assert.Null(second);
        }
    }

    [Fact]
    public async Task ARequestNeverWrittenSendsNoCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        // A token cancelled before the call makes the write throw before any byte of the request goes out.
        await caller.CancelAsync();
        Task<JsonNode?> sent = connection.SendAsync(
            "frame",
            new JsonObject { ["action"] = "step" },
            TimeSpan.FromSeconds(15),
            caller.Token,
            TimeSpan.FromSeconds(10)
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sent.WaitAsync(Wait, cancellation));
        JsonObject? after = await ReadWithinAsync(bridge, Quiet, cancellation);

        Assert.Null(after);
    }

    [Fact]
    public async Task ARequestWithoutReleaseSendsNoCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync("wait_for", new JsonObject { ["timeoutMs"] = 10_000 }, TimeSpan.FromSeconds(15), cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(15.2));
        await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(Wait, cancellation));
        JsonObject? after = await ReadWithinAsync(bridge, Quiet, cancellation);

        Assert.Equal("""{"timeoutMs":10000}""", request["params"]?.ToJsonString());
        Assert.Null(after);
    }

    [Fact]
    public async Task AReleasedRequestCarriesTheBackstop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendAsync(
            "wait_for",
            new JsonObject { ["timeoutMs"] = 2000 },
            TimeSpan.FromSeconds(7),
            cancellation,
            TimeSpan.FromSeconds(2)
        );
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await bridge.ReplyAsync(request, new JsonObject { ["name"] = "met" }, cancellation);
        JsonNode? reply = await sent.WaitAsync(Wait, cancellation);

        Assert.Equal("""{"timeoutMs":2000,"backstopMs":10000}""", request["params"]?.ToJsonString());
        Assert.Equal("met", reply?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task AWallCapBeforeTheReleaseCancelsAndMarksTheUnmetAnswerWallCapped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // Every core busy with other work: the 10 s release would take 200 s of wall time; the 2 s cap counts wall time only.
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        Task<JsonNode?> sent = SendCapped(connection, TimeSpan.FromSeconds(2), cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(1.8));
        Task<JsonObject> next = ReadRequiredAsync(bridge, "the cancel", cancellation);
        await Task.Delay(Quiet, cancellation);
        bool cancelledEarly = next.IsCompleted;
        Run(TimeSpan.FromSeconds(0.4));
        JsonObject cancel = await next;
        // The bridge answers within the reply allowance after the cancel.
        Run(TimeSpan.FromSeconds(4));
        await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = true }, cancellation);
        await bridge.ReplyAsync(request, new JsonObject { ["met"] = false, ["last"] = false }, cancellation);
        JsonNode? reply = await sent.WaitAsync(Wait, cancellation);

        Assert.False(cancelledEarly, "the cancel went before the 2 s cap");
        Assert.Equal($$"""{"request":{{request["id"]!.ToJsonString()}}}""", cancel["params"]!.ToJsonString());
        Assert.Equal("""{"met":false,"last":false,"wallCapped":true}""", reply?.ToJsonString());
    }

    [Fact]
    public async Task AReleaseBeforeTheWallCapSendsOneCancelAndNoWallCapped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = connection.SendCappedAsync(
            "wait_for",
            new JsonObject { ["timeoutMs"] = 2000 },
            TimeSpan.FromSeconds(7),
            new Release(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)),
            cancellation
        );
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(2.2));
        JsonObject cancel = await ReadRequiredAsync(bridge, "the cancel", cancellation);
        // The cap passes too before the bridge answers: its expiry sends no second cancel.
        Run(TimeSpan.FromSeconds(1.2));
        JsonObject? second = await ReadWithinAsync(bridge, Quiet, cancellation);
        await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = true }, cancellation);
        await bridge.ReplyAsync(request, new JsonObject { ["met"] = false, ["last"] = false }, cancellation);
        JsonNode? reply = await sent.WaitAsync(Wait, cancellation);

        Assert.Equal("cancel", cancel["command"]!.GetValue<string>());
        Assert.Null(second);
        Assert.Equal("""{"met":false,"last":false}""", reply?.ToJsonString());
    }

    [Fact]
    public async Task ACappedRequestWhoseCancelGoesUnansweredEndsAtTheCapPlusTheReplyAllowance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // A game whose main thread hangs on a saturated machine: the load-adjusted reply deadline would run to its 75 s backstop.
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        Task<JsonNode?> sent = SendCapped(connection, TimeSpan.FromSeconds(2), cancellation);
        await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(2.2));
        await ReadRequiredAsync(bridge, "the cancel", cancellation);
        await RunUntilAsync(sent, TimeSpan.FromSeconds(120), cancellation);
        LoadTimeoutException timedOut = await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(HangGuard, cancellation));

        // The 2 s cap plus the 5 s reply allowance (the 15 s timeout less the 10 s release), on the fake clock.
        Assert.Equal(DeadlineReason.WallCap, timedOut.Deadline.Reason);
        Assert.InRange(timedOut.Deadline.Wall, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.2));
        Assert.Contains(
            "within 15 s; that is its 2 s wall cap and 5 s to answer the cancel, 7 s of wall time",
            timedOut.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AMetAnswerAfterTheWallCapsCancelIsNotWallCapped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        Task<JsonNode?> sent = SendCapped(connection, TimeSpan.FromSeconds(2), cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(2.2));
        JsonObject cancel = await ReadRequiredAsync(bridge, "the cancel", cancellation);
        // The bridge met the condition in the frame the cancel reached it.
        await bridge.ReplyAsync(request, new JsonObject { ["met"] = true }, cancellation);
        await bridge.ReplyAsync(cancel, new JsonObject { ["cancelled"] = false }, cancellation);
        JsonNode? reply = await sent.WaitAsync(Wait, cancellation);

        Assert.Equal("""{"met":true}""", reply?.ToJsonString());
    }

    [Fact]
    public async Task TheReplyTimeoutIsLoadAdjusted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // Half of the machine busy with other work: after the first sample, 2 s of wall time per second of the timeout.
        _machine.SetLoad(busyShare: 0.5, ownCores: 0);
        Task<JsonNode?> sent = connection.SendAsync("wait_for", null, TimeSpan.FromSeconds(10), cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        Run(TimeSpan.FromSeconds(15));
        await Task.Delay(Quiet, cancellation);
        bool endedAtWallTime = sent.IsCompleted;
        Run(TimeSpan.FromSeconds(5));
        LoadTimeoutException timedOut = await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(Wait, cancellation));

        Assert.False(endedAtWallTime, "the reply timeout ended at 15 s of wall time, before 10 s of load-adjusted time");
        Assert.Equal(DeadlineReason.Ceiling, timedOut.Deadline.Reason);
        Assert.Equal(
            $"The bridge did not answer 'wait_for' (request {request["id"]?.GetValue<long>()}) within 10 s; a late reply will be dropped.",
            timedOut.Message
        );
    }

    [Fact]
    public async Task APingStaysOnWallTime()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;

        // A saturated machine and a fake time that never moves: a load-adjusted ping would never time out.
        _machine.SetLoad(busyShare: 1, ownCores: 0);
        Task<JsonNode?> ping = connection.SendRawAsync("ping", null, Quiet, cancellation);
        Task first = await Task.WhenAny(ping, Task.Delay(Wait, cancellation));

        Assert.Same(ping, first);
        TimeoutException timedOut = await Assert.ThrowsAsync<TimeoutException>(() => ping);
        Assert.EndsWith("within 0.3 s; a late reply will be dropped.", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorsFrameBeforeOnErrorsIsReplayed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await bridge.WriteAsync(ErrorsFrame("early"), cancellation);
        await using BridgeConnection connection = await accept;

        // Frames are read in order, so once the reply is in, the errors frame ahead of it has been read too.
        Task answer = bridge.AnswerOneAsync("pong", cancellation);
        await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;
        List<string> received = [];
        connection.OnErrors(frame => received.Add(MessageIn(frame)));

        Assert.Equal(["early"], received);
    }

    [Fact]
    public async Task ErrorsSentBeforeAReplyAreDeliveredBeforeSendAsyncCompletes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<string> received = [];
        connection.OnErrors(frame => received.Add(MessageIn(frame)));

        Task answer = bridge.AnswerOneAfterAsync([ErrorsFrame("first"), ErrorsFrame("second")], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        string[] atReply = [.. received];
        await answer;

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Equal(["first", "second"], atReply);
    }

    [Fact]
    public async Task AThrowingErrorsHandlerDoesNotStopReplies()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        int calls = 0;
        connection.OnErrors(_ =>
        {
            calls++;
            throw new InvalidOperationException("the handler failed");
        });

        Task answer = bridge.AnswerOneAfterAsync([ErrorsFrame("boom")], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;

        Assert.Equal(1, calls);
        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task ACapturedFrameReachesItsHandlerAndAnUnknownIdlessFrameIsDropped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<string> captured = [];
        List<string> errors = [];
        connection.OnCaptured(frame => captured.Add(frame["events"]![0]!["key"]!.GetValue<string>()));
        connection.OnErrors(frame => errors.Add(MessageIn(frame)));

        JsonObject unknown = new() { ["type"] = "mystery", ["events"] = new JsonArray(new JsonObject { ["key"] = "X" }) };
        JsonObject frame = new() { ["type"] = "captured", ["events"] = new JsonArray(new JsonObject { ["type"] = "key", ["key"] = "A" }) };
        Task answer = bridge.AnswerOneAfterAsync([unknown, frame], "pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        string[] atReply = [.. captured];
        await answer;

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Equal(["A"], atReply);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AReplysFramesReachTheirObserverBeforeTheCallReturns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<(long Read, long Replied)> frames = [];
        connection.OnReplyFrames((read, replied) => frames.Add((read, replied)));

        Task<JsonNode?> sent = connection.SendAsync("ping", null, Wait, cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await bridge.WriteAsync(ReplyWithFrames(request, new JsonArray(7, 12)), cancellation);
        await sent.WaitAsync(Wait, cancellation);
        (long, long)[] atReply = [.. frames];

        Assert.Equal([(7L, 12L)], atReply);
    }

    [Fact]
    public async Task AReplyWithoutFramesReachesNoObserver()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<(long Read, long Replied)> frames = [];
        connection.OnReplyFrames((read, replied) => frames.Add((read, replied)));

        Task answer = bridge.AnswerOneAsync("pong", cancellation);
        JsonNode? reply = await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Empty(frames);
    }

    [Theory]
    [InlineData("[9, 8]")]
    [InlineData("[1, 2, 3]")]
    [InlineData("[4]")]
    [InlineData("\"7, 12\"")]
    [InlineData("[\"7\", 12]")]
    [InlineData("[-1, 4]")]
    [InlineData("[7.5, 12]")]
    public async Task MalformedFramesReachNoObserver(string framesJson)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<(long Read, long Replied)> frames = [];
        connection.OnReplyFrames((read, replied) => frames.Add((read, replied)));

        Task<JsonNode?> sent = connection.SendAsync("ping", null, Wait, cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await bridge.WriteAsync(ReplyWithFrames(request, JsonNode.Parse(framesJson)), cancellation);
        JsonNode? reply = await sent.WaitAsync(Wait, cancellation);

        Assert.Equal("pong", reply?["name"]?.GetValue<string>());
        Assert.Empty(frames);
    }

    [Fact]
    public async Task AWholeFloatFrameIsReadAsItsNumber()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        List<(long Read, long Replied)> frames = [];
        connection.OnReplyFrames((read, replied) => frames.Add((read, replied)));

        Task<JsonNode?> sent = connection.SendAsync("ping", null, Wait, cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await bridge.WriteAsync(ReplyWithFrames(request, JsonNode.Parse("[7.0, 12]")), cancellation);
        await sent.WaitAsync(Wait, cancellation);
        (long, long)[] atReply = [.. frames];

        Assert.Equal([(7L, 12L)], atReply);
    }

    [Fact]
    public async Task ALateReplysFramesStillReachTheObserver()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        TaskCompletionSource<(long Read, long Replied)> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnReplyFrames((read, replied) => observed.TrySetResult((read, replied)));

        Task<JsonNode?> sent = connection.SendAsync("call_method", [], TimeSpan.FromSeconds(6), cancellation);
        JsonObject request = await ReadRequiredAsync(bridge, "the request", cancellation);
        await RunUntilAsync(sent, TimeSpan.FromSeconds(60), cancellation);
        await Assert.ThrowsAsync<LoadTimeoutException>(() => sent.WaitAsync(HangGuard, cancellation));
        await bridge.WriteAsync(ReplyWithFrames(request, new JsonArray(30, 95)), cancellation);

        Assert.Equal((30L, 95L), await observed.Task.WaitAsync(Wait, cancellation));
    }

    [Fact]
    public async Task AThrowingFramesObserverDoesNotStopReplies()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation, answerPings: false);
        await using BridgeConnection connection = await accept;
        int calls = 0;
        connection.OnReplyFrames(
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("the observer failed");
            }
        );

        Task<JsonNode?> first = connection.SendAsync("ping", null, Wait, cancellation);
        await bridge.WriteAsync(
            ReplyWithFrames(await ReadRequiredAsync(bridge, "the first request", cancellation), new JsonArray(1, 2)),
            cancellation
        );
        JsonNode? firstReply = await first.WaitAsync(Wait, cancellation);
        Task answer = bridge.AnswerOneAsync("again", cancellation);
        JsonNode? secondReply = await connection.SendAsync("ping", null, Wait, cancellation);
        await answer;

        Assert.Equal(1, calls);
        Assert.Equal("pong", firstReply?["name"]?.GetValue<string>());
        Assert.Equal("again", secondReply?["name"]?.GetValue<string>());
    }

    /// <summary>An ok reply to <paramref name="request"/> named pong, carrying <paramref name="frames"/> as its frames field.</summary>
    private static JsonObject ReplyWithFrames(JsonObject request, JsonNode? frames) =>
        new()
        {
            ["id"] = request["id"]!.DeepClone(),
            ["ok"] = true,
            ["result"] = new JsonObject { ["name"] = "pong" },
            ["frames"] = frames,
        };

    private async Task<BridgeConnection> AcceptAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        return await _listener.AcceptBridgeAsync(new HandshakeExpectation(Token, ProjectDir), timeout.Token);
    }

    /// <summary>A wait_for released at 10 s, its reply waited for 5 s more, capped at <paramref name="wallCap"/> of wall time.</summary>
    private static Task<JsonNode?> SendCapped(BridgeConnection connection, TimeSpan wallCap, CancellationToken cancellation) =>
        connection.SendCappedAsync(
            "wait_for",
            new JsonObject { ["timeoutMs"] = 10_000 },
            TimeSpan.FromSeconds(15),
            new Release(TimeSpan.FromSeconds(10), wallCap),
            cancellation
        );

    /// <summary>The bridge's next request, failing the test instead of hanging when none comes within <see cref="Wait"/>.</summary>
    private static async Task<JsonObject> ReadRequiredAsync(FakeBridge bridge, string what, CancellationToken cancellation) =>
        await ReadWithinAsync(bridge, Wait, cancellation) ?? throw new InvalidOperationException($"{what} did not come within 5 s");

    /// <summary>The bridge's next request, or null when none comes within <paramref name="wait"/>.</summary>
    private static async Task<JsonObject?> ReadWithinAsync(FakeBridge bridge, TimeSpan wait, CancellationToken cancellation)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(wait);
        try
        {
            return await bridge.ReadRequestAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Moves the fake time on by <paramref name="span"/> in steps, so the clock's timers fire as they fall due.</summary>
    private void Run(TimeSpan span)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < span; passed += Step)
        {
            _time.Advance(Step);
        }
    }

    /// <summary>
    /// Moves the fake time on in steps until <paramref name="task"/> completes or <paramref name="limit"/> has passed, letting the
    /// thread pool run between steps, so a timer the product starts late still falls due.
    /// </summary>
    private async Task RunUntilAsync(Task task, TimeSpan limit, CancellationToken cancellation)
    {
        for (TimeSpan passed = TimeSpan.Zero; passed < limit && !task.IsCompleted; passed += Step)
        {
            _time.Advance(Step);
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(1), cancellation));
        }
    }

    private static JsonObject ErrorsFrame(string message) =>
        new()
        {
            ["type"] = "errors",
            ["entries"] = new JsonArray(new JsonObject { ["type"] = "error", ["message"] = message }),
            ["dropped"] = 0,
        };

    private static string MessageIn(JsonObject frame) => frame["entries"]![0]!["message"]!.GetValue<string>();
}
