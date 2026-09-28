using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
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
    public async Task DeleteNodesRemovesThemAndSaves()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode deleted = JsonNode.Parse(await _tools.DeleteNodesAsync(probe.Directory, "level.tscn", ["Btn", "./Box"], cancellation))!;

        Assert.Equal("""["Btn","./Box"]""", deleted["deleted"]!.ToJsonString());
        Assert.Equal([".", "Boss", "Boss/Sprite"], Paths(await TreeAsync(probe.Directory, "level.tscn", cancellation)));
        Assert.Equal($"[gd_scene format=3 uid=\"{LevelUid}\"]", FirstLine(probe.Directory, "level.tscn"));
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
        Assert.Equal("""{"resource":"res://b.gd"}""", attached["script"]!.ToJsonString());
        Assert.Equal("""{"resource":"res://a.gd"}""", attached["previous"]!.ToJsonString());
        string text = File.ReadAllText(Path.Combine(probe.Directory, "scripted.tscn"));
        Assert.Contains("path=\"res://b.gd\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("res://a.gd", text, StringComparison.Ordinal);
        JsonNode root = (await TreeAsync(probe.Directory, "scripted.tscn", cancellation))["nodes"]![0]!;
        Assert.Equal("res://b.gd", root["script"]!.GetValue<string>());
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
        Assert.Equal($"[gd_scene format=3 uid=\"{LevelUid}\"]", FirstLine(probe.Directory, "level.tscn"));
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

        Assert.Equal("""{"resource":"res://b.gd"}""", attached["script"]!.ToJsonString());
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

    private static string FirstLine(string directory, string relative) => File.ReadLines(Path.Combine(directory, relative)).First();

    /// <summary>The scene's ext_resource line for resourcePath carries uid, just before its path.</summary>
    private static void AssertExtUid(string directory, string scene, string uid, string resourcePath)
    {
        string expected = $"uid=\"{uid}\" path=\"{resourcePath}\"";
        Assert.Contains(
            File.ReadLines(Path.Combine(directory, scene)),
            line => line.StartsWith("[ext_resource ", StringComparison.Ordinal) && line.Contains(expected, StringComparison.Ordinal)
        );
    }

    private static void WriteScenes(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "enemy.tscn"), EnemyScene);
        File.WriteAllText(Path.Combine(directory, "level.tscn"), LevelScene);
        File.WriteAllText(Path.Combine(directory, "elite.tscn"), EliteScene);
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
