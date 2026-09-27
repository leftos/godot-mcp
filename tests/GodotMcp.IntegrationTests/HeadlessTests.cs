using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless tools against the real Godot: validate on InputProbe and CsProbe copies, and get_scene_file_tree on scenes
/// the tests write into the copy (never into the tracked fixtures).
/// </summary>
public sealed class HeadlessTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private const int BuildTestTimeoutMs = 240_000;
    private const string BrokenScript = "extends Node\n\n\nfunc _ready() -> void:\n\tif true\n\t\tpass\n";

    private const string EnemyScene =
        "[gd_scene format=3]\n\n[node name=\"Enemy\" type=\"CharacterBody2D\"]\n\n[node name=\"Sprite\" type=\"Sprite2D\" parent=\".\"]\n";

    private const string LevelScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\n\n[node name=\"Boss\" parent=\".\" instance=ExtResource(\"1\")]\n";

    private const string EliteScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://enemy.tscn\" id=\"1\"]\n\n"
        + "[node name=\"Elite\" instance=ExtResource(\"1\")]\n\n[node name=\"Shield\" type=\"Node2D\" parent=\".\"]\n";

    private const string MissingClassSource = "namespace CsProbe;\n\n// No class here, so Godot finds none for the script.\n";

    private const string MissingClassScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Ghost.cs\" id=\"1\"]\n\n"
        + "[node name=\"Ghost\" type=\"Node\"]\nscript = ExtResource(\"1\")\n";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ValidateFindsAParseErrorWithItsLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(probe.Directory, "broken.gd", BrokenScript);

        JsonNode result = await ValidateAsync(probe.Directory, ["broken.gd", "res://main.gd"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Equal(2, result["checked"]!.GetValue<int>());
        JsonNode broken = Assert.Single(result["results"]!.AsArray())!;
        Assert.Equal("res://broken.gd", broken["path"]!.GetValue<string>());
        JsonNode error = broken["errors"]![0]!;
        Assert.StartsWith("Parse Error:", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("res://broken.gd", error["file"]!.GetValue<string>());
        Assert.Equal(5, error["line"]!.GetValue<int>());
        Assert.Null(result["csharp"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ValidateGroupsAnErrorUnderTheFileItNames()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(probe.Directory, "b.gd", BrokenScript);
        WriteFile(
            probe.Directory,
            "a.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://b.gd\" id=\"1\"]\n\n"
                + "[node name=\"A\" type=\"Node\"]\nscript = ExtResource(\"1\")\n"
        );

        JsonNode result = await ValidateAsync(probe.Directory, ["a.tscn"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>());
        // The scene also reports its script failing, under res://a.tscn; the parse error itself is under the script.
        JsonNode grouped = Assert.Single(result["results"]!.AsArray(), group => group!["path"]!.GetValue<string>() == "res://b.gd")!;
        Assert.StartsWith("Parse Error:", grouped["errors"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(5, grouped["errors"]![0]!["line"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ValidateSweepsVersionedFilesWhenNoTargetsAreGiven()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(probe.Directory, "uncommitted.gd", BrokenScript);
        int versioned = Git.Run(probe.Directory, "ls-files")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(path => path.EndsWith(".gd", StringComparison.Ordinal) || path.EndsWith(".tscn", StringComparison.Ordinal));

        JsonNode result = JsonNode.Parse(await _tools.ValidateAsync(probe.Directory, null, null, cancellation))!;

        Assert.True(versioned > 5);
        Assert.Equal(versioned, result["checked"]!.GetValue<int>());
        Assert.Empty(result["results"]!.AsArray());
        Assert.True(result["valid"]!.GetValue<bool>());
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ValidateReportsACSharpBuildErrorAndStillChecksGdscript()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));
        WriteFile(csProbe.Directory, "broken.gd", BrokenScript);

        JsonNode result = await ValidateAsync(csProbe.Directory, ["broken.gd"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Equal("failed", result["csharp"]!["build"]!.GetValue<string>());
        string errors = result["csharp"]!["errors"]!.ToJsonString();
        Assert.Contains("CsProbeNode.cs:10: CS1002", errors, StringComparison.Ordinal);
        Assert.Equal("failed", result["prep"]!["build"]!.GetValue<string>());
        Assert.Equal("res://broken.gd", Assert.Single(result["results"]!.AsArray())!["path"]!.GetValue<string>());
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ValidateGroupsAMissingCSharpClassUnderItsScript()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        csProbe.WriteSource("Ghost.cs", "namespace CsProbe;\n\n// No class here, so Godot finds none for the script.\n");
        WriteFile(
            csProbe.Directory,
            "ghost.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://Ghost.cs\" id=\"1\"]\n\n"
                + "[node name=\"Ghost\" type=\"Node\"]\nscript = ExtResource(\"1\")\n"
        );

        JsonNode result = await ValidateAsync(csProbe.Directory, ["ghost.tscn"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("built", result["csharp"]!["build"]!.GetValue<string>());
        JsonNode ghost = Assert.Single(result["results"]!.AsArray())!;
        Assert.Equal("res://Ghost.cs", ghost["path"]!.GetValue<string>());
        Assert.Contains("associated class could not be found", ghost["errors"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ValidateReportsAMissingCSharpClassInAnInstancedScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        csProbe.WriteSource("Ghost.cs", MissingClassSource);
        WriteFile(csProbe.Directory, "GhostInner.tscn", MissingClassScene);
        WriteFile(csProbe.Directory, "GhostOuter.tscn", InstancingScene("GhostOuter"));

        JsonNode result = await ValidateAsync(csProbe.Directory, ["GhostOuter.tscn"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("built", result["csharp"]!["build"]!.GetValue<string>());
        JsonNode ghost = Assert.Single(Groups(result, "res://Ghost.cs"))!;
        Assert.Contains("associated class could not be found", ghost["errors"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ValidateChecksASharedCSharpScriptOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        csProbe.WriteSource("Ghost.cs", MissingClassSource);
        WriteFile(csProbe.Directory, "GhostInner.tscn", MissingClassScene);
        WriteFile(csProbe.Directory, "GhostOne.tscn", InstancingScene("GhostOne"));
        WriteFile(csProbe.Directory, "GhostTwo.tscn", InstancingScene("GhostTwo"));

        JsonNode result = await ValidateAsync(csProbe.Directory, ["GhostOne.tscn", "GhostTwo.tscn"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>(), result.ToJsonString());
        JsonNode ghost = Assert.Single(Groups(result, "res://Ghost.cs"))!;
        Assert.Single(ghost["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ANewClassNameIsKnownAfterTheImport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string cache = PrepScan.ClassCachePath(probe.Directory);
        WriteFile(probe.Directory, "probe_helper.gd", "class_name ProbeHelper\nextends RefCounted\n\n\nstatic func answer() -> int:\n\treturn 42\n");
        WriteFile(probe.Directory, "helper_user.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tprint(ProbeHelper.answer())\n");

        JsonNode first = await ValidateAsync(probe.Directory, ["helper_user.gd"], cancellation);
        JsonNode second = await ValidateAsync(probe.Directory, ["helper_user.gd"], cancellation);
        // A second class_name script newer than the cache: both older than now, so the import's rewrite is newer still.
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddSeconds(-20));
        WriteFile(probe.Directory, "probe_helper2.gd", "class_name ProbeHelper2\nextends RefCounted\n");
        File.SetLastWriteTimeUtc(Path.Combine(probe.Directory, "probe_helper2.gd"), DateTime.UtcNow.AddSeconds(-10));
        JsonNode third = await ValidateAsync(probe.Directory, ["helper_user.gd"], cancellation);
        JsonNode fourth = await ValidateAsync(probe.Directory, ["helper_user.gd"], cancellation);

        Assert.Equal("done", first["prep"]!["import"]!.GetValue<string>());
        Assert.True(first["valid"]!.GetValue<bool>(), first.ToJsonString());
        Assert.Equal("not-needed", second["prep"]!["import"]!.GetValue<string>());
        Assert.True(second["valid"]!.GetValue<bool>(), second.ToJsonString());
        Assert.Equal("done", third["prep"]!["import"]!.GetValue<string>());
        Assert.Contains("ProbeHelper2", File.ReadAllText(cache), StringComparison.Ordinal);
        Assert.Equal("not-needed", fourth["prep"]!["import"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAutoloadIsFreedBeforeItsReady()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(
            probe.Directory,
            "auto_marker.gd",
            "extends Node\n\n\nfunc _init() -> void:\n\tFileAccess.open(\"res://init.marker\", FileAccess.WRITE).store_string(\"init\")\n\n\n"
                + "func _ready() -> void:\n\tFileAccess.open(\"res://ready.marker\", FileAccess.WRITE).store_string(\"ready\")\n"
        );
        File.AppendAllText(probe.ProjectFile, "\n[autoload]\n\nProbeAuto=\"*res://auto_marker.gd\"\n");

        JsonNode result = await ValidateAsync(probe.Directory, ["main.gd"], cancellation);

        Assert.True(result["valid"]!.GetValue<bool>(), result.ToJsonString());
        Assert.True(File.Exists(Path.Combine(probe.Directory, "init.marker")));
        Assert.False(File.Exists(Path.Combine(probe.Directory, "ready.marker")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnAutoloadErrorBeforeTheFirstFileIsReported()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(probe.Directory, "auto_broken.gd", "extends Node\n\n\nfunc _init() -> void:\n\tvar missing: Node = null\n\tmissing.get_name()\n");
        File.AppendAllText(probe.ProjectFile, "\n[autoload]\n\nProbeBroken=\"*res://auto_broken.gd\"\n");

        JsonNode result = await ValidateAsync(probe.Directory, ["main.gd"], cancellation);

        Assert.False(result["valid"]!.GetValue<bool>(), result.ToJsonString());
        JsonNode reported = Assert.Single(result["results"]!.AsArray())!;
        Assert.Equal("res://auto_broken.gd", reported["path"]!.GetValue<string>());
        Assert.Equal(6, reported["errors"]![0]!["line"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHeadlessRunLeavesNoRequestOrResultFiles()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        OverrideFile.Write(probe.Directory, Installation.FindBridgeScript(), shutOutRealGamepads: false, quiet: true);

        await ValidateAsync(probe.Directory, ["main.gd"], cancellation);
        await TreeAsync(probe.Directory, "main.tscn", null, cancellation);

        string folder = Path.Combine(ProjectPrep.LogFolder(probe.Directory), "headless");
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.True(File.Exists(Path.Combine(ProjectPrep.LogFolder(probe.Directory), "headless.log")));
        Assert.False(File.Exists(probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreeListsNodesWithoutRunningScripts()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(
            probe.Directory,
            "tree_probe.gd",
            "extends Node2D\n\n\nfunc _init() -> void:\n\tFileAccess.open(\"res://init.marker\", FileAccess.WRITE).store_string(\"init\")\n"
        );
        WriteFile(
            probe.Directory,
            "tree_probe.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://tree_probe.gd\" id=\"1\"]\n\n"
                + "[node name=\"TreeProbe\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n"
                + "[node name=\"Label\" type=\"Label\" parent=\".\" groups=[\"texts\"]]\n\n[node name=\"Timer\" type=\"Timer\" parent=\".\"]\n"
        );

        JsonNode result = await TreeAsync(probe.Directory, "res://tree_probe.tscn", null, cancellation);

        AssertNodes(
            [
                """{"path":".","name":"TreeProbe","type":"Node2D","script":"res://tree_probe.gd","childCount":2}""",
                """{"path":"Label","name":"Label","type":"Label","groups":["texts"],"childCount":0}""",
                """{"path":"Timer","name":"Timer","type":"Timer","childCount":0}""",
            ],
            result
        );
        Assert.Equal(3, result["total"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(probe.Directory, "init.marker")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreeExpandsAnInstancedScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode result = await TreeAsync(probe.Directory, "level.tscn", null, cancellation);

        AssertNodes(
            [
                """{"path":".","name":"Level","type":"Node2D","childCount":1}""",
                """{"path":"Boss","name":"Boss","type":"CharacterBody2D","instance":"res://enemy.tscn","childCount":1}""",
                """{"path":"Boss/Sprite","name":"Sprite","type":"Sprite2D","childCount":0}""",
            ],
            result
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreeMergesAnInheritedScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode result = await TreeAsync(probe.Directory, "elite.tscn", null, cancellation);

        AssertNodes(
            [
                """{"path":".","name":"Elite","type":"CharacterBody2D","instance":"res://enemy.tscn","childCount":2}""",
                """{"path":"Sprite","name":"Sprite","type":"Sprite2D","childCount":0}""",
                """{"path":"Shield","name":"Shield","type":"Node2D","childCount":0}""",
            ],
            result
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreeStartsAtRoot()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScenes(probe.Directory);

        JsonNode result = await TreeAsync(probe.Directory, "level.tscn", "Boss", cancellation);
        McpException unknown = await Assert.ThrowsAsync<McpException>(() => TreeAsync(probe.Directory, "level.tscn", "Nope", cancellation));

        Assert.Equal(["Boss", "Boss/Sprite"], Paths(result));
        Assert.Equal(
            "get_scene_file_tree failed: res://level.tscn has no node Nope; get_scene_file_tree without root lists its nodes",
            unknown.Message
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreeQuotesTheErrorsGodotLoggedForABrokenScene()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteFile(probe.Directory, "broken.tscn", "[gd_scene format=3]\n\n[node name=\"Broken\" type=\"Node2D\"]\n\nposition = )\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() => TreeAsync(probe.Directory, "broken.tscn", null, cancellation));

        Assert.StartsWith(
            "get_scene_file_tree failed: res://broken.tscn did not load as a scene; engineErrors say why\nGodot logged:\n",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains("Parse Error", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task GetSceneFileTreePages()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string labels = string.Concat(Enumerable.Range(1, 5).Select(index => $"\n[node name=\"Label{index}\" type=\"Label\" parent=\".\"]\n"));
        WriteFile(probe.Directory, "many.tscn", "[gd_scene format=3]\n\n[node name=\"Many\" type=\"Control\"]\n" + labels);

        JsonNode first = await TreeAsync(probe.Directory, new SceneFileTreeOptions(Limit: 2, Prepare: "never"), cancellation);
        JsonNode last = await TreeAsync(probe.Directory, new SceneFileTreeOptions(Offset: 4, Limit: 2), cancellation);
        JsonNode shallow = await TreeAsync(probe.Directory, new SceneFileTreeOptions(MaxDepth: 0), cancellation);

        Assert.Equal(["."], Paths(shallow));
        Assert.Equal([".", "Label1"], Paths(first));
        Assert.Equal(6, first["total"]!.GetValue<int>());
        Assert.Equal(2, first["next"]!.GetValue<int>());
        Assert.Equal(["Label4", "Label5"], Paths(last));
        Assert.Null(last["next"]);
    }

    /// <summary>The page's nodes, each equal as JSON to its expected text, in order and keys in order.</summary>
    private static void AssertNodes(string[] expected, JsonNode page)
    {
        JsonArray nodes = page["nodes"]!.AsArray();
        Assert.Equal(expected.Length, nodes.Count);
        foreach ((string text, JsonNode? node) in expected.Zip(nodes))
        {
            Assert.Equal(JsonNode.Parse(text)!.ToJsonString(), node!.ToJsonString());
        }
    }

    private static IEnumerable<string> Paths(JsonNode page) => page["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>());

    /// <summary>An empty results entry for each group the validate result lists at path (one, or none).</summary>
    private static IEnumerable<JsonNode> Groups(JsonNode result, string path) =>
        result["results"]!.AsArray().Where(group => group!["path"]!.GetValue<string>() == path)!;

    /// <summary>A scene that instances the missing-class scene, so the script is one dependency further away.</summary>
    private static string InstancingScene(string name) =>
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://GhostInner.tscn\" id=\"1\"]\n\n"
        + $"[node name=\"{name}\" type=\"Node2D\"]\n\n[node name=\"Inner\" parent=\".\" instance=ExtResource(\"1\")]\n";

    private static void WriteFile(string directory, string name, string content) => File.WriteAllText(Path.Combine(directory, name), content);

    private static void WriteScenes(string directory)
    {
        WriteFile(directory, "enemy.tscn", EnemyScene);
        WriteFile(directory, "level.tscn", LevelScene);
        WriteFile(directory, "elite.tscn", EliteScene);
    }

    private async Task<JsonNode> ValidateAsync(string projectDir, string[] targets, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.ValidateAsync(projectDir, targets, null, cancellation))!;

    private async Task<JsonNode> TreeAsync(string projectDir, string scenePath, string? root, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.GetSceneFileTreeAsync(projectDir, scenePath, root, null, cancellation))!;

    private async Task<JsonNode> TreeAsync(string projectDir, SceneFileTreeOptions options, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.GetSceneFileTreeAsync(projectDir, "many.tscn", null, options, cancellation))!;

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
