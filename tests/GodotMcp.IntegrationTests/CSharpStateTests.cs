using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// get_game_state against the CsProbe: a CsState node (tests/fixtures/CsProbe/CsState.cs) added under /root as States, whose
/// C# children each carry a different state method, with a GDScript node the test puts second among them and one returning
/// the process frame last, all read in one call:
/// the C# nodes by the helper, merged with the GDScript node in tree order. The States node is added once for the class.
/// A wait_for state condition reads its C# Counter through the helper at each check.
/// </summary>
public sealed class CSharpStateTests(SharedCsProbeSession shared) : IClassFixture<SharedCsProbeSession>
{
    private const int TestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string States = "/root/States";

    private const string AddStatesScript = """
        extends RefCounted


        func execute(scene_tree: SceneTree) -> Variant:
        	if scene_tree.root.has_node("States"):
        		return true
        	var holder: Node = load("res://CsState.cs").new()
        	holder.name = "States"
        	scene_tree.root.add_child(holder)
        	var script := GDScript.new()
        	script.source_code = 'extends Node\n\n\nfunc _mcp_state() -> Dictionary:\n\treturn {"lang": "gd", "hp": 5}\n'
        	script.reload()
        	var gd := Node.new()
        	gd.name = "Gd"
        	gd.set_script(script)
        	gd.add_to_group("mcp_state")
        	holder.add_child(gd)
        	holder.move_child(gd, 1)
        	var frame_script := GDScript.new()
        	frame_script.source_code = 'extends Node\n\n\nfunc _mcp_state() -> int:\n\treturn Engine.get_process_frames()\n'
        	frame_script.reload()
        	var gd_frame := Node.new()
        	gd_frame.name = "GdFrame"
        	gd_frame.set_script(frame_script)
        	gd_frame.add_to_group("mcp_state")
        	holder.add_child(gd_frame)
        	return true

        """;

    // The States node's children in tree order: the GDScript Gd second among the C# ones, and GdFrame last.
    private static readonly string[] TreeOrder =
    [
        "Record",
        "Gd",
        "Dictionary",
        "Throwing",
        "Waiting",
        "Both",
        "Bare",
        "Broken",
        "Visible",
        "Frame",
        "Counter",
        "GdFrame",
    ];

    private readonly RuntimeTools _tools = new(shared.Sessions, shared.Bridge);

