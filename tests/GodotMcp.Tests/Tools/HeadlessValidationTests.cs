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
        foreach (string target in new[] { "notes.txt", "res://icon.svg", "project.godot" })
        {
            McpException refused = Assert.Throws<McpException>(() => HeadlessTools.CheckTargets(_project, [target]));

            Assert.Equal(
                $"targets '{target}' is not a script, scene or resource: validate checks .gd, .cs, .tscn, .scn, .tres and .res files.",
                refused.Message
            );
        }
    }

    [Fact]
    public void ACSharpTargetIsAccepted() => Assert.Equal(["res://Main.cs"], HeadlessTools.CheckTargets(_project, ["Main.cs"]));

    [Fact]
    public void ACSharpTargetNeedsTheProjectToHaveOneCsproj()
    {
        string several = _temp.Combine("several");
        Directory.CreateDirectory(several);
        foreach (string file in new[] { "project.godot", "A.csproj", "B.csproj" })
        {
            File.WriteAllText(Path.Combine(several, file), string.Empty);
        }

        McpException none = Assert.Throws<McpException>(() => HeadlessTools.CsprojFor(_project, "res://Main.cs"));
        McpException many = Assert.Throws<McpException>(() => HeadlessTools.CsprojFor(several, "res://Main.cs"));

        Assert.Equal($"res://Main.cs is a C# script, but {_project} has no .csproj, so nothing compiles it.", none.Message);
        Assert.Equal(
            $"res://Main.cs is a C# script, but {several} has several .csproj files; validate builds only a project with one.",
            many.Message
        );
    }

    [Fact]
    public void ACSharpTargetsListsAreCappedAtTwentyAndOnlyErrorsMakeItInvalid()
    {
        string main = Path.Combine(_project, "Main.cs");
        string other = Path.Combine(_project, "Other.cs");
        BuildDiagnostic[] diagnostics =
        [
            .. Enumerable.Range(1, 25).Select(line => new BuildDiagnostic(main.ToUpperInvariant(), line, 1, "CS1002", "; expected", "error")),
            .. Enumerable.Range(1, 25).Select(line => new BuildDiagnostic(main, line, 2, "CS0219", "unused", "warning")),
            .. Enumerable.Range(1, 25).Select(line => new BuildDiagnostic(other, line, 1, "CS0103", "no oops", "error")),
            new BuildDiagnostic(other, 1, 1, "CS0168", "declared, never used", "warning"),
        ];
        DateTime builtAt = new(2026, 9, 27, 10, 11, 12, DateTimeKind.Utc);
        PrepResult upToDate = new() { Build = "up-to-date", Import = "not-needed" };
        CsTarget target = new("res://Main.cs", main, ["res://main.tscn"], 3);
        JsonNode reply = JsonNode.Parse("""{"checked": 1, "results": [], "engineErrors": []}""")!;

        JsonObject red = HeadlessTools.ShapeValidation(
            new HeadlessResult(reply, [], upToDate, null) { LastBuild = new SavedBuild(builtAt, "failed", diagnostics) },
            [target]
        );
        JsonObject warned = HeadlessTools.ShapeValidation(
            new HeadlessResult(reply, [], upToDate, null)
            {
                LastBuild = new SavedBuild(builtAt, "built", [.. diagnostics.Where(d => d.Severity == "warning")]),
            },
            [target]
        );

        Assert.False(red["valid"]!.GetValue<bool>());
        Assert.Equal(2, red["checked"]!.GetValue<int>());
        JsonNode file = Assert.Single(red["csharp"]!["files"]!.AsArray())!;
        Assert.Equal("res://Main.cs", file["path"]!.GetValue<string>());
        Assert.Equal(20, file["errors"]!.AsArray().Count);
        Assert.Equal(5, file["errorsOmitted"]!.GetValue<int>());
        Assert.Equal(20, file["warnings"]!.AsArray().Count);
        Assert.Equal(5, file["warningsOmitted"]!.GetValue<int>());
        Assert.Equal("last build at 2026-09-27T10:11:12Z", file["from"]!.GetValue<string>());
        Assert.Equal(["res://main.tscn"], file["scenes"]!.AsArray().Select(scene => scene!.GetValue<string>()));
        Assert.Equal(3, file["scenesOmitted"]!.GetValue<int>());
        Assert.Equal(20, red["csharp"]!["otherErrors"]!.AsArray().Count);
        Assert.Equal(5, red["csharp"]!["otherErrorsOmitted"]!.GetValue<int>());
        Assert.Equal("CS0103", red["csharp"]!["otherErrors"]![0]!["code"]!.GetValue<string>());
        JsonNode warning = file["warnings"]![0]!;
        Assert.Equal(main, warning["file"]!.GetValue<string>());
        Assert.Equal(1, warning["line"]!.GetValue<int>());
        Assert.Equal(2, warning["column"]!.GetValue<int>());
        Assert.Equal("warning", warning["severity"]!.GetValue<string>());
        Assert.True(warned["valid"]!.GetValue<bool>(), warned.ToJsonString());
        Assert.Empty(warned["csharp"]!["otherErrors"]!.AsArray());
        Assert.Null(warned["csharp"]!["files"]![0]!["errorsOmitted"]);
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

        Assert.Equal("scenePath 'res://main.gd' is not a scene: the scene tools take .tscn and .scn files.", refused.Message);
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

        JsonObject shaped = HeadlessTools.ShapeValidation(new HeadlessResult(reply, [], PrepResult.Skipped, null), []);

        Assert.False(shaped["valid"]!.GetValue<bool>());
        Assert.Equal("bad setting", shaped["engineErrors"]![0]!["message"]!.GetValue<string>());
        Assert.Null(shaped["csharp"]);
    }
}
