using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// get_game_state against the CsProbe: a CsState node (tests/fixtures/CsProbe/CsState.cs) added under /root as States, whose
/// C# children each carry a different state method, with a GDScript node the test puts second among them and one returning
/// the process frame last, all read in one call:
/// the C# nodes by the helper, merged with the GDScript node in tree order. The States node is added once for the class.
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
        Assert.Equal((frame, frame), (nodes[9]!["state"]!.GetValue<long>(), nodes[10]!["state"]!.GetValue<long>()));
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

    private static string Error(JsonNode entry) => entry["error"]?.GetValue<string>() ?? $"no error in {entry.ToJsonString()}";

    private async Task<JsonObject> ReadAsync(CancellationToken cancellation, StateOptions? options = null) =>
        JsonNode.Parse(await _tools.GetGameStateAsync(null, options, cancellationToken: cancellation))!.AsObject();

    /// <summary>Adds the States node with its C# children and the GDScript node second among them, once for the game.</summary>
    private async Task AddStatesAsync(CancellationToken cancellation)
    {
        JsonNode reply = JsonNode.Parse(await _tools.RunScriptAsync(AddStatesScript, ScriptTimeoutMs, cancellationToken: cancellation))!;
        Assert.True(reply["value"]?.GetValue<bool>() == true, reply.ToJsonString());
    }
}
