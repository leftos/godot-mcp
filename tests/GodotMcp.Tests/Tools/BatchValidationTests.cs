using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
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

        Assert.Equal("step 1: give exactly one of tool and assert.", refused.Message);
    }

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

        Assert.Equal("step 0: assert 'equals' is not one of property, expression, wait, no_errors, screenshot.", refused.Message);
    }

    [Theory]
    [InlineData("property", "step 0: the property assertion needs node, property and equals.")]
    [InlineData("expression", "step 0: the expression assertion needs expression.")]
    [InlineData("screenshot", "step 0: the screenshot assertion needs name.")]
    [InlineData(
        "wait",
        "step 0: condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true}, "
            + "{gameMs}, {frames}."
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

    [Fact]
    public void AUiChangedWaitAssertionIsAccepted() => RuntimeTools.CheckBatch([new BatchStep(Assert: "wait", UiChanged: true)]);

    [Fact]
    public async Task AUiChangedWaitAssertionWithANodeIsRefused()
    {
        McpException refused = await RefusedAsync([new BatchStep(Assert: "wait", Node: "Main", UiChanged: true)]);

        Assert.StartsWith("step 0: condition needs exactly one of: ", refused.Message, StringComparison.Ordinal);
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
        Assert.DoesNotContain("run_project", RuntimeTools.BatchableTools);
        Assert.DoesNotContain("list_sessions", RuntimeTools.BatchableTools);
        Assert.DoesNotContain("batch_drive", RuntimeTools.BatchableTools);
        Assert.Equal(36, RuntimeTools.BatchableTools.Count);
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

    private Task<McpException> RefusedAsync(BatchStep[] steps) =>
        Assert.ThrowsAsync<McpException>(() => _tools.BatchDriveAsync(steps, null!, null, TestContext.Current.CancellationToken));
}
