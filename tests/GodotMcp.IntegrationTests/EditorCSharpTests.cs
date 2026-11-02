using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// editor build in an editor session on a C# project: the server's build of the CsProbe copy, opened once before, and the editor's
/// reload of the new assembly, seen through the reloaded script's method list. Each test restores the source it changes.
/// </summary>
public sealed class EditorCSharpTests : IClassFixture<OpenedCsProbeProject>, IAsyncDisposable
{
    private const int ScriptTimeoutMs = 30_000;
    private const string Session = "editor";
    private const string MainScene = "res://main.tscn";
    private const string Source = "CsProbeNode.cs";
    private const string AddedMember = "AddedByTheBuildTest";
    private const string MethodNamesScript =
        "return Array(load(\"res://CsProbeNode.cs\").get_script_method_list().map(func(method: Dictionary) -> String: return method[\"name\"]))";

    private readonly OpenedCsProbeProject _opened;
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public EditorCSharpTests(OpenedCsProbeProject opened)
    {
        _opened = opened;
        _project = new ProjectTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task BuildReloadsANewMemberIntoTheEditor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string original = await File.ReadAllTextAsync(_opened.SourcePath(Source), token);
        await RunEditorAsync(token);
        JsonNode opened = await EditorAsync("open", token, scene: MainScene);
        try
        {
            int end = original.LastIndexOf('}');
            _opened.WriteSource(Source, original[..end] + $"\n    public int {AddedMember}() => 7;\n" + original[end..]);

            JsonNode built = await EditorAsync("build", token);
            JsonNode methods = await RunScriptAsync(MethodNamesScript, token);
            JsonNode again = await EditorAsync("build", token);

            Assert.Equal("built", built["build"]!.GetValue<string>());
            Assert.True(built["reloadWaited"]!.GetValue<bool>(), built.ToJsonString());
            Assert.Contains(methods.AsArray(), name => name!.GetValue<string>() == AddedMember);
            Assert.Equal(opened["openScenes"]!.ToJsonString(), built["openScenes"]!.ToJsonString());
            Assert.Equal("up-to-date", again["build"]!.GetValue<string>());
        }
        finally
        {
            _opened.WriteSource(Source, original);
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARedBuildIsRefusedWithItsErrors()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string original = await File.ReadAllTextAsync(_opened.SourcePath(Source), token);
        await RunEditorAsync(token);
        try
        {
            _opened.WriteSource(Source, original.Replace("=> n + 1;", "=> n + missingByTheBuildTest;", StringComparison.Ordinal));

            McpException refused = await Assert.ThrowsAsync<McpException>(() =>
                _runtime.EditorAsync("build", session: Session, cancellationToken: token)
            );

            Assert.StartsWith("the C# build failed, so the editor keeps its previous assembly: ", refused.Message, StringComparison.Ordinal);
            Assert.Contains("CS0103", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            _opened.WriteSource(Source, original);
        }
    }

    private async Task<JsonNode> EditorAsync(string action, CancellationToken token, string? scene = null) =>
        JsonNode.Parse(await _runtime.EditorAsync(action, scene: scene, session: Session, cancellationToken: token))!;

    private Task<string> RunEditorAsync(CancellationToken token) =>
        _project.RunEditorAsync(_opened.Directory, options: new EditorOptions(Session: Session), cancellationToken: token);

    private async Task<JsonNode> RunScriptAsync(string body, CancellationToken token)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, session: Session, cancellationToken: token);
        return JsonNode.Parse(json)!["value"]!;
    }
}
