using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless scene edits against the real Godot: create_scene, save_scene and delete_nodes on scenes the tests write into
/// InputProbe and CsProbe copies (never into the tracked fixtures), read back with get_scene_file_tree and as text. The copies
/// are never imported, so Godot's uid cache is empty and every uid comes from the files themselves.
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

        Assert.Equal(
            "save_scene failed: res://main.tscn uses C# scripts and the project's C# build failed; fix it first (validate lists the errors).",
            refused.Message
        );
        Assert.Equal(before, File.ReadAllText(scene));
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
