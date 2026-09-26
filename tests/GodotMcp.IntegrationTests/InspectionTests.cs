using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// get_scene_tree, inspect_node, set_property and call_method against the InputProbe with inspect_probe.tscn added under the
/// root, and call_method against the CsProbe, a Godot C# project built once for the class.
/// </summary>
public sealed class InspectionTests : IAsyncDisposable, IClassFixture<CsProbeBuild>
{
    private const int TestTimeoutMs = 45_000;
    private const int CSharpTestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Probe = "/root/InspectProbe";
    private static readonly string[] Rgba = ["r", "g", "b", "a"];

    // inspect_probe.tscn's Swatch: a ColorRect of Color(0, 0, 1) at (580, 300), 50 x 50, clear of main.tscn's controls.
    private static readonly ScreenshotCrop Swatch = new(580, 300, 50, 50);
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly CsProbeBuild _csProbe;
    private readonly RuntimeTools _tools;

    public InspectionTests(CsProbeBuild csProbe)
    {
        _csProbe = csProbe;
        _tools = new RuntimeTools(_harness.Sessions);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TreeFiltersByClassAndGroup()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode controls = await TreeAsync(Probe, "Control", null);
        JsonNode node2Ds = await TreeAsync(Probe, "Node2D", null);
        JsonNode grouped = await TreeAsync(null, null, "probe_group");
        await _harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);

