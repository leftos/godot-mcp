using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The headless tools' argument checks, which refuse before a headless Godot starts; no Godot runs here.</summary>
public sealed class HeadlessValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly string _project;

    public HeadlessValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _project = _temp.Combine("game");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        foreach (string file in new[] { "main.gd", Path.Combine("levels", "a.tscn"), Path.Combine("data", "x.tres"), "notes.txt", "Main.cs" })
        {
            string path = Path.Combine(_project, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void MoreThanFiftyTargetsAreRefused()
    {
        string[] targets = [.. Enumerable.Repeat("main.gd", 51)];

        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, targets));
        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, []));

        Assert.StartsWith("targets takes 1 to 50 paths; got 51.", refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("targets takes 1 to 50 paths; got 0.", none.Message, StringComparison.Ordinal);
        Assert.Equal(50, HeadlessTools.CheckTargets(_project, [.. Enumerable.Repeat("main.gd", 50)]).Count);
    }

    [Fact]
    public void ATargetEscapingTheProjectIsRefused()
    {
        string outside = _temp.Combine("outside.gd");
        File.WriteAllText(outside, string.Empty);

        foreach (string target in new[] { "../outside.gd", "res://../outside.gd", @"levels\..\..\outside.gd", outside })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, [target]));

            Assert.Equal($"targets '{target}' is outside the project folder {_project}; pass a res:// path or a path inside it.", refused.Message);
        }
    }

    [Fact]
    public void AnUnsupportedExtensionIsRefused()
    {
        foreach (string target in new[] { "notes.txt", "res://Main.cs", "project.godot" })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, [target]));

            Assert.Equal(
                $"targets '{target}' is not a script, scene or resource: validate checks .gd, .tscn, .scn, .tres and .res files.",
                refused.Message
            );
        }
    }

    [Fact]
    public void ResAndRelativeTargetsAreAccepted()
    {
        string[] targets = ["res://main.gd", "levels/a.tscn", @"levels\a.tscn", Path.Combine(_project, "data", "x.tres")];

        IReadOnlyList<string> checkedFiles = HeadlessTools.CheckTargets(_project, targets);

        Assert.Equal(["res://main.gd", "res://levels/a.tscn", "res://levels/a.tscn", "res://data/x.tres"], checkedFiles);
    }

    [Fact]
    public void AMissingOrEmptyTargetIsRefused()
    {
        McpException missing = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, ["gone.gd"]));
        McpException empty = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, [" "]));

        Assert.Equal($"targets 'gone.gd' does not exist: {Path.Combine(_project, "gone.gd")}.", missing.Message);
        Assert.Equal("targets holds an empty path. Pass res:// paths or paths relative to the project folder.", empty.Message);
    }

    [Fact]
    public void AScenePathMustBeAScene()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckScenePath(_project, "res://main.gd"));

        Assert.Equal("scenePath 'res://main.gd' is not a scene: get_scene_file_tree reads .tscn and .scn files.", refused.Message);
        Assert.Equal("res://levels/a.tscn", HeadlessTools.CheckScenePath(_project, "levels/a.tscn"));
    }

    [Theory]
    [InlineData(-1, null, null, null, "maxDepth must be 0 or more; got -1.")]
    [InlineData(null, -1, null, null, "offset must be 0 or more; got -1.")]
    [InlineData(null, null, 501, null, "limit must be 1 to 500; got 501.")]
    [InlineData(null, null, 0, null, "limit must be 1 to 500; got 0.")]
    [InlineData(null, null, null, "sometimes", "prepare takes \"auto\" or \"never\"; got \"sometimes\".")]
    public async Task SceneFileTreeOptionsAreChecked(int? maxDepth, int? offset, int? limit, string? prepare, string message)
    {
        HeadlessTools tools = new(_sessions);
        SceneFileTreeOptions options = new(maxDepth, offset, limit, prepare);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.GetSceneFileTreeAsync(_project, "levels/a.tscn", null, options, TestContext.Current.CancellationToken)
        );

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public void ASweepOfMoreThanFiveHundredFilesIsRefused()
    {
        string sweep = _temp.Combine("sweep");
        Directory.CreateDirectory(sweep);
        File.WriteAllText(Path.Combine(sweep, "project.godot"), "config_version=5\n");
        foreach (int index in Enumerable.Range(0, 501))
        {
            File.WriteAllText(Path.Combine(sweep, $"s{index:000}.gd"), "extends Node\n");
        }

        File.WriteAllText(Path.Combine(sweep, "notes.txt"), "not swept");
        Git.InitAndCommitAll(sweep);

        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.VersionedTargets(sweep, NullLogger.Instance));
        File.Delete(Path.Combine(sweep, "s500.gd"));
        IReadOnlyList<string> swept = HeadlessTools.VersionedTargets(sweep, NullLogger.Instance);

        Assert.Equal(
            "the project has 501 versioned scripts, scenes and resources; validate takes at most 500 at once: pass targets.",
            refused.Message
        );
        Assert.Equal(500, swept.Count);
        Assert.Equal("res://s000.gd", swept[0]);
    }

    [Fact]
    public void ASweepOutsideGitIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => HeadlessTools.VersionedTargets(_project, NullLogger.Instance));

        Assert.Equal($"git could not list the files of {_project} (is it in a git repository?), so validate needs targets.", refused.Message);
    }

    [Fact]
    public void ErrorsLoggedBeforeTheFirstFileMakeTheProjectInvalid()
    {
        JsonNode reply = JsonNode.Parse(
            """{"checked": 1, "results": [], "engineErrors": [{"message": "bad setting", "file": "core/config/project_settings.cpp", "line": 9}]}"""
        )!;

        JsonObject shaped = HeadlessTools.ShapeValidation(new HeadlessResult(reply, [], PrepResult.Skipped, null));

        Assert.False(shaped["valid"]!.GetValue<bool>());
        Assert.Equal("bad setting", shaped["engineErrors"]![0]!["message"]!.GetValue<string>());
        Assert.Null(shaped["csharp"]);
    }
}
