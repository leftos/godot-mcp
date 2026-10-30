using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>run_scratches' argument checks, which refuse before anything launches, its plan, and its sessions' names; no Godot runs here.</summary>
public sealed class ScratchValidationTests : IDisposable
{
    private const string NotAToken = "a token is a step index, a step's name, or a range such as \"2-5\".";

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
        Assert.Equal(ScratchDetails.Default, plan.Details);
    }

    [Theory]
    [InlineData("true", nameof(ScratchDetails.Full))]
    [InlineData("\"full\"", nameof(ScratchDetails.Full))]
    [InlineData("\"compact\"", nameof(ScratchDetails.Compact))]
    [InlineData("false", nameof(ScratchDetails.Default))]
    [InlineData("null", nameof(ScratchDetails.Default))]
    public void DetailsTakesTrueFullCompactOrFalse(string details, string expected) =>
        Assert.Equal(expected, Plan(["res://Other.tscn"], new ScratchOptions(Details: Tokens(details))).Details.ToString());

    [Theory]
    [InlineData("\"short\"")]
    [InlineData("3")]
    [InlineData("{}")]
    public void DetailsOtherThanTrueFullCompactOrFalseIsRefused(string details) =>
        Assert.Equal(
            $"details is {details}; it must be true, \"full\" or \"compact\".",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Details: Tokens(details))))
        );

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

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-1.0, "-1")]
    [InlineData(0.009, "0.009")]
    [InlineData(1e-9, "1E-09")]
    [InlineData(100.5, "100.5")]
    public void ATimeScaleOutsideAHundredthToAHundredIsRefused(double scale, string shown) =>
        Assert.Equal(
            $"options.timeScale must be at least 0.01 and at most 100; got {shown}.",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(TimeScale: scale)))
        );

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(20001)]
    public void ADetailCharsOutsideOneToTwentyThousandIsRefused(int chars) =>
        Assert.Equal(
            $"options.detailChars must be at least 1 and at most 20000; got {chars.ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(DetailChars: chars)))
        );

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-1.0, "-1")]
    [InlineData(3600.5, "3600.5")]
    [InlineData(double.NaN, "NaN")]
    public void AnOptionsCeilingOutsideItsRangeIsRefused(double seconds, string shown)
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Ceilings: new() { ["Other"] = seconds })));

        Assert.Equal($"options.ceilings.Other must be a number of seconds above 0 and at most 3600; got {shown}.", message);
    }

    [Fact]
    public void OptionsCeilingsNamingASceneOutsideThePlanIsRefused()
    {
        string message = Refused(() => Plan(["res://Scratch/Beta.tscn"], new ScratchOptions(Ceilings: new() { ["Alpha"] = 5 })));

        Assert.Equal(
            "options.ceilings scene 'Alpha' is not in the plan; its scenes are Beta. Pass one of those, or leave the scene's ceiling "
                + "to its paces.",
            message
        );
    }

    [Fact]
    public void AnOptionsCeilingWinsOverTheProfilesForItsScene()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "ceilings": { "Beta": 20, "Alpha": 30 } } }""");

        ScratchPlan plan = Plan(["Beta", "Alpha", "res://Other.tscn"], new ScratchOptions(Ceilings: new() { ["Beta"] = 5 }));

        Assert.Equal(new ScratchCeiling(5, ScratchCeiling.OptionsSource), plan.Scenes[0].Ceiling);
        Assert.Equal(new ScratchCeiling(30, ScratchCeiling.ProfileSource), plan.Scenes[1].Ceiling);
        Assert.Null(plan.Scenes[2].Ceiling);
    }

    [Fact]
    public void ASceneExpectsTheProfilesExpectThenItsOwnExpectFor()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "expect": ["^A", "^B"], "expectFor": { "Beta": ["^C"], "Gamma": ["^D"] } } }""");

        ScratchPlan plan = Plan(["Beta", "Alpha"]);

        Assert.Equal(["^A", "^B", "^C"], plan.Scenes[0].Expect.Select(pattern => pattern.Regex.ToString()));
        Assert.Equal(["^A", "^B"], plan.Scenes[1].Expect.Select(pattern => pattern.Regex.ToString()));
    }

    [Fact]
    public void ASceneOfAProfileWithoutExpectExpectsNothing()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "expectFor": { "Alpha": ["^C"] } } }""");

        Assert.Empty(Assert.Single(Plan(["Beta"]).Scenes).Expect);
    }

    [Fact]
    public void AProfileCeilingForASceneOutsideTheRunIsIgnored()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "ceilings": { "Alpha": 30 } } }""");

        ScratchScenePlan planned = Assert.Single(Plan(["Beta"]).Scenes);

        Assert.Equal("Beta", planned.Name);
        Assert.Null(planned.Ceiling);
    }

    [Theory]
    [InlineData("Red")]
    [InlineData("failed")]
    [InlineData("")]
    public void AnOnlyOtherThanAllOrRedIsRefused(string only) =>
        Assert.Equal($"only must be \"all\" or \"red\", not \"{only}\"", Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Only: only))));

    [Theory]
    [InlineData("all")]
    [InlineData("red")]
    public void OnlyWithBackgroundIsRefused(string only) =>
        Assert.Equal(
            "options.only is not read by a background run; pass only to scratch_status, which returns the run's result",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Background: true, Only: only)))
        );

    [Theory]
    [InlineData("all")]
    [InlineData("red")]
    public void AllAndRedAreAcceptedAsOnly(string only) =>
        Assert.Equal("res://Other.tscn", Assert.Single(Plan(["res://Other.tscn"], new ScratchOptions(Only: only)).Scenes).ResPath);

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(100.0)]
    public void ATimeScaleRidesOnThePlanAndEveryScene(double scale)
    {
        ScratchPlan plan = Plan(["res://Other.tscn"], new ScratchOptions(TimeScale: scale));

        Assert.Equal(scale, plan.TimeScale);
        Assert.Equal(scale, Assert.Single(plan.Scenes).TimeScale);
    }

    [Fact]
    public void TheTimeScaleIsOneByDefault()
    {
        ScratchPlan plan = Plan(["res://Other.tscn"]);

        Assert.Equal(1.0, plan.TimeScale);
        Assert.Equal(1.0, Assert.Single(plan.Scenes).TimeScale);
    }

    [Fact]
    public void AProfilesTimeScaleOptOutBeatsOptionsTimeScaleAndOptionsPaceKeepsIt()
    {
        WriteProfile(
            """
            {
              "scratch": {
                "folder": "Scratch",
                "pace": { "Beta": { "seconds": 2, "reason": "its physics needs real frames", "timeScale": 1 } }
              }
            }
            """
        );

        ScratchPlan scaled = Plan(null, new ScratchOptions(TimeScale: 4));
        ScratchPlan paced = Plan(null, new ScratchOptions(Pace: 1, TimeScale: 4));

        Assert.Equal(4.0, scaled.TimeScale);
        Assert.Equal([4.0, 1.0], scaled.Scenes.Select(scene => scene.TimeScale));
        Assert.Equal([4.0, 1.0], paced.Scenes.Select(scene => scene.TimeScale));
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
        ScratchPlan given = Plan(null, new ScratchOptions(Pace: 1.5, Details: Tokens("true")));

        Assert.Equal([ScratchTools.DefaultPace, 3.0], profiled.Scenes.Select(scene => scene.Pace));
        Assert.Equal([null, "the tray lies at rest"], profiled.Scenes.Select(scene => scene.PaceReason));
        Assert.Equal([null, "slow tray"], profiled.Scenes.Select(scene => scene.Known));
        Assert.Equal([1.5, 1.5], given.Scenes.Select(scene => scene.Pace));
        Assert.All(given.Scenes, scene => Assert.Null(scene.PaceReason));
        Assert.Equal(ScratchDetails.Full, given.Details);
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
    public void TheProfilesExitHoldRidesOnThePlanAndOptionsPaceKeepsIt()
    {
        WriteProfile(
            """
            {
              "scratch": {
                "folder": "Scratch",
                "pace": { "Beta": { "seconds": 10, "reason": "its until waits for the card", "exitHold": 2 } }
              }
            }
            """
        );

        ScratchPlan profiled = Plan(null);
        ScratchPlan paced = Plan(null, new ScratchOptions(Pace: 1.5));

        Assert.Equal([null, 2.0], profiled.Scenes.Select(scene => scene.ExitHold));
        Assert.Equal([null, 2.0], paced.Scenes.Select(scene => scene.ExitHold));
        Assert.Equal([1.5, 1.5], paced.Scenes.Select(scene => scene.Pace));
    }

    [Fact]
    public void KeepGoingIsTheOptionsElseOff()
    {
        Assert.False(Plan(["res://Other.tscn"]).KeepGoing);
        Assert.True(Plan(["res://Other.tscn"], new ScratchOptions(KeepGoing: true)).KeepGoing);
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(KeepGoing: false)).KeepGoing);
    }

    [Fact]
    public void ARangeOfNonAsciiDigitsIsANameTheScenesStepsDecide() =>
        Assert.Equal(
            [new ScratchStepToken("١-٢", IsNumber: false)],
            Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("[\"١-٢\"]"))).Steps
        );

    [Fact]
    public void StepsAreNoneByDefaultAndRideOnThePlanAsTheCallWroteThem()
    {
        ScratchPlan none = Plan(["res://Other.tscn"]);
        ScratchPlan given = Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("""[3, "last", "1-2", "03", 2, 4.0, "2-2"]""")));

        Assert.Null(none.Steps);
        Assert.Equal(
            [
                new("3", IsNumber: true),
                new("last", IsNumber: false),
                new("1-2", IsNumber: false),
                new("03", IsNumber: false),
                new("2", IsNumber: true),
                new("4", IsNumber: true),
                new ScratchStepToken("2-2", IsNumber: false),
            ],
            given.Steps
        );
    }

    [Theory]
    [InlineData("[]", "options.steps is empty; list the steps to play by index, name or range (\"2-5\"), or leave it out to play every step.")]
    [InlineData("[-1]", "options.steps token -1 is not a step index; a number names a step by its index, a whole number from 0 up.")]
    [InlineData("[2.5]", "options.steps token 2.5 is not a step index; a number names a step by its index, a whole number from 0 up.")]
    [InlineData("[\"5-2\"]", "options.steps token \"5-2\" is a range whose start is after its end; write it \"2-5\".")]
    [InlineData("[3, \"3\"]", "options.steps tokens 3 and \"3\" are the same token; keep one.")]
    [InlineData("[\"a\", \"a\"]", "options.steps tokens \"a\" and \"a\" are the same token; keep one.")]
    [InlineData("[true]", "options.steps token true is neither a number nor a string; " + NotAToken)]
    [InlineData("[{}]", "options.steps token {} is neither a number nor a string; " + NotAToken)]
    public void ABadStepsListIsRefusedBeforeAnythingLaunches(string steps, string refusal) =>
        Assert.Equal(refusal, Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(steps)))));

    [Fact]
    public void AStepsListAppliesToEveryScene()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan plan = Plan(["Beta", "Alpha"], new ScratchOptions(Steps: Tokens("""[0, "fight"]""")));

        ScratchStepToken[] tokens = [new("0", IsNumber: true), new("fight", IsNumber: false)];
        Assert.Equal(tokens, plan.Steps);
        Assert.Empty(plan.StepsByScene);
        Assert.All(plan.Scenes, scene => Assert.Equal(tokens, plan.StepsFor(scene.Name)));
    }

    [Fact]
    public void AStepsMapGivesEachSceneItsOwnTokensAndASceneItLeavesOutPlaysEveryStep()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan plan = Plan(
            ["Beta", "Alpha", "res://Other.tscn"],
            new ScratchOptions(Steps: Tokens("""{ "Beta": [1, "fight"], "Other": ["2-3"] }"""))
        );

        Assert.Null(plan.Steps);
        Assert.Equal([new("1", IsNumber: true), new ScratchStepToken("fight", IsNumber: false)], plan.StepsFor("Beta"));
        Assert.Null(plan.StepsFor("Alpha"));
        Assert.Equal([new ScratchStepToken("2-3", IsNumber: false)], plan.StepsFor("Other"));
    }

    [Fact]
    public void AStepsMapKeyNamingASceneOutsideThePlanIsRefused()
    {
        string message = Refused(() => Plan(["res://Scratch/Beta.tscn"], new ScratchOptions(Steps: Tokens("""{ "Alpha": [0] }"""))));

        Assert.Equal(
            "options.steps scene 'Alpha' is not in the plan; its scenes are Beta. Pass one of those; a scene the map leaves out plays "
                + "every step.",
            message
        );
    }

    [Theory]
    [InlineData(
        """{ "Other": [] }""",
        "steps for Other: options.steps is empty; list the steps to play by index, name or range (\"2-5\"), or leave it out to play every step."
    )]
    [InlineData("""{ "Other": [1, 1] }""", "steps for Other: options.steps tokens 1 and 1 are the same token; keep one.")]
    [InlineData(
        """{ "Other": ["5-2"] }""",
        "steps for Other: options.steps token \"5-2\" is a range whose start is after its end; write it \"2-5\"."
    )]
    [InlineData("""{ "Other": 3 }""", "steps for Other: it is a number; it must be an array of step indices, names or ranges.")]
    public void ABadListInAStepsMapIsRefusedNamingItsScene(string steps, string refusal) =>
        Assert.Equal(refusal, Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(steps)))));

    [Theory]
    [InlineData("\"3\"", "string")]
    [InlineData("3", "number")]
    [InlineData("true", "true")]
    public void StepsNeitherAnArrayNorAnObjectIsRefused(string steps, string kind) =>
        Assert.Equal(
            $"steps is a {kind}; it must be an array of step indices, names or ranges, or an object mapping a scene name to such an array.",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(steps))))
        );

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
        WriteScene("Scratch/Gamma.tscn", "Gamma");
        WriteProfile("""{ "scratch": { "folder": "Scratch", "parallel": 3 } }""");
        ScratchPlan profiled = Plan(null);
        ScratchPlan given = Plan(null, new ScratchOptions(Parallel: 2));

        Assert.Equal((1, 3, 2), (none.Parallel, profiled.Parallel, given.Parallel));
    }

    [Fact]
    public void AOneSceneRunPlansOneSlotWhateverParallelSays()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "parallel": 4 } }""");

        ScratchPlan given = Plan(["res://Other.tscn"], new ScratchOptions(Parallel: 3));
        ScratchPlan profiled = Plan(["Beta"]);
        ScratchPlan folder = Plan(null);

        Assert.Equal((1, 1, 2), (given.Parallel, profiled.Parallel, folder.Parallel));
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
    public void ProfileEngineArgsComeBeforeTheOptionsOnes()
    {
        WriteProfile("""{ "scratch": { "engineArgs": ["--a"] } }""");

        ScratchPlan plan = Plan(["res://Other.tscn"], new ScratchOptions(EngineArgs: ["--b"]));

        Assert.Equal(["--a", "--b"], plan.Scenes[0].EngineArgs);
    }

    [Theory]
    [MemberData(nameof(RunnerOwnedEngineArgs))]
    public void AnEngineArgumentTheRunnerOwnsIsRefusedWhateverItsSource(string argument, bool fromProfile)
    {
        string source = "options.engineArgs";
        ScratchOptions options = new(EngineArgs: [argument]);
        if (fromProfile)
        {
            WriteProfile(JsonSerializer.Serialize(new { scratch = new { engineArgs = new[] { argument } } }));
            source = ProfilePath;
            options = new ScratchOptions();
        }

        string message = Refused(() => Plan(["res://Other.tscn"], options));

        Assert.Equal(
            "run_scratches sets --headless and --time-scale itself and puts the scene's own arguments after --, so engineArgs "
                + $"cannot hold {argument}; it came from {source}.",
            message
        );
    }

    /// <summary>Every argument the runner sets itself or takes as its separator, however Godot trims or extends it.</summary>
    private static readonly string[] RunnerOwnedArgs =
    [
        "--",
        "++",
        "--headless",
        "--headless=x",
        "--time-scale",
        "--time-scale=2",
        "--scratch-auto",
        "--scratch-auto=3",
        " --",
        "-- ",
        " --headless",
    ];

    /// <summary>Each owner argument from each source: options.engineArgs and the profile.</summary>
    public static TheoryData<string, bool> RunnerOwnedEngineArgs()
    {
        TheoryData<string, bool> cases = [];
        foreach (string argument in RunnerOwnedArgs)
        {
            foreach (bool fromProfile in new[] { false, true })
            {
                cases.Add(argument, fromProfile);
            }
        }

        return cases;
    }

    [Fact]
    public void ANullEngineArgIsRefusedNamingItsSource()
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(EngineArgs: ["--ok", null!])));

        Assert.Equal("engineArgs holds a null entry; it came from options.engineArgs.", message);
    }

    [Theory]
    [InlineData("--display-driver", false)]
    [InlineData("--display-driver", true)]
    [InlineData("--audio-driver", false)]
    [InlineData("--audio-driver", true)]
    [InlineData(" --display-driver", false)]
    [InlineData(" --display-driver", true)]
    public void EngineArgsNamingADisplayOrAudioDriverAreRefused(string argument, bool fromProfile)
    {
        string source = "options.engineArgs";
        ScratchOptions options = new(EngineArgs: [argument]);
        if (fromProfile)
        {
            WriteProfile(JsonSerializer.Serialize(new { scratch = new { engineArgs = new[] { argument } } }));
            source = ProfilePath;
            options = new ScratchOptions();
        }

        string message = Refused(() => Plan(["res://Other.tscn"], options));

        Assert.Equal(
            "run_scratches runs every scene headless, and Godot keeps the last driver argument it reads, so engineArgs cannot "
                + $"hold {argument}: the scene would open a window or play sound; it came from {source}.",
            message
        );
    }

    [Fact]
    public void ADriverArgumentWithAnEqualsSignIsAccepted()
    {
        ScratchPlan plan = Plan(["res://Other.tscn"], new ScratchOptions(EngineArgs: ["--display-driver=x"]));

        Assert.Equal(["--display-driver=x"], plan.Scenes[0].EngineArgs);
    }

    [Theory]
    [MemberData(nameof(BadResolutions))]
    public void ABadResolutionIsRefusedUpFrontNamingItsSource(string[] engineArgs, string parserText, bool fromProfile)
    {
        string source = "options.engineArgs";
        ScratchOptions options = new(EngineArgs: engineArgs);
        if (fromProfile)
        {
            WriteProfile(JsonSerializer.Serialize(new { scratch = new { engineArgs } }));
            source = ProfilePath;
            options = new ScratchOptions();
        }

        string message = Refused(() => Plan(["res://Other.tscn"], options));

        Assert.StartsWith(parserText, message, StringComparison.Ordinal);
        Assert.EndsWith($" It came from {source}.", message, StringComparison.Ordinal);
    }

    /// <summary>Each unreadable --resolution with the parser's own refusal text, and whether it comes from the profile.</summary>
    public static TheoryData<string[], string, bool> BadResolutions()
    {
        TheoryData<string[], string, bool> cases = [];
        cases.Add(["--resolution"], "--resolution is the last engine argument and has no WIDTHxHEIGHT after it, e.g. 1280x720.", true);
        cases.Add(
            ["--resolution", "1280"],
            "--resolution is \"1280\"; it must be WIDTHxHEIGHT in pixels, with a lowercase x and no spaces, e.g. 1280x720.",
            false
        );
        cases.Add(
            ["--resolution", "0x720"],
            "--resolution 0x720 has a side outside 1 to 16384 pixels, the largest window Godot can draw and screenshot; pick a size within it.",
            false
        );
        return cases;
    }

    [Fact]
    public void AResolutionGodotCanReadIsAccepted()
    {
        ScratchPlan plan = Plan(["res://Other.tscn"], new ScratchOptions(EngineArgs: ["--resolution", "1280x720"]));

        Assert.Equal(["--resolution", "1280x720"], plan.Scenes[0].EngineArgs);
    }

    [Fact]
    public void ScratchEngineArgsNeverFallBackToTheTopLevelOnes()
    {
        WriteProfile("""{ "engineArgs": ["--headless"], "scratch": { "folder": "Scratch" } }""");

        ScratchPlan plan = Plan(null);

        Assert.All(plan.Scenes, scene => Assert.Empty(scene.EngineArgs));
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

    private static JsonElement Tokens(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private void WriteProfile(string json) => File.WriteAllText(ProfilePath, json);

    private void WriteScene(string relative, string root)
    {
        string text = $"[gd_scene format=3]\n\n[node name=\"{root}\" type=\"Node\"]\n\n[node name=\"Child\" type=\"Node\" parent=\".\"]\n";
        File.WriteAllText(Path.Combine(_project, relative), text);
    }
}
