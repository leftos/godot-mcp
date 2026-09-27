using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// batch_scene_operations against the real Godot, on scenes the tests write into InputProbe copies (never into the tracked
/// fixtures), read back as text: the steps share one open scene, which is saved once, and a failed step saves and writes nothing.
/// </summary>
public sealed class HeadlessBatchTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private const string StageUid = "uid://bqstage00000a";

    private const string StageScene =
        "[gd_scene format=3 uid=\"" + StageUid + "\"]\n\n[node name=\"Stage\" type=\"Node2D\"]\n\n[node name=\"Box\" type=\"Node2D\" parent=\".\"]\n";

    private const string YardScene = "[gd_scene format=3 uid=\"uid://bqyard000000a\"]\n\n[node name=\"Yard\" type=\"Node3D\"]\n";

    private const string TilesScene =
        "[gd_scene format=3]\n\n[sub_resource type=\"BoxMesh\" id=\"BoxMesh_crate\"]\n\n[node name=\"Tiles\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\nmesh = SubResource(\"BoxMesh_crate\")\n";

    private const string TilesLibrary = "[gd_resource type=\"MeshLibrary\" format=3 uid=\"uid://bqtileslib00a\"]\n\n[resource]\n";

    private const string SpriteScene =
        "[gd_scene format=3]\n\n[node name=\"Stage\" type=\"Node2D\"]\n\n[node name=\"Icon\" type=\"Sprite2D\" parent=\".\"]\n";

    // A 1x1 PNG.
    private const string DotPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessBatchTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task EachStepSeesTheOnesBeforeItAndTheSceneIsSavedOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "stage.tscn"), StageScene);
        SceneBatchStep[] steps =
        [
            AddNode("Button", "Go"),
            new("set_node_properties", new JsonObject { ["updates"] = new JsonArray(Update("Go", "text", "Go")) }),
            new(
                "connect_signal",
                new JsonObject
                {
                    ["nodePath"] = "Go",
                    ["signal"] = "pressed",
                    ["target"] = new JsonObject { ["nodePath"] = ".", ["method"] = "hide" },
                }
            ),
        ];

        JsonObject batch = await BatchAsync(probe.Directory, "stage.tscn", steps, cancellation);

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Null(batch["failedAt"]);
        Assert.Equal(StageUid, batch["uid"]!.GetValue<string>());
        JsonArray entries = batch["steps"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.True(entry!["ok"]!.GetValue<bool>(), batch.ToJsonString()));
        Assert.Equal("Go", entries[0]!["result"]!["path"]!.GetValue<string>());
        Assert.Equal("Go", entries[1]!["result"]!["results"]![0]!["after"]!.GetValue<string>());
        Assert.Equal("Go", entries[2]!["result"]!["from"]!.GetValue<string>());
        string saved = Read(probe.Directory, "stage.tscn");
        Assert.StartsWith($"[gd_scene format=3 uid=\"{StageUid}\"]", saved, StringComparison.Ordinal);
        Assert.Contains("[node name=\"Go\" type=\"Button\" parent=\".\"", saved, StringComparison.Ordinal);
        Assert.Contains("\ntext = \"Go\"\n", saved, StringComparison.Ordinal);
        Assert.Contains("[connection signal=\"pressed\" from=\"Go\" to=\".\" method=\"hide\"]", saved, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AFailedStepStopsTheBatchAndSavesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "stage.tscn"), StageScene);
        JsonObject position = new() { ["x"] = 1, ["y"] = 2 };
        SceneBatchStep[] steps =
        [
            AddNode("Node2D", "Marker"),
            new("set_node_properties", new JsonObject { ["updates"] = new JsonArray(Update("Marker", "position", position)) }),
            DeleteNodes("Missing"),
            AddNode("Node2D", "Never"),
        ];

        JsonObject batch = await BatchAsync(probe.Directory, "stage.tscn", steps, cancellation);

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(2, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal("delete_nodes", batch["failedAt"]!["tool"]!.GetValue<string>());
        Assert.Contains("Missing", batch["failedAt"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(batch["uid"]);
        JsonArray entries = batch["steps"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.Equal("Marker", entries[0]!["result"]!["path"]!.GetValue<string>());
        Assert.Equal("Marker", entries[1]!["result"]!["results"]![0]!["nodePath"]!.GetValue<string>());
        Assert.False(entries[2]!["ok"]!.GetValue<bool>());
        Assert.Equal(batch["failedAt"]!["error"]!.GetValue<string>(), entries[2]!["error"]!.GetValue<string>());
        Assert.Equal(StageScene, Read(probe.Directory, "stage.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMeshLibraryBeforeAFailedStepIsNotWritten()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        JsonObject batch = await BatchAsync(probe.Directory, "tiles.tscn", [ExportLibrary("tiles.tres"), DeleteNodes("Missing")], cancellation);

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(1, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.True(batch["steps"]![0]!["ok"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(File.Exists(Path.Combine(probe.Directory, "tiles.tres")));
        Assert.Equal(TilesScene, Read(probe.Directory, "tiles.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AMeshLibraryExportsTheNodesAddedBeforeIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "yard.tscn"), YardScene);
        JsonObject options = new() { ["properties"] = new JsonObject { ["mesh"] = new JsonObject { ["type"] = "BoxMesh" } } };

        JsonObject batch = await BatchAsync(
            probe.Directory,
            "yard.tscn",
            [
                new(
                    "add_node",
                    new JsonObject
                    {
                        ["nodeType"] = "MeshInstance3D",
                        ["nodeName"] = "Crate",
                        ["options"] = options,
                    }
                ),
                ExportLibrary("yard.tres"),
            ],
            cancellation
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        JsonNode item = Assert.Single(batch["steps"]![1]!["result"]!["items"]!.AsArray())!;
        Assert.Equal("Crate", item["name"]!.GetValue<string>());
        Assert.Contains("[gd_resource type=\"MeshLibrary\"", Read(probe.Directory, "yard.tres"), StringComparison.Ordinal);
        Assert.Equal("uid://bqyard000000a", batch["uid"]!.GetValue<string>());
        Assert.Contains("[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"", Read(probe.Directory, "yard.tscn"), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABatchThatAttachesALibraryThenExportsOverItIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tres"), TilesLibrary);
        JsonObject gridOptions = new() { ["properties"] = new JsonObject { ["mesh_library"] = "res://tiles.tres" } };
        JsonObject export = new()
        {
            ["outputPath"] = "tiles.tres",
            ["options"] = new JsonObject { ["overwrite"] = true },
        };

        JsonObject batch = await BatchAsync(
            probe.Directory,
            "tiles.tscn",
            [
                new(
                    "add_node",
                    new JsonObject
                    {
                        ["nodeType"] = "GridMap",
                        ["nodeName"] = "Grid",
                        ["options"] = gridOptions,
                    }
                ),
                new("export_mesh_library", export),
            ],
            cancellation
        );

        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(1, batch["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal("export_mesh_library", batch["failedAt"]!["tool"]!.GetValue<string>());
        Assert.Equal(
            "res://tiles.tres is used by res://tiles.tscn through node Grid; export the library to another file.",
            batch["failedAt"]!["error"]!.GetValue<string>()
        );
        Assert.Null(batch["uid"]);
        Assert.Equal(TilesLibrary, Read(probe.Directory, "tiles.tres"));
        Assert.Equal(TilesScene, Read(probe.Directory, "tiles.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ABatchOfOnlyMeshLibrariesLeavesTheSceneAlone()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        JsonObject batch = await BatchAsync(
            probe.Directory,
            "tiles.tscn",
            [ExportLibrary("tiles.tres"), ExportLibrary("more/tiles.res")],
            cancellation
        );

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Null(batch["uid"]);
        Assert.True(File.Exists(Path.Combine(probe.Directory, "tiles.tres")));
        Assert.True(File.Exists(Path.Combine(probe.Directory, "more", "tiles.res")));
        Assert.Equal(TilesScene, Read(probe.Directory, "tiles.tscn"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LoadSpriteImportsANewPngFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "sprite.tscn"), SpriteScene);
        Directory.CreateDirectory(Path.Combine(probe.Directory, "art"));
        File.WriteAllBytes(Path.Combine(probe.Directory, "art", "dot.png"), Convert.FromBase64String(DotPng));
        JsonObject load = new() { ["nodePath"] = "Icon", ["texturePath"] = "res://art/dot.png" };

        JsonObject batch = await BatchAsync(probe.Directory, "sprite.tscn", [AddNode("Node2D", "Extra"), new("load_sprite", load)], cancellation);

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.True(File.Exists(Path.Combine(probe.Directory, "art", "dot.png.import")));
        JsonNode texture = batch["steps"]![1]!["result"]!["texture"]!;
        Assert.Equal("res://art/dot.png", texture["resource"]!.GetValue<string>());
        string uid = texture["uid"]!.GetValue<string>();
        Assert.StartsWith("uid://", uid, StringComparison.Ordinal);
        Assert.Contains($"uid=\"{uid}\" path=\"res://art/dot.png\"", Read(probe.Directory, "sprite.tscn"), StringComparison.Ordinal);
    }

    private static SceneBatchStep AddNode(string type, string name) => new("add_node", new JsonObject { ["nodeType"] = type, ["nodeName"] = name });

    private static SceneBatchStep DeleteNodes(string path) => new("delete_nodes", new JsonObject { ["nodePaths"] = new JsonArray(path) });

    private static SceneBatchStep ExportLibrary(string outputPath) => new("export_mesh_library", new JsonObject { ["outputPath"] = outputPath });

    private static JsonObject Update(string nodePath, string property, JsonNode value) =>
        new()
        {
            ["nodePath"] = nodePath,
            ["property"] = property,
            ["value"] = value,
        };

    private static string Read(string directory, string relative) => File.ReadAllText(Path.Combine(directory, relative));

    private async Task<JsonObject> BatchAsync(string projectDir, string scenePath, SceneBatchStep[] steps, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.BatchSceneOperationsAsync(projectDir, scenePath, steps, cancellation))!.AsObject();

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
