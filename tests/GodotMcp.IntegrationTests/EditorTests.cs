using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// An editor session (run_editor): the Godot editor opened on a project with the bridge inside it, read and driven through the
/// runtime tools and closed through its own close request, leaving the project's tree as it found it. Every test opens its own
/// quiet editor on one copy of the InputProbe fixture, opened once before.
/// </summary>
public sealed class EditorTests : IClassFixture<OpenedProbeProject>, IAsyncDisposable
{
    private const int ScriptTimeoutMs = 30_000;
    private const string Session = "editor";
    private readonly OpenedProbeProject _opened;
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public EditorTests(OpenedProbeProject opened)
    {
        _opened = opened;
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task RunEditorOpensTheEditorQuietlyAndListsTheFileSystemDock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        JsonNode listed = JsonNode.Parse(_project.ListSessions())!;
        JsonNode elements = JsonNode.Parse(await _runtime.GetUiElementsAsync(filter: "FileSystemDock", session: Session, cancellationToken: token))!;
        JsonNode driver = await RunScriptAsync("return AudioServer.get_driver_name()", token);

        JsonNode session = Assert.Single(listed["sessions"]!.AsArray())!;
        Assert.Equal("editor", session["kind"]!.GetValue<string>());
        Assert.Single(elements["elements"]!.AsArray());
        Assert.Equal("Dummy", driver.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ClickAndScreenshotReachTheEditorWindow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        JsonNode trees = JsonNode.Parse(await _runtime.GetUiElementsAsync(filter: "Tree", limit: 500, session: Session, cancellationToken: token))!;
        JsonNode dock = trees["elements"]!
            .AsArray()
            .Single(element => element!["items"] is JsonArray items && items.Any(item => item?["text"]?.GetValue<string>() == "main.tscn"))!;
        // An aimed click, an element or an item target, is refused in an editor session: the cover
        // check reads the root viewport's hovered Control as null after the aim's injected motion
        // (booked as its own item). A point inside the dock's rect takes no cover check and reaches
        // the window.
        JsonNode rect = dock["rect"]!;
        JsonNode clicked = JsonNode.Parse(
            await _runtime.ClickAsync(
                new InputTarget(
                    X: rect["x"]!.GetValue<double>() + (rect["width"]!.GetValue<double>() / 2),
                    Y: rect["y"]!.GetValue<double>() + (rect["height"]!.GetValue<double>() / 2)
                ),
                session: Session,
                cancellationToken: token
            )
        )!;
        IEnumerable<ModelContextProtocol.Protocol.ContentBlock> blocks = await _runtime.TakeScreenshotAsync(
            "path_only",
            session: Session,
            cancellationToken: token
        );

        Assert.True(clicked.AsObject().ContainsKey("pressedOn"), clicked.ToJsonString());
        string text = blocks.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        Assert.True(File.Exists(JsonNode.Parse(text)!["path"]!.GetValue<string>()), text);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StopProjectClosesTheEditorAndLeavesTheTreeClean()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] projectBefore = await File.ReadAllBytesAsync(_opened.ProjectFile, token);
        await RunEditorAsync(token);
        string output = _project.GetDebugOutput(session: Session);

        JsonNode stopped = JsonNode.Parse(await _project.StopProjectAsync(Session, cancellationToken: token))!;

        Assert.DoesNotContain("progress_dialog.cpp:183", output, StringComparison.Ordinal);
        Assert.False(stopped["killed"]!.GetValue<bool>(), stopped.ToJsonString());
        Assert.Equal(string.Empty, Git.Status(_opened.Directory));
        Assert.Equal(projectBefore, await File.ReadAllBytesAsync(_opened.ProjectFile, token));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StopProjectRefusesUnsavedScenesUnlessDiscard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);
        _ = await RunScriptAsync(
            "var editor: Object = Engine.get_singleton(\"EditorInterface\")\n\t"
                + "editor.open_scene_from_path(\"res://main.tscn\")\n\t"
                + "await scene_tree.process_frame\n\t"
                + "editor.mark_scene_as_unsaved()\n\t"
                + "return true",
            token
        );

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _project.StopProjectAsync(Session, cancellationToken: token));
        JsonNode discarded = JsonNode.Parse(await _project.StopProjectAsync(Session, discard: true, cancellationToken: token))!;

        Assert.Equal(
            "The editor has unsaved scenes: res://main.tscn. Save them in the editor, or pass discard: true to close without saving.",
            refused.Message
        );
        Assert.True(discarded["killed"]!.GetValue<bool>(), discarded.ToJsonString());
        Assert.Equal("discarded", discarded["killReason"]!.GetValue<string>());
        Assert.Equal(string.Empty, Git.Status(_opened.Directory));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheEditorEnvironmentHoldsNoGodotMcpVariables()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        JsonNode value = await RunScriptAsync($"return OS.get_environment(\"{GodotCommandLine.TokenVariable}\")", token);

        Assert.Equal(string.Empty, value.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task RestartProjectRefusesAnEditorSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Sessions.RestartAsync(Session, prepare: false, token));

        Assert.Equal(
            "restart_project does not restart an editor session; stop it with stop_project and start it again with run_editor.",
            refused.Message
        );
    }

    private Task<string> RunEditorAsync(CancellationToken token) =>
        _project.RunEditorAsync(_opened.Directory, options: new EditorOptions(Session: Session), cancellationToken: token);

    private async Task<JsonNode> RunScriptAsync(string body, CancellationToken token)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, session: Session, cancellationToken: token);
        return JsonNode.Parse(json)!["value"]!;
    }
}
