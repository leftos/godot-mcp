using System.Text.Json;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// What a save refuses on a scene that lost something while it loaded, on InputProbe copies answered by a warm host: a load
/// that lost an instance, a texture or a script is refused every time, however the script caches stand, and a scene that
/// loaded whole is saved whatever its own scripts log once they run.
/// </summary>
public sealed class HeadlessLoadErrorTests : IAsyncDisposable
{
    private const string Refusal = "loaded with errors, so it was not saved";
    private const string Moved = "position = Vector2(1, 2)";
    private const string SibNode = "[node name=\"Sib\" type=\"Node2D\" parent=\".\"]\n";
    private const string ScriptLine = "script = ExtResource(\"1\")";
    private const string ParseErrorScript = "extends Node2D\n\n@export var title: String = \"x\"\n\nfunc broken(:\n\tpass\n";
    private const string TitleLine = "title = \"kept\"";
    private const string SubResourceLine = "metadata/data = SubResource(\"Resource_1\")";

    private const string BrokenScriptRefusal =
        "res://scene.tscn was not saved: its script res://parse_error.gd does not load, so saving it now would drop the script and "
        + "its values. Fix the script first.";

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<ProbeProject> _projects = [];

    public HeadlessLoadErrorTests() => _tools = new HeadlessTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (ProbeProject project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARootWhoseInitPushesAnErrorIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(project, "noisy.gd", "extends Node2D\n\nfunc _init() -> void:\n\tpush_error(\"noisy _init\")\n");
        WriteFile(project, "scene.tscn", ScriptedScene("noisy.gd", "Node2D", ""));

        await EditAsync(project, cancellation);

        AssertSaved(project, ScriptLine);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARootWhoseInitReadsAnAutoloadIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(project, "service.gd", "extends Node\n\nfunc ping() -> int:\n\treturn 1\n");
        AddAutoload(project, "Service", "service.gd");
        WriteScript(project, "uses_service.gd", "extends Node2D\n\nfunc _init() -> void:\n\tService.ping()\n");
        WriteFile(project, "scene.tscn", ScriptedScene("uses_service.gd", "Node2D", ""));

        await EditAsync(project, cancellation);

        AssertSaved(project, ScriptLine);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnExportedSetterReadingAChildBeforeItExistsIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(
            project,
            "setter.gd",
            "extends Node2D\n\n@export var value: int = 0:\n\tset(given):\n\t\tvalue = given\n\t\t$Label.text = str(given)\n"
        );
        WriteFile(
            project,
            "scene.tscn",
            ScriptedScene("setter.gd", "Node2D", "value = 3\n") + "\n[node name=\"Label\" type=\"Label\" parent=\".\"]\n"
        );

        await EditAsync(project, cancellation);

        AssertSaved(project, "value = 3");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASubResourceWhoseInitPushesAnErrorIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(project, "noisy_resource.gd", "extends Resource\n\nfunc _init() -> void:\n\tpush_error(\"noisy resource\")\n");
        WriteFile(project, "scene.tscn", SubResourceScene("noisy_resource.gd"));

        await EditAsync(project, cancellation);

        AssertSaved(project, SubResourceLine);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AScriptThatLoadsAMissingFileIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(project, "loads_nothing.gd", "extends Resource\n\nfunc _init() -> void:\n\tload(\"res://nope.tres\")\n");
        WriteFile(project, "scene.tscn", SubResourceScene("loads_nothing.gd"));

        await EditAsync(project, cancellation);
        AssertSaved(project, SubResourceLine);
        await EditAsync(project, cancellation);

        AssertSaved(project, SubResourceLine);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnInstanceWhoseFileIsMissingIsRefusedTwice()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteFile(
            project,
            "scene.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://gone.tscn\" id=\"1\"]\n\n"
                + "[node name=\"Root\" type=\"Node2D\"]\n\n"
                + SibNode
                + "\n[node name=\"Gone\" parent=\".\" instance=ExtResource(\"1\")]\n"
        );

        await AssertRefusedTwiceAsync(project, Refusal, cancellation);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ATextureWithNoImportIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteFile(project, "pic.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2\" height=\"2\"/>\n");
        WriteFile(
            project,
            "scene.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Texture2D\" path=\"res://pic.svg\" id=\"1\"]\n\n"
                + "[node name=\"Root\" type=\"Node2D\"]\n\n[node name=\"Spr\" type=\"Sprite2D\" parent=\".\"]\ntexture = ExtResource(\"1\")\n\n"
                + SibNode
        );
        byte[] before = SceneBytes(project);

        await AssertRefusedAsync(project, Refusal, cancellation);

        Assert.Equal(before, SceneBytes(project));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AParseErrorScriptIsRefusedTwice()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = ParseErrorProject();

        await AssertRefusedTwiceAsync(project, BrokenScriptRefusal, cancellation);

        string text = SceneText(project);
        Assert.Contains(ScriptLine, text, StringComparison.Ordinal);
        Assert.Contains(TitleLine, text, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AParseErrorScriptReadFirstIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = ParseErrorProject();
        byte[] before = SceneBytes(project);

        await _tools.GetSceneFileTreeAsync(project, "scene.tscn", null, null, cancellation);
        await AssertRefusedAsync(project, BrokenScriptRefusal, cancellation);

        Assert.Equal(before, SceneBytes(project));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AParseErrorScriptAnAutoloadLoadsIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = ParseErrorProject();
        AddAutoload(project, "Broken", "parse_error.gd");
        byte[] before = SceneBytes(project);

        await AssertRefusedAsync(project, BrokenScriptRefusal, cancellation);

        Assert.Equal(before, SceneBytes(project));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnInstanceOfASceneWithAParseErrorScriptIsSaved()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = NewProject();
        WriteScript(project, "parse_error.gd", ParseErrorScript);
        WriteFile(project, "inner.tscn", ScriptedScene("parse_error.gd", "Node2D", "title = \"kept\"\n"));
        WriteFile(
            project,
            "scene.tscn",
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://inner.tscn\" id=\"1\"]\n\n"
                + "[node name=\"Root\" type=\"Node2D\"]\n\n"
                + SibNode
                + "\n[node name=\"Inner\" parent=\".\" instance=ExtResource(\"1\")]\n"
        );

        await EditAsync(project, cancellation);

        AssertSaved(project, "[node name=\"Inner\" parent=\".\" instance=ExtResource(");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task SaveSceneOfAParseErrorScriptIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = ParseErrorProject();
        await AssertRefusedAsync(project, BrokenScriptRefusal, cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SaveSceneAsync(project, "scene.tscn", "copy.tscn", cancellationToken: cancellation)
        );

        Assert.Contains(BrokenScriptRefusal, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, "copy.tscn")));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABatchOnAParseErrorScriptIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string project = ParseErrorProject();
        byte[] before = SceneBytes(project);
        await AssertRefusedAsync(project, BrokenScriptRefusal, cancellation);
        SceneBatchStep add = new("add_node", new System.Text.Json.Nodes.JsonObject { ["nodeType"] = "Node2D", ["nodeName"] = "D" });

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.BatchSceneOperationsAsync(project, "scene.tscn", [add], cancellation)
        );

        Assert.Contains(BrokenScriptRefusal, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, SceneBytes(project));
    }

    /// <summary>A scene whose root holds a sub-resource of the Resource script <paramref name="script"/>, and Sib.</summary>
    private static string SubResourceScene(string script) =>
        $"[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"Script\" path=\"res://{script}\" id=\"1\"]\n\n"
        + $"[sub_resource type=\"Resource\" id=\"Resource_1\"]\n{ScriptLine}\n\n"
        + $"[node name=\"Root\" type=\"Node2D\"]\n{SubResourceLine}\n\n{SibNode}";

    /// <summary>A project whose <c>scene.tscn</c> has a root with a parse-error script and an exported value in its file.</summary>
    private string ParseErrorProject()
    {
        string project = NewProject();
        WriteScript(project, "parse_error.gd", ParseErrorScript);
        WriteFile(project, "scene.tscn", ScriptedScene("parse_error.gd", "Node2D", "title = \"kept\"\n"));
        return project;
    }

    /// <summary>A scene whose root of <paramref name="type"/> runs <paramref name="script"/>, holding Sib.</summary>
    private static string ScriptedScene(string script, string type, string rootProperties) =>
        $"[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://{script}\" id=\"1\"]\n\n"
        + $"[node name=\"Root\" type=\"{type}\"]\n{ScriptLine}\n{rootProperties}\n{SibNode}";

    private static void WriteFile(string project, string name, string text) => File.WriteAllText(Path.Combine(project, name), text);

    private static void WriteScript(string project, string name, string text) => WriteFile(project, name, text);

    private static void AddAutoload(string project, string name, string script) =>
        File.AppendAllText(Path.Combine(project, "project.godot"), $"\n[autoload]\n\n{name}=\"*res://{script}\"\n");

    private static byte[] SceneBytes(string project) => File.ReadAllBytes(Path.Combine(project, "scene.tscn"));

    private static string SceneText(string project) => File.ReadAllText(Path.Combine(project, "scene.tscn"));

    private void AssertSaved(string project, string kept)
    {
        Assert.NotNull(_harness.Sessions.HeadlessHosts.ProcessIdOf(project));
        string text = SceneText(project);
        Assert.Contains(Moved, text, StringComparison.Ordinal);
        Assert.Contains(kept, text, StringComparison.Ordinal);
    }

    /// <summary>Two refusals in a row by one warm host, the file's bytes unchanged.</summary>
    private async Task AssertRefusedTwiceAsync(string project, string refusal, CancellationToken cancellation)
    {
        byte[] before = SceneBytes(project);

        await AssertRefusedAsync(project, refusal, cancellation);
        int? firstHost = _harness.Sessions.HeadlessHosts.ProcessIdOf(project);
        await AssertRefusedAsync(project, refusal, cancellation);
        int? secondHost = _harness.Sessions.HeadlessHosts.ProcessIdOf(project);

        Assert.NotNull(firstHost);
        Assert.Equal(firstHost, secondHost);
        Assert.Equal(before, SceneBytes(project));
    }

    private async Task AssertRefusedAsync(string project, string refusal, CancellationToken cancellation)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => EditAsync(project, cancellation));
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
    }

    private Task<string> EditAsync(string project, CancellationToken cancellation) =>
        _tools.SetNodePropertiesAsync(
            project,
            "scene.tscn",
            [new PropertyUpdate("Sib", "position", JsonDocument.Parse("{\"x\": 1, \"y\": 2}").RootElement.Clone())],
            cancellation
        );

    private string NewProject()
    {
        ProbeProject project = new();
        _projects.Add(project);
        return project.Directory;
    }
}
