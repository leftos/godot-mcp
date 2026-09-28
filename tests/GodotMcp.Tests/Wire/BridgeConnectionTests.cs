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
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
    public async Task AnAbandonedReleasedRequestSendsCancel()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
    public async Task TheReplyTimeoutIsLoadAdjusted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<BridgeConnection> accept = AcceptAsync(cancellation);
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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
        using FakeBridge bridge = await FakeBridge.DialAsync(_listener.Port, Token, ProjectDir, cancellation);
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

    private async Task<BridgeConnection> AcceptAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Wait);
        return await _listener.AcceptBridgeAsync(new HandshakeExpectation(Token, ProjectDir), timeout.Token);
    }

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

    private static JsonObject ErrorsFrame(string message) =>
        new()
        {
            ["type"] = "errors",
            ["entries"] = new JsonArray(new JsonObject { ["type"] = "error", ["message"] = message }),
            ["dropped"] = 0,
        };

    private static string MessageIn(JsonObject frame) => frame["entries"]![0]!["message"]!.GetValue<string>();
}
