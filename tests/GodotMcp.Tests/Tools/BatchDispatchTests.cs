using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
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
    private readonly TempDirectory _temp = new();

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
        _temp.Dispose();
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

    [Fact(Timeout = HangGuardMs)]
    public async Task EachErrorElsewhereShowsOnExactlyOneEntry()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        Raise(b, "before the batch");
        int clicks = 0;
        await using McpServer server = FakeServer(
            Fake(
                "click",
                _ =>
                {
                    clicks++;
                    Raise(a, $"own {clicks}");
                    Raise(b, $"raised by click {clicks}");
                    return Task.FromResult("{}");
                }
            ),
            Fake("hover", _ => Task.FromResult("{}"))
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            Step("click", a.Name),
            Step("click", a.Name),
            Step("hover", b.Name)
        );

        JsonArray steps = batch["steps"]!.AsArray();
        Assert.Equal(["raised by click 1"], Messages(steps[0], b.Name));
        Assert.Equal(["raised by click 2"], Messages(steps[1], b.Name));
        Assert.Single(steps[0]!["errorsElsewhere"]!.AsObject());
        Assert.Single(steps[1]!["errorsElsewhere"]!.AsObject());
        Assert.False(steps[2]!.AsObject().ContainsKey("errorsElsewhere"), batch.ToJsonString());
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AGroupEntryCarriesEveryErrorOutsideTheCallOfTheMemberAddressingItsSessionAndItsMembersNone()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        GodotSession c = Session();
        await using McpServer server = FakeServer(
            // hover writes no request, so click starts only once hover's member has ended and its call's end is marked.
            Fake(
                "hover",
                _ =>
                {
                    Raise(b, "inside its member's call");
                    return Task.FromResult("{}");
                }
            ),
            Fake(
                "click",
                _ =>
                {
                    Raise(b, "after its member ended");
                    Raise(c, "in no member's session");
                    return Task.FromResult("{}");
                }
            ),
            Fake("scroll", _ => Task.FromResult("{}"))
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Parallel: [Step("hover", b.Name), Step("click", a.Name)]),
            Step("scroll", c.Name)
        );

        JsonObject group = batch["steps"]![0]!.AsObject();
        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(["after its member ended"], Messages(group, b.Name));
        Assert.Equal(["in no member's session"], Messages(group, c.Name));
        Assert.Equal([b.Name, c.Name], group["errorsElsewhere"]!.AsObject().Select(session => session.Key));
        Assert.All(group["members"]!.AsArray(), member => Assert.False(member!.AsObject().ContainsKey("errorsElsewhere"), batch.ToJsonString()));
        Assert.False(batch["steps"]![1]!.AsObject().ContainsKey("errorsElsewhere"), batch.ToJsonString());
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ACancelledMembersSessionCountsAsNotAddressed()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        await using McpServer server = FakeServer(
            Fake(
                "click",
                async cancellationToken =>
                {
                    Raise(a, "raised in the cancelled member's call");
                    WriteWatch.Written();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return "{}";
                }
            ),
            Fake("hover", _ => throw new McpException("the fake hover failed"))
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Parallel: [Step("click", a.Name), Step("hover", b.Name)])
        );

        JsonObject group = batch["steps"]![0]!.AsObject();
        Assert.True(group["members"]![0]!["cancelled"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(["raised in the cancelled member's call"], Messages(group, a.Name));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ACancelledMemberCarriesNoErrorsElsewhere()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        GodotSession c = Session();
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
                _ =>
                {
                    Raise(c, "raised as the group failed");
                    throw new McpException("the fake hover failed");
                }
            )
        );

        JsonObject batch = await BatchAsync(
            server,
            TestContext.Current.CancellationToken,
            new BatchStep(Parallel: [Step("click", a.Name), Step("hover", b.Name)]),
            Step("hover", c.Name)
        );

        JsonObject group = batch["steps"]![0]!.AsObject();
        JsonObject cancelled = group["members"]![0]!.AsObject();
        Assert.True(cancelled["cancelled"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(cancelled.ContainsKey("errorsElsewhere"), batch.ToJsonString());
        Assert.Equal(["raised as the group failed"], Messages(group, c.Name));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task AOneSessionBatchNeverCarriesErrorsElsewhere()
    {
        GodotSession a = Session();
        GodotSession unnamed = Session();
        await using McpServer server = FakeServer(
            Fake(
                "click",
                _ =>
                {
                    Raise(a, "in the batch's session");
                    Raise(unnamed, "in a session the batch does not name");
                    return Task.FromResult("{}");
                }
            ),
            Fake("hover", _ => Task.FromResult("{}"))
        );

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, Step("click", a.Name), Step("hover", a.Name));

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.All(batch["steps"]!.AsArray(), entry => Assert.False(entry!.AsObject().ContainsKey("errorsElsewhere"), batch.ToJsonString()));
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task ErrorsElsewherePastTwentyASessionAreCountedAsOmitted()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        await using McpServer server = FakeServer(
            Fake(
                "click",
                _ =>
                {
                    for (int error = 1; error <= 23; error++)
                    {
                        Raise(b, $"error {error}");
                    }

                    return Task.FromResult("{}");
                }
            ),
            Fake("hover", _ => Task.FromResult("{}"))
        );

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, Step("click", a.Name), Step("hover", b.Name));

        JsonObject entry = batch["steps"]![0]!.AsObject();
        Assert.Equal(ErrorReport.MaxPerResult, entry["errorsElsewhere"]![b.Name]!.AsArray().Count);
        Assert.Equal(3, entry["errorsElsewhereOmitted"]![b.Name]!.GetValue<int>());
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task APassingNoErrorsOverAllSessionsListsTheSessionsItChecked()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        await using McpServer server = FakeServer(Fake("click", _ => Task.FromResult("{}")), Fake("hover", _ => Task.FromResult("{}")));
        BatchStep all = new(Assert: "no_errors", Sessions: JsonSerializer.SerializeToElement("all"));

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, Step("hover", b.Name), Step("click", a.Name), all);

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(
            new JsonObject { ["errors"] = 0, ["sessions"] = new JsonArray(a.Name, b.Name) }.ToJsonString(),
            batch["steps"]![2]!["result"]!.ToJsonString()
        );
    }

    [Fact(Timeout = HangGuardMs)]
    public async Task NoErrorsOverAllSessionsChecksEverySessionTheBatchNames()
    {
        GodotSession a = Session();
        GodotSession b = Session();
        GodotSession unnamed = Session();
        await using McpServer server = FakeServer(
            Fake(
                "click",
                _ =>
                {
                    Raise(a, "boom in a");
                    Raise(b, "boom in b");
                    Raise(unnamed, "boom in a session the batch does not name");
                    return Task.FromResult("{}");
                }
            ),
            Fake("hover", _ => Task.FromResult("{}"))
        );
        BatchStep all = new(Assert: "no_errors", Sessions: JsonSerializer.SerializeToElement("all"));

        JsonObject batch = await BatchAsync(server, TestContext.Current.CancellationToken, Step("click", a.Name), Step("hover", b.Name), all);

        string reason = batch["failedAt"]!["reason"]!.GetValue<string>();
        Assert.Equal(2, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.Contains(
            $"session '{a.Name}' raised 1 error(s) since the batch started or its previous no_errors:\nboom in a at ",
            reason,
            StringComparison.Ordinal
        );
        Assert.Contains(
            $"session '{b.Name}' raised 1 error(s) since the batch started or its previous no_errors:\nboom in b at ",
            reason,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(unnamed.Name, reason, StringComparison.Ordinal);
        Assert.False(batch["steps"]![2]!.AsObject().ContainsKey("errorsElsewhere"), batch.ToJsonString());
    }

    /// <summary>A pending session on the test's folder, whose error feed a test fills as a game would.</summary>
    private GodotSession Session()
    {
        string project = _temp.Combine("game");
        Directory.CreateDirectory(project);
        return _sessions.ReservePreview(project);
    }

    private static void Raise(GodotSession session, string message) => session.Errors.Add([Error(message)], 0);

    private static ErrorEntry Error(string message) => new(0, ErrorFeed.ErrorType, message, "res://x.gd", 1, "f", [], string.Empty);

    /// <summary>The messages an entry's errorsElsewhere lists for a session.</summary>
    private static string[] Messages(JsonNode? entry, string session) =>
        [.. entry!["errorsElsewhere"]![session]!.AsArray().Select(error => error!["message"]!.GetValue<string>())];

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
