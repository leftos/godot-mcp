using System.Globalization;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless scene edits against the real Godot: create_scene, save_scene, delete_nodes, attach_script, duplicate_node and
/// load_sprite on scenes the tests write into InputProbe and CsProbe copies (never into the tracked fixtures), read back with
/// get_scene_file_tree and as text. The copies are never imported (but for load_sprite's new PNG), so Godot's uid cache is
/// empty and every uid comes from the files themselves.
/// </summary>
public sealed class HeadlessSceneTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private const int BuildTestTimeoutMs = 240_000;
    private const string LevelUid = "uid://bqlevel00000a";
    private const string EnemyUid = "uid://bqenemy0000a";
    private const string OtherUid = "uid://bqother00000a";
    private const string ScriptUid = "uid://bqscript0000a";

    private const string ShadowTrailSource =
        "using Godot;\n\npublic partial class ShadowTrail : Node2D\n{\n    private float scale = 1.0f;\n\n    public float Drawn() => scale;\n}\n";

    private const string ShadowScaled =
        "[node name=\"Scaled\" type=\"Node2D\" parent=\".\"]\nscale = Vector2(2, 2)\nscript = ExtResource(\"1_shadow\")\n";

    private const string ShadowWarning = "ShadowTrail.scale (a C# field) hides Node2D.scale; the file stores the engine's value";

    private const string EnemyScene =
        "[gd_scene format=3 uid=\""
        + EnemyUid
        + "\"]\n\n[node name=\"Enemy\" type=\"CharacterBody2D\"]\n\n"
        + "[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\n";

    private const string LevelScene =
        "[gd_scene load_steps=2 format=3 uid=\""
        + LevelUid
        + "\"]\n\n"
        + "[ext_resource type=\"PackedScene\" uid=\""
        + EnemyUid
        + "\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\n\n[node name=\"Boss\" parent=\".\" instance=ExtResource(\"1\")]\n\n"
        + "[node name=\"Btn\" type=\"Button\" parent=\".\"]\n\n[node name=\"Box\" type=\"Node2D\" parent=\".\"]\n";

    private const string EliteScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Elite\" instance=ExtResource(\"1\")]\n\n[node name=\"Shield\" type=\"Node2D\" parent=\".\"]\n";

    private const string GradientUid = "uid://bqgradient0a";

    private const string CardScript = "extends Node2D\n\n@export var card: PackedScene\n";

    // A 1x1 PNG.
    private const string DotPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private const string SpriteScene =
        "[gd_scene format=3]\n\n[node name=\"Stage\" type=\"Node2D\"]\n\n[node name=\"Icon\" type=\"Sprite2D\" parent=\".\"]\n";

    // Group holds an instance with an overridden position and a child of its own, and a button connected to it; Btn, outside
    // Group, is connected to the instance too.
    private const string SquadScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" uid=\""
        + EnemyUid
        + "\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Squad\" type=\"Node2D\"]\n\n[node name=\"Group\" type=\"Node2D\" parent=\".\"]\n\n"
        + "[node name=\"Grunt\" parent=\"Group\" instance=ExtResource(\"1\")]\nposition = Vector2(7, 7)\n\n"
        + "[node name=\"UnderGrunt\" type=\"Node2D\" parent=\"Group/Grunt\"]\n\n[node name=\"B\" type=\"Button\" parent=\"Group\"]\n\n"
        + "[node name=\"Btn\" type=\"Button\" parent=\".\"]\n\n"
        + "[connection signal=\"pressed\" from=\"Group/B\" to=\"Group/Grunt\" method=\"hide\"]\n"
        + "[connection signal=\"pressed\" from=\"Btn\" to=\"Group/Grunt\" method=\"hide\"]\n";

    // combat.tscn as Godot 4.7 writes it, a unique_id on every node: Combat holding Layer, Ties with a Tie of its own, and Hud.
    private const string CombatScene =
        "[gd_scene format=3]\n\n[node name=\"Combat\" type=\"Node2D\" unique_id=100]\n\n"
        + "[node name=\"Layer\" type=\"CanvasLayer\" parent=\".\" unique_id=150]\n\n"
        + "[node name=\"Ties\" type=\"Node2D\" parent=\".\" unique_id=442001752]\n\n"
        + "[node name=\"Tie\" type=\"Line2D\" parent=\"Ties\" unique_id=200]\n\n"
        + "[node name=\"Hud\" type=\"Node2D\" parent=\".\" unique_id=300]\n";

    // stage.tscn, in parts: Stage holding A, B and C.
    private const string StageHeader = "[gd_scene format=3 uid=\"uid://bqstage00000a\"]\n\n";
    private const string StageRoot = "[node name=\"Stage\" type=\"Node2D\"]\n\n";
    private const string StageA = "[node name=\"A\" type=\"Node2D\" parent=\".\"]\nposition = Vector2(1, 0)\n";
    private const string StageB = "[node name=\"B\" type=\"Node2D\" parent=\".\"]\n";
    private const string StageC = "[node name=\"C\" type=\"Node2D\" parent=\".\"]\nposition = Vector2(3, 0)\n";

    // screen.tscn: a full-rect Control holding a ColorRect in position mode, which stores no layout_mode (Godot's pack
    // compares a ColorRect's layout_mode with its class default, 3, and would store the 0 it reads).
    private const string ScreenBackdrop =
        "[node name=\"Backdrop\" type=\"ColorRect\" parent=\".\"]\nanchor_right = 1.0\nanchor_bottom = 1.0\nmouse_filter = 2\n";
    private const string ScreenScene =
        "[gd_scene format=3]\n\n[node name=\"Screen\" type=\"Control\"]\nlayout_mode = 3\nanchors_preset = 15\nanchor_right = 1.0\n"
        + "anchor_bottom = 1.0\ngrow_horizontal = 2\ngrow_vertical = 2\n\n"
        + ScreenBackdrop;

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessSceneTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CreateSceneWritesARootNamedAfterTheFileWithAUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());

        JsonNode created = JsonNode.Parse(
            await _tools.CreateSceneAsync(probe.Directory, "levels/player_ship.tscn", cancellationToken: cancellation)
        )!;

        string uid = created["uid"]!.GetValue<string>();
        Assert.Equal("res://levels/player_ship.tscn", created["scenePath"]!.GetValue<string>());
        Assert.Equal("""{"name":"PlayerShip","type":"Node2D"}""", created["root"]!.ToJsonString());
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        Assert.Equal($"[gd_scene format=3 uid=\"{uid}\"]", FirstLine(probe.Directory, "levels/player_ship.tscn"));
        JsonNode tree = await TreeAsync(probe.Directory, "levels/player_ship.tscn", cancellation);
        Assert.Equal("""[{"path":".","name":"PlayerShip","type":"Node2D","childCount":0}]""", tree["nodes"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CreateSceneRefusesAnExistingFileWithoutOverwrite()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string before = File.ReadAllText(Path.Combine(probe.Directory, "main.tscn"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CreateSceneAsync(probe.Directory, "main.tscn", cancellationToken: cancellation)
        );

        Assert.Equal("res://main.tscn already exists; pass options.overwrite: true to replace it.", refused.Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(probe.Directory, "main.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CreateSceneWithOverwriteReplacesIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode created = JsonNode.Parse(
            await _tools.CreateSceneAsync(
                probe.Directory,
                "res://level.tscn",
                "Control",
                "Menu",
                new SceneWriteOptions(Overwrite: true),
                cancellation
            )
        )!;

        Assert.Equal("""{"name":"Menu","type":"Control"}""", created["root"]!.ToJsonString());
        // Replacing a file keeps its uid, so what referred to the old scene finds the new one.
        Assert.Equal(LevelUid, created["uid"]!.GetValue<string>());
        JsonNode tree = await TreeAsync(probe.Directory, "level.tscn", cancellation);
        Assert.Equal("""[{"path":".","name":"Menu","type":"Control","childCount":0}]""", tree["nodes"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task CreateSceneRefusesANonNodeType()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());

        foreach (string type in new[] { "Resource", "NoSuchType" })
        {
            McpException refused = await Assert.ThrowsAsync<McpException>(() =>
                _tools.CreateSceneAsync(probe.Directory, "odd.tscn", type, cancellationToken: cancellation)
            );

            Assert.Equal($"create_scene failed: rootType '{type}' is not a Node class or a script class_name.", refused.Message);
        }

        Assert.False(File.Exists(Path.Combine(probe.Directory, "odd.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneKeepsTheHeaderUidAndExtResourceUids()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "level.tscn", cancellationToken: cancellation))!;

        Assert.Equal("res://level.tscn", saved["scenePath"]!.GetValue<string>());
        Assert.Equal("res://level.tscn", saved["savedTo"]!.GetValue<string>());
        Assert.Equal(LevelUid, saved["uid"]!.GetValue<string>());
        Assert.Equal($"[gd_scene format=3 uid=\"{LevelUid}\"]", FirstLine(probe.Directory, "level.tscn"));
        AssertExtUid(probe.Directory, "level.tscn", EnemyUid, "res://enemy.tscn");
        Assert.Equal([".", "Boss", "Boss/Sprite", "Btn", "Box"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneKeepsGodotsFullForm()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "level.tscn", cancellationToken: cancellation))!;

        Assert.Null(saved["warning"]);
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "level.tscn"));
        Assert.DoesNotContain(lines, line => line.Contains("load_steps=", StringComparison.Ordinal));
        string[] nodes = [.. lines.Where(line => line.StartsWith("[node ", StringComparison.Ordinal))];
        Assert.Equal(4, nodes.Length);
        Assert.All(nodes, node => Assert.Contains(" unique_id=", node, StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneKeepsAnExtResourceUidTheSourceCarried()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        // No s.gd.uid beside the script: the uid is known only from the scene's own ext_resource line.
        File.WriteAllText(Path.Combine(probe.Directory, "s.gd"), "extends Node2D\n");
        File.WriteAllText(
            Path.Combine(probe.Directory, "scripted.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" uid=\""
                + ScriptUid
                + "\" path=\"res://s.gd\" id=\"1\"]\n\n"
                + "[node name=\"Scripted\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n"
        );

        await _tools.SaveSceneAsync(probe.Directory, "scripted.tscn", cancellationToken: cancellation);

        AssertExtUid(probe.Directory, "scripted.tscn", ScriptUid, "res://s.gd");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneKeepsAnInheritedSceneInherited()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "elite.tscn", cancellationToken: cancellation))!;

        string text = File.ReadAllText(Path.Combine(probe.Directory, "elite.tscn"));
        Assert.Contains("[node name=\"Elite\" ", text, StringComparison.Ordinal);
        Assert.Contains("instance=ExtResource(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Sprite\"", text, StringComparison.Ordinal);
        // The source had neither uid: the scene gets a new one, and the base's is read from enemy.tscn's header.
        Assert.Equal($"[gd_scene format=3 uid=\"{saved["uid"]!.GetValue<string>()}\"]", FirstLine(probe.Directory, "elite.tscn"));
        AssertExtUid(probe.Directory, "elite.tscn", EnemyUid, "res://enemy.tscn");
        Assert.Equal([".", "Sprite", "Shield"], Paths(await TreeAsync(probe.Directory, "elite.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneAsNewPathGetsAFreshUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "level.tscn", "copies/level_copy.tscn", null, cancellation))!;

        string uid = saved["uid"]!.GetValue<string>();
        Assert.Equal("res://copies/level_copy.tscn", saved["savedTo"]!.GetValue<string>());
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        Assert.NotEqual(LevelUid, uid);
        Assert.Equal($"[gd_scene format=3 uid=\"{uid}\"]", FirstLine(probe.Directory, "copies/level_copy.tscn"));
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
        Assert.Equal([".", "Boss", "Boss/Sprite", "Btn", "Box"], Paths(await TreeAsync(probe.Directory, "copies/level_copy.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneAsOverAnExistingFileTakesItsUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(
            Path.Combine(probe.Directory, "other.tscn"),
            "[gd_scene format=3 uid=\"" + OtherUid + "\"]\n\n[node name=\"Other\" type=\"Node\"]\n"
        );

        JsonNode saved = JsonNode.Parse(
            await _tools.SaveSceneAsync(probe.Directory, "level.tscn", "other.tscn", new SceneWriteOptions(Overwrite: true), cancellation)
        )!;

        Assert.Equal(OtherUid, saved["uid"]!.GetValue<string>());
        Assert.Equal($"[gd_scene format=3 uid=\"{OtherUid}\"]", FirstLine(probe.Directory, "other.tscn"));
        Assert.Equal([".", "Boss", "Boss/Sprite", "Btn", "Box"], Paths(await TreeAsync(probe.Directory, "other.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneAsWritesANodeAsTheSourceStoresIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "screen.tscn"), ScreenScene);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "screen.tscn", "screen_copy.tscn", null, cancellation))!;

        // The copy's header is its own (a new file gets a new uid); every section after it is the source's text.
        string uid = saved["uid"]!.GetValue<string>();
        string copy = File.ReadAllText(Path.Combine(probe.Directory, "screen_copy.tscn"));
        Assert.Null(saved["warning"]);
        Assert.Equal($"[gd_scene format=3 uid=\"{uid}\"]", FirstLine(probe.Directory, "screen_copy.tscn"));
        Assert.Equal(ScreenScene[ScreenScene.IndexOf("[node", StringComparison.Ordinal)..], copy[copy.IndexOf("[node", StringComparison.Ordinal)..]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneInPlaceAddsNoLayoutModeTheSourceLacks()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "screen.tscn"), ScreenScene);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(probe.Directory, "screen.tscn", cancellationToken: cancellation))!;

        Assert.Null(saved["warning"]);
        Assert.Equal(["anchor_bottom = 1.0", "anchor_right = 1.0", "mouse_filter = 2"], BackdropProperties(probe.Directory, "screen.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneKeepsALayoutModeTheSourceStores()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string storedBackdrop = ScreenBackdrop.Replace("]\n", "]\nlayout_mode = 0\n", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(probe.Directory, "screen.tscn"),
            ScreenScene.Replace(ScreenBackdrop, storedBackdrop, StringComparison.Ordinal)
        );

        await _tools.SaveSceneAsync(probe.Directory, "screen.tscn", cancellationToken: cancellation);

        Assert.Contains("layout_mode = 0", BackdropProperties(probe.Directory, "screen.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task SaveSceneAsKeepsTheSourceExtResourceIds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "s.gd"), "extends Node2D\n");
        File.WriteAllText(
            Path.Combine(probe.Directory, "scripted.tscn"),
            "[gd_scene format=3]\n\n[ext_resource type=\"Script\" uid=\""
                + ScriptUid
                + "\" path=\"res://s.gd\" id=\"2_lye0u\"]\n\n"
                + "[node name=\"Scripted\" type=\"Node2D\"]\nscript = ExtResource(\"2_lye0u\")\n"
        );

        await _tools.SaveSceneAsync(probe.Directory, "scripted.tscn", "scripted_copy.tscn", null, cancellation);

        string copy = File.ReadAllText(Path.Combine(probe.Directory, "scripted_copy.tscn"));
        Assert.Contains($"[ext_resource type=\"Script\" uid=\"{ScriptUid}\" path=\"res://s.gd\" id=\"2_lye0u\"]", copy, StringComparison.Ordinal);
        Assert.Contains("script = ExtResource(\"2_lye0u\")", copy, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesRemovesThemAndSaves()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode deleted = JsonNode.Parse(await _tools.DeleteNodesAsync(probe.Directory, "level.tscn", ["Btn", "./Box"], cancellation))!;

        Assert.Equal("""["Btn","./Box"]""", deleted["deleted"]!.ToJsonString());
        Assert.Equal([".", "Boss", "Boss/Sprite"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
        Assert.Equal($"[gd_scene load_steps=2 format=3 uid=\"{LevelUid}\"]", FirstLine(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesDeletesAnAncestorAndItsDescendantInOneList()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(
            Path.Combine(probe.Directory, "nest.tscn"),
            "[gd_scene format=3]\n\n[node name=\"Nest\" type=\"Node2D\"]\n\n[node name=\"Group\" type=\"Node2D\" parent=\".\"]\n\n"
                + "[node name=\"Leaf\" type=\"Node2D\" parent=\"Group\"]\n\n[node name=\"Keep\" type=\"Node2D\" parent=\".\"]\n"
        );

        JsonNode deleted = JsonNode.Parse(await _tools.DeleteNodesAsync(probe.Directory, "nest.tscn", ["Group", "Group/Leaf"], cancellation))!;

        Assert.Equal("""["Group","Group/Leaf"]""", deleted["deleted"]!.ToJsonString());
        string text = File.ReadAllText(Path.Combine(probe.Directory, "nest.tscn"));
        Assert.DoesNotContain("name=\"Group\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Leaf\"", text, StringComparison.Ordinal);
        Assert.Equal([".", "Keep"], Paths(await TreeAsync(probe.Directory, "nest.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesRefusesTheRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DeleteNodesAsync(probe.Directory, "level.tscn", ["."], cancellation)
        );

        Assert.Equal("delete_nodes failed: The scene root cannot be deleted; create a new scene instead.", refused.Message);
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesRefusesANodeInsideAnInstanceAndSavesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DeleteNodesAsync(probe.Directory, "level.tscn", ["Btn", "Boss/Sprite", "Ghost"], cancellation)
        );

        Assert.Equal(
            "delete_nodes failed: Boss/Sprite is inside the instance of res://enemy.tscn at Boss; its changes would not be saved. "
                + "Edit res://enemy.tscn instead. res://level.tscn has no node Ghost.",
            refused.Message
        );
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesRefusesANodeInheritedFromTheBaseScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DeleteNodesAsync(probe.Directory, "elite.tscn", ["Sprite"], cancellation)
        );

        Assert.Equal(
            "delete_nodes failed: Sprite is inherited from res://enemy.tscn; its deletion would not be saved. Edit res://enemy.tscn instead.",
            refused.Message
        );
        Assert.Equal(EliteScene, File.ReadAllText(Path.Combine(probe.Directory, "elite.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DeleteNodesCanDeleteAnInstanceRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        await _tools.DeleteNodesAsync(probe.Directory, "level.tscn", ["Boss"], cancellation);

        Assert.Equal([".", "Btn", "Box"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
        Assert.DoesNotContain("name=\"Boss\"", File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")), StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveRefusesACSharpSceneWhenTheBuildFailed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        string scene = Path.Combine(csProbe.Directory, "main.tscn");
        string before = File.ReadAllText(scene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(csProbe.Directory, "main.tscn", cancellationToken: cancellation)
        );

        // The refusal quotes the build's configuration and its compiler errors; the missing class is what Godot logs while the scene loads.
        Assert.StartsWith(
            "save_scene failed: res://main.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("CsProbeNode.cs:10: CS1002 ; expected\nGodot logged:\n", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(scene));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task BatchAttachScriptRefusalQuotesTheCompilerErrors()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        const string plainScene = "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node\"]\n";
        string scene = Path.Combine(csProbe.Directory, "plain.tscn");
        File.WriteAllText(scene, plainScene);
        SceneBatchStep attach = new("attach_script", new JsonObject { ["nodePath"] = ".", ["scriptPath"] = "CsProbeNode.cs" });

        JsonObject batch = JsonNode
            .Parse(await _tools.BatchSceneOperationsAsync(csProbe.Directory, "plain.tscn", [attach], cancellation))!
            .AsObject();

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        string error = batch["steps"]![0]!["error"]!.GetValue<string>();
        Assert.StartsWith(
            "res://plain.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n",
            error,
            StringComparison.Ordinal
        );
        Assert.Contains("CsProbeNode.cs:10: CS1002 ; expected", error, StringComparison.Ordinal);
        Assert.Equal(plainScene, File.ReadAllText(scene));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task CreateSceneUnderARedBuildReportsTheBuildNotTheAutoload()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = RedBuildWithAutoloads("Probe=\"*res://CsProbeNode.cs\"");

        JsonObject created = JsonNode
            .Parse(await _tools.CreateSceneAsync(csProbe.Directory, "fresh.tscn", cancellationToken: cancellation))!
            .AsObject();

        Assert.Equal("failed", created["csharp"]?["build"]?.GetValue<string>());
        Assert.Contains("CsProbeNode.cs:10: CS1002 ; expected", created["csharp"]!["errors"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(created["errors"]);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SceneFileTreeUnderARedBuildReportsTheBuildNotItsSymptoms()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = RedBuildWithAutoloads("Probe=\"*res://CsProbeNode.cs\"");

        JsonNode tree = await TreeAsync(csProbe.Directory, "main.tscn", cancellation);

        Assert.Equal("failed", tree["csharp"]?["build"]?.GetValue<string>());
        Assert.Contains("CsProbeNode.cs:10: CS1002 ; expected", tree["csharp"]!["errors"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(tree["errors"]);
        Assert.Contains(".", Paths(tree));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AGDScriptAutoloadThatIsNotANodeStillReportsUnderARedBuild()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = RedBuildWithAutoloads("Probe=\"*res://CsProbeNode.cs\"\nPlain=\"*res://not_node.gd\"");
        File.WriteAllText(Path.Combine(csProbe.Directory, "not_node.gd"), "extends RefCounted\n");

        JsonObject created = JsonNode
            .Parse(await _tools.CreateSceneAsync(csProbe.Directory, "fresh.tscn", cancellationToken: cancellation))!
            .AsObject();

        Assert.Equal("failed", created["csharp"]?["build"]?.GetValue<string>());
        JsonNode error = Assert.Single(created["errors"]!.AsArray())!;
        Assert.Contains("res://not_node.gd", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveSceneRefusesWhenAnInstancedSceneUsesCSharpAndTheBuildFailed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        string scene = Path.Combine(csProbe.Directory, "holder.tscn");
        File.WriteAllText(
            scene,
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://main.tscn\" id=\"1_main\"]\n\n"
                + "[node name=\"Holder\" type=\"Node\"]\n\n[node name=\"Probe\" parent=\".\" instance=ExtResource(\"1_main\")]\nSpeed = 5\n"
        );
        string before = File.ReadAllText(scene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(csProbe.Directory, "holder.tscn", cancellationToken: cancellation)
        );

        // The C# script's missing class is what Godot logs while the instanced scene loads.
        Assert.StartsWith(
            "save_scene failed: res://holder.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("CsProbeNode.cs:10: CS1002 ; expected\nGodot logged:\n", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(scene));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptSetsAndReturnsThePrevious()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), "extends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "b.gd"), "extends Node2D\n");
        File.WriteAllText(
            Path.Combine(probe.Directory, "scripted.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://a.gd\" id=\"1\"]\n\n"
                + "[node name=\"Scripted\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n"
        );

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "scripted.tscn", ".", "res://b.gd", cancellation))!;

        Assert.Equal(".", attached["path"]!.GetValue<string>());
        // Neither script had a .uid file: each gets the one the editor's scan would write.
        Assert.Equal(ScriptFacts(probe.Directory, "b.gd"), attached["script"]!.ToJsonString());
        Assert.Equal(ScriptFacts(probe.Directory, "a.gd"), attached["previous"]!.ToJsonString());
        Assert.Equal("""["res://b.gd.uid","res://a.gd.uid"]""", attached["uidFilesWritten"]!.ToJsonString());
        string text = File.ReadAllText(Path.Combine(probe.Directory, "scripted.tscn"));
        Assert.Contains("path=\"res://b.gd\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("res://a.gd", text, StringComparison.Ordinal);
        JsonNode root = (await TreeAsync(probe.Directory, "scripted.tscn", cancellation))["nodes"]![0]!;
        Assert.Equal("res://b.gd", root["script"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptGivesAScriptWithNoUidFileTheUidTheEditorWould()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "c.gd"), "extends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "plain2d.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node2D\"]\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "plain2d.tscn", ".", "c.gd", cancellation))!;

        string uidFile = Path.Combine(probe.Directory, "c.gd.uid");
        Assert.True(File.Exists(uidFile));
        string uid = File.ReadAllText(uidFile).Trim();
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        Assert.Equal(uid, attached["script"]!["uid"]!.GetValue<string>());
        AssertExtUid(probe.Directory, "plain2d.tscn", uid, "res://c.gd");
        Assert.Equal("""["res://c.gd.uid"]""", attached["uidFilesWritten"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptLeavesAUidAPlainRunKnows()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        // Imported first so .godot/uid_cache.bin exists; c.gd is written after it, so it has no .uid.
        await RunGodotAsync(probe.Directory, ["--import"], cancellation);
        File.WriteAllText(Path.Combine(probe.Directory, "c.gd"), "extends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "plain2d.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node2D\"]\n");

        await _tools.AttachScriptAsync(probe.Directory, "plain2d.tscn", ".", "c.gd", cancellation);

        // Started outside the server, as a game repo's own scripts start Godot: no prep, no import.
        string log = await RunGodotAsync(probe.Directory, ["--quit-after", "3", "res://plain2d.tscn"], cancellation);
        Assert.DoesNotContain("invalid UID", log, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASceneCreatedHereIsKnownByUidToAPlainRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        await RunGodotAsync(probe.Directory, ["--import"], cancellation);
        await _tools.CreateSceneAsync(probe.Directory, "child.tscn", cancellationToken: cancellation);
        await _tools.CreateSceneAsync(probe.Directory, "parent.tscn", cancellationToken: cancellation);
        await _tools.AddNodeAsync(probe.Directory, "parent.tscn", "res://child.tscn", "Child", null, cancellation);

        string log = await RunGodotAsync(probe.Directory, ["--quit-after", "3", "res://parent.tscn"], cancellation);
        Assert.DoesNotContain("invalid UID", log, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAppendKeepsTheUidsTheImportRecorded()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        const string importedUid = "uid://bimported1";
        File.WriteAllText(
            Path.Combine(probe.Directory, "imported.tscn"),
            $"[gd_scene format=3 uid=\"{importedUid}\"]\n\n[node name=\"Imported\" type=\"Node2D\"]\n"
        );
        await RunGodotAsync(probe.Directory, ["--import"], cancellation);
        string cache = Path.Combine(probe.Directory, ".godot", "uid_cache.bin");
        List<(long Id, string Path)> before = ReadUidCache(cache);

        await _tools.CreateSceneAsync(probe.Directory, "fresh.tscn", cancellationToken: cancellation);

        List<(long Id, string Path)> after = ReadUidCache(cache);
        string freshUid = HeaderUid(Path.Combine(probe.Directory, "fresh.tscn"));
        Assert.Contains((PrepScan.TextToId(importedUid)!.Value, "res://imported.tscn"), after);
        Assert.Contains((PrepScan.TextToId(freshUid)!.Value, "res://fresh.tscn"), after);
        Assert.Equal(before, after.Take(before.Count));
        File.WriteAllText(
            Path.Combine(probe.Directory, "holder.tscn"),
            $"[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"PackedScene\" uid=\"{importedUid}\" path=\"res://imported.tscn\" id=\"1\"]\n"
                + $"[ext_resource type=\"PackedScene\" uid=\"{freshUid}\" path=\"res://fresh.tscn\" id=\"2\"]\n\n"
                + "[node name=\"Holder\" type=\"Node2D\"]\n\n[node name=\"Imported\" parent=\".\" instance=ExtResource(\"1\")]\n\n"
                + "[node name=\"Fresh\" parent=\".\" instance=ExtResource(\"2\")]\n"
        );

        string log = await RunGodotAsync(probe.Directory, ["--quit-after", "3", "res://holder.tscn"], cancellation);
        Assert.DoesNotContain("invalid UID", log, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptInAProjectNeverImportedWritesNoUidCache()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "c.gd"), "extends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "plain2d.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node2D\"]\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "plain2d.tscn", ".", "c.gd", cancellation))!;

        Assert.StartsWith("uid://", attached["script"]!["uid"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(probe.Directory, ".godot", "uid_cache.bin")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptKeepsTheExportsTheNewScriptDeclares()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), CardScript);
        File.WriteAllText(Path.Combine(probe.Directory, "b.gd"), CardScript);
        WriteCardedScene(probe.Directory, "res://a.gd");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "carded.tscn", ".", "b.gd", cancellation))!;

        AssertCardKept(probe.Directory, attached, "res://b.gd");
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AttachScriptKeepsTheCSharpExportsTheNewScriptDeclares()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        csProbe.WriteSource("CardHolder.cs", CardHolderSource("CardHolder"));
        csProbe.WriteSource("OtherCardHolder.cs", CardHolderSource("OtherCardHolder"));
        File.WriteAllText(Path.Combine(csProbe.Directory, "enemy.tscn"), EnemyScene);
        WriteCardedScene(csProbe.Directory, "res://CardHolder.cs");

        JsonNode attached = JsonNode.Parse(
            await _tools.AttachScriptAsync(csProbe.Directory, "carded.tscn", ".", "OtherCardHolder.cs", cancellation)
        )!;

        AssertCardKept(csProbe.Directory, attached, "res://OtherCardHolder.cs");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptDropsAValueWhoseTypeTheNewScriptChanges()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), CardScript);
        File.WriteAllText(Path.Combine(probe.Directory, "counted.gd"), "extends Node2D\n\n@export var card: int\n");
        WriteCardedScene(probe.Directory, "res://a.gd");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "carded.tscn", ".", "counted.gd", cancellation))!;

        string text = File.ReadAllText(Path.Combine(probe.Directory, "carded.tscn"));
        Assert.Contains("path=\"res://counted.gd\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("card =", text, StringComparison.Ordinal);
        Assert.Equal("[]", attached["kept"]!.ToJsonString());
        Assert.Equal("""["card"]""", attached["dropped"]!.ToJsonString());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptRefusesAScriptForAnotherBaseType()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "menu.gd"), "extends Control\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AttachScriptAsync(probe.Directory, "level.tscn", "Box", "menu.gd", cancellation)
        );

        Assert.Equal("attach_script failed: res://menu.gd extends Control, so it cannot be attached to Box, a Node2D.", refused.Message);
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptRefusesANodeInsideAnInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), "extends Node2D\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AttachScriptAsync(probe.Directory, "level.tscn", "Boss/Sprite", "a.gd", cancellation)
        );

        Assert.Equal(
            "attach_script failed: Boss/Sprite is inside the instance of res://enemy.tscn at Boss; its changes would not be saved. "
                + "Edit res://enemy.tscn instead.",
            refused.Message
        );
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptToAnInheritedNodeSavesAsAnOverride()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), "extends Node2D\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "elite.tscn", "Sprite", "a.gd", cancellation))!;

        Assert.Null(attached["previous"]);
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "elite.tscn"));
        int sprite = Array.FindIndex(lines, line => line.StartsWith("[node name=\"Sprite\" parent=\".\"", StringComparison.Ordinal));
        Assert.True(sprite >= 0, string.Join('\n', lines));
        // An override names no type: the node comes from the base scene.
        Assert.DoesNotContain("type=", lines[sprite], StringComparison.Ordinal);
        Assert.StartsWith("script = ExtResource(", lines[sprite + 1], StringComparison.Ordinal);
        JsonNode tree = await TreeAsync(probe.Directory, "elite.tscn", cancellation);
        Assert.Equal([".", "Sprite", "Shield"], Paths(tree));
        Assert.Equal("res://a.gd", tree["nodes"]![1]!["script"]!.GetValue<string>());
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AttachScriptRefusesACSharpScriptWhenTheBuildFailed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        const string plainScene = "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node\"]\n";
        string scene = Path.Combine(csProbe.Directory, "plain.tscn");
        File.WriteAllText(scene, plainScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AttachScriptAsync(csProbe.Directory, "plain.tscn", ".", "CsProbeNode.cs", cancellation)
        );

        Assert.StartsWith(
            "attach_script failed: res://plain.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith("CsProbeNode.cs:10: CS1002 ; expected", refused.Message, StringComparison.Ordinal);
        Assert.Equal(plainScene, File.ReadAllText(scene));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeCopiesAnInstanceAsAnInstance()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "squad.tscn"), SquadScene);

        JsonNode copied = JsonNode.Parse(await _tools.DuplicateNodeAsync(probe.Directory, "squad.tscn", "Group", cancellationToken: cancellation))!;

        Assert.Equal("Group", copied["originalPath"]!.GetValue<string>());
        Assert.Equal("Group2", copied["newPath"]!.GetValue<string>());
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "squad.tscn"));
        string grunt = Assert.Single(lines, line => line.StartsWith("[node name=\"Grunt\" parent=\"Group2\"", StringComparison.Ordinal));
        Assert.Contains("instance=ExtResource(", grunt, StringComparison.Ordinal);
        // The instance's own nodes stay in enemy.tscn: none is baked into the copy.
        Assert.DoesNotContain(lines, line => line.Contains("name=\"Sprite\"", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(line => line == "position = Vector2(7, 7)"));
        Assert.Equal(
            [
                "[connection signal=\"pressed\" from=\"Btn\" to=\"Group/Grunt\" method=\"hide\"]",
                "[connection signal=\"pressed\" from=\"Group/B\" to=\"Group/Grunt\" method=\"hide\"]",
                "[connection signal=\"pressed\" from=\"Group2/B\" to=\"Group2/Grunt\" method=\"hide\"]",
            ],
            lines.Where(line => line.StartsWith("[connection ", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            [
                ".",
                "Group",
                "Group/Grunt",
                "Group/Grunt/Sprite",
                "Group/Grunt/UnderGrunt",
                "Group/B",
                "Group2",
                "Group2/Grunt",
                "Group2/Grunt/Sprite",
                "Group2/Grunt/UnderGrunt",
                "Group2/B",
                "Btn",
            ],
            Paths(await TreeAsync(probe.Directory, "squad.tscn", cancellation))
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeGivesTheCopyFreshUniqueIds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "combat.tscn"), CombatScene);

        // Under Layer the copy comes before Ties, so the save meets the copy's nodes first.
        await _tools.DuplicateNodeAsync(probe.Directory, "combat.tscn", "Ties", "Lines", new DuplicateNodeOptions("Layer"), cancellation);

        AssertCopiesHaveFreshIds(probe.Directory, ["Layer/Lines", "Layer/Lines/Tie"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeTwiceInOneBatchGivesEachCopyFreshUniqueIds()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "combat.tscn"), CombatScene);
        SceneBatchStep[] steps =
        [
            new("duplicate_node", new JsonObject { ["nodePath"] = "Ties" }),
            new(
                "duplicate_node",
                new JsonObject
                {
                    ["nodePath"] = "Ties",
                    ["options"] = new JsonObject { ["parent"] = "Layer" },
                }
            ),
        ];

        await _tools.BatchSceneOperationsAsync(probe.Directory, "combat.tscn", steps, cancellation);

        AssertCopiesHaveFreshIds(probe.Directory, ["Ties2", "Ties2/Tie", "Layer/Ties", "Layer/Ties/Tie"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeDefaultNameCountsUp()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode first = JsonNode.Parse(await _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Btn", cancellationToken: cancellation))!;
        JsonNode second = JsonNode.Parse(await _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Btn2", cancellationToken: cancellation))!;

        Assert.Equal("Btn2", first["newPath"]!.GetValue<string>());
        Assert.Equal("Btn3", second["newPath"]!.GetValue<string>());
        Assert.Equal([".", "Boss", "Boss/Sprite", "Btn", "Btn2", "Btn3", "Box"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
        Assert.Equal($"[gd_scene load_steps=2 format=3 uid=\"{LevelUid}\"]", FirstLine(probe.Directory, "level.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeRefusesACollidingName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Btn", "Box", cancellationToken: cancellation)
        );

        Assert.Equal("duplicate_node failed: Level already has a child named Box.", refused.Message);
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeUnderAnotherParent()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DuplicateNodeAsync(
                probe.Directory,
                "level.tscn",
                "Btn",
                options: new DuplicateNodeOptions("Boss/Sprite"),
                cancellationToken: cancellation
            )
        );
        // An instance's root is a valid parent: the copy is saved in this scene, under it.
        JsonNode copied = JsonNode.Parse(
            await _tools.DuplicateNodeAsync(
                probe.Directory,
                "level.tscn",
                "Btn",
                options: new DuplicateNodeOptions("Boss"),
                cancellationToken: cancellation
            )
        )!;

        Assert.StartsWith(
            "duplicate_node failed: Boss/Sprite is inside the instance of res://enemy.tscn at Boss;",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal("Boss/Btn", copied["newPath"]!.GetValue<string>());
        Assert.Equal([".", "Boss", "Boss/Sprite", "Boss/Btn", "Btn", "Box"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeOfAnInheritedNode()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(
            Path.Combine(probe.Directory, "base.tscn"),
            "[gd_scene format=3]\n\n[node name=\"Body\" type=\"Node2D\"]\n\n[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\n"
                + "offset = Vector2(3, 4)\n\n[node name=\"Glow\" type=\"Node2D\" parent=\"Sprite\"]\n\n"
                + "[node name=\"Tail\" type=\"Node2D\" parent=\".\"]\n"
        );
        File.WriteAllText(
            Path.Combine(probe.Directory, "derived.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://base.tscn\" id=\"1\"]\n\n"
                + "[node name=\"Derived\" instance=ExtResource(\"1\")]\n"
        );

        JsonNode copied = JsonNode.Parse(
            await _tools.DuplicateNodeAsync(probe.Directory, "derived.tscn", "Sprite", cancellationToken: cancellation)
        )!;

        Assert.Equal("Sprite2", copied["newPath"]!.GetValue<string>());
        string text = File.ReadAllText(Path.Combine(probe.Directory, "derived.tscn"));
        string copy = Assert.Single(
            text.Split('\n'),
            line => line.StartsWith("[node name=\"Sprite2\" type=\"Sprite2D\" parent=\".\"", StringComparison.Ordinal)
        );
        // A node added to an inherited scene is saved with its index, which puts it back after Sprite when the scene is
        // instantiated (4.7.2 packed_scene.cpp L821-831, L545-546); get_scene_file_tree lists it after the base's nodes.
        Assert.Contains(" index=\"1\"", copy, StringComparison.Ordinal);
        Assert.Contains("offset = Vector2(3, 4)", text, StringComparison.Ordinal);
        Assert.Equal(
            [".", "Sprite", "Sprite/Glow", "Tail", "Sprite2", "Sprite2/Glow"],
            Paths(await TreeAsync(probe.Directory, "derived.tscn", cancellation))
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeKeepsConnectionsToNodesOutsideIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        const string outbound = "[connection signal=\"pressed\" from=\"Group/B\" to=\"Btn\" method=\"hide\"]";
        File.WriteAllText(Path.Combine(probe.Directory, "squad.tscn"), SquadScene + outbound + "\n");

        JsonNode copied = JsonNode.Parse(await _tools.DuplicateNodeAsync(probe.Directory, "squad.tscn", "Group", cancellationToken: cancellation))!;

        Assert.Null(copied["errors"]);
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "squad.tscn"));
        Assert.Contains(outbound, lines);
        Assert.Contains("[connection signal=\"pressed\" from=\"Group2/B\" to=\"Btn\" method=\"hide\"]", lines);
        Assert.Equal(5, lines.Count(line => line.StartsWith("[connection ", StringComparison.Ordinal)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeKeepsGroups()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(
            Path.Combine(probe.Directory, "tagged.tscn"),
            "[gd_scene format=3]\n\n[node name=\"Stage\" type=\"Node2D\"]\n\n"
                + "[node name=\"Tagged\" type=\"Node2D\" parent=\".\" groups=[\"enemies\"]]\n"
        );

        await _tools.DuplicateNodeAsync(probe.Directory, "tagged.tscn", "Tagged", cancellationToken: cancellation);

        string copy = Assert.Single(
            File.ReadAllLines(Path.Combine(probe.Directory, "tagged.tscn")),
            line => line.StartsWith("[node name=\"Tagged2\" ", StringComparison.Ordinal)
        );
        Assert.Contains("groups=[\"enemies\"]", copy, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeCopiesAnInstanceRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode copied = JsonNode.Parse(await _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Boss", cancellationToken: cancellation))!;

        Assert.Equal("Boss2", copied["newPath"]!.GetValue<string>());
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "level.tscn"));
        string copy = Assert.Single(lines, line => line.StartsWith("[node name=\"Boss2\" parent=\".\"", StringComparison.Ordinal));
        Assert.Contains("instance=ExtResource(", copy, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("name=\"Sprite\"", StringComparison.Ordinal));
        Assert.Equal(
            [".", "Boss", "Boss/Sprite", "Boss2", "Boss2/Sprite", "Btn", "Box"],
            Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation))
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeKeepsAnEditableInstanceAndItsOverride()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(
            Path.Combine(probe.Directory, "arena.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" uid=\""
                + EnemyUid
                + "\" path=\"res://enemy.tscn\" id=\"1\"]\n\n[node name=\"Arena\" type=\"Node2D\"]\n\n"
                + "[node name=\"Boss\" parent=\".\" instance=ExtResource(\"1\")]\n\n[node name=\"Sprite\" parent=\"Boss\"]\n"
                + "offset = Vector2(2, 2)\n\n[editable path=\"Boss\"]\n"
        );

        await _tools.DuplicateNodeAsync(probe.Directory, "arena.tscn", "Boss", cancellationToken: cancellation);

        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "arena.tscn"));
        Assert.Contains("[editable path=\"Boss\"]", lines);
        Assert.Contains("[editable path=\"Boss2\"]", lines);
        Assert.Single(lines, line => line.StartsWith("[node name=\"Sprite\" parent=\"Boss2\"", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(line => line == "offset = Vector2(2, 2)"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeUnderItsOwnChild()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(
            Path.Combine(probe.Directory, "nest.tscn"),
            "[gd_scene format=3]\n\n[node name=\"Nest\" type=\"Node2D\"]\n\n[node name=\"Group\" type=\"Node2D\" parent=\".\"]\n\n"
                + "[node name=\"Leaf\" type=\"Node2D\" parent=\"Group\"]\n"
        );

        JsonNode copied = JsonNode.Parse(
            await _tools.DuplicateNodeAsync(
                probe.Directory,
                "nest.tscn",
                "Group",
                options: new DuplicateNodeOptions("Group/Leaf"),
                cancellationToken: cancellation
            )
        )!;

        Assert.Equal("Group/Leaf/Group", copied["newPath"]!.GetValue<string>());
        Assert.Equal(
            [".", "Group", "Group/Leaf", "Group/Leaf/Group", "Group/Leaf/Group/Leaf"],
            Paths(await TreeAsync(probe.Directory, "nest.tscn", cancellation))
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task DuplicateNodeTakesAnExplicitName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode copied = JsonNode.Parse(
            await _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Btn", "Start", cancellationToken: cancellation)
        )!;

        Assert.Equal("Start", copied["newPath"]!.GetValue<string>());
        Assert.Equal([".", "Boss", "Boss/Sprite", "Btn", "Start", "Box"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("%Boss")]
    [InlineData("a/b")]
    public async Task DuplicateNodeRefusesAnInvalidName(string name)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DuplicateNodeAsync(probe.Directory, "level.tscn", "Btn", name, cancellationToken: cancellation)
        );

        Assert.Equal($"duplicate_node failed: '{name}' is not a valid node name: it cannot hold . : @ / \" or %.", refused.Message);
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptWithAScriptClassBase()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "base_actor.gd"), "class_name BaseActor\nextends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "hero.gd"), "extends BaseActor\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "level.tscn", "Box", "hero.gd", cancellation))!;

        Assert.Equal("res://hero.gd", attached["script"]!["resource"]!.GetValue<string>());
        Assert.Contains("path=\"res://hero.gd\"", File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptExtendingAScript()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "a.gd"), "extends Node2D\n");
        File.WriteAllText(Path.Combine(probe.Directory, "b.gd"), "extends \"res://a.gd\"\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(probe.Directory, "level.tscn", "Box", "b.gd", cancellation))!;

        Assert.Equal(ScriptFacts(probe.Directory, "b.gd"), attached["script"]!.ToJsonString());
        Assert.Contains("path=\"res://b.gd\"", File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AttachScriptQuotesTheErrorsOfAScriptThatDoesNotCompile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(Path.Combine(probe.Directory, "broken.gd"), "extends Node2D\n\nfunc f(:\n\tpass\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.AttachScriptAsync(probe.Directory, "level.tscn", "Box", "broken.gd", cancellation)
        );

        // The load's own parse error, quoted from the engine log.
        Assert.StartsWith(
            "attach_script failed: res://broken.gd cannot be instantiated: Parse Error: Expected par",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AttachScriptSetsACSharpScriptWhenTheBuildIsGreen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(new CsProbeProject());
        File.WriteAllText(Path.Combine(csProbe.Directory, "plain.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node\"]\n");

        JsonNode attached = JsonNode.Parse(await _tools.AttachScriptAsync(csProbe.Directory, "plain.tscn", ".", "CsProbeNode.cs", cancellation))!;

        Assert.Equal("res://CsProbeNode.cs", attached["script"]!["resource"]!.GetValue<string>());
        Assert.Contains("path=\"res://CsProbeNode.cs\"", File.ReadAllText(Path.Combine(csProbe.Directory, "plain.tscn")), StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AddNodeTakesACSharpScriptPath()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(new CsProbeProject());
        File.WriteAllText(Path.Combine(csProbe.Directory, "plain.tscn"), "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node\"]\n");

        JsonNode added = JsonNode.Parse(
            await _tools.AddNodeAsync(csProbe.Directory, "plain.tscn", "res://CsProbeNode.cs", "Probe", cancellationToken: cancellation)
        )!;

        Assert.Equal("Node", added["type"]!.GetValue<string>());
        Assert.Equal("res://CsProbeNode.cs", added["script"]!.GetValue<string>());
        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "plain.tscn"));
        Assert.Contains("[node name=\"Probe\" type=\"Node\"", text, StringComparison.Ordinal);
        Assert.Contains("script = ExtResource(", text, StringComparison.Ordinal);
        Assert.Contains("path=\"res://CsProbeNode.cs\"", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AddNodeSavesTheNativeValueUnderAScriptFieldOfTheSameName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowScene(csProbe);
        AddNodeOptions options = new(Properties: new() { ["visible"] = System.Text.Json.JsonSerializer.SerializeToElement(false) });

        JsonNode added = JsonNode.Parse(
            await _tools.AddNodeAsync(csProbe.Directory, "effects.tscn", "res://ShadowTrail.cs", "Added", options, cancellation)
        )!;

        Assert.Equal(ShadowWarning, added["warning"]!.GetValue<string>());
        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "effects.tscn"));
        Assert.Contains(ShadowScaled, text, StringComparison.Ordinal);
        string section = text[text.IndexOf("[node name=\"Added\"", StringComparison.Ordinal)..];
        Assert.EndsWith("]\nvisible = false\nscript = ExtResource(\"1_shadow\")\n", section, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task DuplicateNodeKeepsTheNativeValueUnderAScriptFieldOfTheSameName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowScene(csProbe);

        JsonNode copied = JsonNode.Parse(
            await _tools.DuplicateNodeAsync(csProbe.Directory, "effects.tscn", "Scaled", cancellationToken: cancellation)
        )!;

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "effects.tscn"));
        string name = copied["newPath"]!.GetValue<string>();
        string section = text[text.IndexOf($"[node name=\"{name}\"", StringComparison.Ordinal)..];
        int end = section.IndexOf("\n[", StringComparison.Ordinal);
        Assert.Contains("\nscale = Vector2(2, 2)\n", end < 0 ? section : section[..(end + 1)], StringComparison.Ordinal);
        Assert.DoesNotContain("scale = 1.0", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveSceneKeepsAnInstanceOverrideOfAPropertyAScriptFieldHides()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowInstance(csProbe, "scale = Vector2(2, 2)\n");

        await _tools.SaveSceneAsync(csProbe.Directory, "host.tscn", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "host.tscn"));
        Assert.Contains("instance=ExtResource(\"1_trail\")]\nscale = Vector2(2, 2)\n", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveSceneAddsNoOverrideToAnInstanceWhoseScriptFieldHidesAProperty()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowInstance(csProbe, "");

        await _tools.SaveSceneAsync(csProbe.Directory, "host.tscn", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "host.tscn"));
        Assert.EndsWith("instance=ExtResource(\"1_trail\")]\n", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveSceneAddsNoOverrideWhereTheBaseSceneSetsAHiddenProperty()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowInstance(csProbe, "");
        File.WriteAllText(Path.Combine(csProbe.Directory, "trail.tscn"), ShadowTrailScene("scale = Vector2(3, 3)\n"));

        await _tools.SaveSceneAsync(csProbe.Directory, "host.tscn", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "host.tscn"));
        Assert.DoesNotContain("scale", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task SaveSceneKeepsAnInheritedRootOverrideOfAPropertyAScriptFieldHides()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowInstance(csProbe, "");
        File.WriteAllText(
            Path.Combine(csProbe.Directory, "big_trail.tscn"),
            "[gd_scene format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://trail.tscn\" id=\"1_trail\"]\n\n"
                + "[node name=\"Trail\" instance=ExtResource(\"1_trail\")]\nscale = Vector2(2, 2)\n"
        );

        await _tools.SaveSceneAsync(csProbe.Directory, "big_trail.tscn", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "big_trail.tscn"));
        Assert.Contains("instance=ExtResource(\"1_trail\")]\nscale = Vector2(2, 2)\n", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task DuplicateNodeKeepsAnInstanceOverrideOfAPropertyAScriptFieldHides()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteShadowInstance(csProbe, "scale = Vector2(2, 2)\n");

        await _tools.DuplicateNodeAsync(csProbe.Directory, "host.tscn", "Trail", "Copy", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(csProbe.Directory, "host.tscn"));
        Assert.Equal(2, text.Split("scale = Vector2(2, 2)\n").Length - 1);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteRefusesAResourceThatIsNotATexture()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "sprite.tscn"), SpriteScene);
        File.WriteAllText(Path.Combine(probe.Directory, "gradient.tres"), "[gd_resource type=\"Gradient\" format=3]\n\n[resource]\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.LoadSpriteAsync(probe.Directory, "sprite.tscn", "Icon", "gradient.tres", cancellation)
        );

        Assert.Equal("load_sprite failed: res://gradient.tres is a Gradient, not a Texture2D.", refused.Message);
        Assert.Equal(SpriteScene, File.ReadAllText(Path.Combine(probe.Directory, "sprite.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteRefusesAMissingTextureWithoutImporting()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "sprite.tscn"), SpriteScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.LoadSpriteAsync(probe.Directory, "sprite.tscn", "Icon", "art/none.png", cancellation)
        );

        Assert.Equal("res://art/none.png does not exist.", refused.Message);
        Assert.False(Directory.Exists(Path.Combine(probe.Directory, ".godot", "imported")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteSetsATextureFromATres()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "sprite.tscn"), SpriteScene);
        File.WriteAllText(
            Path.Combine(probe.Directory, "gradient.tres"),
            "[gd_resource type=\"GradientTexture2D\" format=3 uid=\"" + GradientUid + "\"]\n\n[resource]\nwidth = 8\nheight = 8\n"
        );

        JsonNode loaded = JsonNode.Parse(await _tools.LoadSpriteAsync(probe.Directory, "sprite.tscn", "Icon", "gradient.tres", cancellation))!;

        Assert.Equal("Icon", loaded["path"]!.GetValue<string>());
        Assert.Equal($$"""{"resource":"res://gradient.tres","uid":"{{GradientUid}}"}""", loaded["texture"]!.ToJsonString());
        AssertExtUid(probe.Directory, "sprite.tscn", GradientUid, "res://gradient.tres");
        Assert.Contains("texture = ExtResource(", File.ReadAllText(Path.Combine(probe.Directory, "sprite.tscn")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(probe.Directory, "gradient.tres.import")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteImportsANewPngFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "sprite.tscn"), SpriteScene);
        Directory.CreateDirectory(Path.Combine(probe.Directory, "art"));
        File.WriteAllBytes(Path.Combine(probe.Directory, "art", "dot.png"), Convert.FromBase64String(DotPng));

        JsonNode loaded = JsonNode.Parse(await _tools.LoadSpriteAsync(probe.Directory, "sprite.tscn", "Icon", "res://art/dot.png", cancellation))!;

        Assert.True(File.Exists(Path.Combine(probe.Directory, "art", "dot.png.import")));
        Assert.Equal("res://art/dot.png", loaded["texture"]!["resource"]!.GetValue<string>());
        string uid = loaded["texture"]!["uid"]!.GetValue<string>();
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        AssertExtUid(probe.Directory, "sprite.tscn", uid, "res://art/dot.png");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteRefusesANodeWithoutATexture()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        File.WriteAllText(
            Path.Combine(probe.Directory, "gradient.tres"),
            "[gd_resource type=\"GradientTexture2D\" format=3]\n\n[resource]\nwidth = 8\nheight = 8\n"
        );

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.LoadSpriteAsync(probe.Directory, "level.tscn", "Box", "gradient.tres", cancellation)
        );

        Assert.Equal("load_sprite failed: Box is a Node2D, which has no Texture2D texture property.", refused.Message);
        Assert.Equal(LevelScene, File.ReadAllText(Path.Combine(probe.Directory, "level.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MoveNodeReordersSiblingsAndKeepsTheRestOfTheFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteMoveScenes(probe.Directory);

        JsonNode moved = JsonNode.Parse(await MoveAsync(probe.Directory, "stage.tscn", "C", null, new NodePosition(Index: 0), cancellation))!;

        Assert.Equal("""{"path":"C","previousPath":"C","index":0}""", moved.ToJsonString());
        string expected =
            StageHeader + StageRoot + "[node name=\"C\" type=\"Node2D\" parent=\".\"]\nposition = Vector2(3, 0)\n\n" + StageA + "\n" + StageB;
        Assert.Equal(expected, File.ReadAllText(Path.Combine(probe.Directory, "stage.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MoveNodeBeforeAndAfterASibling()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteMoveScenes(probe.Directory);

        JsonNode after = JsonNode.Parse(await MoveAsync(probe.Directory, "stage.tscn", "A", null, new NodePosition(After: "B"), cancellation))!;
        string[] afterOrder = NodeNames(probe.Directory, "stage.tscn");
        JsonNode before = JsonNode.Parse(await MoveAsync(probe.Directory, "stage.tscn", "C", null, new NodePosition(Before: "B"), cancellation))!;

        Assert.Equal(1, after["index"]!.GetValue<int>());
        Assert.Equal(["Stage", "B", "A", "C"], afterOrder);
        Assert.Equal(0, before["index"]!.GetValue<int>());
        Assert.Equal(["Stage", "C", "B", "A"], NodeNames(probe.Directory, "stage.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MoveNodeWithANegativeIndex()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteMoveScenes(probe.Directory);

        JsonNode moved = JsonNode.Parse(await MoveAsync(probe.Directory, "stage.tscn", "A", null, new NodePosition(Index: -1), cancellation))!;

        Assert.Equal(2, moved["index"]!.GetValue<int>());
        Assert.Equal(["Stage", "B", "C", "A"], NodeNames(probe.Directory, "stage.tscn"));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true, "Vector2(110, 0)")]
    [InlineData(false, "Vector2(10, 0)")]
    public async Task MoveNodeReparentsKeepingTheGlobalPosition(bool keep, string position)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteMoveScenes(probe.Directory);

        JsonNode moved = JsonNode.Parse(
            await _tools.MoveNodeAsync(probe.Directory, "nest.tscn", "From/Mover", new MoveNodeOptions("To", KeepGlobalTransform: keep), cancellation)
        )!;

        Assert.Equal("""{"path":"To/Mover","previousPath":"From/Mover","index":1}""", moved.ToJsonString());
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "nest.tscn"));
        int mover = Array.FindIndex(lines, line => line.StartsWith("[node name=\"Mover\" type=\"Node2D\" parent=\"To\"", StringComparison.Ordinal));
        Assert.True(mover > 0, string.Join("\n", lines));
        Assert.Equal($"position = {position}", lines[mover + 1]);
        Assert.Equal(["Nest", "From", "To", "Leaf", "Mover", "Leaf", "Btn"], NodeNames(probe.Directory, "nest.tscn"));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true, "Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 11, 0, -5)")]
    [InlineData(false, "Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0)")]
    public async Task MoveNodeReparentsA3DNodeKeepingItsGlobalPosition(bool keep, string transform)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(
            Path.Combine(probe.Directory, "yard.tscn"),
            "[gd_scene format=3 uid=\"uid://bqyard3d0000a\"]\n\n[node name=\"Yard\" type=\"Node3D\"]\n\n"
                + "[node name=\"From\" type=\"Node3D\" parent=\".\"]\ntransform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 0)\n\n"
                + "[node name=\"Mover\" type=\"Node3D\" parent=\"From\"]\ntransform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0)\n\n"
                + "[node name=\"To\" type=\"Node3D\" parent=\".\"]\ntransform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 5)\n"
        );

        JsonNode moved = JsonNode.Parse(
            await _tools.MoveNodeAsync(probe.Directory, "yard.tscn", "From/Mover", new MoveNodeOptions("To", KeepGlobalTransform: keep), cancellation)
        )!;

        Assert.Equal("""{"path":"To/Mover","previousPath":"From/Mover","index":0}""", moved.ToJsonString());
        string[] lines = File.ReadAllLines(Path.Combine(probe.Directory, "yard.tscn"));
        int mover = Array.FindIndex(lines, line => line.StartsWith("[node name=\"Mover\" type=\"Node3D\" parent=\"To\"", StringComparison.Ordinal));
        Assert.True(mover > 0, string.Join("\n", lines));
        Assert.Equal($"transform = {transform}", lines[mover + 1]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MoveNodeKeepsASignalConnection()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteMoveScenes(probe.Directory);
        File.AppendAllText(
            Path.Combine(probe.Directory, "nest.tscn"),
            "\n[connection signal=\"pressed\" from=\"Btn\" to=\"From/Mover\" method=\"hide\"]\n"
        );

        await _tools.MoveNodeAsync(probe.Directory, "nest.tscn", "From/Mover", new MoveNodeOptions("."), cancellation);
        JsonNode read = JsonNode.Parse(await _tools.GetNodeSignalsAsync(probe.Directory, "nest.tscn", "Btn", null, cancellation))!;

        JsonNode pressed = Assert.Single(read["signals"]!.AsArray(), entry => entry!["name"]!.GetValue<string>() == "pressed")!;
        Assert.Equal("""[{"target":"Mover","method":"hide"}]""", pressed["connections"]!.ToJsonString());
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("stage.tscn", ".", null, 0, "the scene's root cannot be moved.")]
    [InlineData(
        "level.tscn",
        "Boss/Sprite",
        ".",
        null,
        "Boss/Sprite is inside the instance of res://enemy.tscn at Boss, so its move would not be saved. Edit res://enemy.tscn instead."
    )]
    [InlineData(
        "elite.tscn",
        "Sprite",
        null,
        0,
        "Sprite comes from the base scene res://enemy.tscn, so its move would not be saved. Edit res://enemy.tscn instead."
    )]
    [InlineData("nest.tscn", "From", "From/Mover", null, "From cannot move under itself or its own child From/Mover.")]
    [InlineData("nest.tscn", "To/Leaf", ".", null, "The scene root already has a child named Leaf.")]
    [InlineData(
        "stage.tscn",
        "A",
        null,
        3,
        "position.index 3 is out of range: the scene root has 3 children once the node is placed, so index takes -3 to 2."
    )]
    public async Task MoveNodeRefusalsLeaveTheFileAsItWas(string scene, string nodePath, string? parent, int? index, string refusal)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);
        WriteMoveScenes(probe.Directory);
        string path = Path.Combine(probe.Directory, scene);
        string before = File.ReadAllText(path);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            MoveAsync(probe.Directory, scene, nodePath, parent, index is null ? null : new NodePosition(index), cancellation)
        );

        Assert.Equal($"move_node failed: {refusal}", refused.Message);
        Assert.Equal(before, File.ReadAllText(path));
    }

    private Task<string> MoveAsync(
        string projectDir,
        string scene,
        string nodePath,
        string? parent,
        NodePosition? position,
        CancellationToken cancellation
    ) => _tools.MoveNodeAsync(projectDir, scene, nodePath, new MoveNodeOptions(parent, position), cancellation);

    /// <summary>The names of the scene file's node sections, in file order.</summary>
    private static string[] NodeNames(string directory, string scene) =>
        [
            .. File.ReadLines(Path.Combine(directory, scene))
                .Where(line => line.StartsWith("[node name=\"", StringComparison.Ordinal))
                .Select(line => line.Split('"')[1]),
        ];

    /// <summary>
    /// Asserts combat.tscn holds CombatScene's nodes with their own unique_ids and the copies, and that no two nodes share an id.
    /// </summary>
    private static void AssertCopiesHaveFreshIds(string directory, string[] copies)
    {
        (string Path, long Id)[] originals = [(".", 100), ("Layer", 150), ("Ties", 442001752), ("Ties/Tie", 200), ("Hud", 300)];
        List<(string Path, long Id)> saved = UniqueIds(directory, "combat.tscn");
        Assert.Equal(
            originals.Select(node => node.Path).Concat(copies).Order(StringComparer.Ordinal),
            saved.Select(node => node.Path).Order(StringComparer.Ordinal)
        );
        Assert.All(originals, node => Assert.Contains(node, saved));
        Assert.Equal(saved.Count, saved.Select(node => node.Id).Distinct().Count());
    }

    /// <summary>Each node section's path (the root as ".") and unique_id, in file order.</summary>
    private static List<(string Path, long Id)> UniqueIds(string directory, string scene) =>
        [
            .. File.ReadLines(Path.Combine(directory, scene))
                .Where(line => line.StartsWith("[node name=\"", StringComparison.Ordinal))
                .Select(line => (NodeSectionPath(line), long.Parse(TagValue(line, "unique_id="), CultureInfo.InvariantCulture))),
        ];

    /// <summary>The path from the root of the node a [node] tag line opens, the root as ".".</summary>
    private static string NodeSectionPath(string line)
    {
        string name = line.Split('"')[1];
        string parent = TagValue(line, "parent=").Trim('"');
        if (parent.Length == 0)
        {
            return ".";
        }
        return parent == "." ? name : parent + "/" + name;
    }

    /// <summary>The value of a tag line's attribute (key given with its "="), up to the next space or "]"; "" when absent.</summary>
    private static string TagValue(string line, string key)
    {
        int start = line.IndexOf(" " + key, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        start += key.Length + 1;
        return line[start..line.IndexOfAny([' ', ']'], start)];
    }

    /// <summary>
    /// stage.tscn: Stage holding A, B and C. nest.tscn: Nest holding From at (100, 0) with Mover at (10, 0), To at the origin with
    /// a Leaf, a Leaf of its own, and a button.
    /// </summary>
    private static void WriteMoveScenes(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "stage.tscn"), StageHeader + StageRoot + StageA + "\n" + StageB + "\n" + StageC);
        File.WriteAllText(
            Path.Combine(directory, "nest.tscn"),
            "[gd_scene format=3 uid=\"uid://bqnest000000a\"]\n\n[node name=\"Nest\" type=\"Node2D\"]\n\n"
                + "[node name=\"From\" type=\"Node2D\" parent=\".\"]\nposition = Vector2(100, 0)\n\n"
                + "[node name=\"Mover\" type=\"Node2D\" parent=\"From\"]\nposition = Vector2(10, 0)\n\n"
                + "[node name=\"To\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Leaf\" type=\"Node2D\" parent=\"To\"]\n\n"
                + "[node name=\"Leaf\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Btn\" type=\"Button\" parent=\".\"]\n"
        );
    }

    /// <summary>An unbuilt CsProbe copy whose CsProbeNode.cs does not compile, with the given [autoload] entries in its project.godot.</summary>
    private CsProbeProject RedBuildWithAutoloads(string autoloads)
    {
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(csProbe.Directory, "project.godot"), $"\n[autoload]\n\n{autoloads}\n");
        return csProbe;
    }

    private static IEnumerable<string> Paths(JsonNode page) => page["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>());

    /// <summary>
    /// Writes ShadowTrail.cs, a Node2D script whose private field scale shadows Node2D.scale, and effects.tscn, whose Scaled
    /// node has that script and an engine scale of (2, 2).
    /// </summary>
    private static void WriteShadowScene(CsProbeProject csProbe)
    {
        csProbe.WriteSource("ShadowTrail.cs", ShadowTrailSource);
        File.WriteAllText(
            Path.Combine(csProbe.Directory, "effects.tscn"),
            "[gd_scene format=3]\n\n[ext_resource type=\"Script\" path=\"res://ShadowTrail.cs\" id=\"1_shadow\"]\n\n"
                + "[node name=\"Effects\" type=\"Node2D\"]\n\n"
                + ShadowScaled
        );
    }

    /// <summary>
    /// Writes ShadowTrail.cs, trail.tscn (a Node2D root Trail with that script) and host.tscn, whose child Trail instances
    /// trail.tscn with the property lines overrides.
    /// </summary>
    private static void WriteShadowInstance(CsProbeProject csProbe, string overrides)
    {
        csProbe.WriteSource("ShadowTrail.cs", ShadowTrailSource);
        File.WriteAllText(Path.Combine(csProbe.Directory, "trail.tscn"), ShadowTrailScene(""));
        File.WriteAllText(
            Path.Combine(csProbe.Directory, "host.tscn"),
            "[gd_scene format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://trail.tscn\" id=\"1_trail\"]\n\n"
                + "[node name=\"Host\" type=\"Node2D\"]\n\n"
                + "[node name=\"Trail\" parent=\".\" instance=ExtResource(\"1_trail\")]\n"
                + overrides
        );
    }

    /// <summary>trail.tscn: a Node2D root Trail holding the property lines properties, then the ShadowTrail script.</summary>
    private static string ShadowTrailScene(string properties) =>
        "[gd_scene format=3]\n\n[ext_resource type=\"Script\" path=\"res://ShadowTrail.cs\" id=\"1_shadow\"]\n\n"
        + "[node name=\"Trail\" type=\"Node2D\"]\n"
        + properties
        + "script = ExtResource(\"1_shadow\")\n";

    private static string FirstLine(string directory, string relative) => File.ReadLines(Path.Combine(directory, relative)).First();

    // The property lines of the Backdrop node's section in the scene, sorted.
    private static string[] BackdropProperties(string directory, string scene) =>
        [
            .. File.ReadLines(Path.Combine(directory, scene))
                .SkipWhile(line => !line.StartsWith("[node name=\"Backdrop\"", StringComparison.Ordinal))
                .Skip(1)
                .TakeWhile(line => line.Length > 0 && !line.StartsWith('['))
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>The scene's ext_resource line for resourcePath carries uid, just before its path.</summary>
    private static void AssertExtUid(string directory, string scene, string uid, string resourcePath)
    {
        string expected = $"uid=\"{uid}\" path=\"{resourcePath}\"";
        Assert.Contains(
            File.ReadLines(Path.Combine(directory, scene)),
            line => line.StartsWith("[ext_resource ", StringComparison.Ordinal) && line.Contains(expected, StringComparison.Ordinal)
        );
    }

    /// <summary>The {resource, uid} facts of the script at the project-relative path, its uid read from the .uid file beside it.</summary>
    private static string ScriptFacts(string directory, string script)
    {
        string uid = File.ReadAllText(Path.Combine(directory, script + ".uid")).Trim();
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        return $$"""{"resource":"res://{{script}}","uid":"{{uid}}"}""";
    }

    /// <summary>A C# Node2D script named name whose exported private field card holds a PackedScene.</summary>
    private static string CardHolderSource(string name) =>
        "using Godot;\n\nnamespace CsProbe;\n\npublic partial class "
        + name
        + " : Node2D\n{\n    [Export]\n    private PackedScene card = null!;\n}\n";

    /// <summary>Writes carded.tscn, whose root has the script at scriptPath and its card set to enemy.tscn.</summary>
    private static void WriteCardedScene(string directory, string scriptPath) =>
        File.WriteAllText(
            Path.Combine(directory, "carded.tscn"),
            "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\""
                + scriptPath
                + "\" id=\"1\"]\n[ext_resource type=\"PackedScene\" uid=\""
                + EnemyUid
                + "\" path=\"res://enemy.tscn\" id=\"2\"]\n\n"
                + "[node name=\"Carded\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\ncard = ExtResource(\"2\")\n"
        );

    /// <summary>carded.tscn's root has the script at scriptPath and still has its card, and the result says card was kept.</summary>
    private static void AssertCardKept(string directory, JsonNode attached, string scriptPath)
    {
        string text = File.ReadAllText(Path.Combine(directory, "carded.tscn"));
        Assert.Contains($"path=\"{scriptPath}\"", text, StringComparison.Ordinal);
        Assert.Contains("\ncard = ExtResource(", text, StringComparison.Ordinal);
        AssertExtUid(directory, "carded.tscn", EnemyUid, "res://enemy.tscn");
        Assert.Equal("""["card"]""", attached["kept"]!.ToJsonString());
        Assert.Equal("[]", attached["dropped"]!.ToJsonString());
    }

    private static void WriteScenes(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "enemy.tscn"), EnemyScene);
        File.WriteAllText(Path.Combine(directory, "level.tscn"), LevelScene);
        File.WriteAllText(Path.Combine(directory, "elite.tscn"), EliteScene);
    }

    /// <summary>
    /// The entries of a uid cache in the engine's layout (a little-endian 32-bit count, then per entry a 64-bit id, a 32-bit
    /// byte length and the UTF-8 path), asserting the count equals the entries the file holds, with nothing after them.
    /// </summary>
    private static List<(long Id, string Path)> ReadUidCache(string cache)
    {
        using BinaryReader reader = new(File.OpenRead(cache));
        uint count = reader.ReadUInt32();
        List<(long Id, string Path)> entries = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            long id = reader.ReadInt64();
            int length = reader.ReadInt32();
            entries.Add((id, System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length))));
        }

        Assert.Equal(count, (uint)entries.Count);
        return entries;
    }

    /// <summary>The uid="…" of a text scene's header line.</summary>
    private static string HeaderUid(string scene)
    {
        string header = File.ReadLines(scene).First();
        int start = header.IndexOf(" uid=\"", StringComparison.Ordinal) + " uid=\"".Length;
        return header[start..header.IndexOf('"', start)];
    }

    /// <summary>Runs Godot headless on project with arguments, outside the server, and returns its log.</summary>
    private static async Task<string> RunGodotAsync(string project, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        string log = Path.Combine(Path.GetTempPath(), "godot-mcp-tests", $"run-{Guid.NewGuid():N}.log");
        ToolProcessRequest request = new(
            Installation.FindGodot(),
            ["--headless", "--path", project, .. arguments],
            project,
            log,
            TimeSpan.FromSeconds(60)
        );
        ToolProcessResult result = await ToolProcess.RunAsync(request, NullLogger.Instance, cancellation);
        string text = File.ReadAllText(log);
        File.Delete(log);
        Assert.Equal(0, result.ExitCode);
        return text;
    }

    private async Task<JsonNode> TreeAsync(string projectDir, string scenePath, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.GetSceneFileTreeAsync(projectDir, scenePath, null, null, cancellation))!;

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
