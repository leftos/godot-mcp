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
/// root, in one shared run reset before each test (the test that stops its game has its own), and call_method against the
/// CsProbe, a Godot C# project built once for the class.
/// </summary>
public sealed class InspectionTests(CsProbeBuild csProbe, SharedProbeSession shared)
    : IAsyncLifetime,
        IClassFixture<CsProbeBuild>,
        IClassFixture<SharedProbeSession>
{
    private const int TestTimeoutMs = 45_000;
    private const int CSharpTestTimeoutMs = 150_000;
    private const int ScriptTimeoutMs = 10_000;
    private const string Probe = "/root/InspectProbe";

    // A script class for describe_class, written into a probe copy of its own.
    private const string ProbeClassScript =
        "class_name ProbeClass\nextends Node\n\nsignal hit(amount: int)\n\n@export var speed: float = 2.5\n\n\n"
        + "func jump(height: float) -> bool:\n\treturn height > 0.0\n";
    private static readonly string[] Rgba = ["r", "g", "b", "a"];

    // inspect_probe.tscn's Swatch: a ColorRect of Color(0, 0, 1) at (580, 300), 50 x 50, clear of main.tscn's controls.
    private static readonly ScreenshotCrop Swatch = new(580, 300, 50, 50);
    private readonly SharedProbeSession _shared = shared;
    private readonly CsProbeBuild _csProbe = csProbe;
    private readonly RuntimeTools _tools = new(shared.Sessions, TestCSharp.Unused());

    public async ValueTask InitializeAsync() => await _shared.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TreeFiltersByClassAndGroup()
    {
        // The test stops its game to check the tree after the run, so it has a run of its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject project = new();
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await LaunchAsync(harness, project.Directory, cancellation);
        await AddInspectProbeAsync(tools, cancellation);

        JsonNode controls = await TreeAsync(tools, Probe, "Control", null);
        JsonNode node2Ds = await TreeAsync(tools, Probe, "Node2D", null);
        JsonNode grouped = await TreeAsync(tools, null, null, "probe_group");
        await harness.Sessions.StopAsync(null, cancellation);

        // Loading inspect_probe.tscn and its script in a run writes nothing beside them (no .uid files).
        Assert.Equal(string.Empty, Git.Status(project.Directory));
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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        // A saved resource reads as its path and class (and its uid when the project has one), not its contents.
        Assert.Equal("res://inspect_probe.gd", properties["script"]!["resource"]!.GetValue<string>());
        Assert.Equal("GDScript", properties["script"]!["class"]!.GetValue<string>());
        Assert.Equal([0.0, 0.0, 1.0, 1.0], Components(swatch["properties"]!["color"], Rgba));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task InspectNamedPropertiesOnly()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode probe = await InspectAsync("InspectProbe", ["count", "position"]);
        McpException unknown = await Assert.ThrowsAsync<McpException>(() => InspectAsync("InspectProbe", ["count", "nope"]));

        Assert.Equal(["count", "position"], probe["properties"]!.AsObject().Select(property => property.Key));
        Assert.Contains($"Node '{Probe}' has no property 'nope'.", unknown.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetColorFromJsonObjectChangesPixel()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "position", """{"x": 12.5, "y": -3}""");
        JsonNode position = await RunAsync("return scene_tree.root.get_node(\"InspectProbe\").position");

        Assert.Equal([12.5, -3.0], Components(set["after"], "x", "y"));
        Assert.Equal([12.5, -3.0], Components(position, "x", "y"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetTypedIntFromFloat()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        // GDScript's JSON parser reads the 7 as 7.0; the bridge converts it by the member's declared int.
        JsonNode set = await SetAsync("InspectProbe", "count", "7");
        JsonNode isInt = await RunAsync("return typeof(scene_tree.root.get_node(\"InspectProbe\").count) == TYPE_INT");

        Assert.Equal(0, set["before"]!.GetValue<int>());
        Assert.Equal(7, set["after"]!.GetValue<int>());
        Assert.True(isInt.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetsAStringNamePropertyDeclaredAsString()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("Swatch", "theme_type_variation", "\"Big\"");
        JsonNode isStringName = await RunAsync(
            "return typeof(scene_tree.root.get_node(\"InspectProbe/Swatch\").theme_type_variation) == TYPE_STRING_NAME"
        );

        Assert.Equal(string.Empty, set["before"]!.GetValue<string>());
        Assert.Equal("Big", set["after"]!.GetValue<string>());
        Assert.True(isStringName.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetsAPackedArrayPropertyThatReadsBackAsAnArray()
    {
        // CodeEdit declares line_length_guidelines a PackedInt32Array and reads it back as an Array.
        await RunAsync(
            _tools,
            "var edit := CodeEdit.new()\n\tedit.name = \"Guides\"\n\tscene_tree.root.add_child(edit)\n\treturn true",
            TestContext.Current.CancellationToken
        );

        JsonNode set = await SetAsync("Guides", "line_length_guidelines", "[80, 100]");

        Assert.Equal("[]", set["before"]!.ToJsonString());
        Assert.Equal("[80,100]", set["after"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetUnknownPropertyFails()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "nope", "1"));

        Assert.Contains($"Node '{Probe}' has no property 'nope'.", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetWrongShapeFails()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "position", "\"abc\""));
        JsonNode position = await RunAsync("return scene_tree.root.get_node(\"InspectProbe\").position");

        Assert.Contains($"Property 'position' on '{Probe}' is Vector2; \"abc\" does not convert to it.", failed.Message, StringComparison.Ordinal);
        Assert.Equal([0.0, 0.0], Components(position, "x", "y"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWithIntArgs()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_add", "2", "3");

        Assert.Equal(Probe, called["path"]!.GetValue<string>());
        Assert.Equal("probe_add", called["method"]!.GetValue<string>());
        Assert.Equal(5, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWrongArgCountFails()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        McpException failed = await Assert.ThrowsAsync<McpException>(() => CallAsync("InspectProbe", "probe_add", "1"));

        Assert.Contains($"Method 'probe_add' on '{Probe}' takes 2 arguments; 1 was given.", failed.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodReturnsNodeAsPath()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        // Node.get_node takes a NodePath, which the bridge makes from the JSON string.
        JsonNode called = await CallAsync("InspectProbe", "get_node", "\"Inner/Leaf\"");

        Assert.Equal("/root/InspectProbe/Inner/Leaf", called["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallAwaitingMethodReturnsValue()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_later", "21");

        Assert.Equal(42, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodIgnoresTheMethodsOwnFailedCallv()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "numbers", "[1, 2]");
        JsonNode typed = await RunAsync(
            "var numbers: Array = scene_tree.root.get_node(\"InspectProbe\").numbers\n\treturn numbers.get_typed_builtin()"
        );

        Assert.Equal([1, 2], set["after"]!.AsArray().Select(number => number!.GetValue<int>()));
        Assert.Equal(2, typed.GetValue<int>()); // TYPE_INT
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetExportedTypedArrayFromJsonArray()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "exported_numbers", "[1, 2, 3]");
        JsonNode read = await RunAsync(
            "var numbers: Array = scene_tree.root.get_node(\"InspectProbe\").exported_numbers\n\treturn [numbers, numbers.get_typed_builtin()]"
        );

        Assert.Equal([1, 2, 3], set["after"]!.AsArray().Select(number => number!.GetValue<int>()));
        Assert.Equal([1, 2, 3], read[0]!.AsArray().Select(number => number!.GetValue<int>()));
        Assert.Equal(2, read[1]!.GetValue<int>()); // TYPE_INT
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetExportedTypedDictionaryFromJsonObject()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode set = await SetAsync("InspectProbe", "exported_scores", """{"a": 1}""");
        JsonNode read = await RunAsync(
            "var scores: Dictionary = scene_tree.root.get_node(\"InspectProbe\").exported_scores\n\t"
                + "return [scores, scores.get_typed_key_builtin(), scores.get_typed_value_builtin()]"
        );

        Assert.Equal("""{"a":1}""", set["after"]!.ToJsonString());
        Assert.Equal("""{"a":1}""", read[0]!.ToJsonString());
        Assert.Equal(4, read[1]!.GetValue<int>()); // TYPE_STRING
        Assert.Equal(2, read[2]!.GetValue<int>()); // TYPE_INT
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetPropertyRefusalCarriesTheReason()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetAsync("InspectProbe", "exported_nodes", "[]"));

        Assert.EndsWith(
            $"Property 'exported_nodes' on '{Probe}': arrays of Object types (here Array[Node2D]) cannot be set from JSON.",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodWithTypedArrayArg()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_sum", "[4, 5]");

        Assert.Equal(9, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetPackedVector2ArrayFromJsonArray()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        await SetAsync("InspectProbe", "target", """{"x": 1, "y": 2}""");
        await SetAsync("InspectProbe", "loose", "7");
        JsonNode types = await RunAsync(
            "var probe := scene_tree.root.get_node(\"InspectProbe\")\n"
                + "\treturn [typeof(probe.target) == TYPE_VECTOR2, typeof(probe.loose) == TYPE_INT, probe.loose]"
        );

        Assert.Equal("[true,true,7]", types.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetClampedValueIsPutBackAndFails()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

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
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        // Object.call is vararg: its first parameter is declared, the rest pass through as they are.
        JsonNode called = await CallAsync("InspectProbe", "call", "\"get_node\"", "\"Inner/Leaf\"");

        Assert.Equal("/root/InspectProbe/Inner/Leaf", called["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CallMethodLongValueIsCut()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync("InspectProbe", "probe_long");

        // 3000 letters and the string's two quotes.
        Assert.Null(called["value"]);
        Assert.Equal(3002, called["valueLength"]!.GetValue<int>());
        Assert.Equal(RuntimeTools.MaxPropertyValueLength, called["valuePreview"]!.GetValue<string>().Length);
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallCSharpPublicMethod()
    {
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await LaunchAsync(harness, _csProbe.Directory, TestContext.Current.CancellationToken);

        JsonNode called = await CallAsync(tools, "CsProbe", "PlayStep", "4");
        await harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);

        // The build's output and the run's files stay under the ignored .godot/.
        Assert.Equal(string.Empty, Git.Status(_csProbe.Directory));
        Assert.Equal("/root/CsProbe", called["path"]!.GetValue<string>());
        Assert.Equal(5, called["value"]!.GetValue<int>());
    }

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task CallCSharpInternalMethod()
    {
        await using SessionHarness harness = new();
        RuntimeTools tools = new(harness.Sessions, TestCSharp.Unused());
        await LaunchAsync(harness, _csProbe.Directory, TestContext.Current.CancellationToken);

        // Godot's C# source generator exposes every method with a Godot-compatible signature, whatever its accessibility.
        JsonNode called = await CallAsync(tools, "CsProbe", "Secret");

        Assert.Equal("hidden", called["value"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetPropertyNullClearsAnObjectProperty()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);
        await SetAsync("Sprite", "material", """{"type": "CanvasItemMaterial"}""");

        JsonNode cleared = await SetAsync("Sprite", "material", "null");

        Assert.Equal("CanvasItemMaterial", cleared["before"]!["class"]!.GetValue<string>());
        Assert.Null(cleared["after"]);
        Assert.Null((await InspectAsync("Sprite", ["material"]))["properties"]!["material"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASnapshotDiffedWithTheLiveGameShowsASetProperty()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode snapshot = await SnapshotAsync("InspectProbe");
        await SetAsync("InspectProbe", "count", "7");
        JsonNode diff = await DiffAsync(snapshot["snapshotId"]!.GetValue<string>(), null);

        Assert.Equal(Probe, snapshot["node"]!.GetValue<string>());
        Assert.Equal(5, snapshot["nodeCount"]!.GetValue<int>());
        JsonNode change = Assert.Single(diff["changed"]!.AsArray())!;
        Assert.Equal(".", change["node"]!.GetValue<string>());
        Assert.Equal("count", change["property"]!.GetValue<string>());
        Assert.Equal(0, change["before"]!.GetValue<int>());
        Assert.Equal(7, change["after"]!.GetValue<int>());
        Assert.Empty(diff["added"]!.AsArray());
        Assert.Empty(diff["removed"]!.AsArray());
        Assert.Equal(1, diff["changedCount"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TwoSnapshotsShowAnAddedChild()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode before = await SnapshotAsync("InspectProbe");
        await RunAsync(
            "var extra := Node.new()\n\textra.name = \"Extra\"\n\tscene_tree.root.get_node(\"InspectProbe/Inner\").add_child(extra)\n\treturn true"
        );
        JsonNode after = await SnapshotAsync("InspectProbe");
        JsonNode diff = await DiffAsync(before["snapshotId"]!.GetValue<string>(), after["snapshotId"]!.GetValue<string>());

        Assert.Equal(6, after["nodeCount"]!.GetValue<int>());
        Assert.Equal(["Inner/Extra"], diff["added"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.Equal(1, diff["addedCount"]!.GetValue<int>());
        Assert.Empty(diff["removed"]!.AsArray());
        Assert.Empty(diff["changed"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASnapshotTakesTheSceneRootByDefaultAndItsGroups()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        JsonNode scene = await SnapshotAsync(null);
        JsonNode grouped = await SnapshotAsync("InspectProbe", new SnapshotOptions(Properties: ["groups"]));
        JsonNode diff = await DiffAsync(grouped["snapshotId"]!.GetValue<string>(), null);
        await RunAsync("scene_tree.root.get_node(\"InspectProbe\").add_to_group(\"probe_extra\")\n\treturn true");
        JsonNode regrouped = await DiffAsync(grouped["snapshotId"]!.GetValue<string>(), null);

        Assert.Equal("/root/Main", scene["node"]!.GetValue<string>());
        Assert.Empty(diff["changed"]!.AsArray());
        JsonNode change = Assert.Single(regrouped["changed"]!.AsArray())!;
        Assert.Equal("groups", change["property"]!.GetValue<string>());
        Assert.Equal(["probe_extra", "probe_group"], change["after"]!.AsArray().Select(group => group!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUnknownSnapshotAndATooLargeSubtreeAreRefused()
    {
        await AddInspectProbeAsync(_tools, TestContext.Current.CancellationToken);

        McpException unknown = await Assert.ThrowsAsync<McpException>(() => DiffAsync("s99999", null));
        McpException tooLarge = await Assert.ThrowsAsync<McpException>(() => SnapshotAsync("InspectProbe", new SnapshotOptions(MaxNodes: 1)));

        Assert.Equal(
            "snapshot s99999 is not held (evicted, or from a run that stopped or restarted, or an attached game that has gone); "
                + "take a new one with snapshot_subtree",
            unknown.Message
        );
        Assert.Contains($"'{Probe}' has 5 nodes in its subtree, more than maxNodes (1)", tooLarge.Message, StringComparison.Ordinal);
    }

    private async Task<JsonNode> SnapshotAsync(string? node, SnapshotOptions? options = null) =>
        JsonNode.Parse(await _tools.SnapshotSubtreeAsync(node, options, cancellationToken: TestContext.Current.CancellationToken))!;

    private async Task<JsonNode> DiffAsync(string beforeId, string? afterId) =>
        JsonNode.Parse(await _tools.DiffSnapshotsAsync(beforeId, afterId, cancellationToken: TestContext.Current.CancellationToken))!;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DescribeClassAsksTheRunningGame()
    {
        // A headless run is refused while the shared session is live on the probe, so the answer is the bridge's.
        HeadlessTools tools = new(_shared.Sessions);

        JsonNode described = JsonNode.Parse(
            await tools.DescribeClassAsync(_shared.ProbeDirectory, "Button", null, null, TestContext.Current.CancellationToken)
        )!;

        Assert.Equal("Button", described["className"]!.GetValue<string>());
        Assert.Equal("BaseButton", described["inherits"]!.GetValue<string>());
        // The same key order as the headless answer for Button (HeadlessTests.DescribeClassReadsAnEngineClassHeadless): sorted.
        string[] keys = [.. described.AsObject().Select(entry => entry.Key)];
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        JsonNode flat = described["properties"]!.AsArray().Single(property => property!["name"]!.GetValue<string>() == "flat")!;
        // The bridge's replies carry their keys sorted, so entries are compared by field.
        Assert.Equal("bool", flat["type"]!.GetValue<string>());
        Assert.False(flat["default"]!.GetValue<bool>());
        Assert.True(described["methodCount"]!.GetValue<int>() > 0);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DescribeClassFindsAScriptClassOfTheRunningGame()
    {
        // A class_name script in the tracked fixture would make every InputProbe launch import first, so this copy gets its own.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ProbeProject project = new();
        File.WriteAllText(Path.Combine(project.Directory, "probe_class.gd"), ProbeClassScript);
        await using SessionHarness harness = new();
        await LaunchAsync(harness, project.Directory, cancellation);
        HeadlessTools tools = new(harness.Sessions);

        JsonNode described = JsonNode.Parse(await tools.DescribeClassAsync(project.Directory, "ProbeClass", null, null, cancellation))!;
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.DescribeClassAsync(project.Directory, "ProbeClas", null, null, cancellation)
        );

        Assert.True(described["isScript"]!.GetValue<bool>());
        Assert.Equal("res://probe_class.gd", described["scriptPath"]!.GetValue<string>());
        Assert.Equal("GDScript", described["language"]!.GetValue<string>());
        Assert.Equal("Node", described["inherits"]!.GetValue<string>());
        JsonNode speed = described["properties"]!.AsArray().Single(property => property!["name"]!.GetValue<string>() == "speed")!;
        Assert.Equal("float", speed["type"]!.GetValue<string>());
        Assert.Equal(2.5, speed["default"]!.GetValue<double>());
        JsonNode hit = described["signals"]!.AsArray().Single(signal => signal!["name"]!.GetValue<string>() == "hit")!;
        Assert.Equal("""[{"name":"amount","type":"int"}]""", hit["args"]!.ToJsonString());
        JsonNode jump = described["methods"]!.AsArray().Single(method => method!["name"]!.GetValue<string>() == "jump")!;
        Assert.Equal("bool", jump["returnType"]!.GetValue<string>());
        Assert.Contains("Did you mean: ProbeClass", refused.Message, StringComparison.Ordinal);
    }

    private static Task<LaunchResult> LaunchAsync(SessionHarness harness, string directory, CancellationToken cancellation) =>
        harness.Sessions.LaunchAsync(new LaunchRequest(directory, null, [], [], true, false, Prepare: true), null, cancellation);

    private Task<JsonNode> TreeAsync(string? root, string? className, string? group, TreeOptions? options = null) =>
        TreeAsync(_tools, root, className, group, options);

    private static async Task<JsonNode> TreeAsync(RuntimeTools tools, string? root, string? className, string? group, TreeOptions? options = null) =>
        JsonNode.Parse(await tools.GetSceneTreeAsync(root, className, group, options, cancellationToken: TestContext.Current.CancellationToken))!;

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

    private Task<JsonNode> CallAsync(string node, string method, params string[] argsJson) => CallAsync(_tools, node, method, argsJson);

    private static async Task<JsonNode> CallAsync(RuntimeTools tools, string node, string method, params string[] argsJson)
    {
        JsonElement[] args = [.. argsJson.Select(arg => JsonSerializer.Deserialize<JsonElement>(arg))];
        string json = await tools.CallMethodAsync(node, method, args, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    private Task<JsonNode> RunAsync(string body) => RunAsync(_tools, body, TestContext.Current.CancellationToken);

    private static Task<JsonNode> AddInspectProbeAsync(RuntimeTools tools, CancellationToken cancellation) =>
        RunAsync(tools, "scene_tree.root.add_child(load(\"res://inspect_probe.tscn\").instantiate())\n\treturn true", cancellation);

    private static async Task<JsonNode> RunAsync(RuntimeTools tools, string body, CancellationToken cancellation)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation);
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
