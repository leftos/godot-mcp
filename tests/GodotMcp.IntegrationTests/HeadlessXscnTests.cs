using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The headless scene tools on <c>.xscn</c> scenes and <c>.xres</c> resources, read and written through a project's own C#
/// loader and saver (<see cref="FakeXamlFormat"/>, standing in for the godot-xaml addon) in CsProbe copies: the comment lines
/// the saver keeps, the <c>.uid</c> file it writes, and the hint a project that cannot load the format gets.
/// </summary>
public sealed class HeadlessXscnTests : IAsyncDisposable
{
    private const string Comment = "; kept by the saver";
    private const string SceneUid = "uid://bqxscnscene0a";
    private const string TargetUid = "uid://bqxscntarget";

    private const string SceneBody =
        "[gd_scene format=3]\n\n[node name=\"Root\" type=\"Node2D\"]\n\n[node name=\"A\" type=\"Node2D\" parent=\".\"]\n\n"
        + "[node name=\"B\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Btn\" type=\"Button\" parent=\".\"]\n";

    private const string SceneText = Comment + "\n" + SceneBody;

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];

    public HeadlessXscnTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task CreateSceneWritesAXscnAndItsUidFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();

        JsonNode created = JsonNode.Parse(await _tools.CreateSceneAsync(project, "made.xscn", cancellationToken: cancellation))!;

        Assert.Contains("res://made.xscn.uid", created["uidFilesWritten"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.Equal(created["uid"]!.GetValue<string>(), File.ReadAllText(Path.Combine(project, "made.xscn.uid")).Trim());
        JsonNode tree = await TreeAsync(project, "made.xscn", cancellation);
        Assert.Equal("Made", tree["nodes"]![0]!["name"]!.GetValue<string>());
        JsonNode signals = JsonNode.Parse(await _tools.GetNodeSignalsAsync(project, "made.xscn", ".", cancellationToken: cancellation))!;
        Assert.NotNull(signals);
        JsonNode properties = JsonNode.Parse(
            await _tools.GetNodePropertiesAsync(project, "made.xscn", [new NodePropertyQuery(".", ["position"])], cancellationToken: cancellation)
        )!;
        Assert.Equal("Node2D", properties["results"]![0]!["type"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnEditKeepsTheCommentAndTheUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "edited.xscn", SceneUid);

        await _tools.SetNodePropertiesAsync(
            project,
            "edited.xscn",
            [new PropertyUpdate("A", "position", Json("{\"x\": 3, \"y\": 4}"))],
            cancellation
        );
        AssertKept(project, "edited.xscn", SceneUid);
        Assert.Contains("position = Vector2(3, 4)", File.ReadAllText(Path.Combine(project, "edited.xscn")), StringComparison.Ordinal);

        await _tools.DeleteNodesAsync(project, "edited.xscn", ["B"], cancellation);
        await _tools.AddNodeAsync(project, "edited.xscn", "Node2D", "C", cancellationToken: cancellation);
        await _tools.MoveNodeAsync(project, "edited.xscn", "C", new MoveNodeOptions(Parent: "A"), cancellation);
        await _tools.DuplicateNodeAsync(project, "edited.xscn", "A", cancellationToken: cancellation);
        await _tools.ConnectSignalAsync(project, "edited.xscn", "Btn", "pressed", new ConnectTarget(".", "hide"), cancellation);
        SceneBatchStep add = new("add_node", new JsonObject { ["nodeType"] = "Node2D", ["nodeName"] = "D" });
        JsonNode batch = JsonNode.Parse(await _tools.BatchSceneOperationsAsync(project, "edited.xscn", [add], cancellation))!;

        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        AssertKept(project, "edited.xscn", SceneUid);
        string[] names =
        [
            .. (await TreeAsync(project, "edited.xscn", cancellation))["nodes"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()),
        ];
        Assert.Equal([".", "A", "A/C", "A2", "A2/C", "Btn", "D"], names);
        Assert.Contains(
            "[connection signal=\"pressed\" from=\"Btn\" to=\".\" method=\"hide\"]",
            File.ReadAllText(Path.Combine(project, "edited.xscn"))
        );
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveAsKeepsTheCommentWithANewUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "source.xscn", SceneUid);
        byte[] source = File.ReadAllBytes(Path.Combine(project, "source.xscn"));

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(project, "source.xscn", "copy.xscn", cancellationToken: cancellation))!;

        string copyUid = File.ReadAllText(Path.Combine(project, "copy.xscn.uid")).Trim();
        Assert.StartsWith(Comment + "\n", File.ReadAllText(Path.Combine(project, "copy.xscn")), StringComparison.Ordinal);
        Assert.NotEqual(SceneUid, copyUid);
        Assert.Equal(copyUid, saved["uid"]!.GetValue<string>());
        Assert.Contains("res://copy.xscn.uid", saved["uidFilesWritten"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(project, "source.xscn")));
        Assert.Equal(SceneUid, File.ReadAllText(Path.Combine(project, "source.xscn.uid")).Trim());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveAsOverAnExistingXscnTakesItsUid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "source.xscn", SceneUid);
        WriteScene(project, "target.xscn", TargetUid);

        JsonNode saved = JsonNode.Parse(
            await _tools.SaveSceneAsync(project, "source.xscn", "target.xscn", new SceneWriteOptions(Overwrite: true), cancellation)
        )!;

        Assert.Equal(TargetUid, saved["uid"]!.GetValue<string>());
        AssertKept(project, "target.xscn", TargetUid);
        Assert.Null(saved["uidFilesWritten"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveAsToATscnWritesNoComment()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "source.xscn", SceneUid);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(project, "source.xscn", "plain.tscn", cancellationToken: cancellation))!;

        string text = File.ReadAllText(Path.Combine(project, "plain.tscn"));
        Assert.StartsWith("[gd_scene", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Comment, text, StringComparison.Ordinal);
        Assert.Null(saved["warning"]);
        Assert.DoesNotContain(
            text.Split('\n'),
            line =>
                line.StartsWith(FakeXamlFormat.BodyPrefix, StringComparison.Ordinal)
                || line.StartsWith(FakeXamlFormat.CommentPrefix, StringComparison.Ordinal)
        );
        JsonNode tree = await TreeAsync(project, "plain.tscn", cancellation);
        Assert.Equal("Root", tree["nodes"]![0]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveAsToAXscnFromATscn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        File.WriteAllText(Path.Combine(project, "source.tscn"), SceneBody);

        JsonNode saved = JsonNode.Parse(await _tools.SaveSceneAsync(project, "source.tscn", "copy.xscn", cancellationToken: cancellation))!;

        string text = File.ReadAllText(Path.Combine(project, "copy.xscn"));
        Assert.StartsWith(FakeXamlFormat.BodyPrefix + "[gd_scene", text, StringComparison.Ordinal);
        Assert.Null(saved["warning"]);
        Assert.Equal(saved["uid"]!.GetValue<string>(), File.ReadAllText(Path.Combine(project, "copy.xscn.uid")).Trim());
        JsonNode tree = await TreeAsync(project, "copy.xscn", cancellation);
        Assert.Equal("Root", tree["nodes"]![0]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASaverFailureAfterTheCopyLeavesTheTargetAsItWas()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        File.WriteAllText(Path.Combine(project, "fail.xscn"), Comment + "\n[gd_scene format=3]\n\n[node name=\"FailSave\" type=\"Node2D\"]\n");
        File.WriteAllText(Path.Combine(project, "fail.xscn.uid"), SceneUid + "\n");
        WriteScene(project, "target.xscn", TargetUid);
        byte[] target = File.ReadAllBytes(Path.Combine(project, "target.xscn"));

        McpException over = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(project, "fail.xscn", "target.xscn", new SceneWriteOptions(Overwrite: true), cancellation)
        );
        McpException fresh = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(project, "fail.xscn", "fresh.xscn", cancellationToken: cancellation)
        );

        Assert.Contains("could not be saved", over.Message, StringComparison.Ordinal);
        Assert.Equal(target, File.ReadAllBytes(Path.Combine(project, "target.xscn")));
        Assert.Contains("could not be saved", fresh.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, "fresh.xscn")));
        Assert.False(File.Exists(Path.Combine(project, "fresh.xscn.uid")));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ATscnInstancesAXscn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "part.xscn", SceneUid);
        File.WriteAllText(Path.Combine(project, "host.tscn"), "[gd_scene format=3]\n\n[node name=\"Host\" type=\"Node2D\"]\n");

        await _tools.AddNodeAsync(project, "host.tscn", "res://part.xscn", "Part", cancellationToken: cancellation);

        string text = File.ReadAllText(Path.Combine(project, "host.tscn"));
        Assert.Contains("path=\"res://part.xscn\"", text, StringComparison.Ordinal);
        string part = text.Split('\n').Single(line => line.StartsWith("[node name=\"Part\" parent=\".\"", StringComparison.Ordinal));
        Assert.Contains(" instance=ExtResource(", part, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ValidateChecksXscnAndXres()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        WriteScene(project, "good.xscn", SceneUid);
        File.WriteAllText(Path.Combine(project, "box.xres"), Comment + "\n[gd_resource type=\"Gradient\" format=3]\n\n[resource]\n");

        JsonNode targeted = JsonNode.Parse(await _tools.ValidateAsync(project, ["good.xscn", "box.xres"], cancellationToken: cancellation))!;
        File.WriteAllText(Path.Combine(project, "broken.xscn"), Comment + "\nnot a scene\n");
        File.WriteAllText(Path.Combine(project, "broken.xres"), Comment + "\nnot a resource\n");
        Git.CommitAll(project);
        JsonNode swept = JsonNode.Parse(await _tools.ValidateAsync(project, cancellationToken: cancellation))!;

        Assert.True(targeted["valid"]!.GetValue<bool>(), targeted.ToJsonString());
        Assert.Equal(2, targeted["checked"]!.GetValue<int>());
        string[] failing = [.. swept["results"]!.AsArray().Select(result => result!["path"]!.GetValue<string>())];
        Assert.Contains("res://broken.xscn", failing);
        Assert.Contains("res://broken.xres", failing);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARefusedSaveAsLeavesNoFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject probe = Track(CsProbeProject.BuiltWith(FakeXamlFormat.Sources));
        File.WriteAllText(
            Path.Combine(probe.Directory, "uses.xscn"),
            Comment
                + "\n[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://CsProbeNode.cs\" id=\"1\"]\n\n"
                + "[node name=\"Uses\" type=\"Node\"]\nscript = ExtResource(\"1\")\n"
        );
        string source = File.ReadAllText(probe.SourcePath("CsProbeNode.cs"));
        probe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(probe.Directory, "uses.xscn", "copy.xscn", cancellationToken: cancellation)
        );

        Assert.Contains("C# build", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(probe.Directory, "copy.xscn")));
        Assert.False(File.Exists(Path.Combine(probe.Directory, "copy.xscn.uid")));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AProjectImportedBeforeItsFirstBuildLoadsAXscn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Unbuilt();
        await RunGodotAsync(project, ["--import"], cancellation);

        JsonNode first = await TreeAsync(project, "scene.xscn", cancellation);
        JsonNode second = await TreeAsync(project, "scene.xscn", cancellation);

        Assert.Equal("built", first["prep"]!["build"]!.GetValue<string>());
        Assert.Equal("done", first["prep"]!["import"]!.GetValue<string>());
        Assert.Equal("Root", first["nodes"]![0]!["name"]!.GetValue<string>());
        Assert.NotEqual("done", second["prep"]?["import"]?.GetValue<string>());
        Assert.Equal("Root", second["nodes"]![0]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMissingLoaderSaysToBuildFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Unbuilt();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetSceneFileTreeAsync(project, "scene.xscn", null, new SceneFileTreeOptions(Prepare: "never"), cancellation)
        );

        Assert.Contains("no loader reads .xscn files in this project: build the C# project first", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AddingAnInstanceWithNoLoaderSaysToBuildFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Unbuilt();
        File.WriteAllText(Path.Combine(project, "host.tscn"), "[gd_scene format=3]\n\n[node name=\"Host\" type=\"Node2D\"]\n");

        // add_node has no prepare option, and the prep would build the loader, so the request goes in without one.
        JsonObject parameters = new()
        {
            ["scene"] = "res://host.tscn",
            ["nodeType"] = "res://scene.xscn",
            ["nodeName"] = "Part",
            ["parent"] = ".",
        };
        HeadlessRequest request = new(ProjectPaths.Normalise(project), "add_node", parameters, Prepare: false, TimeSpan.FromSeconds(60));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            HeadlessRunner.RunAsync(_harness.Sessions, request, cancellation)
        );

        Assert.Contains("build the C# project first", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFailedLoadThatLoggedErrorsCarriesNoLineOfItsOwn()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        File.WriteAllText(Path.Combine(project, "broken.tscn"), "not a scene\n");

        string validated = await _tools.ValidateAsync(project, ["broken.tscn"], cancellationToken: cancellation);

        Assert.Contains("res://broken.tscn", validated, StringComparison.Ordinal);
        Assert.False(JsonNode.Parse(validated)!["valid"]!.GetValue<bool>(), validated);
        Assert.DoesNotContain("Godot could not load it", validated, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFailedLoadLogsItsErrorsOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        File.WriteAllText(Path.Combine(project, "broken.tscn"), "not a scene\n");

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetSibPositionAsync(project, "broken.tscn", cancellation));

        string[] logged = [.. refused.Message.Split('\n').SkipWhile(line => line != "Godot logged:").Skip(1)];
        Assert.NotEmpty(logged);
        Assert.Equal(logged.Distinct(StringComparer.Ordinal), logged);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnEditOfAXscnThatLoadedWithErrorsIsRefusedAndLeavesTheFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        string scene = Path.Combine(project, "broken.xscn");
        File.WriteAllText(scene, Comment + "\n" + GoneInstanceScene);
        // Given its own .uid: the prep's import writes one for a file that has none, so its absence would test the import.
        File.WriteAllText(scene + ".uid", SceneUid + "\n");
        byte[] before = File.ReadAllBytes(scene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetSibPositionAsync(project, "broken.xscn", cancellation));

        Assert.Contains(LoadedWithErrors("res://broken.xscn"), refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(scene));
        Assert.Equal(SceneUid, File.ReadAllText(scene + ".uid").Trim());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveSceneOfAXscnThatLoadedWithErrorsIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        File.WriteAllText(Path.Combine(project, "broken.xscn"), Comment + "\n" + GoneInstanceScene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(project, "broken.xscn", "copy.xscn", cancellationToken: cancellation)
        );

        Assert.Contains(LoadedWithErrors("res://broken.xscn"), refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, "copy.xscn")));
        Assert.False(File.Exists(Path.Combine(project, "copy.xscn.uid")));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABatchOnAXscnThatLoadedWithErrorsIsRefusedAndWritesNothing()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        string scene = Path.Combine(project, "broken.xscn");
        File.WriteAllText(scene, Comment + "\n" + GoneInstanceScene);
        File.WriteAllText(scene + ".uid", SceneUid + "\n");
        byte[] before = File.ReadAllBytes(scene);
        SceneBatchStep add = new("add_node", new JsonObject { ["nodeType"] = "Node2D", ["nodeName"] = "D" });

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.BatchSceneOperationsAsync(project, "broken.xscn", [add], cancellation)
        );

        Assert.Contains(LoadedWithErrors("res://broken.xscn"), refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(scene));
        Assert.Equal(SceneUid, File.ReadAllText(scene + ".uid").Trim());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnEditOfATscnThatLoadedWithErrorsIsRefusedAndLeavesTheFile()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = Built();
        string scene = Path.Combine(project, "broken.tscn");
        File.WriteAllText(scene, GoneInstanceScene);
        byte[] before = File.ReadAllBytes(scene);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => SetSibPositionAsync(project, "broken.tscn", cancellation));

        Assert.Contains(LoadedWithErrors("res://broken.tscn"), refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(scene));
    }

    private const string GoneInstanceScene =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://gone.xscn\" id=\"1_gone\"]\n\n"
        + "[node name=\"Root\" type=\"Node2D\"]\n\n[node name=\"Sib\" type=\"Node2D\" parent=\".\"]\n\n"
        + "[node name=\"Gone\" parent=\".\" instance=ExtResource(\"1_gone\")]\n";

    private static string LoadedWithErrors(string scene) =>
        $"{scene} loaded with errors, so it was not saved: saving it now would drop whatever failed to load, such as an instance "
        + "whose file was not found. Fix what engineErrors names first.";

    private Task<string> SetSibPositionAsync(string project, string scene, CancellationToken cancellation) =>
        _tools.SetNodePropertiesAsync(project, scene, [new PropertyUpdate("Sib", "position", Json("{\"x\": 1, \"y\": 2}"))], cancellation);

    /// <summary>A built CsProbe copy with the fake format's sources, never imported.</summary>
    private string Built() => Track(CsProbeProject.BuiltWith(FakeXamlFormat.Sources)).Directory;

    /// <summary>A committed CsProbe copy with the fake format's sources and a scene.xscn, nothing built.</summary>
    private string Unbuilt()
    {
        CsProbeProject probe = Track(CsProbeProject.Unbuilt());
        foreach ((string name, string content) in FakeXamlFormat.Sources)
        {
            probe.WriteSource(name, content);
        }

        WriteScene(probe.Directory, "scene.xscn", SceneUid);
        return probe.Directory;
    }

    private static void WriteScene(string project, string name, string uid)
    {
        File.WriteAllText(Path.Combine(project, name), SceneText);
        File.WriteAllText(Path.Combine(project, name + ".uid"), uid + "\n");
    }

    private static void AssertKept(string project, string name, string uid)
    {
        Assert.StartsWith(
            $"{Comment}\n{FakeXamlFormat.BodyPrefix}[gd_scene",
            File.ReadAllText(Path.Combine(project, name)),
            StringComparison.Ordinal
        );
        Assert.Equal(uid, File.ReadAllText(Path.Combine(project, name + ".uid")).Trim());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

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

    private CsProbeProject Track(CsProbeProject project)
    {
        _projects.Add(project);
        return project;
    }
}
