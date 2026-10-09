using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>run_scratches' argument checks, which refuse before anything launches, its plan, and its sessions' names; no Godot runs here.</summary>
public sealed class ScratchValidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _project;

    public ScratchValidationTests()
    {
        _project = _temp.Combine("game");
        Directory.CreateDirectory(Path.Combine(_project, "Scratch", "Deeper"));
        File.WriteAllText(Path.Combine(_project, "project.godot"), "config_version=5\n");
        WriteScene("Scratch/Beta.tscn", "Beta");
        WriteScene("Scratch/Alpha.tscn", "AlphaRoot");
        WriteScene("Scratch/Deeper/Hidden.tscn", "Hidden");
        WriteScene("Other.tscn", "Other");
        File.WriteAllText(Path.Combine(_project, "Scratch", "notes.txt"), "not a scene");
        File.WriteAllText(Path.Combine(_project, "Scratch", "Upper.TSCN"), "[node name=\"Upper\" type=\"Node\"]\n");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LeftOutScenesAreEveryTscnDirectlyInTheFolderInNameOrder()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan plan = Plan(null);

        Assert.Equal(["Alpha", "Beta"], plan.Scenes.Select(scene => scene.Name));
        Assert.Equal(["res://Scratch/Alpha.tscn", "res://Scratch/Beta.tscn"], plan.Scenes.Select(scene => scene.ResPath));
        Assert.True(plan.Prepare);
        Assert.False(plan.Details);
    }

    [Fact]
    public void AFolderThatDiffersInCaseFromTheDiskIsRefused()
    {
        WriteProfile("""{ "scratch": { "folder": "scratch" } }""");

        string refusal = Refused(() => Plan(null));

        Assert.Equal("scratch.folder \"scratch\" differs in case from the folder on disk, res://Scratch; use the exact case.", refusal);
    }

    [Fact]
    public void AScenesRootNameIsNeverReadFromItsFile()
    {
        File.WriteAllText(Path.Combine(_project, "Broken.tscn"), "[gd_scene format=3]\n");

        Assert.Equal("res://Broken.tscn", Assert.Single(Plan(["res://Broken.tscn"]).Scenes).ResPath);
    }

    [Fact]
    public void AResFolderAndAResPathAreAccepted()
    {
        WriteProfile("""{ "scratch": { "folder": "res://Scratch/" } }""");

        ScratchPlan plan = Plan(["Beta", "res://Other.tscn"]);

        Assert.Equal(["res://Scratch/Beta.tscn", "res://Other.tscn"], plan.Scenes.Select(scene => scene.ResPath));
        Assert.Equal(["Beta", "Other"], plan.Scenes.Select(scene => scene.Name));
    }

    [Fact]
    public void AResPathNeedsNoScratchSection()
    {
        ScratchPlan plan = Plan(["res://Scratch/Beta.tscn"]);

        ScratchScenePlan scene = Assert.Single(plan.Scenes);
        Assert.Equal(ScratchTools.DefaultPace, scene.Pace);
        Assert.Same(ScratchProfile.DefaultPatterns, plan.Patterns);
    }

    [Fact]
    public void NoScenesAndNoScratchSectionIsRefused()
    {
        string message = Refused(() => Plan(null));

        Assert.Equal($"{ProfilePath} has no scratch section; pass scenes, or add scratch.folder.", message);
    }

    [Fact]
    public void NoScenesAndNoFolderIsRefused()
    {
        WriteProfile("""{ "scratch": { "pace": { "Beta": 1 } } }""");

        string message = Refused(() => Plan(null));

        Assert.Equal($"{ProfilePath} (scratch): \"folder\" is not set; pass scenes, or add scratch.folder.", message);
    }

    [Fact]
    public void ASceneNameNotInTheFolderIsRefusedListingItsScenes()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        string message = Refused(() => Plan(["Beta", "Hidden"]));

        Assert.Equal(
            "scene 'Hidden' is not in the scratch folder res://Scratch; its scenes are Alpha, Beta. Pass one of those, or a res:// path.",
            message
        );
    }

    [Fact]
    public void ASceneNameWithoutAFolderIsRefused()
    {
        string message = Refused(() => Plan(["Beta"]));

        Assert.Contains("scene 'Beta' is a name", message, StringComparison.Ordinal);
        Assert.Contains("add scratch.folder", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("res://Scratch/Missing.tscn")]
    [InlineData("res://Scratch/notes.txt")]
    public void AResPathThatIsNoSceneIsRefused(string scene) => Refused(() => Plan([scene]));

    [Fact]
    public void AMissingFolderIsRefused()
    {
        WriteProfile("""{ "scratch": { "folder": "Nowhere" } }""");

        Assert.StartsWith("scratch.folder \"Nowhere\" is not a folder in the project", Refused(() => Plan(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySceneListIsRefused() => Assert.StartsWith("scenes is empty", Refused(() => Plan([])), StringComparison.Ordinal);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(120.5)]
    [InlineData(double.NaN)]
    public void ABadPaceIsRefused(double pace)
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Pace: pace)));

        Assert.EndsWith("it must be a number of seconds above 0 and at most 120.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePaceIsTheOptionsElseTheProfilesElseTheDefault()
    {
        WriteProfile(
            """
            {
              "scratch": {
                "folder": "Scratch",
                "pace": { "Beta": { "seconds": 3, "reason": "the tray lies at rest" } },
                "known": { "Beta": "slow tray" }
              }
            }
            """
        );

        ScratchPlan profiled = Plan(null);
        ScratchPlan given = Plan(null, new ScratchOptions(Pace: 1.5, Details: true));

        Assert.Equal([ScratchTools.DefaultPace, 3.0], profiled.Scenes.Select(scene => scene.Pace));
        Assert.Equal([null, "the tray lies at rest"], profiled.Scenes.Select(scene => scene.PaceReason));
        Assert.Equal([null, "slow tray"], profiled.Scenes.Select(scene => scene.Known));
        Assert.Equal([1.5, 1.5], given.Scenes.Select(scene => scene.Pace));
        Assert.All(given.Scenes, scene => Assert.Null(scene.PaceReason));
        Assert.True(given.Details);
    }

    [Fact]
    public void TheProfilesStepPacesRideOnThePlanAndOptionsPaceDropsThem()
    {
        WriteProfile(
            """
            {
              "scratch": {
                "folder": "Scratch",
                "pace": {
                  "Beta": { "seconds": 0.5, "reason": "quick", "steps": { "2": 30, "fight": { "seconds": 20, "reason": "the batch" } } }
                }
              }
            }
            """
        );

        ScratchPlan profiled = Plan(null);
        ScratchScenePlan given = Plan(["Beta"], new ScratchOptions(Pace: 1.5)).Scenes[0];

        Assert.Empty(profiled.Scenes[0].StepPaces);
        ScratchScenePlan beta = profiled.Scenes[1];
        Assert.Equal(0.5, beta.Pace);
        Assert.Equal(new Dictionary<string, ScratchPace> { ["2"] = new(30, null), ["fight"] = new(20, "the batch") }, beta.StepPaces);
        Assert.Equal(1.5, given.Pace);
        Assert.Empty(given.StepPaces);
    }

    [Fact]
    public void KeepGoingIsTheOptionsElseOff()
    {
        Assert.False(Plan(["res://Other.tscn"]).KeepGoing);
        Assert.True(Plan(["res://Other.tscn"], new ScratchOptions(KeepGoing: true)).KeepGoing);
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(KeepGoing: false)).KeepGoing);
    }

    [Fact]
    public void ABareNumberPaceCarriesNoReason()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "pace": { "Beta": 2 } } }""");

        ScratchScenePlan beta = Plan(["Beta"]).Scenes[0];

        Assert.Equal(2.0, beta.Pace);
        Assert.Null(beta.PaceReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void AParallelOutsideOneToFourIsRefused(int parallel)
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Parallel: parallel)));

        Assert.Equal($"options.parallel is {parallel}; it must be a whole number from 1 to 4.", message);
    }

    [Fact]
    public void ParallelIsTheOptionsElseTheProfilesElseOne()
    {
        ScratchPlan none = Plan(["res://Other.tscn"]);
        WriteProfile("""{ "scratch": { "folder": "Scratch", "parallel": 3 } }""");
        ScratchPlan profiled = Plan(null);
        ScratchPlan given = Plan(null, new ScratchOptions(Parallel: 2));

        Assert.Equal((1, 3, 2), (none.Parallel, profiled.Parallel, given.Parallel));
    }

    [Fact]
    public void ScratchUserArgsReplaceTheTopLevelOnesAndOptionsAppend()
    {
        WriteProfile("""{ "userArgs": ["--top"], "scratch": { "folder": "Scratch", "userArgs": ["--scratch"] } }""");
        ScratchPlan scratch = Plan(["Beta"], new ScratchOptions(UserArgs: ["--given"]));
        WriteProfile("""{ "userArgs": ["--top"], "scratch": { "folder": "Scratch" } }""");
        ScratchPlan top = Plan(["Beta"]);

        Assert.Equal(["--scratch", "--given"], scratch.Scenes[0].UserArgs);
        Assert.Equal(["--top"], top.Scenes[0].UserArgs);
    }

    [Fact]
    public void ScratchAutoInTheUserArgumentsIsRefused()
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(UserArgs: ["--scratch-auto"])));

        Assert.StartsWith("The user arguments hold --scratch-auto", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareNeverIsHonouredAndAnUnknownPrepareIsRefused()
    {
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(Prepare: "never")).Prepare);
        Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Prepare: "sometimes")));
    }

    [Theory]
    [InlineData("probe", "TrayScratch", "probe.scratch-TrayScratch")]
    [InlineData("probe", "Odd Name!", "probe.scratch-Odd_Name_")]
    public void AScratchSessionIsNamedForItsFolderAndScene(string folder, string scene, string expected) =>
        Assert.Equal(expected, SessionRegistry.ScratchName(folder, scene));

    [Fact]
    public void ASessionPrefixNamesItsSessions() => Assert.Equal("agent-a.scratch-Tray", SessionRegistry.ScratchName("agent-a", "Tray"));

    [Fact]
    public void TheSessionPrefixIsTheOptionsElseTheFolderName()
    {
        Assert.Equal("game", Plan(["res://Other.tscn"]).SessionPrefix);
        Assert.Equal("agent-a", Plan(["res://Other.tscn"], new ScratchOptions(Session: "agent-a")).SessionPrefix);
    }

    [Fact]
    public void ABadSessionNameIsRefusedBeforeAnythingLaunches()
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Session: "agent a")));

        Assert.Equal("session 'agent a' is not a valid name: a session name is 1 to 64 characters of letters, digits, '.', '_' and '-'.", message);
    }

    [Fact]
    public void ALongScratchSessionNameIsCutToSixtyFourCharacters()
    {
        string name = SessionRegistry.ScratchName(new string('f', 60), new string('s', 70));

        Assert.Equal(64, name.Length);
        Assert.StartsWith(".scratch-sss", name, StringComparison.Ordinal);
    }

    private string ProfilePath => Path.Combine(_project, ProjectProfile.FileName);

    private ScratchPlan Plan(string[]? scenes, ScratchOptions? options = null) =>
        ScratchTools.Plan(_project, scenes, options ?? new ScratchOptions());

    private static string Refused(Func<object> action) => Assert.Throws<McpException>(action).Message;

    private void WriteProfile(string json) => File.WriteAllText(ProfilePath, json);

    private void WriteScene(string relative, string root)
    {
        string text = $"[gd_scene format=3]\n\n[node name=\"{root}\" type=\"Node\"]\n\n[node name=\"Child\" type=\"Node\" parent=\".\"]\n";
        File.WriteAllText(Path.Combine(_project, relative), text);
    }
}
