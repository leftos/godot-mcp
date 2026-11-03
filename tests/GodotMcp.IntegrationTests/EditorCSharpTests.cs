using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// editor build in an editor session on a C# project: the server's build of the CsProbe copy, opened once before, and the editor's
/// reload of the new assembly, seen through the reloaded script's method list. Each test restores the source it changes.
/// </summary>
public sealed class EditorCSharpTests : IClassFixture<OpenedCsProbeProject>, IClassFixture<OpenedFakeXamlProject>, IAsyncDisposable
{
    private const int ScriptTimeoutMs = 30_000;
    private const string Session = "editor";
    private const string MainScene = "res://main.tscn";
    private const string Source = "CsProbeNode.cs";
    private const string AddedMember = "AddedByTheBuildTest";
    private const string AddedByThePlayBuild = "AddedByThePlayBuild";
    private const string AssemblyName = "CsProbe";
    private const string MethodNamesScript =
        "return Array(load(\"res://CsProbeNode.cs\").get_script_method_list().map(func(method: Dictionary) -> String: return method[\"name\"]))";

    // A prep wrapper taking {log}, then "--", then the command: it notes the command's program, runs the command with its
    // output in {log}, prints the verdict a gate prints to its own output once the command ends, and exits with its code.
    private const string PrepWrapperScript =
        "$log = $args[0]\n"
        + "$command = @($args | Select-Object -Skip 1)\n"
        + "if ($command.Count -gt 0 -and $command[0] -eq '--') { $command = @($command | Select-Object -Skip 1) }\n"
        + "Add-Content -LiteralPath (Join-Path $PSScriptRoot 'prep-wrapper-ran.txt') -Value ([IO.Path]::GetFileName($command[0]))\n"
        + "& $command[0] @($command | Select-Object -Skip 1) *> $log\n"
        + "Write-Output 'gate: passed in 1 s (load-adjusted 1 s, ceiling 300 s)'\n"
        + "exit $LASTEXITCODE\n";

    private readonly OpenedCsProbeProject _opened;
    private readonly OpenedFakeXamlProject _fakeXaml;
    private readonly SessionHarness _harness = new();
    private readonly ProjectTools _project;
    private readonly RuntimeTools _runtime;

