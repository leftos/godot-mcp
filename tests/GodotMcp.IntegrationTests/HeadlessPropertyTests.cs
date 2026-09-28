using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless property tools against the real Godot: add_node, set_node_properties and get_node_properties on scenes the
/// tests write into InputProbe copies (never into the tracked fixtures), read back as text and with the tools themselves.
/// </summary>
public sealed class HeadlessPropertyTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;

    private const string EnemyScene =
        "[gd_scene format=3 uid=\"uid://bpenemy0000a\"]\n\n[node name=\"Enemy\" type=\"CharacterBody2D\"]\n\n"
        + "[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\nposition = Vector2(1, 2)\n";

    private const string HolderScript = "extends Node2D\n\n@export var target: Node2D\n";

    private const string LevelScene =
        "[gd_scene load_steps=3 format=3 uid=\"uid://bplevel00000a\"]\n\n"
        + "[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n"
        + "[ext_resource type=\"Script\" path=\"res://holder.gd\" id=\"2\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\n\n[node name=\"Boss\" parent=\".\" instance=ExtResource(\"1\")]\n\n"
        + "[node name=\"Box\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Pic\" type=\"Sprite2D\" parent=\".\"]\n\n"
        + "[node name=\"Holder\" type=\"Node2D\" parent=\".\"]\nscript = ExtResource(\"2\")\n";

    private const string EliteScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Elite\" instance=ExtResource(\"1\")]\n\n[node name=\"Sprite\" parent=\".\"]\nvisible = false\n\n"
        + "[node name=\"Shield\" type=\"Node2D\" parent=\".\"]\nposition = Vector2(3, 4)\n";

    private const string WiredScene =
        "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://holder.gd\" id=\"1\"]\n"
        + "[ext_resource type=\"Texture2D\" path=\"res://art/grad.tres\" id=\"2\"]\n\n[node name=\"Wired\" type=\"Node2D\"]\n\n"
        + "[node name=\"Box\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Pic\" type=\"Sprite2D\" parent=\".\"]\n"
        + "texture = ExtResource(\"2\")\n\n[node name=\"Holder\" type=\"Node2D\" parent=\".\" node_paths=PackedStringArray(\"target\")]\n"
        + "script = ExtResource(\"1\")\ntarget = NodePath(\"../Box\")\n";

    private const string GradientTexture = "[gd_resource type=\"GradientTexture2D\" format=3]\n\n[resource]\nwidth = 8\nheight = 8\n";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessPropertyTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeAddsAClassNodeWithProperties()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        AddNodeOptions options = new(
            "Box",
            new() { ["position"] = Json("""{"x": 3, "y": 4}"""), ["modulate"] = Json("""{"r": 1, "g": 0, "b": 0, "a": 0.5}""") }
        );

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "Sprite2D", "Marker", options, cancellation);

        Assert.Equal("""{"path":"Box/Marker","type":"Sprite2D"}""", added);
        (string header, string[] body) = Section(probe.Directory, "level.tscn", "Marker");
        Assert.Contains("type=\"Sprite2D\" parent=\"Box\"", header, StringComparison.Ordinal);
        Assert.Equal(["modulate = Color(1, 0, 0, 0.5)", "position = Vector2(3, 4)"], body);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeSetsAThemeTypeVariation()
    {
        // Godot 4.7.2 declares Control.theme_type_variation as a String and reads it back as a StringName (control.cpp L5033, L3670).
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        AddNodeOptions options = new("Box", new() { ["theme_type_variation"] = Json("\"PopoverIconRow\"") });

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "HBoxContainer", "Icons", options, cancellation);

        Assert.Equal("""{"path":"Box/Icons","type":"HBoxContainer"}""", added);
        string[] body = Section(probe.Directory, "level.tscn", "Icons").Body;
        Assert.Contains("theme_type_variation = &\"PopoverIconRow\"", body);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeInstancesAScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "enemy.tscn", "Minion", cancellationToken: cancellation);

        Assert.Equal("""{"path":"Minion","type":"CharacterBody2D","instance":"res://enemy.tscn"}""", added);
        string header = Section(probe.Directory, "level.tscn", "Minion").Header;
        Assert.Contains("parent=\".\"", header, StringComparison.Ordinal);
        Assert.Contains("instance=ExtResource(", header, StringComparison.Ordinal);
        Assert.Contains("Minion/Sprite", await PathsAsync(probe.Directory, "level.tscn", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeTakesAGDScriptPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "plain.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node2D\"]\n");
        File.WriteAllText(Path.Combine(probe.Directory, "plain_node.gd"), "extends Node2D\n");

        string added = await _tools.AddNodeAsync(probe.Directory, "plain.tscn", "plain_node.gd", "Scripted", cancellationToken: cancellation);

        Assert.Equal("""{"path":"Scripted","type":"Node2D","script":"res://plain_node.gd"}""", added);
        (string header, string[] body) = Section(probe.Directory, "plain.tscn", "Scripted");
        Assert.Contains("type=\"Node2D\"", header, StringComparison.Ordinal);
        Assert.Contains("script = ExtResource(", string.Join("\n", body), StringComparison.Ordinal);
        Assert.Contains("[ext_resource type=\"Script\" path=\"res://plain_node.gd\"", Read(probe.Directory, "plain.tscn"), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeRefusesACollidingName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "Node2D", "Box", cancellationToken: cancellation)
        );

        Assert.Equal("add_node failed: The scene root already has a child named Box.", refused.Message);
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeWithABadPropertyAddsNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        AddNodeOptions options = new(Properties: new() { ["position"] = Json("\"nope\""), ["nope"] = Json("1") });

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "Sprite2D", "Marker", options, cancellation)
        );

        Assert.Equal(
            "add_node failed: Property 'position' on 'Marker' is Vector2; \"nope\" does not convert to it. Marker has no property nope.",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeUnderAnInstanceRootIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "Node2D", "Hat", new AddNodeOptions("Boss"), cancellation);

        Assert.Equal("""{"path":"Boss/Hat","type":"Node2D"}""", added);
        Assert.Contains("type=\"Node2D\" parent=\"Boss\"", Section(probe.Directory, "level.tscn", "Hat").Header, StringComparison.Ordinal);
        Assert.Contains("Boss/Hat", await PathsAsync(probe.Directory, "level.tscn", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsAndReadsBack()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(
                probe.Directory,
                "level.tscn",
                [new PropertyUpdate("Box", "position", Json("""{"x": 5, "y": 6}""")), new PropertyUpdate("Box", "visible", Json("false"))],
                cancellation
            )
        )!;

        JsonArray results = set["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("Box", results[0]!["nodePath"]!.GetValue<string>());
        Assert.Equal("position", results[0]!["property"]!.GetValue<string>());
        AssertVector(results[0]!["before"]!, 0, 0);
        AssertVector(results[0]!["after"]!, 5, 6);
        Assert.Equal("""{"nodePath":"Box","property":"visible","before":true,"after":false}""", results[1]!.ToJsonString());
        Assert.Equal(["visible = false", "position = Vector2(5, 6)"], Section(probe.Directory, "level.tscn", "Box").Body);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeKeepsTheRestOfTheFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "Node2D", "Marker", cancellationToken: cancellation);

        Assert.Equal("""{"path":"Marker","type":"Node2D"}""", added);
        string[] original = LevelScene.Split('\n');
        string[] lines = Read(probe.Directory, "level.tscn").Split('\n');
        int marker = original.Length;
        Assert.Equal(marker + 2, lines.Length);
        Assert.StartsWith("[node name=\"Marker\" type=\"Node2D\" parent=\".\" unique_id=", lines[marker], StringComparison.Ordinal);
        Assert.Equal([.. original[..^1], "", lines[marker], ""], lines);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesChangesOnlyThatNode()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        await _tools.SetNodePropertiesAsync(
            probe.Directory,
            "level.tscn",
            [new PropertyUpdate("Box", "position", Json("""{"x": 5, "y": 6}"""))],
            cancellation
        );

        string[] original = LevelScene.Split('\n');
        string[] lines = Read(probe.Directory, "level.tscn").Split('\n');
        int box = Array.IndexOf(original, "[node name=\"Box\" type=\"Node2D\" parent=\".\"]");
        Assert.Equal(original.Length + 1, lines.Length);
        Assert.StartsWith("[node name=\"Box\" type=\"Node2D\" parent=\".\" unique_id=", lines[box], StringComparison.Ordinal);
        Assert.Equal([.. original[..box], lines[box], "position = Vector2(5, 6)", .. original[(box + 1)..]], lines);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsATextureFromAPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(
                probe.Directory,
                "level.tscn",
                [new PropertyUpdate("Pic", "texture", Json("\"res://art/grad.tres\""))],
                cancellation
            )
        )!;

        Assert.Equal(
            """{"nodePath":"Pic","property":"texture","before":null,"after":{"resource":"res://art/grad.tres","class":"GradientTexture2D"}}""",
            set["results"]![0]!.ToJsonString()
        );
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "level.tscn"));
        string ext = Assert.Single(lines, line => line.Contains("path=\"res://art/grad.tres\"", StringComparison.Ordinal));
        Assert.StartsWith("[ext_resource type=\"Texture2D\"", ext, StringComparison.Ordinal);
        Assert.StartsWith("texture = ExtResource(", Assert.Single(Section(probe.Directory, "level.tscn", "Pic").Body), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsANodeExportByPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(
                probe.Directory,
                "level.tscn",
                [new PropertyUpdate("Holder", "target", Json("\"Box\""))],
                cancellation
            )
        )!;

        Assert.Equal("""{"nodePath":"Holder","property":"target","before":null,"after":"Box"}""", set["results"]![0]!.ToJsonString());
        (string header, string[] body) = Section(probe.Directory, "level.tscn", "Holder");
        Assert.Contains("node_paths=PackedStringArray(\"target\")", header, StringComparison.Ordinal);
        Assert.Equal("target = NodePath(\"../Box\")", body[^1]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesRefusesAnArrayOfNodesSayingWhy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "squad.gd"), "extends Node2D\n\n@export var targets: Array[Node2D]\n");
        string scene =
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://squad.gd\" id=\"1\"]\n\n"
            + "[node name=\"Squad\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"T\" type=\"Node2D\" parent=\".\"]\n";
        File.WriteAllText(Path.Combine(probe.Directory, "squad.tscn"), scene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SetNodePropertiesAsync(probe.Directory, "squad.tscn", [new PropertyUpdate(".", "targets", Json("[\"T\"]"))], cancellation)
        );

        Assert.Equal(
            "set_node_properties failed: Property 'targets' on '.': arrays of Object types (here Array[Node2D]) cannot be set from JSON.",
            refused.Message
        );
        Assert.Equal(scene, Read(probe.Directory, "squad.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsAnExportedTypedArray()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tally.gd"), "extends Node2D\n\n@export var counts: Array[int]\n");
        string scene =
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://tally.gd\" id=\"1\"]\n\n"
            + "[node name=\"Tally\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n";
        File.WriteAllText(Path.Combine(probe.Directory, "tally.tscn"), scene);

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(probe.Directory, "tally.tscn", [new PropertyUpdate(".", "counts", Json("[1, 2]"))], cancellation)
        )!;
        JsonNode read = await PropertiesAsync(probe.Directory, "tally.tscn", [new NodePropertyQuery(".", ["counts"])], cancellation);

        Assert.Equal("[1,2]", set["results"]![0]!["after"]!.ToJsonString());
        Assert.Equal("[1,2]", read["results"]![0]!["properties"]!["counts"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsAPackedArrayThatReadsBackAsAnArray()
    {
        // CodeEdit declares line_length_guidelines a PackedInt32Array and reads it back as an Array.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "editor.tscn"), "[gd_scene format=3]\n\n[node name=\"Editor\" type=\"CodeEdit\"]\n");

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(
                probe.Directory,
                "editor.tscn",
                [new PropertyUpdate(".", "line_length_guidelines", Json("[80, 100]"))],
                cancellation
            )
        )!;
        JsonNode read = await PropertiesAsync(probe.Directory, "editor.tscn", [new NodePropertyQuery(".", ["line_length_guidelines"])], cancellation);

        Assert.Equal("[80,100]", set["results"]![0]!["after"]!.ToJsonString());
        Assert.Equal("[80,100]", read["results"]![0]!["properties"]!["line_length_guidelines"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesIsAllOrNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        PropertyUpdate[] updates =
        [
            new("Box", "position", Json("""{"x": 1, "y": 1}""")),
            new("Box", "nope", Json("1")),
            new("Missing", "position", Json("""{"x": 1, "y": 1}""")),
            new("Box", "visible", Json("\"no\"")),
        ];

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SetNodePropertiesAsync(probe.Directory, "level.tscn", updates, cancellation)
        );

        Assert.Equal(
            "set_node_properties failed: Box has no property nope. res://level.tscn has no node Missing. "
                + "Property 'visible' on 'Box' is bool; \"no\" does not convert to it.",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesOverridesAnInheritedNode()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        await _tools.SetNodePropertiesAsync(
            probe.Directory,
            "elite.tscn",
            [new PropertyUpdate("Sprite", "position", Json("""{"x": 7, "y": 8}"""))],
            cancellation
        );

        (string header, string[] body) = Section(probe.Directory, "elite.tscn", "Sprite");
        Assert.Contains("parent=\".\"", header, StringComparison.Ordinal);
        Assert.Equal(["visible = false", "position = Vector2(7, 8)"], body);
        Assert.Equal(EnemyScene, Read(probe.Directory, "enemy.tscn"));
        JsonNode read = await PropertiesAsync(probe.Directory, "elite.tscn", [new NodePropertyQuery("Sprite", ["position"])], cancellation);
        AssertVector(read["results"]![0]!["properties"]!["position"]!, 7, 8);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesRefusesANodeInsideANonEditableInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SetNodePropertiesAsync(probe.Directory, "level.tscn", [new PropertyUpdate("Boss/Sprite", "visible", Json("false"))], cancellation)
        );

        Assert.Equal(
            "set_node_properties failed: Boss/Sprite is inside the instance of res://enemy.tscn at Boss; its changes would not be saved. "
                + "Edit res://enemy.tscn instead.",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodePropertiesReadsNodesAndResourcesAsPaths()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode read = await PropertiesAsync(
            probe.Directory,
            "wired.tscn",
            [new NodePropertyQuery("Holder", ["target"]), new NodePropertyQuery("Pic", ["texture"]), new NodePropertyQuery("Holder")],
            cancellation
        );

        JsonArray results = read["results"]!.AsArray();
        Assert.Equal(
            """{"nodePath":"Holder","type":"Node2D","script":"res://holder.gd","properties":{"target":"Box"}}""",
            results[0]!.ToJsonString()
        );
        Assert.Equal(
            """{"nodePath":"Pic","type":"Sprite2D","properties":{"texture":{"resource":"res://art/grad.tres","class":"GradientTexture2D"}}}""",
            results[1]!.ToJsonString()
        );
        JsonObject shown = results[2]!["properties"]!.AsObject();
        Assert.Equal("Box", shown["target"]!.GetValue<string>());
        Assert.True(shown.ContainsKey("position"));
        Assert.True(shown.ContainsKey("visible"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodePropertiesChangedOnlyReadsTheStoredValues()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        NodePropertyQuery[] queries =
        [
            new("Sprite", ChangedOnly: true),
            new("Shield", ChangedOnly: true),
            new("Sprite", ["visible", "z_index"], true),
        ];

        JsonNode read = await PropertiesAsync(probe.Directory, "elite.tscn", queries, cancellation);

        JsonArray results = read["results"]!.AsArray();
        JsonObject sprite = results[0]!["properties"]!.AsObject();
        Assert.Equal(["position", "visible"], sprite.Select(entry => entry.Key));
        AssertVector(sprite["position"]!, 1, 2);
        Assert.False(sprite["visible"]!.GetValue<bool>());
        JsonObject shield = results[1]!["properties"]!.AsObject();
        Assert.Equal(["position"], shield.Select(entry => entry.Key));
        AssertVector(shield["position"]!, 3, 4);
        Assert.Equal("""{"visible":false}""", results[2]!["properties"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodePropertiesReportsAMissingNodeInItsEntry()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode read = await PropertiesAsync(
            probe.Directory,
            "level.tscn",
            [new NodePropertyQuery("Box", ["visible"]), new NodePropertyQuery("Nope"), new NodePropertyQuery("Box", ["nope"])],
            cancellation
        );

        Assert.Equal(
            """[{"nodePath":"Box","type":"Node2D","properties":{"visible":true}},"""
                + """{"nodePath":"Nope","error":"res://level.tscn has no node Nope."},"""
                + """{"nodePath":"Box","error":"Box has no property nope."}]""",
            read["results"]!.ToJsonString()
        );
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    private const string EditableScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\n\n[node name=\"Boss\" parent=\".\" instance=ExtResource(\"1\")]\n\n[editable path=\"Boss\"]\n";

    private const string CrateScene =
        "[gd_scene format=3]\n\n[node name=\"Crate\" type=\"Node2D\"]\nposition = Vector2(4, 4)\n\n"
        + "[node name=\"Lid\" type=\"Node2D\" parent=\".\"]\n";

    private const string MarkerScript = "class_name MarkerNode\nextends Sprite2D\n\n@export var target: Node2D\n";

    private const string GreeterScript = "extends Node2D\n\nvar greeting := \"\"\n\n\nfunc _init() -> void:\n\tgreeting = \"hello\"\n";

    private const string GreeterScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://greeter.gd\" id=\"1\"]\n\n"
        + "[node name=\"Greeter\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n";

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesNullClearsATexture()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode set = JsonNode.Parse(
            await _tools.SetNodePropertiesAsync(probe.Directory, "wired.tscn", [new PropertyUpdate("Pic", "texture", Json("null"))], cancellation)
        )!;

        Assert.Null(set["results"]![0]!["after"]);
        Assert.Empty(Section(probe.Directory, "wired.tscn", "Pic").Body);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeRefusesASceneThatInstancesTheTargetThroughAnother()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "enemy.tscn", "wrapper.tscn", "Loop", cancellationToken: cancellation)
        );

        Assert.Equal("add_node failed: res://wrapper.tscn is or instances res://enemy.tscn, which cannot contain itself.", refused.Message);
        Assert.Equal(EnemyScene, Read(probe.Directory, "enemy.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeUnderAnEditableInstanceChildIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        string added = await _tools.AddNodeAsync(probe.Directory, "editable.tscn", "Node2D", "Hat", new AddNodeOptions("Boss/Sprite"), cancellation);

        Assert.Equal("""{"path":"Boss/Sprite/Hat","type":"Node2D"}""", added);
        Assert.Contains("parent=\"Boss/Sprite\"", Section(probe.Directory, "editable.tscn", "Hat").Header, StringComparison.Ordinal);
        Assert.Contains("[editable path=\"Boss\"]", Read(probe.Directory, "editable.tscn"), StringComparison.Ordinal);
        Assert.Contains("Boss/Sprite/Hat", await PathsAsync(probe.Directory, "editable.tscn", cancellation));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeRefusesNameAndOwnerInProperties()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException named = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(
                probe.Directory,
                "level.tscn",
                "Node2D",
                "Marker",
                new AddNodeOptions(Properties: new() { ["name"] = Json("\"Box\"") }),
                cancellation
            )
        );
        McpException owned = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(
                probe.Directory,
                "level.tscn",
                "Node2D",
                "Marker",
                new AddNodeOptions(Properties: new() { ["owner"] = Json("\"Box\"") }),
                cancellation
            )
        );

        Assert.Equal("add_node failed: Set the node's name with nodeName, not options.properties.", named.Message);
        Assert.Equal("add_node failed: add_node sets the node's owner itself; leave owner out of options.properties.", owned.Message);
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetsANodeInsideAnEditableInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        await _tools.SetNodePropertiesAsync(
            probe.Directory,
            "editable.tscn",
            [new PropertyUpdate("Boss/Sprite", "position", Json("""{"x": 9, "y": 9}"""))],
            cancellation
        );

        (string header, string[] body) = Section(probe.Directory, "editable.tscn", "Sprite");
        Assert.Contains("parent=\"Boss\"", header, StringComparison.Ordinal);
        Assert.Equal(["position = Vector2(9, 9)"], body);
        Assert.Contains("[editable path=\"Boss\"]", Read(probe.Directory, "editable.tscn"), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeRefusesANameWithReservedCharacters()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "Node2D", "Bad:Name", cancellationToken: cancellation)
        );

        Assert.Equal("add_node failed: nodeName Bad:Name holds a character no node name may hold: . : @ / \" %.", refused.Message);
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SetNodePropertiesSetPhaseFailureSavesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        PropertyUpdate[] updates = [new("Box", "position", Json("""{"x": 1, "y": 1}""")), new("Pic", "hframes", Json("0"))];

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SetNodePropertiesAsync(probe.Directory, "level.tscn", updates, cancellation)
        );

        Assert.Equal(
            "set_node_properties failed: Property 'hframes' on 'Pic' did not take the value: it read 1 after the set.\nGodot logged:\n"
                + "Amount of hframes cannot be smaller than 1. (scene/2d/sprite_2d.cpp:347)",
            refused.Message
        );
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeInstancesASceneWithoutBakingIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        await _tools.AddNodeAsync(probe.Directory, "level.tscn", "crate.tscn", "Crate", cancellationToken: cancellation);

        Assert.Empty(Section(probe.Directory, "level.tscn", "Crate").Body);
        Assert.DoesNotContain("parent=\"Crate\"", Read(probe.Directory, "level.tscn"), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeAddsAScriptClassNode()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "marker_node.gd"), MarkerScript);

        string added = await _tools.AddNodeAsync(probe.Directory, "level.tscn", "MarkerNode", "Marker", cancellationToken: cancellation);

        Assert.Equal("""{"path":"Marker","type":"MarkerNode"}""", added);
        (string header, string[] body) = Section(probe.Directory, "level.tscn", "Marker");
        Assert.Contains("type=\"Sprite2D\"", header, StringComparison.Ordinal);
        Assert.StartsWith("script = ExtResource(", Assert.Single(body), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeRefusesUnknownTypesAndAMissingParent()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException unknown = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "NoSuchNode", "Marker", cancellationToken: cancellation)
        );
        McpException notANode = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "Resource", "Marker", cancellationToken: cancellation)
        );
        McpException noParent = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AddNodeAsync(probe.Directory, "level.tscn", "Node2D", "Marker", new AddNodeOptions("Nope"), cancellation)
        );

        Assert.Equal("add_node failed: nodeType 'NoSuchNode' is not a Node class, a script class_name or a scene.", unknown.Message);
        Assert.Equal("add_node failed: nodeType 'Resource' is not a Node class, a script class_name or a scene.", notANode.Message);
        Assert.Equal("add_node failed: res://level.tscn has no node Nope.", noParent.Message);
        Assert.Equal(LevelScene, Read(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AddNodeSetsANodeExportAndAResourcePath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "marker_node.gd"), MarkerScript);
        AddNodeOptions options = new(Properties: new() { ["target"] = Json("\"Box\""), ["texture"] = Json("\"res://art/grad.tres\"") });

        await _tools.AddNodeAsync(probe.Directory, "level.tscn", "MarkerNode", "Marker", options, cancellation);

        (string header, string[] body) = Section(probe.Directory, "level.tscn", "Marker");
        Assert.Contains("node_paths=PackedStringArray(\"target\")", header, StringComparison.Ordinal);
        Assert.Contains(body, line => line.StartsWith("texture = ExtResource(", StringComparison.Ordinal));
        Assert.Contains("target = NodePath(\"../Box\")", body);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodePropertiesChangedOnlyReadsANodeInsideAnInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode read = await PropertiesAsync(probe.Directory, "level.tscn", [new NodePropertyQuery("Boss/Sprite", ChangedOnly: true)], cancellation);

        JsonObject sprite = read["results"]![0]!["properties"]!.AsObject();
        Assert.Equal(["position"], sprite.Select(entry => entry.Key));
        AssertVector(sprite["position"]!, 1, 2);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetNodePropertiesShowsAValueTheScriptInitSet()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode read = await PropertiesAsync(probe.Directory, "greeter.tscn", [new NodePropertyQuery(".", ["greeting"])], cancellation);

        Assert.Equal("""{"greeting":"hello"}""", read["results"]![0]!["properties"]!.ToJsonString());
    }

    private static string InstancingScene(string instanced, string rootName) =>
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\""
        + instanced
        + "\" id=\"1\"]\n\n[node name=\""
        + rootName
        + "\" type=\"Node2D\"]\n\n[node name=\"Inner\" parent=\".\" instance=ExtResource(\"1\")]\n";

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void AssertVector(JsonNode vector, double x, double y)
    {
        Assert.Equal(x, vector["x"]!.GetValue<double>());
        Assert.Equal(y, vector["y"]!.GetValue<double>());
    }

    private static string Read(string directory, string relative) => File.ReadAllText(Path.Combine(directory, relative));

    /// <summary>
    /// The header line of the node named name in a saved scene, and the property lines under it. A 4.7 save adds unique_id= to
    /// node headers, so a header is checked by the attributes it holds.
    /// </summary>
    private static (string Header, string[] Body) Section(string directory, string scene, string name)
    {
        string[] lines = File.ReadAllLines(Path.Combine(directory, scene));
        int start = Array.FindIndex(lines, line => line.StartsWith($"[node name=\"{name}\" ", StringComparison.Ordinal));
        Assert.True(start >= 0, $"{scene} has no node {name}.");
        return (lines[start], [.. lines.Skip(start + 1).TakeWhile(line => line.Length > 0 && !line.StartsWith('['))]);
    }

    private static void WriteScenes(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "art"));
        File.WriteAllText(Path.Combine(directory, "art", "grad.tres"), GradientTexture);
        File.WriteAllText(Path.Combine(directory, "holder.gd"), HolderScript);
        File.WriteAllText(Path.Combine(directory, "enemy.tscn"), EnemyScene);
        File.WriteAllText(Path.Combine(directory, "level.tscn"), LevelScene);
        File.WriteAllText(Path.Combine(directory, "elite.tscn"), EliteScene);
        File.WriteAllText(Path.Combine(directory, "wired.tscn"), WiredScene);
        File.WriteAllText(Path.Combine(directory, "editable.tscn"), EditableScene);
        File.WriteAllText(Path.Combine(directory, "wrapper.tscn"), InstancingScene("res://middle.tscn", "Wrapper"));
        File.WriteAllText(Path.Combine(directory, "middle.tscn"), InstancingScene("res://ENEMY.tscn", "Middle"));
        File.WriteAllText(Path.Combine(directory, "crate.tscn"), CrateScene);
        File.WriteAllText(Path.Combine(directory, "greeter.gd"), GreeterScript);
        File.WriteAllText(Path.Combine(directory, "greeter.tscn"), GreeterScene);
    }

    private async Task<JsonNode> PropertiesAsync(string projectDir, string scenePath, NodePropertyQuery[] nodes, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.GetNodePropertiesAsync(projectDir, scenePath, nodes, null, cancellation))!;

    private async Task<string[]> PathsAsync(string projectDir, string scenePath, CancellationToken cancellation)
    {
        JsonNode tree = JsonNode.Parse(await _tools.GetSceneFileTreeAsync(projectDir, scenePath, null, null, cancellation))!;
        return [.. tree["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>())];
    }

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
