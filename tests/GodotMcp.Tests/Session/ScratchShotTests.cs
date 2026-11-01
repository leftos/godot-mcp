using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scratch run's shots (options.capture), with no Godot: how a scene or step name becomes one safe path part, where a shot
/// is kept, which folder each scene of a run shoots into, how an until's reply names its capture, and what the run warns of.
/// </summary>
public sealed class ScratchShotTests
{
    [Theory]
    [InlineData("..", "__")]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("x.", "x_")]
    [InlineData("x ", "x_")]
    [InlineData(".hidden", "_hidden")]
    [InlineData("", "step")]
    [InlineData("Ström-run_2", "Ström-run_2")]
    [InlineData("CON", "CON_")]
    [InlineData("com1", "com1_")]
    [InlineData("nul", "nul_")]
    [InlineData("LPT9", "LPT9_")]
    [InlineData("COM0", "COM0")]
    [InlineData("CONSOLE", "CONSOLE")]
    public void ANameBecomesOneSafePathPart(string name, string safe) => Assert.Equal(safe, ScratchRun.FileSafe(name));

    [Fact]
    public void ALongNameIsCutTo64Characters() => Assert.Equal(new string('a', 64), ScratchRun.FileSafe(new string('a', 70)));

    [Fact]
    public void AShotIsKeptUnderTheRunsFolderAsItsIndexThenItsSafeStepName() =>
        Assert.Equal(
            Path.Combine("logs", "scratch-20261101T000000-abcd", "Fight.1", "3-a_b.png"),
            ScratchRun.ShotPath("logs", "20261101T000000-abcd", "Fight.1", 3, "a/b")
        );

    [Fact]
    public void TwoScenesOfTheSameNameInOtherFoldersEachTakeTheirPlaceInThePlan() =>
        Assert.Equal(["Fight.0", "Fight.1", "Boss"], ScratchRun.ShotFolders(Scenes("res://a/Fight.tscn", "res://b/Fight.tscn", "res://Boss.tscn")));

    [Fact]
    public void ScenesWhoseNamesMakeTheSameFolderCollideAsWell() =>
        Assert.Equal(
            ["Intro", "Boss_Fight.1", "Boss_Fight.2", "fight.3", "Fight.4"],
            ScratchRun.ShotFolders(
                Scenes("res://Intro.tscn", "res://Boss.Fight.tscn", "res://Boss_Fight.tscn", "res://fight.tscn", "res://x/Fight.tscn")
            )
        );

    [Fact]
    public void AUniqueSceneKeepsItsPlainNameAsItsFolder() => Assert.Equal(["Fight"], ScratchRun.ShotFolders(Scenes("res://a/Fight.tscn")));

    [Fact]
    public void AReplayAloneShootsIntoItsOwnFolder()
    {
        Assert.Equal("Fight.alone", ScratchRun.ReplayFolder("Fight"));
        Assert.Equal("Fight.0.alone", ScratchRun.ReplayFolder("Fight.0"));
    }

    [Fact]
    public void ANumberedFolderCannotClashWithASceneNamedLikeIt() =>
        Assert.Equal(
            ["Fight.0", "Fight.1", "Fight_1"],
            ScratchRun.ShotFolders(Scenes("res://a/Fight.tscn", "res://b/Fight.tscn", "res://Fight.1.tscn"))
        );

    [Fact]
    public void ASceneNamedLikeAReplayFolderKeepsItsOwnFolder()
    {
        string[] folders = ScratchRun.ShotFolders(Scenes("res://Fight.tscn", "res://Fight.alone.tscn"));

        Assert.Equal(["Fight", "Fight_alone"], folders);
        Assert.DoesNotContain(ScratchRun.ReplayFolder(folders[0]), folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRunsShotFolderIsCreatedBeforeAnySceneShoots()
    {
        using TempDirectory logs = new();
        RecordingLogger logger = new();

        ScratchRun.CreateShotRoot(logs.Path, "20261101T000000-abcd", logger);

        Assert.True(Directory.Exists(Path.Combine(logs.Path, "scratch-20261101T000000-abcd")));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void AShotFolderThatCannotBeCreatedIsLoggedAndTheRunGoesOn()
    {
        using TempDirectory logs = new();
        File.WriteAllText(Path.Combine(logs.Path, "scratch-20261101T000000-abcd"), "a file where the folder goes");
        RecordingLogger logger = new();

        ScratchRun.CreateShotRoot(logs.Path, "20261101T000000-abcd", logger);

        string warning = Assert.Single(logger.Warnings);
        Assert.Contains(ScratchRun.ShotRoot(logs.Path, "20261101T000000-abcd"), warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntilReplyWithAWarningGivesTheWarningAsWhyThereIsNoShot()
    {
        (string? capture, string? reason) = ScratchRun.UntilShot(JsonNode.Parse("""{"met": true, "warning": "the frame was not drawn"}"""));

        Assert.Null(capture);
        Assert.Equal("the frame was not drawn", reason);
        Assert.Equal("the screenshot at step 2 'hit' failed: the frame was not drawn", ScratchVerdict.ShotFailed(2, "hit", reason!));
    }

    [Fact]
    public void AnUntilReplyWithNeitherAScreenshotNorAWarningSaysSo() =>
        Assert.Equal(((string?)null, "the until's reply carried no screenshot"), ScratchRun.UntilShot(JsonNode.Parse("""{"met": true}""")));

    [Fact]
    public void AnUntilReplyWithAScreenshotGivesItsPath() =>
        Assert.Equal(
            ("C:/game/.godot/godot-mcp/screenshots/a.png", (string?)null),
            ScratchRun.UntilShot(JsonNode.Parse("""{"met": true, "screenshot": {"path": "C:/game/.godot/godot-mcp/screenshots/a.png"}}"""))
        );

    [Fact]
    public void CapturedStepsTheRunDoesNotPlayAreNamedAscending() =>
        Assert.Equal(
            "options.capture names steps this run did not play: 1 'hit', 3 'close'",
            ScratchRun.CaptureNotPlayed([3, 0, 1], [0, 2], ["open", "hit", "fight", "close"])
        );

    [Fact]
    public void CapturedStepsTheRunPlaysAllGiveNoWarning() => Assert.Null(ScratchRun.CaptureNotPlayed([0, 2], [0, 1, 2], ["open", "hit", "fight"]));

    private static ScratchScenePlan[] Scenes(params string[] resPaths) => [.. resPaths.Select(Scene)];

    private static ScratchScenePlan Scene(string resPath) =>
        new(Path.GetFileNameWithoutExtension(resPath), resPath, 1, []) { TimeScale = 1, EngineArgs = [] };

    /// <summary>A logger that keeps each warning's message.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
