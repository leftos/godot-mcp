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

        JsonObject batch = await BatchAsync(server, new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = 5 }));

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

    private McpServer CreateServer(McpServerOptions options) =>
        McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);

    private async Task<JsonObject> BatchAsync(McpServer server, params BatchStep[] steps) =>
        JsonNode.Parse(await _tools.BatchDriveAsync(steps, server, null, TestContext.Current.CancellationToken))!.AsObject();
}
