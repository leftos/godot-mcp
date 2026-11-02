using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// An editor session (run_editor): the Godot editor opened on a project with the bridge inside it, read and driven through the
/// runtime tools and the editor tool, and closed through its own close request, leaving the project's tree as it found it. Most
/// tests open their own quiet editor on one copy of the InputProbe fixture, opened once before; the test of the open's
/// changed-files warning, the save test (which changes a scene) and the game-session refusal each use a fresh copy of their own.
/// </summary>
public sealed class EditorTests : IClassFixture<OpenedProbeProject>, IAsyncDisposable
{
    private const int ScriptTimeoutMs = 30_000;
    private const string Session = "editor";
    private const string FirstOpenSession = "editor-first-open";
    private const string GameSession = "game";
    private const string MainScene = "res://main.tscn";
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
        JsonNode launched = JsonNode.Parse(await RunEditorAsync(token))!;

        JsonNode listed = JsonNode.Parse(_project.ListSessions())!;
        JsonNode elements = JsonNode.Parse(await _runtime.GetUiElementsAsync(filter: "FileSystemDock", session: Session, cancellationToken: token))!;
        JsonNode driver = await RunScriptAsync("return AudioServer.get_driver_name()", token);

        JsonNode session = Assert.Single(listed["sessions"]!.AsArray())!;
        Assert.Equal("editor", session["kind"]!.GetValue<string>());
        Assert.Single(elements["elements"]!.AsArray());
        Assert.Equal("Dummy", driver.GetValue<string>());
        // The fixture's copy was opened once and what that open wrote committed: this open changes nothing, so no warning.
        Assert.Null(launched["warning"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task RunEditorWarnsWhenItsFirstOpenChangesTheProject()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        Git.CommitAll(probe.Directory);

        JsonNode launched = JsonNode.Parse(
            await _project.RunEditorAsync(probe.Directory, options: new EditorOptions(Session: FirstOpenSession), cancellationToken: token)
        )!;
        string warning = launched["warning"]?.GetValue<string>() ?? "";
        _ = JsonNode.Parse(await _project.StopProjectAsync(FirstOpenSession, discard: true, cancellationToken: token));

        Assert.StartsWith("The editor's open changed files in the project: ", warning);
        Assert.Contains("project.godot", warning);
        Assert.Contains(".uid", warning);
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

        Assert.Equal(dock["path"]!.GetValue<string>(), clicked["pressedOn"]?["path"]?.GetValue<string>());
        string text = blocks.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        Assert.True(File.Exists(JsonNode.Parse(text)!["path"]!.GetValue<string>()), text);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnItemClickOnTheFileSystemDockSelectsTheItem()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);
        string dock = await FileSystemTreePathAsync(token);

        JsonNode clicked = JsonNode.Parse(
            await _runtime.ClickAsync(
                new InputTarget(Element: dock, Item: new InputItem(Text: "Favorites")),
                session: Session,
                cancellationToken: token
            )
        )!;
        JsonNode selected = await RunScriptAsync($"return scene_tree.root.get_node(\"{dock}\").get_selected().get_text(0)", token);

        Assert.Equal(dock, clicked["pressedOn"]?["path"]?.GetValue<string>());
        Assert.Equal("Favorites", selected.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ATextClickOnAnEditorButtonPressesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        JsonNode clicked = JsonNode.Parse(await _runtime.ClickAsync(new InputTarget(Text: "Script"), session: Session, cancellationToken: token))!;

        Assert.EndsWith("/EditorMainScreenButtons/Script", clicked["pressedOn"]?["path"]?.GetValue<string>());
        Assert.Contains(clicked["fired"]!.AsArray(), signal => signal?["signal"]?.GetValue<string>() == "pressed");
    }

    private async Task<string> FileSystemTreePathAsync(CancellationToken token)
    {
        JsonNode trees = JsonNode.Parse(await _runtime.GetUiElementsAsync(filter: "Tree", limit: 500, session: Session, cancellationToken: token))!;
        return trees["elements"]!
            .AsArray()
            .Single(element => element!["items"] is JsonArray items && items.Any(item => item?["text"]?.GetValue<string>() == "main.tscn"))![
            "path"
        ]!.GetValue<string>();
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorOpensASceneAndReportsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        JsonNode opened = await EditorAsync("open", token, scene: MainScene);
        JsonNode state = JsonNode.Parse(await _runtime.GetEditorStateAsync(Session, token))!;

        Assert.Equal("open", opened["action"]!.GetValue<string>());
        Assert.Equal(MainScene, opened["currentScene"]!.GetValue<string>());
        Assert.Contains(opened["openScenes"]!.AsArray(), scene => scene!.GetValue<string>() == MainScene);
        Assert.Equal(MainScene, state["currentScene"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorSavesAnEditedScene()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        Git.CommitAll(probe.Directory);
        string stamp = $"editor-save-{Guid.NewGuid():N}";
        _ = await _project.RunEditorAsync(probe.Directory, options: new EditorOptions(Session: Session), cancellationToken: token);
        _ = await EditorAsync("open", token, scene: MainScene);
        string root = (
            await RunScriptAsync("return str(Engine.get_singleton(\"EditorInterface\").get_edited_scene_root().get_path())", token)
        ).GetValue<string>();

        _ = JsonNode.Parse(await _runtime.SetPropertyAsync(root, "editor_description", JsonValue(stamp), Session, token));
        _ = await RunScriptAsync("Engine.get_singleton(\"EditorInterface\").mark_scene_as_unsaved()\n\treturn true", token);
        JsonNode saved = await EditorAsync("save", token);
        _ = await _project.StopProjectAsync(Session, discard: true, cancellationToken: token);

        Assert.Empty(saved["unsavedScenes"]!.AsArray());
        Assert.True(saved["saved"]!.GetValue<bool>(), saved.ToJsonString());
        Assert.Contains(stamp, await File.ReadAllTextAsync(Path.Combine(probe.Directory, "main.tscn"), token), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorPlaysAndStopsTheCurrentScene()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);
        _ = await EditorAsync("open", token, scene: MainScene);

        JsonNode played = await EditorAsync("play", token);
        JsonNode stopped = await EditorAsync("stop", token);

        Assert.Equal(MainScene, played["playingScene"]!.GetValue<string>());
        Assert.True(stopped["wasPlaying"]!.GetValue<bool>(), stopped.ToJsonString());
        Assert.Equal(string.Empty, stopped["playingScene"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorRefusesCloseOfAnUnsavedSceneWithoutDiscard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);
        _ = await EditorAsync("open", token, scene: MainScene);
        _ = await RunScriptAsync("Engine.get_singleton(\"EditorInterface\").mark_scene_as_unsaved()\n\treturn true", token);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.EditorAsync("close", session: Session, cancellationToken: token)
        );
        JsonNode state = JsonNode.Parse(await _runtime.GetEditorStateAsync(Session, token))!;
        _ = await _project.StopProjectAsync(Session, discard: true, cancellationToken: token);

        Assert.Contains(
            $"The scene {MainScene} has unsaved changes; save it first or pass discard: true.",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Contains(state["openScenes"]!.AsArray(), scene => scene!.GetValue<string>() == MainScene);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorToolsRefuseAGameSession()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using ProbeProject probe = new();
        _ = await _project.RunProjectAsync(probe.Directory, options: new RunOptions(Session: GameSession), cancellationToken: token);

        McpException action = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.EditorAsync("stop", session: GameSession, cancellationToken: token)
        );
        McpException state = await Assert.ThrowsAsync<McpException>(() => _runtime.GetEditorStateAsync(GameSession, token));
        _ = await _project.StopProjectAsync(GameSession, cancellationToken: token);

        Assert.Equal("editor works only in an editor session; session 'game' is a run session. Start one with run_editor.", action.Message);
        Assert.Equal("get_editor_state works only in an editor session; session 'game' is a run session. Start one with run_editor.", state.Message);
    }

    private async Task<JsonNode> EditorAsync(string action, CancellationToken token, string? scene = null) =>
        JsonNode.Parse(await _runtime.EditorAsync(action, scene: scene, session: Session, cancellationToken: token))!;

    private static System.Text.Json.JsonElement JsonValue(string text) => System.Text.Json.JsonSerializer.SerializeToElement(text);

    private Task<string> RunEditorAsync(CancellationToken token) =>
        _project.RunEditorAsync(_opened.Directory, options: new EditorOptions(Session: Session), cancellationToken: token);

    private async Task<JsonNode> RunScriptAsync(string body, CancellationToken token)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, session: Session, cancellationToken: token);
        return JsonNode.Parse(json)!["value"]!;
    }
}
