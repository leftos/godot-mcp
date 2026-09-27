using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// export_mesh_library against the real Godot, on 3D scenes the tests write into InputProbe copies (never into the tracked
/// fixtures), the library read back as text. The copies are never imported, so every uid comes from the files themselves.
/// </summary>
public sealed class HeadlessMeshTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private const string LibraryUid = "uid://bqmeshlib00a";
    private const string CrateMeshUid = "uid://bqcratemesh0a";
    private const string BoxShapeUid = "uid://bqboxshape0a";

    // Crate, a child of the root at x 6, has a StaticBody3D at y 1 with a solid box shape at z 2 and a disabled one, and a
    // NavigationRegion3D at z 3; Ball sits three levels down, under Group (x 4) and Inner (y 2), at z 1; Empty has no mesh.
    private const string TilesScene =
        "[gd_scene format=3]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_crate\"]\n\n"
        + "[sub_resource type=\"SphereMesh\" id=\"SphereMesh_ball\"]\n\n"
        + "[sub_resource type=\"BoxShape3D\" id=\"BoxShape3D_crate\"]\n\n"
        + "[sub_resource type=\"NavigationMesh\" id=\"NavigationMesh_crate\"]\n\n"
        + "[node name=\"Tiles\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 6, 0, 0)\nmesh = SubResource(\"BoxMesh_crate\")\n\n"
        + "[node name=\"Body\" type=\"StaticBody3D\" parent=\"Crate\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0)\n\n"
        + "[node name=\"Solid\" type=\"CollisionShape3D\" parent=\"Crate/Body\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 2)\nshape = SubResource(\"BoxShape3D_crate\")\n\n"
        + "[node name=\"Ghost\" type=\"CollisionShape3D\" parent=\"Crate/Body\"]\n"
        + "shape = SubResource(\"BoxShape3D_crate\")\ndisabled = true\n\n"
        + "[node name=\"Walk\" type=\"NavigationRegion3D\" parent=\"Crate\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 3)\nnavigation_mesh = SubResource(\"NavigationMesh_crate\")\n\n"
        + "[node name=\"Group\" type=\"Node3D\" parent=\".\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 4, 0, 0)\n\n"
        + "[node name=\"Inner\" type=\"Node3D\" parent=\"Group\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0)\n\n"
        + "[node name=\"Ball\" type=\"MeshInstance3D\" parent=\"Group/Inner\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1)\nmesh = SubResource(\"SphereMesh_ball\")\n\n"
        + "[node name=\"Empty\" type=\"MeshInstance3D\" parent=\".\"]\n";

    private const string BareScene =
        "[gd_scene format=3]\n\n[node name=\"Bare\" type=\"Node3D\"]\n\n"
        + "[node name=\"Empty\" type=\"MeshInstance3D\" parent=\".\"]\n\n[node name=\"Group\" type=\"Node3D\" parent=\".\"]\n";

    private const string CrateMesh = "[gd_resource type=\"BoxMesh\" format=3 uid=\"" + CrateMeshUid + "\"]\n\n[resource]\n";

    // A crate whose mesh is saved in its own file.
    private const string DepotScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"BoxMesh\" uid=\""
        + CrateMeshUid
        + "\" path=\"res://crate_mesh.tres\" id=\"1\"]\n\n"
        + "[node name=\"Depot\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\nmesh = ExtResource(\"1\")\n";

    private const string OldLibrary = "[gd_resource type=\"MeshLibrary\" format=3 uid=\"" + LibraryUid + "\"]\n\n[resource]\n";

    // Pillar sits under Group (x 4) at y 2, with a StaticBody3D at z 1 whose box shape is at x 1 and a NavigationRegion3D at
    // z 3; Lamp, at x 3 and casting no shadow, is under Holder, a plain Node.
    private const string PropsScene =
        "[gd_scene format=3]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_pillar\"]\n\n"
        + "[sub_resource type=\"SphereMesh\" id=\"SphereMesh_lamp\"]\n\n"
        + "[sub_resource type=\"BoxShape3D\" id=\"BoxShape3D_pillar\"]\n\n"
        + "[sub_resource type=\"NavigationMesh\" id=\"NavigationMesh_pillar\"]\n\n"
        + "[node name=\"Props\" type=\"Node3D\"]\n\n"
        + "[node name=\"Group\" type=\"Node3D\" parent=\".\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 4, 0, 0)\n\n"
        + "[node name=\"Pillar\" type=\"MeshInstance3D\" parent=\"Group\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0)\nmesh = SubResource(\"BoxMesh_pillar\")\n\n"
        + "[node name=\"Body\" type=\"StaticBody3D\" parent=\"Group/Pillar\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1)\n\n"
        + "[node name=\"Solid\" type=\"CollisionShape3D\" parent=\"Group/Pillar/Body\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0)\nshape = SubResource(\"BoxShape3D_pillar\")\n\n"
        + "[node name=\"Walk\" type=\"NavigationRegion3D\" parent=\"Group/Pillar\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 3)\nnavigation_mesh = SubResource(\"NavigationMesh_pillar\")\n\n"
        + "[node name=\"Holder\" type=\"Node\" parent=\".\"]\n\n"
        + "[node name=\"Lamp\" type=\"MeshInstance3D\" parent=\"Holder\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 3, 0, 0)\ncast_shadow = 0\nmesh = SubResource(\"SphereMesh_lamp\")\n";

    // Two MeshInstance3D nodes named Crate: the first, a box at x 1, has a NavigationRegion3D at z 3; the later one, a sphere
    // at x 2 under Shelf, has none.
    private const string TwinsScene =
        "[gd_scene format=3]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_first\"]\n\n"
        + "[sub_resource type=\"SphereMesh\" id=\"SphereMesh_second\"]\n\n"
        + "[sub_resource type=\"NavigationMesh\" id=\"NavigationMesh_first\"]\n\n"
        + "[node name=\"Twins\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0)\nmesh = SubResource(\"BoxMesh_first\")\n\n"
        + "[node name=\"Walk\" type=\"NavigationRegion3D\" parent=\"Crate\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 3)\nnavigation_mesh = SubResource(\"NavigationMesh_first\")\n\n"
        + "[node name=\"Shelf\" type=\"Node3D\" parent=\".\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\"Shelf\"]\n"
        + "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 0, 0)\nmesh = SubResource(\"SphereMesh_second\")\n";

    private const string BoxShape = "[gd_resource type=\"BoxShape3D\" format=3 uid=\"" + BoxShapeUid + "\"]\n\n[resource]\n";

    // A crate whose mesh and collision shape are saved in their own files, its mesh's surface overridden with a red material.
    private const string ShedScene =
        "[gd_scene load_steps=4 format=3]\n\n[ext_resource type=\"BoxMesh\" uid=\""
        + CrateMeshUid
        + "\" path=\"res://crate_mesh.tres\" id=\"1\"]\n[ext_resource type=\"BoxShape3D\" uid=\""
        + BoxShapeUid
        + "\" path=\"res://box_shape.tres\" id=\"2\"]\n\n"
        + "[sub_resource type=\"StandardMaterial3D\" id=\"StandardMaterial3D_red\"]\nalbedo_color = Color(1, 0, 0, 1)\n\n"
        + "[node name=\"Shed\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\n"
        + "mesh = ExtResource(\"1\")\nsurface_material_override/0 = SubResource(\"StandardMaterial3D_red\")\n\n"
        + "[node name=\"Body\" type=\"StaticBody3D\" parent=\"Crate\"]\n\n"
        + "[node name=\"Solid\" type=\"CollisionShape3D\" parent=\"Crate/Body\"]\nshape = ExtResource(\"2\")\n";

    private const string GridScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"MeshLibrary\" path=\"res://tiles.res\" id=\"1\"]\n\n"
        + "[node name=\"Grid\" type=\"GridMap\"]\nmesh_library = ExtResource(\"1\")\n";

    // A GridMap painting with tiles.tres, which is saved with its uid.
    private const string CellScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"MeshLibrary\" uid=\""
        + LibraryUid
        + "\" path=\"res://tiles.tres\" id=\"1\"]\n\n"
        + "[node name=\"Cell\" type=\"GridMap\"]\nmesh_library = ExtResource(\"1\")\n";

    // cell.tscn instanced beside a crate.
    private const string YardScene =
        "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://cell.tscn\" id=\"1\"]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_crate\"]\n\n"
        + "[node name=\"Yard\" type=\"Node3D\"]\n\n"
        + "[node name=\"Cell\" parent=\".\" instance=ExtResource(\"1\")]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\nmesh = SubResource(\"BoxMesh_crate\")\n";

    // A plain resource whose metadata holds tiles.tres.
    private const string HolderResource =
        "[gd_resource type=\"Resource\" load_steps=2 format=3]\n\n"
        + "[ext_resource type=\"MeshLibrary\" path=\"res://tiles.tres\" id=\"1\"]\n\n"
        + "[resource]\nmetadata/library = ExtResource(\"1\")\n";

    // A crate whose root's metadata holds holder.tres.
    private const string StoreScene =
        "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Resource\" path=\"res://holder.tres\" id=\"1\"]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_crate\"]\n\n"
        + "[node name=\"Store\" type=\"Node3D\"]\nmetadata/holder = ExtResource(\"1\")\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\nmesh = SubResource(\"BoxMesh_crate\")\n";

    // loop_a.tres and loop_b.tres each declare the other; ring.tscn declares loop_a.tres and a missing file, and uses neither.
    private const string LoopA =
        "[gd_resource type=\"Resource\" load_steps=2 format=3]\n\n"
        + "[ext_resource type=\"Resource\" path=\"res://loop_b.tres\" id=\"1\"]\n\n[resource]\n";

    private const string LoopB =
        "[gd_resource type=\"Resource\" load_steps=2 format=3]\n\n"
        + "[ext_resource type=\"Resource\" path=\"res://loop_a.tres\" id=\"1\"]\n\n[resource]\n";

    private const string RingScene =
        "[gd_scene load_steps=4 format=3]\n\n[ext_resource type=\"Resource\" path=\"res://loop_a.tres\" id=\"1\"]\n"
        + "[ext_resource type=\"Resource\" path=\"res://missing.tres\" id=\"2\"]\n\n"
        + "[sub_resource type=\"BoxMesh\" id=\"BoxMesh_crate\"]\n\n"
        + "[node name=\"Ring\" type=\"Node3D\"]\n\n"
        + "[node name=\"Crate\" type=\"MeshInstance3D\" parent=\".\"]\nmesh = SubResource(\"BoxMesh_crate\")\n";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessMeshTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryTakesEveryMeshInstanceAtAnyDepth()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "tiles.tscn", "lib/tiles.tres", cancellationToken: cancellation)
        )!;

        Assert.Null(exported["errors"]);
        // No two MeshInstance3D nodes share a name, so no item was taken over.
        Assert.Null(exported["replaced"]);
        Assert.Equal("res://lib/tiles.tres", exported["outputPath"]!.GetValue<string>());
        Assert.Equal(
            """[{"id":0,"name":"Crate","shapes":1,"navigation":true},{"id":1,"name":"Ball","shapes":0,"navigation":false}]""",
            exported["items"]!.ToJsonString()
        );
        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "lib", "tiles.tres"));
        Assert.StartsWith("[gd_resource type=\"MeshLibrary\"", library[0], StringComparison.Ordinal);
        Assert.Contains("item/0/name = \"Crate\"", library);
        Assert.Contains("item/1/name = \"Ball\"", library);
        // Ball's transform is its own: Group's and Inner's offsets are left out, as the editor leaves them.
        Assert.Contains("item/1/mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1)", library);
        Assert.Contains("item/0/mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 6, 0, 0)", library);
        // The scene's built-in meshes are written into the library.
        Assert.Contains(library, line => line.StartsWith("[sub_resource type=\"SphereMesh\"", StringComparison.Ordinal));
        Assert.DoesNotContain(library, line => line.StartsWith("[ext_resource ", StringComparison.Ordinal));
        Assert.Equal(TilesScene, File.ReadAllText(Path.Combine(probe.Directory, "tiles.tscn")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryTakesShapesFromStaticBodyChildren()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        await _tools.ExportMeshLibraryAsync(probe.Directory, "tiles.tscn", "tiles.tres", cancellationToken: cancellation);

        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "tiles.tres"));
        // One shape, the enabled one, placed as the editor places it: the item's transform, then the body's, then the shape's.
        string shapes = Assert.Single(library, line => line.StartsWith("item/0/shapes = ", StringComparison.Ordinal));
        Assert.StartsWith("item/0/shapes = [SubResource(\"", shapes, StringComparison.Ordinal);
        Assert.EndsWith("\"), Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 6, 1, 2)]", shapes, StringComparison.Ordinal);
        Assert.Contains(library, line => line.StartsWith("[sub_resource type=\"BoxShape3D\"", StringComparison.Ordinal));
        Assert.Contains(library, line => line.StartsWith("item/0/navigation_mesh = SubResource(\"", StringComparison.Ordinal));
        Assert.Contains("item/0/navigation_mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 6, 0, 3)", library);
        Assert.Contains("item/1/shapes = []", library);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryFiltersByName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "tiles.tscn", "tiles.tres", ["Ball"], cancellationToken: cancellation)
        )!;

        Assert.Equal("""[{"id":0,"name":"Ball","shapes":0,"navigation":false}]""", exported["items"]!.ToJsonString());
        string library = File.ReadAllText(Path.Combine(probe.Directory, "tiles.tres"));
        Assert.Contains("item/0/name = \"Ball\"", library, StringComparison.Ordinal);
        Assert.DoesNotContain("Crate", library, StringComparison.Ordinal);
        Assert.DoesNotContain("item/1/", library, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryRefusesAnUnknownName()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ExportMeshLibraryAsync(probe.Directory, "tiles.tscn", "tiles.tres", ["Ball", "Barrel"], cancellationToken: cancellation)
        );

        Assert.Equal(
            "export_mesh_library failed: res://tiles.tscn has no MeshInstance3D named Barrel; its mesh items are: Crate, Ball.",
            refused.Message
        );
        Assert.False(File.Exists(Path.Combine(probe.Directory, "tiles.tres")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryRefusesASceneWithoutMeshes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "bare.tscn"), BareScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ExportMeshLibraryAsync(probe.Directory, "bare.tscn", "tiles.tres", cancellationToken: cancellation)
        );

        Assert.Equal("export_mesh_library failed: res://bare.tscn has no MeshInstance3D with a mesh.", refused.Message);
        Assert.False(File.Exists(Path.Combine(probe.Directory, "tiles.tres")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryOverwriteKeepsTheUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "crate_mesh.tres"), CrateMesh);
        File.WriteAllText(Path.Combine(probe.Directory, "depot.tscn"), DepotScene);
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tres"), OldLibrary);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(
                probe.Directory,
                "depot.tscn",
                "tiles.tres",
                options: new SceneWriteOptions(Overwrite: true),
                cancellationToken: cancellation
            )
        )!;

        Assert.Equal("""[{"id":0,"name":"Crate","shapes":0,"navigation":false}]""", exported["items"]!.ToJsonString());
        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "tiles.tres"));
        Assert.StartsWith("[gd_resource type=\"MeshLibrary\"", library[0], StringComparison.Ordinal);
        Assert.EndsWith($" uid=\"{LibraryUid}\"]", library[0], StringComparison.Ordinal);
        // The mesh saved in its own file stays a reference to it, with its uid.
        Assert.Contains(
            library,
            line =>
                line.StartsWith("[ext_resource ", StringComparison.Ordinal)
                && line.Contains($"uid=\"{CrateMeshUid}\" path=\"res://crate_mesh.tres\"", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(library, line => line.StartsWith("[sub_resource type=\"BoxMesh\"", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryPlacesAnItemByTheNodesLocalTransform()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "props.tscn"), PropsScene);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "props.tscn", "props.tres", cancellationToken: cancellation)
        )!;

        Assert.Equal("""{"id":0,"name":"Pillar","shapes":1,"navigation":true}""", exported["items"]![0]!.ToJsonString());
        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "props.tres"));
        // Group's offset is left out of the item, its shape and its navigation mesh alike.
        Assert.Contains("item/0/mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0)", library);
        string shapes = Assert.Single(library, line => line.StartsWith("item/0/shapes = ", StringComparison.Ordinal));
        Assert.EndsWith("\"), Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 2, 1)]", shapes, StringComparison.Ordinal);
        // The navigation mesh is the item's transform, then the region's; the editor would leave the item's out.
        Assert.Contains("item/0/navigation_mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 3)", library);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryWalksThroughANodeThatIsNotANode3D()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "props.tscn"), PropsScene);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "props.tscn", "props.tres", cancellationToken: cancellation)
        )!;

        Assert.Equal("""{"id":1,"name":"Lamp","shapes":0,"navigation":false}""", exported["items"]![1]!.ToJsonString());
        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "props.tres"));
        Assert.Contains("item/1/mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 3, 0, 0)", library);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryKeepsTheShadowCasting()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "props.tscn"), PropsScene);

        await _tools.ExportMeshLibraryAsync(probe.Directory, "props.tscn", "props.tres", cancellationToken: cancellation);

        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "props.tres"));
        Assert.Contains("item/1/mesh_cast_shadow = 0", library);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryLetsALaterNodeOfTheSameNameTakeTheItemOverKeepingTheNavigationMesh()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "twins.tscn"), TwinsScene);

        JsonNode exported = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "twins.tscn", "twins.tres", cancellationToken: cancellation)
        )!;

        Assert.Equal("""[{"id":0,"name":"Crate","shapes":0,"navigation":true}]""", exported["items"]!.ToJsonString());
        Assert.Equal("""[{"name":"Crate","count":2}]""", exported["replaced"]?.ToJsonString());
        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "twins.tres"));
        // The later node's mesh and transform, and the earlier node's navigation mesh, placed as it was under that node.
        Assert.Contains(library, line => line.StartsWith("[sub_resource type=\"SphereMesh\"", StringComparison.Ordinal));
        Assert.DoesNotContain(library, line => line.StartsWith("[sub_resource type=\"BoxMesh\"", StringComparison.Ordinal));
        Assert.Contains("item/0/mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 0, 0)", library);
        Assert.Contains(library, line => line.StartsWith("item/0/navigation_mesh = SubResource(\"", StringComparison.Ordinal));
        Assert.Contains("item/0/navigation_mesh_transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 3)", library);
        Assert.DoesNotContain(library, line => line.StartsWith("item/1/", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryCopiesAnExternalMeshWhoseSurfaceIsOverridden()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(WriteShed(new ProbeProject()));

        await _tools.ExportMeshLibraryAsync(probe.Directory, "shed.tscn", "shed.tres", cancellationToken: cancellation);

        string library = File.ReadAllText(Path.Combine(probe.Directory, "shed.tres"));
        // The item's mesh is a copy written into the library, with the node's material on its surface.
        Assert.Matches("\\[sub_resource type=\"BoxMesh\" id=\"[^\"]+\"\\]\nmaterial = SubResource\\(\"StandardMaterial3D_[^\"]+\"\\)\n", library);
        Assert.Contains("albedo_color = Color(1, 0, 0, 1)", library, StringComparison.Ordinal);
        Assert.Contains("item/0/mesh = SubResource(\"", library, StringComparison.Ordinal);
        Assert.DoesNotContain("path=\"res://crate_mesh.tres\"", library, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryKeepsAnExternalShapeAReference()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(WriteShed(new ProbeProject()));

        await _tools.ExportMeshLibraryAsync(probe.Directory, "shed.tscn", "shed.tres", cancellationToken: cancellation);

        string[] library = File.ReadAllLines(Path.Combine(probe.Directory, "shed.tres"));
        Assert.Contains(
            library,
            line =>
                line.StartsWith("[ext_resource ", StringComparison.Ordinal)
                && line.Contains($"uid=\"{BoxShapeUid}\" path=\"res://box_shape.tres\"", StringComparison.Ordinal)
        );
        Assert.Contains(library, line => line.StartsWith("item/0/shapes = [ExtResource(\"", StringComparison.Ordinal));
        Assert.DoesNotContain(library, line => line.StartsWith("[sub_resource type=\"BoxShape3D\"", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryWritesALoadableResAndAnOverwriteGetsANewUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tscn"), TilesScene);
        File.WriteAllText(Path.Combine(probe.Directory, "grid.tscn"), GridScene);
        string output = Path.Combine(probe.Directory, "tiles.res");

        await _tools.ExportMeshLibraryAsync(probe.Directory, "tiles.tscn", "tiles.res", cancellationToken: cancellation);
        byte[] first = File.ReadAllBytes(output);
        JsonNode read = JsonNode.Parse(
            await _tools.GetNodePropertiesAsync(probe.Directory, "grid.tscn", [new NodePropertyQuery(".", ["mesh_library"])], null, cancellation)
        )!;
        await _tools.ExportMeshLibraryAsync(
            probe.Directory,
            "tiles.tscn",
            "tiles.res",
            options: new SceneWriteOptions(Overwrite: true),
            cancellationToken: cancellation
        );

        Assert.Equal("RSRC", Encoding.ASCII.GetString(first, 0, 4));
        Assert.Equal("MeshLibrary", BinaryClass(first));
        JsonNode meshLibrary = read["results"]![0]!["properties"]!["mesh_library"]!;
        Assert.Equal("res://tiles.res", meshLibrary["resource"]!.GetValue<string>());
        Assert.Equal("MeshLibrary", meshLibrary["class"]!.GetValue<string>());
        long uid = BinaryUid(first);
        Assert.NotEqual(-1, uid);
        Assert.NotEqual(uid, BinaryUid(File.ReadAllBytes(output)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryRefusesAFileTheSceneUses()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "crate_mesh.tres"), CrateMesh);
        File.WriteAllText(Path.Combine(probe.Directory, "depot.tscn"), DepotScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ExportMeshLibraryAsync(
                probe.Directory,
                "depot.tscn",
                "crate_mesh.tres",
                options: new SceneWriteOptions(Overwrite: true),
                cancellationToken: cancellation
            )
        );

        Assert.Equal(
            "export_mesh_library failed: res://crate_mesh.tres is used by res://depot.tscn; export the library to another file.",
            refused.Message
        );
        Assert.Equal(CrateMesh, File.ReadAllText(Path.Combine(probe.Directory, "crate_mesh.tres")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryRefusesALibraryAnInstancedSceneUses()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tres"), OldLibrary);
        File.WriteAllText(Path.Combine(probe.Directory, "cell.tscn"), CellScene);
        File.WriteAllText(Path.Combine(probe.Directory, "yard.tscn"), YardScene);
        byte[] before = File.ReadAllBytes(Path.Combine(probe.Directory, "tiles.tres"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ExportMeshLibraryAsync(
                probe.Directory,
                "yard.tscn",
                "tiles.tres",
                options: new SceneWriteOptions(Overwrite: true),
                cancellationToken: cancellation
            )
        );

        Assert.Equal(
            "export_mesh_library failed: res://tiles.tres is used by res://yard.tscn through res://cell.tscn; "
                + "export the library to another file.",
            refused.Message
        );
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(probe.Directory, "tiles.tres")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryRefusesALibraryReachedThroughAResource()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "tiles.tres"), OldLibrary);
        File.WriteAllText(Path.Combine(probe.Directory, "holder.tres"), HolderResource);
        File.WriteAllText(Path.Combine(probe.Directory, "store.tscn"), StoreScene);
        byte[] before = File.ReadAllBytes(Path.Combine(probe.Directory, "tiles.tres"));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ExportMeshLibraryAsync(
                probe.Directory,
                "store.tscn",
                "tiles.tres",
                options: new SceneWriteOptions(Overwrite: true),
                cancellationToken: cancellation
            )
        );

        Assert.Equal(
            "export_mesh_library failed: res://tiles.tres is used by res://store.tscn through res://holder.tres; "
                + "export the library to another file.",
            refused.Message
        );
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(probe.Directory, "tiles.tres")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ExportMeshLibraryAllowsAnUnrelatedLibraryInACyclicProject()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        File.WriteAllText(Path.Combine(probe.Directory, "loop_a.tres"), LoopA);
        File.WriteAllText(Path.Combine(probe.Directory, "loop_b.tres"), LoopB);
        File.WriteAllText(Path.Combine(probe.Directory, "ring.tscn"), RingScene);

        JsonNode result = JsonNode.Parse(
            await _tools.ExportMeshLibraryAsync(probe.Directory, "ring.tscn", "ring.tres", cancellationToken: cancellation)
        )!;

        JsonNode item = Assert.Single(result["items"]!.AsArray())!;
        Assert.Equal("Crate", item["name"]!.GetValue<string>());
        Assert.Contains("[gd_resource type=\"MeshLibrary\"", File.ReadAllText(Path.Combine(probe.Directory, "ring.tres")), StringComparison.Ordinal);
    }

    // A binary resource's header (4.7.2 core/io/resource_format_binary.cpp L2136-2178, little-endian): RSRC, four int32s,
    // the class as an int32 length counting its NUL and the UTF-8 bytes, the import metadata offset (int64), the format
    // flags (int32), then the uid (int64).
    private static string BinaryClass(byte[] file) => Encoding.UTF8.GetString(file, 28, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24)) - 1);

    private static long BinaryUid(byte[] file) =>
        BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(28 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24)) + 12));

    private static ProbeProject WriteShed(ProbeProject probe)
    {
        File.WriteAllText(Path.Combine(probe.Directory, "crate_mesh.tres"), CrateMesh);
        File.WriteAllText(Path.Combine(probe.Directory, "box_shape.tres"), BoxShape);
        File.WriteAllText(Path.Combine(probe.Directory, "shed.tscn"), ShedScene);
        return probe;
    }

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