        // Loading inspect_probe.tscn and its script in a run writes nothing beside them (no .uid files).
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
        Assert.Equal(["Swatch"], Names(controls));
        Assert.Equal(["InspectProbe", "Sprite", "Inner"], Names(node2Ds));
        JsonNode probe = Assert.Single(grouped["nodes"]!.AsArray())!;
        Assert.Equal(Probe, probe["path"]!.GetValue<string>());
        Assert.Equal("InspectProbe", probe["name"]!.GetValue<string>());
        Assert.Equal("Node2D", probe["class"]!.GetValue<string>());
        Assert.Equal("res://inspect_probe.gd", probe["script"]!.GetValue<string>());
        Assert.Equal(["probe_group"], probe["groups"]!.AsArray().Select(group => group!.GetValue<string>()));
        Assert.Equal(3, probe["childCount"]!.GetValue<int>());
        Assert.Null(node2Ds["nodes"]![1]!["script"]);
        Assert.Null(node2Ds["nodes"]![1]!["groups"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TreePages()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode first = await TreeAsync(Probe, null, null, new TreeOptions(Limit: 2));
        JsonNode second = await TreeAsync(Probe, null, null, new TreeOptions(Offset: 2, Limit: 2));
        JsonNode last = await TreeAsync(Probe, null, null, new TreeOptions(Offset: 4, Limit: 2));
        JsonNode shallow = await TreeAsync(Probe, null, null, new TreeOptions(MaxDepth: 1));

        Assert.Equal(["InspectProbe", "Sprite"], Names(first));
        Assert.Equal(5, first["total"]!.GetValue<int>());
        Assert.Equal(0, first["offset"]!.GetValue<int>());
        Assert.Equal(2, first["next"]!.GetValue<int>());
        Assert.Equal(["Swatch", "Inner"], Names(second));
        Assert.Equal(4, second["next"]!.GetValue<int>());
        Assert.Equal(["Leaf"], Names(last));
        Assert.Null(last["next"]);
        Assert.Equal(["InspectProbe", "Sprite", "Swatch", "Inner"], Names(shallow));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task InspectShowsScriptVarsAndColor()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode probe = await InspectAsync("InspectProbe", null);
        JsonNode swatch = await InspectAsync("Swatch", null);

        Assert.Equal(Probe, probe["path"]!.GetValue<string>());
        Assert.Equal("Node2D", probe["class"]!.GetValue<string>());
        Assert.Equal("res://inspect_probe.gd", probe["script"]!.GetValue<string>());
        JsonNode properties = probe["properties"]!;
        Assert.Equal(0, properties["count"]!.GetValue<int>());
        Assert.Equal("start", properties["label_text"]!.GetValue<string>());
        Assert.Equal([0.0, 0.0], Components(properties["offset"], "x", "y"));
        Assert.Equal([0.0, 0.0], Components(properties["position"], "x", "y"));
        Assert.Equal([1.0, 1.0, 1.0, 1.0], Components(properties["modulate"], Rgba));
        Assert.Equal([0.0, 0.0, 1.0, 1.0], Components(swatch["properties"]!["color"], Rgba));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task InspectNamedPropertiesOnly()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode probe = await InspectAsync("InspectProbe", ["count", "position"]);
        McpException unknown = await Assert.ThrowsAsync<McpException>(() => InspectAsync("InspectProbe", ["count", "nope"]));

        Assert.Equal(["count", "position"], probe["properties"]!.AsObject().Select(property => property.Key));
        Assert.Contains($"Node '{Probe}' has no property 'nope'.", unknown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetColorFromJsonObjectChangesPixel()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("Swatch", "color", """{"r": 0, "g": 1, "b": 0}""");
        List<ContentBlock> blocks =
        [
            .. await _tools.TakeScreenshotAsync("path_only", Swatch, 960, cancellationToken: TestContext.Current.CancellationToken),
        ];
        string path = JsonNode.Parse(string.Concat(blocks.OfType<TextContentBlock>().Select(block => block.Text)))!["path"]!.GetValue<string>();
        JsonNode pixel = await RunAsync(
            $"var c := Image.load_from_file(\"{path.Replace('\\', '/')}\").get_pixel(25, 25)\n\treturn [c.r8, c.g8, c.b8]"
        );

        Assert.Equal("/root/InspectProbe/Swatch", set["path"]!.GetValue<string>());
        Assert.Equal("color", set["property"]!.GetValue<string>());
        Assert.Equal([0.0, 0.0, 1.0, 1.0], Components(set["before"], Rgba));
        Assert.Equal([0.0, 1.0, 0.0, 1.0], Components(set["after"], Rgba));
        Assert.Equal([0, 255, 0], pixel.AsArray().Select(channel => channel!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetPositionFromXY()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "position", """{"x": 12.5, "y": -3}""");
        JsonNode position = await RunAsync("return scene_tree.root.get_node(\"InspectProbe\").position");

        Assert.Equal([12.5, -3.0], Components(set["after"], "x", "y"));
        Assert.Equal([12.5, -3.0], Components(position, "x", "y"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetTypedIntFromFloat()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        // GDScript's JSON parser reads the 7 as 7.0; the bridge converts it by the member's declared int.
        JsonNode set = await SetAsync("InspectProbe", "count", "7");
        JsonNode isInt = await RunAsync("return typeof(scene_tree.root.get_node(\"InspectProbe\").count) == TYPE_INT");

        Assert.Equal(0, set["before"]!.GetValue<int>());
        Assert.Equal(7, set["after"]!.GetValue<int>());
        Assert.True(isInt.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetUnknownPropertyFails()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "nope", "1"));

        Assert.Contains($"Node '{Probe}' has no property 'nope'.", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetWrongShapeFails()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "position", "\"abc\""));
        JsonNode position = await RunAsync("return scene_tree.root.get_node(\"InspectProbe\").position");

        Assert.Contains($"Property 'position' on '{Probe}' is Vector2; \"abc\" does not convert to it.", failed.Message, StringComparison.Ordinal);
        Assert.Equal([0.0, 0.0], Components(position, "x", "y"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWithIntArgs()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_add", "2", "3");

        Assert.Equal(Probe, called["path"]!.GetValue<string>());
        Assert.Equal("probe_add", called["method"]!.GetValue<string>());
        Assert.Equal(5, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWrongArgCountFails()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => CallAsync("InspectProbe", "probe_add", "1"));

        Assert.Contains($"Method 'probe_add' on '{Probe}' takes 2 arguments; 1 was given.", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodReturnsNodeAsPath()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        // Node.get_node takes a NodePath, which the bridge makes from the JSON string.
        JsonNode called = await CallAsync("InspectProbe", "get_node", "\"Inner/Leaf\"");

        Assert.Equal("/root/InspectProbe/Inner/Leaf", called["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallAwaitingMethodReturnsValue()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_later", "21");

        Assert.Equal(42, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodIgnoresTheMethodsOwnFailedCallv()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        // probe_inner_callv's own callv fails and logs Godot's "Error calling method from 'callv'"; the call itself succeeded.
        JsonNode called = await CallAsync("InspectProbe", "probe_inner_callv");

        Assert.Equal(3, called["value"]!.GetValue<int>());
        JsonNode error = Assert.Single(called["errors"]!.AsArray())!;
        Assert.StartsWith("Error calling method from 'callv'", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("res://inspect_probe.gd", error["file"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetTypedArrayFromJsonArray()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "numbers", "[1, 2]");
        JsonNode typed = await RunAsync(
            "var numbers: Array = scene_tree.root.get_node(\"InspectProbe\").numbers\n\treturn numbers.get_typed_builtin()"
        );

        Assert.Equal([1, 2], set["after"]!.AsArray().Select(number => number!.GetValue<int>()));
        Assert.Equal(2, typed.GetValue<int>()); // TYPE_INT
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWithTypedArrayArg()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_sum", "[4, 5]");

        Assert.Equal(9, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetPackedVector2ArrayFromJsonArray()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "points", """[{"x": 1, "y": 2}, {"x": 3, "y": 4}]""");
        JsonNode isPacked = await RunAsync("return typeof(scene_tree.root.get_node(\"InspectProbe\").points) == TYPE_PACKED_VECTOR2_ARRAY");

        JsonArray after = set["after"]!.AsArray();
        Assert.Equal([1.0, 2.0], Components(after[0], "x", "y"));
        Assert.Equal([3.0, 4.0], Components(after[1], "x", "y"));
        Assert.True(isPacked.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetUntypedPropertyKeepsItsType()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        await SetAsync("InspectProbe", "target", """{"x": 1, "y": 2}""");
        await SetAsync("InspectProbe", "loose", "7");
        JsonNode types = await RunAsync(
            "var probe := scene_tree.root.get_node(\"InspectProbe\")\n\treturn [typeof(probe.target) == TYPE_VECTOR2, typeof(probe.loose) == TYPE_INT, probe.loose]"
        );

        Assert.Equal("[true,true,7]", types.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetClampedValueIsPutBackAndFails()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "level", "50"));
        JsonNode level = await RunAsync("return scene_tree.root.get_node(\"InspectProbe\").level");

        Assert.Contains(
            $"Property 'level' on '{Probe}' did not take the value: it read 10 after the set, so it was put back to 1.",
            failed.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(1, level.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheBridgeIsOutOfReach()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode tree = await TreeAsync(null, null, null, new TreeOptions(Limit: 500));
        McpException inspected = await Assert.ThrowsAsync<McpException>(() => InspectAsync("GodotMcpBridge", null));
        McpException called = await Assert.ThrowsAsync<McpException>(() => CallAsync("/root/GodotMcpBridge/Inspect", "get_name"));

        Assert.DoesNotContain(
            tree["nodes"]!.AsArray(),
            node => node!["path"]!.GetValue<string>().StartsWith("/root/GodotMcpBridge", StringComparison.Ordinal)
        );
        Assert.Contains("/root/InspectProbe", Names(tree, "path"));
        Assert.Contains(
            "'/root/GodotMcpBridge' is part of the godot-mcp bridge, which the inspection tools do not reach.",
            inspected.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("'/root/GodotMcpBridge/Inspect' is part of the godot-mcp bridge", called.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWithDefaultedParameter()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode defaulted = await CallAsync("InspectProbe", "probe_scaled", "3");
        JsonNode given = await CallAsync("InspectProbe", "probe_scaled", "3", "5");
        McpException none = await Assert.ThrowsAsync<McpException>(() => CallAsync("InspectProbe", "probe_scaled"));

        Assert.Equal(6, defaulted["value"]!.GetValue<int>());
        Assert.Equal(15, given["value"]!.GetValue<int>());
        Assert.Contains($"Method 'probe_scaled' on '{Probe}' takes 1 to 2 arguments; 0 were given.", none.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallVarargMethod()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        // Object.call is vararg: its first parameter is declared, the rest pass through as they are.
        JsonNode called = await CallAsync("InspectProbe", "call", "\"get_node\"", "\"Inner/Leaf\"");

        Assert.Equal("/root/InspectProbe/Inner/Leaf", called["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodLongValueIsCut()
    {
        await LaunchWithInspectProbeAsync(TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_long");

        // 3000 letters and the string's two quotes.
        Assert.Null(called["value"]);
        Assert.Equal(3002, called["valueLength"]!.GetValue<int>());
        Assert.Equal(RuntimeTools.MaxPropertyValueLength, called["valuePreview"]!.GetValue<string>().Length);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallCSharpPublicMethod()
    {
        await LaunchAsync(_csProbe.Directory, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("CsProbe", "PlayStep", "4");
        await _harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);

        // The build's output and the run's files stay under the ignored .godot/.
        Assert.Equal(string.Empty, Git.Status(_csProbe.Directory));
        Assert.Equal("/root/CsProbe", called["path"]!.GetValue<string>());
        Assert.Equal(5, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallCSharpInternalMethod()
    {
        await LaunchAsync(_csProbe.Directory, TestContext.Current.CancellationToken);

        // Godot's C# source generator exposes every method with a Godot-compatible signature, whatever its accessibility.
        JsonNode called = await CallAsync("CsProbe", "Secret");

        Assert.Equal("hidden", called["value"]!.GetValue<string>());
    }

    private async Task LaunchWithInspectProbeAsync(CancellationToken cancellation)
    {
        await LaunchAsync(_probe.Directory, cancellation);
        await RunAsync("scene_tree.root.add_child(load(\"res://inspect_probe.tscn\").instantiate())\n\treturn true");
    }

    private Task<LaunchResult> LaunchAsync(string directory, CancellationToken cancellation) =>
        _harness.Sessions.LaunchAsync(new LaunchRequest(directory, null, [], [], true, false), null, cancellation);

    private async Task<JsonNode> TreeAsync(string? root, string? className, string? group, TreeOptions? options = null) =>
        JsonNode.Parse(await _tools.GetSceneTreeAsync(root, className, group, options, cancellationToken: TestContext.Current.CancellationToken))!;

    private async Task<JsonNode> InspectAsync(string node, string[]? properties) =>
        JsonNode.Parse(await _tools.InspectNodeAsync(node, properties, cancellationToken: TestContext.Current.CancellationToken))!;

    private async Task<JsonNode> SetAsync(string node, string property, string valueJson) =>
        JsonNode.Parse(
            await _tools.SetPropertyAsync(
                node,
                property,
                JsonSerializer.Deserialize<JsonElement>(valueJson),
                cancellationToken: TestContext.Current.CancellationToken
            )
        )!;

    private async Task<JsonNode> CallAsync(string node, string method, params string[] argsJson)
    {
        JsonElement[] args = [.. argsJson.Select(arg => JsonSerializer.Deserialize<JsonElement>(arg))];
        string json = await _tools.CallMethodAsync(node, method, args, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    private async Task<JsonNode> RunAsync(string body)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!["value"]!;
    }

    // Vectors and colours come back as objects of numbers, which GDScript's JSON writes as floats (0.0), so they are compared as doubles.
    private static double[] Components(JsonNode? value, params string[] keys) => [.. keys.Select(key => value![key]!.GetValue<double>())];

    private static IEnumerable<string> Names(JsonNode page, string field = "name") =>
        page["nodes"]!.AsArray().Select(node => node![field]!.GetValue<string>());
}

/// <summary>The CsProbe copied and built on first use, shared by the class's C# tests and deleted after the last.</summary>
public sealed class CsProbeBuild : IDisposable
{
    private readonly Lazy<CsProbeProject> _project = new(() => new CsProbeProject());

    public string Directory => _project.Value.Directory;

    public void Dispose()
    {
        if (_project.IsValueCreated)
        {
            _project.Value.Dispose();
        }
    }
}
