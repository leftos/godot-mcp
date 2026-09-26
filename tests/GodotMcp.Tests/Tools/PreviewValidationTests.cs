using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>preview_scene's argument checks, which refuse before anything launches, and its session's name; no Godot runs here.</summary>
public sealed class PreviewValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly string _project;

    public PreviewValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _project = _temp.Combine("game");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        foreach (string file in new[] { Path.Combine("levels", "a.tscn"), "b.scn", "c.escn", "d.res", "e.tres", "main.gd", "f.TSCN" })
        {
            string path = Path.Combine(_project, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        File.WriteAllText(_temp.Combine("outside.tscn"), string.Empty);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData("res://levels/a.tscn", "res://levels/a.tscn")]
    [InlineData("levels/a.tscn", "res://levels/a.tscn")]
    [InlineData("res://b.scn", "res://b.scn")]
    [InlineData("c.escn", "res://c.escn")]
    [InlineData("d.res", "res://d.res")]
    [InlineData("e.tres", "res://e.tres")]
    public void ASceneInsideTheProjectBecomesItsResPath(string scene, string expected) =>
        Assert.Equal(expected, PreviewTools.CheckScene(_project, scene));

    [Theory]
    [InlineData("main.gd")]
    [InlineData("res://main.gd")]
    [InlineData("f.TSCN")]
    [InlineData("project.godot")]
    public void AFileGodotWouldNotRunAsASceneIsRefused(string scene)
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.CheckScene(_project, scene));

        Assert.Contains("is not a scene file", refused.Message);
        Assert.Contains(".tscn, .scn, .escn, .res or .tres", refused.Message);
    }

    [Theory]
    [InlineData("../outside.tscn")]
    [InlineData("res://../outside.tscn")]
    public void ASceneOutsideTheProjectIsRefused(string scene)
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.CheckScene(_project, scene));

        Assert.Contains("is outside the project folder", refused.Message);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheProjectIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.CheckScene(_project, _temp.Combine("outside.tscn")));

        Assert.Contains("is outside the project folder", refused.Message);
    }

    [Fact]
    public void AMissingSceneIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.CheckScene(_project, "res://levels/missing.tscn"));

        Assert.Contains("does not exist", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySceneIsRefused(string scene)
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.CheckScene(_project, scene));

        Assert.StartsWith("scene holds an empty path", refused.Message);
    }

    [Theory]
    [InlineData("1280X720")]
    [InlineData("1280x")]
    [InlineData("0x720")]
    [InlineData("1280 x 720")]
    [InlineData("big")]
    public void AMalformedResolutionIsRefused(string resolution)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(Resolution: resolution), "preview", 480)
        );

        Assert.Contains("WIDTHxHEIGHT", refused.Message);
    }

    [Fact]
    public void AResolutionBecomesTheResolutionArgument()
    {
        PreviewPlan plan = PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(Resolution: "1280x720"), "preview", 480);

        Assert.Equal(["--resolution", "1280x720"], plan.Launch.EngineArgs);
    }

    [Fact]
    public void ThePlanIsAQuietPreviewLaunchOfTheScene()
    {
        PreviewPlan plan = PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(Prepare: "never"), "full", 480);

        Assert.Equal("res://levels/a.tscn", plan.Launch.Scene);
        Assert.True(plan.Launch.Quiet);
        Assert.True(plan.Launch.Preview);
        Assert.False(plan.Launch.Prepare);
        Assert.Empty(plan.Launch.EngineArgs);
        Assert.Equal(ScreenshotMode.Full, plan.Mode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void APreviewMaxWidthUnderOneIsRefused(int previewMaxWidth)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(), "preview", previewMaxWidth)
        );

        Assert.Contains("previewMaxWidth must be at least 1", refused.Message);
    }

    [Fact]
    public void APreviewMaxWidthOfOneIsTaken()
    {
        PreviewPlan plan = PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(), "preview", 1);

        Assert.Equal(1, plan.Parameters["previewMaxWidth"]!.GetValue<int>());
    }

    [Fact]
    public void AnUnknownResponseModeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(), "thumb", 480));

        Assert.Contains("is not one of path_only, preview, full", refused.Message);
    }

    [Fact]
    public void AnUnknownPrepareIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            PreviewTools.Plan(_project, "levels/a.tscn", new PreviewOptions(Prepare: "sometimes"), "preview", 480)
        );

        Assert.Contains("prepare takes", refused.Message);
    }

    [Fact]
    public void AFolderWithoutAProjectIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            PreviewTools.Plan(_temp.Path, "levels/a.tscn", new PreviewOptions(), "preview", 480)
        );

        Assert.Contains("holds no project.godot", refused.Message);
    }

    [Fact]
    public void APreviewSessionIsNeverNamedAsTheFolderIsAndEachGetsItsOwnName()
    {
        string folderName = SessionRegistry.NameFor(null, _project);

        GodotSession first = _sessions.ReservePreview(_project);
        GodotSession second = _sessions.ReservePreview(_project);

        Assert.NotEqual(folderName, first.Name, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(folderName, second.Name, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(first.Name, second.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("game.preview-1", first.Name);
        Assert.Equal("game.preview-2", second.Name);
    }
}