    public EditorCSharpTests(OpenedCsProbeProject opened, OpenedFakeXamlProject fakeXaml)
    {
        _opened = opened;
        _fakeXaml = fakeXaml;
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task PlayBuildsThroughThePrepWrapperFirst()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string original = await File.ReadAllTextAsync(_opened.SourcePath(Source), token);
        string ran = WritePrepWrapper();
        string assembly = PrepScan.AssemblyPath(_opened.Directory, AssemblyName);
        await RunEditorAsync(token);
        _ = await EditorAsync("open", token, scene: MainScene);
        try
        {
            _opened.WriteSource(Source, WithMember(original, AddedMember));

            JsonNode built = await EditorAsync("build", token);
            DateTime builtAt = File.GetLastWriteTimeUtc(assembly);
            JsonNode played = await EditorAsync("play", token);
            DateTime playedAt = File.GetLastWriteTimeUtc(assembly);
            _ = await EditorAsync("stop", token);

            _opened.WriteSource(Source, WithMember(original, AddedByThePlayBuild));
            JsonNode gated = await EditorAsync("play", token);

            // An observation, not a pinned engine fact: whether the editor's own pre-play build rewrote the assembly the gated
            // build left (a rerun with --output Detailed shows the times).
            Console.WriteLine(
                $"the editor's own pre-play build: {assembly} written at {builtAt:O} after the gated build, {playedAt:O} after the play"
            );

            Assert.Equal("built", built["build"]!.GetValue<string>());
            Assert.StartsWith("gate: passed", built["buildGate"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("up-to-date", played["build"]!.GetValue<string>());
            Assert.Equal(MainScene, played["playingScene"]!.GetValue<string>());
            Assert.Equal("built", gated["build"]!.GetValue<string>());
            Assert.True(gated["reloadWaited"]!.GetValue<bool>(), gated.ToJsonString());
            Assert.StartsWith("gate: passed", gated["buildGate"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(MainScene, gated["playingScene"]!.GetValue<string>());
            Assert.True(File.Exists(ran), $"{ran} does not exist");
            Assert.Contains(File.ReadAllLines(ran), line => line.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase));
            _ = await EditorAsync("stop", token);
        }
        finally
        {
            _opened.WriteSource(Source, original);
            DeletePrepWrapper();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARedBuildBlocksPlayWithItsErrors()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string original = await File.ReadAllTextAsync(_opened.SourcePath(Source), token);
        _ = WritePrepWrapper();
        await RunEditorAsync(token);
        _ = await EditorAsync("open", token, scene: MainScene);
        try
        {
            _opened.WriteSource(Source, original.Replace("=> n + 1;", "=> n + missingByThePlayBuildTest;", StringComparison.Ordinal));

            McpException refused = await Assert.ThrowsAsync<McpException>(() => EditorAsync("play", token));
            JsonNode state = JsonNode.Parse(await _runtime.GetEditorStateAsync(Session, token))!;

            Assert.StartsWith("the C# build failed, so the editor keeps its previous assembly: ", refused.Message, StringComparison.Ordinal);
            Assert.Contains("CS0103", refused.Message, StringComparison.Ordinal);
            Assert.Equal(string.Empty, state["playingScene"]!.GetValue<string>());
        }
        finally
        {
            _opened.WriteSource(Source, original);
            DeletePrepWrapper();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AMissingSceneIsRefusedBeforeTheBuild()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string original = await File.ReadAllTextAsync(_opened.SourcePath(Source), token);
        string ran = WritePrepWrapper();
        await RunEditorAsync(token);
        _ = await EditorAsync("open", token, scene: MainScene);
        try
        {
            // A change the pre-play build would otherwise compile: the refusal must come before any build of it.
            _opened.WriteSource(Source, WithMember(original, AddedByThePlayBuild));
            File.Delete(ran);

            McpException refused = await Assert.ThrowsAsync<McpException>(() => EditorAsync("play", token, scene: "res://missing.tscn"));

            Assert.Equal("res://missing.tscn does not exist.", refused.Message);
            Assert.False(File.Exists(ran), "the play built the project before refusing the missing scene");
        }
        finally
        {
            _opened.WriteSource(Source, original);
            DeletePrepWrapper();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task EditorOpensAFileALoaderReadsAsAScene()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token, _fakeXaml.Directory);

        JsonNode opened = await EditorAsync("open", token, scene: "res://x.xscn");

        Assert.Contains(opened["openScenes"]!.AsArray(), scene => scene!.GetValue<string>() == "res://x.xscn");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OpenRefusesAFileNoLoaderReadsAsAScene()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.EditorAsync("open", scene: "res://" + Source, session: Session, cancellationToken: token)
        );

        Assert.Contains($"res://{Source} is not a scene: no loader of this project reads .cs as one.", refused.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OpenRefusesAPathOutsideRes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await RunEditorAsync(token);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.EditorAsync("open", scene: "main.tscn", session: Session, cancellationToken: token)
        );

        Assert.Contains("scene must be a res:// path; got 'main.tscn'.", refused.Message, StringComparison.Ordinal);
    }

    private async Task<JsonNode> EditorAsync(string action, CancellationToken token, string? scene = null) =>
        JsonNode.Parse(await _runtime.EditorAsync(action, scene: scene, session: Session, cancellationToken: token))!;

    private Task<string> RunEditorAsync(CancellationToken token, string? directory = null) =>
        _project.RunEditorAsync(directory ?? _opened.Directory, options: new EditorOptions(Session: Session), cancellationToken: token);

    /// <summary>The source with <paramref name="member"/> added as a method before its closing brace.</summary>
    private static string WithMember(string source, string member)
    {
        int end = source.LastIndexOf('}');
        return source[..end] + $"\n    public int {member}() => 7;\n" + source[end..];
    }

    /// <summary>
    /// Writes <see cref="PrepWrapperScript"/> into the copy as its prep wrapper, run by pwsh from the project folder, and the
    /// godot-mcp.json naming it; neither is committed, since the class's other tests share the copy. Returns the file the
    /// running wrapper notes each command in.
    /// </summary>
    private string WritePrepWrapper()
    {
        File.WriteAllText(Path.Combine(_opened.Directory, "prep-wrapper.ps1"), PrepWrapperScript);
        File.WriteAllText(
            Path.Combine(_opened.Directory, ProjectProfile.FileName),
            """{ "prepWrapper": ["pwsh", "-NoProfile", "-File", "prep-wrapper.ps1", "{log}", "--"] }""" + "\n"
        );
        return Path.Combine(_opened.Directory, "prep-wrapper-ran.txt");
    }

    /// <summary>Deletes the prep wrapper, its godot-mcp.json and the commands it noted, leaving the copy as the other tests found it.</summary>
    private void DeletePrepWrapper()
    {
        File.Delete(Path.Combine(_opened.Directory, ProjectProfile.FileName));
        File.Delete(Path.Combine(_opened.Directory, "prep-wrapper.ps1"));
        File.Delete(Path.Combine(_opened.Directory, "prep-wrapper-ran.txt"));
    }

    private async Task<JsonNode> RunScriptAsync(string body, CancellationToken token)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        string json = await _runtime.RunScriptAsync(script, ScriptTimeoutMs, session: Session, cancellationToken: token);
        return JsonNode.Parse(json)!["value"]!;
    }
}
