using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// How batch_drive runs its steps and turns their failures into results, through a real MCP server's tool collection; no
/// Godot runs here, so every step that reaches a game fails for want of a session.
/// </summary>
public sealed class BatchDispatchTests : IAsyncDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;
    private readonly ServiceProvider _services;

    public BatchDispatchTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly, ToolJson.Options);
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public async Task AWrongTypedArgumentFailsTheStepWithTheBindingDetail()
    {
        await using McpServer server = CreateServer(_services.GetRequiredService<IOptions<McpServerOptions>>().Value);

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = 5 })
        );

        string error = batch["steps"]![0]!["error"]!.GetValue<string>();
        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.StartsWith("click: target takes an object, not 5. target: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedExceptionFailsItsStepAndKeepsTheEarlierOnes()
    {
        McpServerOptions options = new() { ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal) };
        options.ToolCollection.Add(McpServerTool.Create((Func<string>)(() => """{"fine":true}"""), new() { Name = "get_ui_elements" }));
        options.ToolCollection.Add(
            McpServerTool.Create((Func<string>)(() => throw new InvalidOperationException("the fake tool broke")), new() { Name = "get_errors" })
        );
        await using McpServer server = CreateServer(options);

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Tool: "get_ui_elements"),
            new BatchStep(Tool: "get_errors"),
            new BatchStep(Tool: "click")
        );

        JsonArray steps = batch["steps"]!.AsArray();
        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(2, steps.Count);
        Assert.True(steps[0]!["result"]!["fine"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal("InvalidOperationException: the fake tool broke", steps[1]!["error"]!.GetValue<string>());
        Assert.Equal(1, batch["failedAt"]!["index"]!.GetValue<int>());
    }

    [Fact]
    public async Task ClientCancellationPropagates()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _tools.BatchDriveAsync([new BatchStep(Tool: "click")], null!, null, cancelled.Token)
        );
    }

    [Fact]
    public async Task AnAssertionsOwnSessionWinsOverTheBatchs()
    {
        JsonElement idle = JsonSerializer.SerializeToElement("idle");
        BatchStep own = new(Assert: "property", Node: "TimeProbe", Property: "state", EqualsValue: idle, Session: "inner");
        BatchStep inherited = own with { Session = null };

        JsonObject first = JsonNode.Parse(await _tools.BatchDriveAsync([own], null!, "outer", TestContext.Current.CancellationToken))!.AsObject();
        JsonObject second = JsonNode
            .Parse(await _tools.BatchDriveAsync([inherited], null!, "outer", TestContext.Current.CancellationToken))!
            .AsObject();

        Assert.StartsWith("No session named 'inner'.", first["failedAt"]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("No session named 'outer'.", second["failedAt"]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoErrorsSessionThatCannotBeFoundNamesItsStep()
    {
        BatchStep[] steps = [new BatchStep(Tool: "click"), new BatchStep(Assert: "no_errors", Session: "absent")];

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.BatchDriveAsync(steps, null!, null, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("step 1: No session named 'absent'.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task GroupMembersStartInListedOrderEachOnceTheOneBeforeHasWritten()
    {
        ConcurrentQueue<string> log = new();
        TaskCompletionSource hoverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using McpServer server = FakeServer(
            Fake(
                "click",
                async cancellationToken =>
                {
                    log.Enqueue("click started");
                    log.Enqueue("click written");
                    WriteWatch.Written();
                    // Ends only once hover has started, so a group that waited for click's end instead of its write never ends.
                    await hoverStarted.Task.WaitAsync(cancellationToken);
                    log.Enqueue("click ended");
                    return "{}";
                }
            ),
            Fake(
                "hover",
                _ =>
                {
                    log.Enqueue("hover started");
                    hoverStarted.SetResult();
                    return Task.FromResult("{}");
                }
            )
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Parallel: [Step("click", "a"), Step("hover", "b")])
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(["click started", "click written", "hover started", "click ended"], log);
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ASetupRequestBeforeAMembersOwnDoesNotStartTheNextMember()
    {
        TaskCompletionSource hoverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool startedOnSetup = false;
        await using McpServer server = FakeServer(
            Fake(
                "run_csharp",
                async cancellationToken =>
                {
                    await SendSetupAsync();
                    // A hold, not a bound: a correct group never starts hover here, however long the hold.
                    startedOnSetup = await Task.WhenAny(hoverStarted.Task, Task.Delay(300, cancellationToken)) == hoverStarted.Task;
                    WriteWatch.Written();
                    await hoverStarted.Task.WaitAsync(cancellationToken);
                    return "{}";
                }
            ),
            Fake(
                "hover",
                _ =>
                {
                    hoverStarted.SetResult();
                    return Task.FromResult("{}");
                }
            )
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Parallel: [Step("run_csharp", "a"), Step("hover", "b")])
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(startedOnSetup, "hover started on run_csharp's setup request, before its own request was written");
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AnUnexpectedExceptionInAMemberCancelsItsSiblingsAtOnce()
    {
        await using McpServer server = FakeServer(
            Fake(
                "click",
                async cancellationToken =>
                {
                    WriteWatch.Written();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            ),
            Fake("hover", _ => throw new TaskCanceledException("an internal wait gave up"))
        );

        TaskCanceledException thrown = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            BatchAsync(server, TestContext.Current.CancellationToken, new BatchStep(Parallel: [Step("click", "a"), Step("hover", "b")]))
        );

        Assert.Equal("an internal wait gave up", thrown.Message);
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task TheDeadlinePassingInAGroupFailsTheFirstMemberToReportAndCancelsTheRest()
    {
        RuntimeTools hurried = new(_sessions, TestCSharp.Unused()) { BatchDeadline = TimeSpan.FromMilliseconds(300) };
        await using McpServer server = FakeServer(
            Fake(
                "click",
                async cancellationToken =>
                {
                    WriteWatch.Written();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            ),
            Fake(
                "hover",
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            )
        );
        BatchStep group = new(Parallel: [Step("click", "a"), Step("hover", "b")]);

        JsonObject batch = JsonNode.Parse(await hurried.BatchDriveAsync([group], server, null, TestContext.Current.CancellationToken))!.AsObject();

        JsonArray members = batch["steps"]![0]!["members"]!.AsArray();
        string reason = batch["failedAt"]!["reason"]!.GetValue<string>();
        int failed = batch["failedAt"]!["member"]!.GetValue<int>();
        Assert.StartsWith(hurried.DeadlineReason, reason, StringComparison.Ordinal);
        Assert.Equal(reason, members[failed]!["error"]!.GetValue<string>());
        Assert.True(members[1 - failed]!["cancelled"]!.GetValue<bool>(), batch.ToJsonString());
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AClientCancelDuringAGroupAwaitsEveryMemberAndPropagates()
    {
        ConcurrentQueue<string> ended = new();
        using var client = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using McpServer server = FakeServer(
            Fake(
                "click",
                async cancellationToken =>
                {
                    try
                    {
                        WriteWatch.Written();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        return "{}";
                    }
                    finally
                    {
                        ended.Enqueue("click");
                    }
                }
            ),
            Fake(
                "hover",
                async cancellationToken =>
                {
                    try
                    {
                        await client.CancelAsync();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        return "{}";
                    }
                    finally
                    {
                        ended.Enqueue("hover");
                    }
                }
            )
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BatchAsync(server, client.Token, new BatchStep(Parallel: [Step("click", "a"), Step("hover", "b")]))
        );

        Assert.Equal(["click", "hover"], ended.Order(StringComparer.Ordinal));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AnOutputWaitMemberStartsTheNextOnceItHasReadItsBaseline()
    {
        await _sessions.ReserveProcessAsync("server", AppContext.BaseDirectory, defaultName: false);
        await using McpServer server = FakeServer(Fake("hover", _ => throw new McpException("the fake hover failed")));
        BatchStep never = new(Assert: "wait", Output: "never printed", TimeoutMs: 10_000, Session: "server");

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, new BatchStep(Parallel: [never, Step("hover", "b")]));

        // Had the output wait held the next member until its end, it would have failed first, at its 10 s timeout.
        JsonObject wait = batch["steps"]![0]!["members"]![0]!.AsObject();
        Assert.Equal(1, batch["failedAt"]!["member"]!.GetValue<int>());
        Assert.True(wait["cancelled"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(wait["sent"]!.GetValue<bool>(), batch.ToJsonString());
    }

    /// <summary>A setup request as a tool sends it ahead of its own: unwatched, so it does not count as the step's request.</summary>
    private static async Task SendSetupAsync()
    {
        WriteWatch.Unwatch();
        await Task.Yield();
        WriteWatch.Written();
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AFailingMemberCancelsTheMembersStillRunningAndStopsTheBatch()
    {
        await using McpServer server = FakeServer(
            Fake("get_ui_elements", _ => Task.FromResult("""{"fine":true}""")),
            Fake(
                "click",
                async cancellationToken =>
                {
                    WriteWatch.Written();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            ),
            Fake("hover", _ => throw new McpException("the fake hover failed")),
            Fake(
                "scroll",
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            )
        );
        BatchStep group = new(Parallel: [Step("get_ui_elements", "a"), Step("click", "b"), Step("hover", "c"), Step("scroll", "d")]);

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, group, Step("get_ui_elements", "a"));

        JsonObject entry = batch["steps"]!.AsArray().Single()!.AsObject();
        JsonArray members = entry["members"]!.AsArray();
        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal("""{"index":0,"member":2,"reason":"the fake hover failed"}""", batch["failedAt"]!.ToJsonString());
        Assert.True(entry["parallel"]!.GetValue<bool>(), entry.ToJsonString());
        Assert.False(entry["ok"]!.GetValue<bool>(), entry.ToJsonString());
        Assert.True(members[0]!["result"]!["fine"]!.GetValue<bool>(), entry.ToJsonString());
        Assert.Equal("the fake hover failed", members[2]!["error"]!.GetValue<string>());
        AssertCancelled(members[1]!.AsObject(), "click", "b", sent: true);
        AssertCancelled(members[3]!.AsObject(), "scroll", "d", sent: false);
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task EveryEntryCarriesItsSpanOnTheDriveClock()
    {
        await using McpServer server = FakeServer(Fake("get_ui_elements", _ => Task.FromResult("{}")), Fake("hover", _ => Task.FromResult("{}")));

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            Step("get_ui_elements", "a"),
            new BatchStep(Parallel: [Step("get_ui_elements", "a"), Step("hover", "b")])
        );

        JsonArray steps = batch["steps"]!.AsArray();
        JsonObject group = steps[1]!.AsObject();
        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.True(batch["startedAt"]!.GetValue<double>() <= Ms(steps[0], "startMs"), batch.ToJsonString());
        Assert.True(Ms(steps[0], "startMs") <= Ms(steps[0], "endMs"), batch.ToJsonString());
        Assert.True(Ms(steps[0], "endMs") <= Ms(group, "startMs"), batch.ToJsonString());
        Assert.All(
            group["members"]!.AsArray(),
            member =>
            {
                Assert.True(Ms(group, "startMs") <= Ms(member, "startMs"), batch.ToJsonString());
                Assert.True(Ms(member, "startMs") <= Ms(member, "endMs"), batch.ToJsonString());
                Assert.True(Ms(member, "endMs") <= Ms(group, "endMs"), batch.ToJsonString());
            }
        );
    }

    /// <summary>A guard against a group that never ends; a passing run never comes near it.</summary>
    private const int HangGuardMs = 120_000;

    private static void AssertCancelled(JsonObject member, string tool, string session, bool sent)
    {
        Assert.Equal(tool, member["tool"]!.GetValue<string>());
        Assert.Equal(session, member["session"]!.GetValue<string>());
        Assert.False(member["ok"]!.GetValue<bool>(), member.ToJsonString());
        Assert.True(member["cancelled"]!.GetValue<bool>(), member.ToJsonString());
        Assert.Equal(sent, member["sent"]!.GetValue<bool>());
        Assert.False(member.ContainsKey("result"), member.ToJsonString());
        Assert.False(member.ContainsKey("error"), member.ToJsonString());
        Assert.True(Ms(member, "startMs") <= Ms(member, "endMs"), member.ToJsonString());
    }

    private static double Ms(JsonNode? entry, string field) => entry![field]!.GetValue<double>();

    private static BatchStep Step(string tool, string session) => new(Tool: tool, Session: session);

    /// <summary>A fake runtime tool taking the session a batch step hands it, and the call's cancellation.</summary>
    private static McpServerTool Fake(string name, Func<CancellationToken, Task<string>> body) =>
        McpServerTool.Create(
            (Func<string?, CancellationToken, Task<string>>)((session, cancellationToken) => body(cancellationToken)),
            new() { Name = name }
        );

    private McpServer FakeServer(params McpServerTool[] tools)
    {
        McpServerOptions options = new() { ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal) };
        foreach (McpServerTool tool in tools)
        {
            options.ToolCollection.Add(tool);
        }

        return CreateServer(options);
    }

    private McpServer CreateServer(McpServerOptions options) =>
        McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);

    private async Task<JsonObject> BatchAsync(McpServer server, CancellationToken cancellation, params BatchStep[] steps) =>
        JsonNode.Parse(await _tools.BatchDriveAsync(steps, server, null, cancellation))!.AsObject();
}
