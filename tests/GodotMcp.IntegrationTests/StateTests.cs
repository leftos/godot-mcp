using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// get_game_state against the InputProbe: each test marks nodes of its own, built by run_script from a GDScript source and
/// added to the mcp_state group under Main, which the reset frees. One shared run, reset before each test; the batch test
/// dispatches through a real MCP server's tool collection over the shared registry, as BatchTests does.
/// </summary>
public sealed class StateTests : IAsyncLifetime, IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int ScriptTimeoutMs = 10_000;

    private const string StateSource = """
        extends Node


        func _mcp_state() -> Dictionary:
        	return {
        		"hp": 3,
        		"name": "alex",
        		"ratio": 0.5,
        		"pos": Vector2(1, 2),
        		"tags": ["a", "b"],
        		"nested": {"deep": {"deeper": {"deepest": {"x": 1}}}},
        		"ready": true,
        		"none": null,
        	}

        """;

    private const string PlainSource = "extends Node\n";
    private const string DepthLimit = "<depth limit: Dictionary>";

    private const string RaisingSource = """
        extends Node


        func _mcp_state() -> Dictionary:
        	var parts: Array = []
        	return {"x": parts[3]}

        """;

    // Counts the process frames the node itself runs, which stop while the tree is paused and advance one per stepped frame.
    private const string TickingSource = """
        extends Node

        var ticks: int = 0


        func _process(_delta: float) -> void:
        	ticks += 1


        func _mcp_state() -> Dictionary:
        	return {"ticks": ticks}

        """;

    private const string EmptyHint =
        "No node is in the mcp_state group. A GDScript node joins it and defines _mcp_state() returning a Dictionary; a C# node "
        + "defines _McpState(). Until a game opts in, snapshot_subtree and get_ui_elements read the tree.";

    private readonly SharedProbeSession _shared;
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly RuntimeTools _tools;

    public StateTests(SharedProbeSession shared)
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
    public async Task AReadReturnsEachMarkedNodesStateInTreeOrderInOneFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Hud", StateSource), ("Main", "Plain", PlainSource));

        JsonObject result = await ReadAsync(cancellation);

        JsonArray nodes = result["nodes"]!.AsArray();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(("/root/Main/Hud", "Node"), (nodes[0]!["path"]!.GetValue<string>(), nodes[0]!["class"]!.GetValue<string>()));
        JsonObject state = nodes[0]!["state"]!.AsObject();
        Assert.Equal((3, "alex", 0.5), (state["hp"]!.GetValue<int>(), state["name"]!.GetValue<string>(), state["ratio"]!.GetValue<double>()));
        Assert.Equal((1.0, 2.0), (state["pos"]!["x"]!.GetValue<double>(), state["pos"]!["y"]!.GetValue<double>()));
        Assert.Equal("""["a","b"]""", state["tags"]!.ToJsonString());
        // The value's fourth level is written, the fifth cut at the default maxDepth of 4.
        Assert.Equal(DepthLimit, state["nested"]!["deep"]!["deeper"]!["deepest"]!.GetValue<string>());
        Assert.True(state["ready"]!.GetValue<bool>(), state.ToJsonString());
        Assert.True(state.ContainsKey("none") && state["none"] is null, state.ToJsonString());
        Assert.Equal(
            ("/root/Main/Plain", "Node", "in the mcp_state group but has no _mcp_state method", false),
            (
                nodes[1]!["path"]!.GetValue<string>(),
                nodes[1]!["class"]!.GetValue<string>(),
                nodes[1]!["error"]!.GetValue<string>(),
                nodes[1]!.AsObject().ContainsKey("state")
            )
        );
        Assert.Equal(2, result["total"]!.GetValue<int>());
        Assert.True(result["frame"]!.GetValue<long>() > 0, result.ToJsonString());
        Assert.False(result.ContainsKey("omitted"), result.ToJsonString());
        Assert.False(result.ContainsKey("hint"), result.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task KeysKeepOnlyTheNamedValuesAndADottedPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Hud", StateSource));

        JsonObject result = await ReadAsync(cancellation, options: new StateOptions(Keys: ["hp", "tags[1]", "nested.deep", "missing"]));

        JsonObject state = result["nodes"]![0]!["state"]!.AsObject();
        Assert.Equal(["hp", "tags[1]", "nested.deep"], state.Select(key => key.Key));
        Assert.Equal((3, "b"), (state["hp"]!.GetValue<int>(), state["tags[1]"]!.GetValue<string>()));
        Assert.Equal(DepthLimit, state["nested.deep"]!["deeper"]!["deepest"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task NodeReadsOnlyTheMarkedNodesAtOrUnderIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Hud", StateSource), ("Main", "Board", StateSource), ("Main/Board", "Card", StateSource));

        JsonObject result = await ReadAsync(cancellation, "Board");

        Assert.Equal(["/root/Main/Board", "/root/Main/Board/Card"], Paths(result));
        Assert.Equal(2, result["total"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MaxNodesReadsTheFirstAndListsTheRestInOmitted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, [.. Enumerable.Range(0, 5).Select(index => ("Main", $"N{index}", StateSource))]);

        JsonObject result = await ReadAsync(cancellation, options: new StateOptions(MaxNodes: 2));

        Assert.Equal(["/root/Main/N0", "/root/Main/N1"], Paths(result));
        Assert.Equal(5, result["total"]!.GetValue<int>());
        Assert.Equal("""{"count":3,"paths":["/root/Main/N2","/root/Main/N3","/root/Main/N4"]}""", result["omitted"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AGameWithNoMarkedNodeGetsTheHint()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject result = await ReadAsync(cancellation);

        Assert.Equal([], result["nodes"]!.AsArray());
        Assert.Equal(0, result["total"]!.GetValue<int>());
        Assert.Equal(EmptyHint, result["hint"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARaisingMethodGivesItsErrorAndTheReadGoesOn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Broken", RaisingSource), ("Main", "Hud", StateSource));

        JsonObject result = await ReadAsync(cancellation);

        string error = result["nodes"]![0]!["error"]!.GetValue<string>();
        Assert.StartsWith("_mcp_state raised: ", error, StringComparison.Ordinal);
        Assert.True(error.Length > "_mcp_state raised: ".Length, error);
        Assert.Equal(3, result["nodes"]![1]!["state"]!["hp"]!.GetValue<int>());
        Assert.NotEmpty(result["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AReadInsideFrameControlStepsSeesEachSteppedFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Ticker", TickingSource));
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);

        JsonObject before = await ReadAsync(cancellation);
        JsonObject stillPaused = await ReadAsync(cancellation);
        await _tools.FrameControlAsync("step", 3, cancellationToken: cancellation);
        JsonObject after = await ReadAsync(cancellation);

        Assert.Equal(Ticks(before), Ticks(stillPaused));
        Assert.Equal(Ticks(before) + 3, Ticks(after));
        Assert.True(after["frame"]!.GetValue<long>() > before["frame"]!.GetValue<long>(), $"{before} then {after}");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task BatchDriveRunsTheReadAsAStep()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await MarkAsync(cancellation, ("Main", "Hud", StateSource));
        BatchStep read = new(
            Tool: RuntimeTools.GetGameStateToolName,
            Args: new JsonObject { ["options"] = new JsonObject { ["keys"] = new JsonArray("hp") } }
        );

        JsonObject batch = JsonNode
            .Parse(await _tools.BatchDriveAsync([read, new BatchStep(Assert: "no_errors")], _server, cancellationToken: cancellation))!
            .AsObject();

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal("""{"hp":3}""", batch["steps"]![0]!["result"]!["nodes"]![0]!["state"]!.ToJsonString());
    }

    private async Task<JsonObject> ReadAsync(CancellationToken cancellation, string? node = null, StateOptions? options = null) =>
        JsonNode.Parse(await _tools.GetGameStateAsync(node, options, cancellationToken: cancellation))!.AsObject();

    /// <summary>Adds each (parent, name, source) node under /root/parent, in order, with its script and in the mcp_state group.</summary>
    private async Task MarkAsync(CancellationToken cancellation, params (string Parent, string Name, string Source)[] nodes)
    {
        string body = string.Concat(nodes.Select(node => $"\tadd(scene_tree, \"{node.Parent}\", \"{node.Name}\", \"{GdString(node.Source)}\")\n"));
        string script =
            "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
            + body
            + "\treturn true\n\n\n"
            + "func add(scene_tree: SceneTree, parent: String, node_name: String, source: String) -> void:\n"
            + "\tvar script := GDScript.new()\n\tscript.source_code = source\n\tscript.reload()\n"
            + "\tvar node := Node.new()\n\tnode.name = node_name\n\tnode.set_script(script)\n"
            + "\tnode.add_to_group(\"mcp_state\")\n\tscene_tree.root.get_node(parent).add_child(node)\n";
        JsonNode reply = JsonNode.Parse(await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(reply["value"]?.GetValue<bool>() == true, reply.ToJsonString());
    }

    /// <summary>The text as the inside of a GDScript string literal.</summary>
    private static string GdString(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string[] Paths(JsonObject result) => [.. result["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>())];

    private static int Ticks(JsonObject result) => result["nodes"]![0]!["state"]!["ticks"]!.GetValue<int>();
}
