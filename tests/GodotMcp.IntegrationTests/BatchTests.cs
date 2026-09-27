using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// batch_drive against the InputProbe, its tool steps dispatched through a real MCP server's tool collection over the test's
/// sessions. The probe's SmallButton counts its presses in press_count. One shared run, reset before each test; each test
/// builds its own server over the shared registry, which the service provider does not dispose, since it was handed an
/// instance.
/// </summary>
public sealed class BatchTests : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const string SmallButton = "Main/SmallButton";
    private readonly SharedProbeSession _shared;
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly RuntimeTools _tools;

    public BatchTests(SharedProbeSession shared)
    {
        _shared = shared;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(shared.Sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly);
        _services = services.BuildServiceProvider();
        McpServerOptions options = _services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        _server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);
        _tools = new RuntimeTools(shared.Sessions, TestCSharp.Unused());
    }

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _services.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABatchRunsStepsInOrderAndPasses()
    {
        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = "SmallButton" } }),
            PressCount(1),
            new BatchStep(Assert: "no_errors"),
            new BatchStep(Tool: "take_screenshot")
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(batch.ContainsKey("failedAt"), batch.ToJsonString());
        JsonArray steps = batch["steps"]!.AsArray();
        Assert.Equal(
            ["click", "property", "no_errors", "take_screenshot"],
            steps.Select(step => (step!["tool"] ?? step["assert"])!.GetValue<string>())
        );
        Assert.All(steps, step => Assert.True(step!["ok"]!.GetValue<bool>(), step.ToJsonString()));
        Assert.Equal([0, 1, 2, 3], steps.Select(step => step!["index"]!.GetValue<int>()));
        Assert.Equal(1, steps[1]!["result"]!["value"]!.GetValue<int>());
        JsonObject screenshot = steps[3]!["result"]!.AsObject();
        Assert.True(File.Exists(screenshot["path"]!.GetValue<string>()), screenshot.ToJsonString());
        Assert.False(screenshot.ContainsKey("previewPath"), screenshot.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SnapshotAndDiffRunAsSteps()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        // Ids count up per session, so the batch's own snapshot takes the one after this one.
        string taken = JsonNode.Parse(await _tools.SnapshotSubtreeAsync(SmallButton, cancellationToken: cancellation))![
            "snapshotId"
        ]!.GetValue<string>();
        string next = $"s{int.Parse(taken[1..], System.Globalization.CultureInfo.InvariantCulture) + 1}";

        JsonObject batch = await BatchAsync(
            cancellation,
            new BatchStep(Tool: "snapshot_subtree", Args: new JsonObject { ["node"] = SmallButton }),
            new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = "SmallButton" } }),
            new BatchStep(Tool: "diff_snapshots", Args: new JsonObject { ["beforeId"] = next })
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        JsonArray steps = batch["steps"]!.AsArray();
        Assert.Equal(next, steps[0]!["result"]!["snapshotId"]!.GetValue<string>());
        JsonNode pressed = Assert.Single(
            steps[2]!["result"]!["changed"]!.AsArray(),
            change => change!["property"]!.GetValue<string>() == "press_count"
        )!;
        Assert.Equal(0, pressed["before"]!.GetValue<int>());
        Assert.Equal(1, pressed["after"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABatchStopsAtTheFirstFailedAssertion()
    {
        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            PressCount(5),
            new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = "SmallButton" } })
        );

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(0, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.Contains("was not met when checked", batch["failedAt"]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonNode step = Assert.Single(batch["steps"]!.AsArray())!;
        Assert.False(step["ok"]!.GetValue<bool>(), step.ToJsonString());
        Assert.Equal(batch["failedAt"]!["reason"]!.GetValue<string>(), step["error"]!.GetValue<string>());
        Assert.Equal(0, await ReadPressCountAsync());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AToolErrorStopsTheBatch()
    {
        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            new BatchStep(Tool: "call_method", Args: new JsonObject { ["node"] = "NoSuchNode", ["method"] = "queue_free" }),
            new BatchStep(Assert: "no_errors")
        );

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(0, batch["failedAt"]!["index"]!.GetValue<int>());
        JsonNode step = Assert.Single(batch["steps"]!.AsArray())!;
        Assert.Equal("call_method", step["tool"]!.GetValue<string>());
        Assert.False(step["ok"]!.GetValue<bool>(), step.ToJsonString());
        Assert.Contains("NoSuchNode", step["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAssertionChecksOnceWhilePaused()
    {
        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            new BatchStep(Tool: "frame_control", Args: new JsonObject { ["action"] = "pause" }),
            PressCount(0)
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.True(batch["steps"]![0]!["result"]!["paused"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(0, batch["steps"]![1]!["result"]!["frames"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheDeadlineStopsTheBatchWithItsSteps()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RuntimeTools hurried = new(_shared.Sessions, TestCSharp.Unused()) { BatchDeadline = TimeSpan.FromMilliseconds(500) };
        BatchStep never = new(Assert: "wait", Expression: "false", TimeoutMs: 5000);

        JsonObject batch = JsonNode
            .Parse(await hurried.BatchDriveAsync([never, new BatchStep(Assert: "no_errors")], _server, null, cancellation))!
            .AsObject();
        JsonObject after = JsonNode.Parse(await _tools.WaitForAsync(new WaitCondition(Expression: "true"), 1000, null, cancellation))!.AsObject();

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(0, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal("the batch's 0.5 s deadline passed", batch["failedAt"]!["reason"]!.GetValue<string>());
        JsonNode step = Assert.Single(batch["steps"]!.AsArray())!;
        Assert.Equal("the batch's 0.5 s deadline passed", step["error"]!.GetValue<string>());
        Assert.True(after["met"]!.GetValue<bool>(), after.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task NoErrorsFailsOnARaisedError()
    {
        const string Script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\tpush_error(\"batch boom\")\n\treturn true\n";

        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            new BatchStep(Assert: "no_errors"),
            new BatchStep(Tool: "run_script", Args: new JsonObject { ["script"] = Script }),
            new BatchStep(Assert: "no_errors")
        );

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(2, batch["failedAt"]!["index"]!.GetValue<int>());
        string reason = batch["failedAt"]!["reason"]!.GetValue<string>();
        Assert.StartsWith("the game raised 1 error(s) since the batch started or its previous no_errors:\n", reason, StringComparison.Ordinal);
        Assert.Contains("batch boom at ", reason, StringComparison.Ordinal);
        Assert.Equal(0, batch["steps"]![0]!["result"]!["errors"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExpressionWaitAndScreenshotAssertionsPass()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _tools.SaveScreenshotBaselineAsync("batch_square", new ScreenshotCrop(400, 40, 120, 80), null, null, cancellation);

        JsonObject batch = await BatchAsync(
            TestContext.Current.CancellationToken,
            new BatchStep(Assert: "expression", Expression: "root.has_node(\"Main/SmallButton\")"),
            new BatchStep(Assert: "wait", Node: SmallButton, Exists: true),
            new BatchStep(Assert: "screenshot", Name: "batch_square")
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        JsonArray steps = batch["steps"]!.AsArray();
        Assert.True(steps[0]!["result"]!["value"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.True(steps[1]!["result"]!["value"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.True(steps[2]!["result"]!["match"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(0, steps[2]!["result"]!["changedPixels"]!.GetValue<int>());
    }

    private static BatchStep PressCount(int count) =>
        new(Assert: "property", Node: SmallButton, Property: "press_count", EqualsValue: JsonSerializer.SerializeToElement(count));

    private async Task<JsonObject> BatchAsync(CancellationToken cancellationToken, params BatchStep[] steps) =>
        JsonNode.Parse(await _tools.BatchDriveAsync(steps, _server, cancellationToken: cancellationToken))!.AsObject();

    private async Task<int> ReadPressCountAsync()
    {
        const string Script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t"
            + "return scene_tree.root.get_node(\"Main/SmallButton\").press_count\n";
        string json = await _tools.RunScriptAsync(Script, 10_000, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!.GetValue<int>();
    }
}
