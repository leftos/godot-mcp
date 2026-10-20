using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>batch_drive's checks of its steps, which refuse the whole batch before any step runs; no Godot runs here.</summary>
public sealed class BatchValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public BatchValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void TheStepTimeoutIsDescribedAsLoadAdjusted()
    {
        DescriptionAttribute? description = typeof(BatchStep).GetProperty(nameof(BatchStep.TimeoutMs))?.GetCustomAttribute<DescriptionAttribute>();

        Assert.NotNull(description);
        Assert.Contains("load-adjusted", description.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AWaitThatWasNotMetWhenCheckedSaysSo() => Assert.Equal("when checked", RuntimeTools.WaitWhenText(0, Reply(83417)));

    [Fact]
    public void ATimedOutWaitNamesItsBudgetAndTheWallTimeItTook() =>
        Assert.Equal("within 20000 ms (load-adjusted; 83.4 s of wall time)", RuntimeTools.WaitWhenText(20000, Reply(83417)));

    [Fact]
    public void ATimedOutWaitWithoutTheRepliesWallTimeNamesItsBudgetAlone() => Assert.Equal("within 20000 ms", RuntimeTools.WaitWhenText(20000, []));

    /// <summary>A wait_for reply that was not met, carrying the bridge's wall time when <paramref name="elapsedMs"/> is given.</summary>
    private static JsonObject Reply(int? elapsedMs) =>
        JsonNode.Parse(elapsedMs is null ? """{"met": false}""" : $"{{\"met\": false, \"elapsedMs\": {elapsedMs}}}")!.AsObject();

    [Fact]
    public async Task AnEmptyBatchIsRefused()
    {
        McpException refused = await RefusedAsync([]);

        Assert.Equal("steps is empty; a batch needs at least one step.", refused.Message);
    }

    [Fact]
    public async Task MoreThanAHundredStepsAreRefused()
    {
        BatchStep[] steps = [.. Enumerable.Repeat(new BatchStep(Assert: "no_errors"), 101)];

        McpException refused = await RefusedAsync(steps);

        Assert.Equal("a batch takes at most 100 steps; split it.", refused.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("click", "no_errors")]
    public async Task AStepNeedsExactlyOneOfToolAndAssert(string? tool, string? assert)
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "no_errors"), new BatchStep(Tool: tool, Assert: assert)]);

        Assert.Equal("step 1: give exactly one of tool, assert and parallel.", refused.Message);
    }

    [Theory]
    [InlineData("click", null)]
    [InlineData(null, "no_errors")]
    public async Task AGroupWithToolOrAssertBesideParallelIsRefused(string? tool, string? assert)
    {
        McpException refused = await RefusedAsync([new BatchStep(Tool: tool, Assert: assert, Parallel: Members(2))]);

        Assert.Equal("step 0: give exactly one of tool, assert and parallel.", refused.Message);
    }

    [Fact]
    public async Task AGroupWithOtherFieldsBesideParallelIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(TimeoutMs: 5, Session: "host", Parallel: Members(2))]);

        Assert.Equal("step 0: a parallel group takes parallel alone; got timeoutMs, session.", refused.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task AGroupOfFewerThanTwoOrMoreThanEightStepsIsRefused(int count)
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "no_errors"), new BatchStep(Parallel: Members(count))]);

        Assert.Equal($"step 1: a parallel group takes 2 to 8 steps; got {count}.", refused.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void AGroupOfTwoToEightStepsIsAccepted(int count)
    {
        BatchStep[] steps = [new BatchStep(Parallel: Members(count))];

        RuntimeTools.CheckBatch(steps);
        RuntimeTools.CheckGroupSessions(steps, null);
    }

    [Fact]
    public async Task AGroupNamingOneSessionTwiceIsRefused()
    {
        BatchStep wait = new(Assert: "wait", GameMs: 500, Session: "guest");

        McpException refused = await RefusedAsync([new BatchStep(Parallel: [Click("host"), wait, Click("host")])]);

        Assert.Equal("step 0: parallel steps 0 and 2 both address session 'host'; each step of a group addresses its own session.", refused.Message);
    }

    [Fact]
    public async Task AGroupMemberWithoutASessionAddressesTheBatchsDefault()
    {
        BatchStep wait = new(Assert: "wait", GameMs: 500, Session: "host");

        McpException refused = await RefusedAsync([new BatchStep(Parallel: [Click(null), wait])], "host");

        Assert.Equal("step 0: parallel steps 0 and 1 both address session 'host'; each step of a group addresses its own session.", refused.Message);
    }

    [Fact]
    public async Task AGroupInsideAGroupIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Parallel: [Click("a"), new BatchStep(Parallel: [Click("b"), Click("c")])])]);

        Assert.Equal("step 0: parallel steps cannot be nested; a group holds tool and assertion steps only.", refused.Message);
    }

    [Theory]
    [InlineData("fired")]
    [InlineData("no_errors")]
    public async Task AFiredOrNoErrorsAssertionIsRefusedInAGroup(string assert)
    {
        BatchStep member = new(Assert: assert, Node: "Ready", Signal: "pressed", Session: "b");

        McpException refused = await RefusedAsync([Click("a"), new BatchStep(Parallel: [Click("a"), member])]);

        Assert.Equal($"step 1: parallel step 1: {assert} cannot run in a parallel group; put it after the group.", refused.Message);
    }

    [Fact]
    public async Task AMembersOwnRefusalNamesItsPlaceInTheGroup()
    {
        BatchStep member = new(Assert: "wait", GameMs: 0, Session: "b");

        McpException refused = await RefusedAsync([new BatchStep(Assert: "no_errors"), new BatchStep(Parallel: [Click("a"), member])]);

        Assert.Equal("step 1: parallel step 1: gameMs must be between 1 and 120000; got 0.", refused.Message);
    }

    [Fact]
    public async Task TheStepLimitCountsAGroupsMembers()
    {
        BatchStep[] full = [.. Enumerable.Repeat(new BatchStep(Assert: "no_errors"), 92), new BatchStep(Parallel: Members(8))];
        BatchStep[] over = [new BatchStep(Assert: "no_errors"), .. full];

        RuntimeTools.CheckBatch(full);
        McpException refused = await RefusedAsync(over);

        Assert.Equal("a batch takes at most 100 steps; split it.", refused.Message);
    }

    [Fact]
    public async Task AToolMemberOnAProcessSessionIsRefusedBeforeAnyStepRuns()
    {
        await _sessions.ReserveProcessAsync("server", AppContext.BaseDirectory, defaultName: false);

        JsonObject refused = JsonNode
            .Parse(
                await _tools.BatchDriveAsync(
                    [Click("game"), new BatchStep(Parallel: [Click("game"), Click("server")])],
                    null!,
                    null,
                    TestContext.Current.CancellationToken
                )
            )!
            .AsObject();

        Assert.False(refused["passed"]!.GetValue<bool>());
        Assert.True(refused.ContainsKey("startedAt"), refused.ToJsonString());
        Assert.Empty(refused["steps"]!.AsArray());
        Assert.Equal(1, refused["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal(1, refused["failedAt"]!["member"]!.GetValue<int>());
        Assert.Equal(GodotSession.ProcessRefusal("server", "a process"), refused["failedAt"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AGroupWhoseMembersResolveToOneSessionIsRefusedAsTheBatchStarts()
    {
        await _sessions.ReserveProcessAsync("server", AppContext.BaseDirectory, defaultName: false);
        BatchStep unnamed = new(Assert: "wait", Output: "ready");
        BatchStep named = unnamed with { Session = "server" };

        JsonObject refused = JsonNode
            .Parse(await _tools.BatchDriveAsync([new BatchStep(Parallel: [unnamed, named])], null!, null, TestContext.Current.CancellationToken))!
            .AsObject();

        Assert.False(refused["passed"]!.GetValue<bool>());
        Assert.True(refused.ContainsKey("startedAt"), refused.ToJsonString());
        Assert.Empty(refused["steps"]!.AsArray());
        JsonObject failedAt = refused["failedAt"]!.AsObject();
        Assert.Equal(["index", "reason"], failedAt.Select(field => field.Key));
        Assert.Equal(0, failedAt["index"]!.GetValue<int>());
        Assert.Equal(
            "step 0: parallel steps 0 and 1 both address session 'server'; each step of a group addresses its own session.",
            failedAt["reason"]!.GetValue<string>()
        );
    }

    private static BatchStep Click(string? session) => new(Tool: "click", Session: session);

    /// <summary>A group's worth of click steps, each on its own session.</summary>
    private static BatchStep[] Members(int count) => [.. Enumerable.Range(0, count).Select(member => Click($"s{member}"))];

    [Theory]
    [InlineData("run_project")]
    [InlineData("stop_project")]
    [InlineData("restart_project")]
    [InlineData("attach_project")]
    [InlineData("detach_project")]
    [InlineData("arm_project")]
    [InlineData("disarm_project")]
    [InlineData("list_sessions")]
    [InlineData("get_debug_output")]
    [InlineData("run_scratches")]
    [InlineData("no_such_tool")]
    public async Task ALifecycleToolIsRefused(string tool)
    {
        McpException refused = await RefusedAsync([new BatchStep(Tool: tool)]);

        Assert.StartsWith(
            $"step 0: '{tool}' cannot run in a batch; a tool step names a runtime tool, one of: ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("wait_for", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchDriveCannotNestItself()
    {
        McpException refused = await RefusedAsync([new BatchStep(Tool: "batch_drive")]);

        Assert.StartsWith("step 0: 'batch_drive' cannot run in a batch; ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownAssertionIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "equals")]);

        Assert.Equal("step 0: assert 'equals' is not one of property, expression, state, wait, no_errors, screenshot, fired.", refused.Message);
    }

    [Fact]
    public async Task AFiredAssertionWithoutAToolStepBeforeItIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "fired", Node: "Ready", Signal: "pressed")]);

        Assert.Equal("step 0: assert 'fired' needs a tool step before it, whose fired list it checks.", refused.Message);
    }

    [Fact]
    public void AFiredAssertionIsAcceptedAfterAToolStepWithAnAssertionInBetween() =>
        RuntimeTools.CheckBatch([
            new BatchStep(Tool: "click"),
            new BatchStep(Assert: "no_errors"),
            new BatchStep(Assert: "fired", Node: "Ready", Signal: "pressed"),
        ]);

    [Theory]
    [InlineData(null, "pressed")]
    [InlineData("Ready", null)]
    [InlineData("", "pressed")]
    [InlineData("Ready", "")]
    public async Task AFiredAssertionWithoutANodeOrSignalIsRefused(string? node, string? signal)
    {
        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), new BatchStep(Assert: "fired", Node: node, Signal: signal)]);

        Assert.Equal("step 1: the fired assertion needs node and signal.", refused.Message);
    }

    [Fact]
    public async Task AFiredAssertionsEqualsThatIsNotAnArrayIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", EqualsValue: JsonSerializer.SerializeToElement(5));

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: equals must be an array of the emission's arguments; got 5.", refused.Message);
    }

    [Fact]
    public void AFiredAssertionsEqualsMayBeAnArrayOrJsonNull() =>
        RuntimeTools.CheckBatch([
            new BatchStep(Tool: "click"),
            new BatchStep(Assert: "fired", Node: "Ready", Signal: "pressed", EqualsValue: JsonSerializer.Deserialize<JsonElement>("[true]")),
            new BatchStep(Assert: "fired", Node: "Ready", Signal: "pressed", EqualsValue: JsonSerializer.Deserialize<JsonElement>("null")),
        ]);

    [Fact]
    public async Task AFiredAssertionWithToolArgsIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", Args: new JsonObject { ["element"] = "Ready" });

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got args.", refused.Message);
    }

    [Fact]
    public async Task AFiredAssertionWithFieldsItDoesNotTakeIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", GameMs: 5, Name: "menu");

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got gameMs, name.", refused.Message);
    }

    [Theory]
    [InlineData("property", "step 0: the property assertion needs node, property and equals.")]
    [InlineData("expression", "step 0: the expression assertion needs expression.")]
    [InlineData("screenshot", "step 0: the screenshot assertion needs name.")]
    [InlineData(
        "wait",
        "step 0: condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, "
            + "{uiChanged: true | \"now\"}, "
            + "{gameMs}, {frames}, {text}, {node, stable}, {node, state, equals}, {output} (with stream and after optional)."
    )]
    public async Task AnAssertionMissingAFieldIsRefused(string assert, string message)
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: assert, Node: "TimeProbe", Property: "state")]);

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public async Task AnAssertionsWaitArgumentsAreCheckedBeforeTheBatchRuns()
    {
        JsonElement done = JsonSerializer.Deserialize<JsonElement>("\"done\"");
        BatchStep property = new(Assert: "property", Node: "TimeProbe", Property: "state", EqualsValue: done, TimeoutMs: -1);

        McpException refused = await RefusedAsync([new BatchStep(Assert: "no_errors"), property]);

        Assert.Equal("step 1: timeoutMs must be between 0 and 120000.", refused.Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"now\"")]
    public void AUiChangedWaitAssertionIsAccepted(string uiChangedJson) =>
        RuntimeTools.CheckBatch([new BatchStep(Assert: "wait", UiChanged: JsonSerializer.Deserialize<JsonElement>(uiChangedJson))]);

    [Theory]
    [InlineData("true")]
    [InlineData("\"now\"")]
    public async Task AUiChangedWaitAssertionWithANodeIsRefused(string uiChangedJson)
    {
        McpException refused = await RefusedAsync([
            new BatchStep(Assert: "wait", Node: "Main", UiChanged: JsonSerializer.Deserialize<JsonElement>(uiChangedJson)),
        ]);

        Assert.StartsWith("step 0: condition needs exactly one of: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckOnceUiChangedNowWaitAssertionIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", UiChanged: JsonSerializer.SerializeToElement("now"), TimeoutMs: 0)]);

        Assert.Equal("step 0: timeoutMs 0 checks once, which a {uiChanged: \"now\"} wait cannot meet; give it a timeout.", refused.Message);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"later\"")]
    [InlineData("3")]
    public async Task AUiChangedWaitAssertionOfAnotherValueIsRefused(string uiChangedJson)
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", UiChanged: JsonSerializer.Deserialize<JsonElement>(uiChangedJson))]);

        Assert.Equal($"step 0: uiChanged takes true or \"now\"; got {uiChangedJson}.", refused.Message);
    }

    [Fact]
    public void ATextWaitAssertionWithUnderIsAcceptedAndCarriesBoth()
    {
        BatchStep step = new(Assert: "wait", Exists: false, Text: "Start", Under: "Lobby");

        RuntimeTools.CheckBatch([step]);
        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), 1000);

        Assert.Equal("Start", parameters["text"]?.GetValue<string>());
        Assert.Equal("Lobby", parameters["under"]?.GetValue<string>());
    }

    [Fact]
    public async Task AWaitAssertionWithUnderAloneIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", Under: "Lobby")]);

        Assert.Equal("step 0: condition.under scopes a text condition; give text with it.", refused.Message);
    }

    [Fact]
    public async Task AFiredAssertionWithTextIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", Text: "Start", Under: "Lobby");

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got text, under.", refused.Message);
    }

    [Fact]
    public void ASettledWaitAssertionIsAcceptedAndCarriesStable()
    {
        BatchStep step = new(Assert: "wait", Node: "Lobby", Stable: 10);

        RuntimeTools.CheckBatch([step]);
        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), 1000);

        Assert.Equal("settled", parameters["kind"]?.GetValue<string>());
        Assert.Equal(10, parameters["stable"]?.GetValue<int>());
    }

    [Fact]
    public void ASettledWaitAssertionWithoutATimeoutGetsADefaultThatGrowsWithStable()
    {
        BatchStep step = new(Assert: "wait", Node: "Lobby", Stable: 600);

        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), step.TimeoutMs);

        Assert.Equal(70_100, parameters["timeoutMs"]?.GetValue<int>());
    }

    [Fact]
    public async Task AFiredAssertionWithStableIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", Stable: 10);

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got stable.", refused.Message);
    }

    [Fact]
    public void AStateAssertionIsAcceptedAndDefaultsToCheckOnce()
    {
        BatchStep step = new(Assert: "state", Node: "Board", EqualsValue: JsonSerializer.Deserialize<JsonElement>("2"), State: "seats[0].hp");

        RuntimeTools.CheckBatch([step]);
        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), RuntimeTools.TimeoutOf(step));

        Assert.Equal("state", parameters["kind"]?.GetValue<string>());
        Assert.Equal("seats[0].hp", parameters["state"]?.GetValue<string>());
        Assert.Equal(0, parameters["timeoutMs"]?.GetValue<int>());
    }

    [Theory]
    [InlineData("node")]
    [InlineData("state")]
    [InlineData("equals")]
    public async Task AStateAssertionWithoutNodeStateOrEqualsIsRefused(string missing)
    {
        JsonElement two = JsonSerializer.Deserialize<JsonElement>("2");
        BatchStep step = new(
            Assert: "state",
            Node: missing == "node" ? null : "Board",
            EqualsValue: missing == "equals" ? null : two,
            State: missing == "state" ? null : "turn"
        );

        McpException refused = await RefusedAsync([step]);

        Assert.Equal("step 0: the state assertion needs node, state and equals.", refused.Message);
    }

    [Fact]
    public void AWaitAssertionTakesAStateCondition()
    {
        BatchStep step = new(Assert: "wait", Node: "Board", EqualsValue: JsonSerializer.Deserialize<JsonElement>("2"), State: "turn");

        RuntimeTools.CheckBatch([step]);
        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), step.TimeoutMs);

        Assert.Equal("state", parameters["kind"]?.GetValue<string>());
        Assert.Equal("turn", parameters["state"]?.GetValue<string>());
    }

    [Fact]
    public async Task AFiredAssertionWithStateIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", State: "turn");

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got state.", refused.Message);
    }

    [Fact]
    public void AGameMsWaitAssertionIsAccepted() => RuntimeTools.CheckBatch([new BatchStep(Assert: "wait", GameMs: 500)]);

    [Fact]
    public void AWaitAssertionHasNoCallToGive()
    {
        const string Wait = """{"assert":"wait","gameMs":500""";

        BatchStep? bound = JsonSerializer.Deserialize<BatchStep>(Wait + "}", ToolJson.Options);
        JsonException refused = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<BatchStep>(Wait + ""","call":{"node":"TimeProbe","method":"start_clock"}}""", ToolJson.Options)
        );

        Assert.Equal(500, bound?.GameMs);
        Assert.Contains("call", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFramesWaitAssertionWhoseDefaultTimeoutPassesTheExplicitCapIsAccepted() =>
        RuntimeTools.CheckBatch([new BatchStep(Assert: "wait", Frames: RuntimeTools.MaxWaitFrames)]);

    [Fact]
    public async Task AGameMsWaitAssertionOutOfRangeIsRefusedBeforeTheBatchRuns()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "no_errors"), new BatchStep(Assert: "wait", GameMs: 0)]);

        Assert.Equal("step 1: gameMs must be between 1 and 120000; got 0.", refused.Message);
    }

    [Fact]
    public void TheBatchableToolsAreTheRuntimeTools()
    {
        Assert.Contains("click", RuntimeTools.BatchableTools);
        Assert.Contains("hover", RuntimeTools.BatchableTools);
        Assert.Contains("scroll", RuntimeTools.BatchableTools);
        Assert.Contains("wait_for", RuntimeTools.BatchableTools);
        Assert.Contains("take_screenshot", RuntimeTools.BatchableTools);
        Assert.Contains("compare_screenshot", RuntimeTools.BatchableTools);
        Assert.Contains("call_method", RuntimeTools.BatchableTools);
        Assert.Contains("cs_members", RuntimeTools.BatchableTools);
        Assert.Contains("cs_get", RuntimeTools.BatchableTools);
        Assert.Contains("cs_set", RuntimeTools.BatchableTools);
        Assert.Contains("cs_call", RuntimeTools.BatchableTools);
        Assert.Contains("run_csharp", RuntimeTools.BatchableTools);
        Assert.Contains("capture_frames", RuntimeTools.BatchableTools);
        Assert.Contains("list_game_tools", RuntimeTools.BatchableTools);
        Assert.Contains("call_game_tool", RuntimeTools.BatchableTools);
        Assert.DoesNotContain("run_project", RuntimeTools.BatchableTools);
        Assert.DoesNotContain("list_sessions", RuntimeTools.BatchableTools);
        Assert.DoesNotContain("batch_drive", RuntimeTools.BatchableTools);
        // save_screenshot writes a project file, which batch_drive's edit-live class does not grant.
        Assert.DoesNotContain("save_screenshot", RuntimeTools.BatchableTools);
        Assert.Contains("watch", RuntimeTools.BatchableTools);
        Assert.Equal(38, RuntimeTools.BatchableTools.Count);
    }

    [Fact]
    public void AToolStepGetsTheSessionAndPathOnly()
    {
        Dictionary<string, JsonElement> screenshot = RuntimeTools.ToolArguments("take_screenshot", new() { ["responseMode"] = "full" }, "game");
        Dictionary<string, JsonElement> compare = RuntimeTools.ToolArguments("compare_screenshot", new() { ["name"] = "menu" }, null);
        Dictionary<string, JsonElement> own = RuntimeTools.ToolArguments("click", new() { ["session"] = "other" }, "game");

        Assert.Equal("path_only", screenshot["responseMode"].GetString());
        Assert.Equal("game", screenshot["session"].GetString());
        Assert.Equal("path_only", compare["options"].GetProperty("responseMode").GetString());
        Assert.False(compare.ContainsKey("session"));
        Assert.Equal("other", own["session"].GetString());
    }

    [Fact]
    public void AWaitAssertionWithOutputIsAccepted()
    {
        BatchStep step = new(Assert: "wait", Output: "joined", Stream: "stdout", After: 2);

        RuntimeTools.CheckBatch([step]);
        JsonObject parameters = RuntimeTools.BuildWaitParameters(RuntimeTools.ConditionOf(step), RuntimeTools.TimeoutOf(step));

        Assert.Equal("output", parameters["kind"]?.GetValue<string>());
        Assert.Equal("joined", parameters["output"]?.GetValue<string>());
        Assert.Equal("stdout", parameters["stream"]?.GetValue<string>());
        Assert.Equal(2L, parameters["after"]?.GetValue<long>());
    }

    [Fact]
    public async Task AWaitAssertionWithOutputAndNodeIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", Node: "Main", Output: "joined")]);

        Assert.Equal(
            "step 0: condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, "
                + "{uiChanged: true | \"now\"}, "
                + "{gameMs}, {frames}, {text}, {node, stable}, {node, state, equals}, {output} (with stream and after optional).",
            refused.Message
        );
    }

    [Fact]
    public async Task AWaitAssertionWithAnUnknownStreamIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", Output: "joined", Stream: "log")]);

        Assert.Equal("step 0: condition.stream must be stdout, stderr or any; got log.", refused.Message);
    }

    [Fact]
    public async Task AFiredAssertionNamingOutputIsRefused()
    {
        BatchStep fired = new(Assert: "fired", Node: "Ready", Signal: "pressed", Output: "joined");

        McpException refused = await RefusedAsync([new BatchStep(Tool: "click"), fired]);

        Assert.Equal("step 1: the fired assertion takes node, signal and equals; got output.", refused.Message);
    }

    private Task<McpException> RefusedAsync(BatchStep[] steps, string? session = null) =>
        Assert.ThrowsAsync<McpException>(() => _tools.BatchDriveAsync(steps, null!, session, TestContext.Current.CancellationToken));
}