    [Fact(Timeout = TestTimeoutMs)]
    public async Task EveryMarkedNodeReadsInTreeOrderAcrossLanguages()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);

        JsonObject result = await ReadAsync(cancellation);

        JsonArray nodes = result["nodes"]!.AsArray();
        Assert.Equal(TreeOrder.Select(name => $"{States}/{name}"), nodes.Select(node => node!["path"]!.GetValue<string>()));
        Assert.All(nodes, node => Assert.Equal("Node", node!["class"]!.GetValue<string>()));
        Assert.All(nodes, node => Assert.False(node!.AsObject().ContainsKey("id"), node.ToJsonString()));
        Assert.Equal(TreeOrder.Length, result["total"]!.GetValue<int>());

        JsonNode record = nodes[0]!["state"]!;
        Assert.Equal(3, record["Turn"]!.GetValue<int>());
        Assert.Equal(("sam", 9), (record["Seats"]![1]!["Name"]!.GetValue<string>(), record["Seats"]![1]!["Hp"]!.GetValue<int>()));
        Assert.Equal(States, record["Holder"]!["$node"]!.GetValue<string>());
        // The bridge writes a GDScript Dictionary's keys sorted.
        Assert.Equal("""{"hp":5,"lang":"gd"}""", nodes[1]!["state"]!.ToJsonString());
        Assert.Equal("""{"hp":7,"name":"dict"}""", nodes[2]!["state"]!.ToJsonString());
        Assert.Equal("InvalidOperationException: state broke", Error(nodes[3]!));
        Assert.Equal("CsProbe.WaitingState._McpState returns a Task<int>; a state method must return its value, not a task", Error(nodes[4]!));
        Assert.Equal("""{"from":"_McpState"}""", nodes[5]!["state"]!.ToJsonString());
        Assert.Equal("reads _McpState; its _mcp_state is not read", nodes[5]!["warning"]!.GetValue<string>());
        Assert.Equal("CsProbe.BareState has no _McpState() (an instance method with no parameters, any accessibility)", Error(nodes[6]!));
        // A state method whose call throws (a Span return) is its own node's error; the nodes before it still read.
        Assert.StartsWith("NotSupportedException: ", Error(nodes[7]!), StringComparison.Ordinal);
        // A C# node with no _McpState reads through its Godot-visible _mcp_state, with no warning.
        Assert.Equal("""{"hp":4,"via":"_mcp_state"}""", nodes[8]!["state"]!.ToJsonString());
        Assert.False(nodes[8]!.AsObject().ContainsKey("warning"), nodes[8]!.ToJsonString());
        // Both languages read in the frame the result names, so a read that waited a frame would show here.
        long frame = result["frame"]!.GetValue<long>();
        Assert.Equal((frame, frame), (nodes[9]!["state"]!.GetValue<long>(), nodes[11]!["state"]!.GetValue<long>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStateWaitOnACSharpNodeIsMetThroughTheHelper()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);
        const string Counter = $"{States}/Counter";
        JsonObject read = JsonNode
            .Parse(await _tools.GetGameStateAsync(Counter, new StateOptions(Keys: ["Turn"]), cancellationToken: cancellation))!
            .AsObject();
        int turn = read["nodes"]![0]!["state"]!["Turn"]!.GetValue<int>();
        WaitCondition condition = new(Node: Counter, EqualsValue: JsonSerializer.SerializeToElement(7 + turn + 1), State: "Seats[0].Hp");
        WaitOptions options = new(Call: new MethodCall(Counter, "Arm"));

        IEnumerable<ContentBlock> blocks = await _tools.WaitForAsync(condition, 10_000, options, cancellationToken: cancellation);
        JsonObject waited = JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!.AsObject();

        Assert.True(waited["met"]!.GetValue<bool>(), waited.ToJsonString());
        Assert.Equal(7 + turn + 1, waited["value"]!.GetValue<double>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepUntilAStateConditionOnACSharpNodeStopsOnTheFrameItsKeyChanges()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);
        const string Counter = $"{States}/Counter";
        JsonObject read = JsonNode
            .Parse(await _tools.GetGameStateAsync(Counter, new StateOptions(Keys: ["Turn"]), cancellationToken: cancellation))!
            .AsObject();
        int turn = read["nodes"]![0]!["state"]!["Turn"]!.GetValue<int>();
        StepOptions options = new(Until: new WaitCondition(Node: Counter, EqualsValue: JsonSerializer.SerializeToElement(turn + 1), State: "Turn"));
        await _tools.FrameControlAsync("pause", cancellationToken: cancellation);
        try
        {
            await RunBodyAsync($"scene_tree.root.get_node(\"{Counter}\").Arm()\n\treturn true", cancellation);

            IEnumerable<ContentBlock> blocks = await _tools.FrameControlAsync("step", count: 100, options: options, cancellationToken: cancellation);
            JsonObject stepped = JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!.AsObject();

            Assert.True(stepped["met"]!.GetValue<bool>(), stepped.ToJsonString());
            Assert.Equal(turn + 1, stepped["value"]!.GetValue<double>());
            // Arm leaves three frames to count down; the third stepped frame's _Process advances the turn, and its check meets it.
            Assert.Equal(3, stepped["processFrames"]!.GetValue<int>());
        }
        finally
        {
            // The class shares one game, whose other tests need it running.
            await _tools.FrameControlAsync("resume", cancellationToken: cancellation);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task KeysApplyToTheMergedStates()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);

        JsonObject result = await ReadAsync(cancellation, new StateOptions(Keys: ["hp", "Turn", "Seats[0].Hp"]));

        JsonArray nodes = result["nodes"]!.AsArray();
        Assert.Equal("""{"Turn":3,"Seats[0].Hp":7}""", nodes[0]!["state"]!.ToJsonString());
        Assert.Equal("""{"hp":5}""", nodes[1]!["state"]!.ToJsonString());
        Assert.Equal("""{"hp":7}""", nodes[2]!["state"]!.ToJsonString());
        Assert.Equal("InvalidOperationException: state broke", Error(nodes[3]!));
        Assert.Equal("{}", nodes[5]!["state"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MaxNodesCountsCSharpNodesLikeAnyOther()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);

        JsonObject result = await ReadAsync(cancellation, new StateOptions(MaxNodes: 2));

        Assert.Equal([$"{States}/Record", $"{States}/Gd"], result["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()));
        Assert.Equal(TreeOrder.Length, result["total"]!.GetValue<int>());
        Assert.Equal(TreeOrder.Length - 2, result["omitted"]!["count"]!.GetValue<int>());
        Assert.Equal($"{States}/Dictionary", result["omitted"]!["paths"]![0]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AKeptReadIsReadAgainThroughTheHelper()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AddStatesAsync(cancellation);
        string stateId = (await ReadAsync(cancellation, new StateOptions(Keep: true)))["stateId"]!.GetValue<string>();
        // Lets the game run on, so the frame each Frame node returns is a later one at the re-read.
        await Task.Delay(200, cancellation);

        JsonObject diff = JsonNode.Parse(await _tools.DiffSnapshotsAsync(stateId, cancellationToken: cancellation))!.AsObject();

        // A re-read without the helper would turn every C# node's leaves into an error; only the two frame counts move.
        JsonArray changed = diff["changed"]!.AsArray();
        Assert.Equal(
            [($"{States}/Frame", "$"), ($"{States}/GdFrame", "$")],
            changed.Select(change => (change!["node"]!.GetValue<string>(), change["property"]!.GetValue<string>()))
        );
        JsonNode frame = changed[0]!;
        Assert.True(frame["after"]!.GetValue<long>() > frame["before"]!.GetValue<long>(), frame.ToJsonString());
        Assert.Equal((0, 0), (diff["addedCount"]!.GetValue<int>(), diff["removedCount"]!.GetValue<int>()));
    }

    private static string Error(JsonNode entry) => entry["error"]?.GetValue<string>() ?? $"no error in {entry.ToJsonString()}";

    private async Task<JsonObject> ReadAsync(CancellationToken cancellation, StateOptions? options = null) =>
        JsonNode.Parse(await _tools.GetGameStateAsync(null, options, cancellationToken: cancellation))!.AsObject();

    /// <summary>Runs <paramref name="body"/> as the body of a run_script execute, which must answer true.</summary>
    private async Task RunBodyAsync(string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        JsonNode reply = JsonNode.Parse(await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(reply["value"]?.GetValue<bool>() == true, reply.ToJsonString());
    }

    /// <summary>Adds the States node with its C# children and the GDScript node second among them, once for the game.</summary>
    private async Task AddStatesAsync(CancellationToken cancellation)
    {
        JsonNode reply = JsonNode.Parse(await _tools.RunScriptAsync(AddStatesScript, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(reply["value"]?.GetValue<bool>() == true, reply.ToJsonString());
    }
}
