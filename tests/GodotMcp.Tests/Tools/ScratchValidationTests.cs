using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Session;
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
    public void LeftOutScenesAreEverySceneFileDirectlyInTheFolderInNameOrder()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan plan = Plan(null);

        Assert.Equal(["Alpha", "Beta"], plan.Scenes.Select(scene => scene.Name));
        Assert.Equal(["res://Scratch/Alpha.tscn", "res://Scratch/Beta.tscn"], plan.Scenes.Select(scene => scene.ResPath));
        Assert.True(plan.Prepare);
        Assert.Equal(ScratchDetails.Default, plan.Details);
    }

    [Fact]
    public void AnXscnInTheFolderIsListedBesideTscnScenes()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        WriteScene("Scratch/Delta.scn", "Delta");
        WriteScene("Scratch/Gamma.xscn", "Gamma");
        File.WriteAllText(Path.Combine(_project, "Scratch", "Gamma.xscn.uid"), "uid://abc\n");

        ScratchPlan plan = Plan(null);

        Assert.Equal(["Alpha", "Beta", "Delta", "Gamma"], plan.Scenes.Select(scene => scene.Name));
        Assert.Equal(
            ["res://Scratch/Alpha.tscn", "res://Scratch/Beta.tscn", "res://Scratch/Delta.scn", "res://Scratch/Gamma.xscn"],
            plan.Scenes.Select(scene => scene.ResPath)
        );
    }

    [Fact]
    public void ABareNameResolvesToTheFilesRealExtension()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        WriteScene("Scratch/Gamma.xscn", "Gamma");

        ScratchScenePlan scene = Assert.Single(Plan(["Gamma"]).Scenes);

        Assert.Equal("Gamma", scene.Name);
        Assert.Equal("res://Scratch/Gamma.xscn", scene.ResPath);
    }

    [Fact]
    public void ATscnAndAnXscnOfOneNameAreRefused()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        WriteScene("Scratch/Alpha.xscn", "AlphaRoot");
        const string refusal =
            "scratch.folder res://Scratch holds Alpha.tscn and Alpha.xscn, which share the scene name Alpha; keep one, or pass res:// paths.";

        Assert.Equal(refusal, Refused(() => Plan(null)));
        Assert.Equal(refusal, Refused(() => Plan(["Alpha"])));
        Assert.Equal("res://Scratch/Alpha.xscn", Assert.Single(Plan(["res://Scratch/Alpha.xscn"]).Scenes).ResPath);
    }

    [Fact]
    public void AFolderWithOnlyOtherFilesIsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_project, "Empty"));
        File.WriteAllText(Path.Combine(_project, "Empty", "notes.txt"), "not a scene");
        WriteProfile("""{ "scratch": { "folder": "Empty" } }""");

        string message = Refused(() => Plan(null));

        Assert.StartsWith("scratch.folder res://Empty holds no scene (.tscn, .scn or .xscn): looked in ", message, StringComparison.Ordinal);
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
        WriteScene("Scratch/Gamma.xscn", "Gamma");

        string message = Refused(() => Plan(["Beta", "Hidden"]));

        Assert.Equal(
            "scene 'Hidden' is not in the scratch folder res://Scratch; its scenes are Alpha, Beta, Gamma. Pass one of those, or a " + "res:// path.",
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
    public void AResPathThatIsNoSceneIsRefused(string scene) => Refused(() => Plan([scene]));

    [Fact]
    public void AResPathOfAnyExtensionIsTakenAsAScratchScene() =>
        Assert.Equal("res://Scratch/notes.txt", Assert.Single(Plan(["res://Scratch/notes.txt"]).Scenes).ResPath);

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

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(63.0, "63")]
    [InlineData(1048577.0, "1048577")]
    [InlineData(double.NaN, "NaN")]
    public void AnOptionsMemoryCeilingOutsideItsRangeIsRefused(double megabytes, string shown)
    {
        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(MemoryCeilings: new() { ["Other"] = megabytes })));

        Assert.Equal($"options.memoryCeilings.Other must be a number of megabytes at least 64 and at most 1048576; got {shown}.", message);
    }

    [Fact]
    public void OptionsMemoryCeilingsNamingASceneOutsideThePlanIsRefused()
    {
        string message = Refused(() => Plan(["res://Scratch/Beta.tscn"], new ScratchOptions(MemoryCeilings: new() { ["Alpha"] = 512 })));

        Assert.Equal(
            "options.memoryCeilings scene 'Alpha' is not in the plan; its scenes are Beta. Pass one of those, or leave the scene " + "unbounded.",
            message
        );
    }

    [Fact]
    public void AnOptionsMemoryCeilingWinsOverTheProfilesForItsScene()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch", "memoryCeilings": { "Beta": 2048, "Alpha": 4096 } } }""");

        ScratchPlan plan = Plan(["Beta", "Alpha", "res://Other.tscn"], new ScratchOptions(MemoryCeilings: new() { ["Beta"] = 512 }));

        Assert.Equal(new ScratchMemoryCeiling(512, ScratchMemoryCeiling.OptionsSource), plan.Scenes[0].MemoryCeiling);
        Assert.Equal(new ScratchMemoryCeiling(4096, ScratchMemoryCeiling.ProfileSource), plan.Scenes[1].MemoryCeiling);
        Assert.Null(plan.Scenes[2].MemoryCeiling);
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

    [Fact]
    public void ListStepsIsCarriedOntoThePlan() => Assert.True(Plan(["res://Other.tscn"], new ScratchOptions(ListSteps: true)).ListSteps);

    [Fact]
    public void ListStepsIsAcceptedWithAWindowedRun()
    {
        ScratchPlan plan = Plan(["res://Other.tscn"], new ScratchOptions(ListSteps: true, Windowed: true));

        Assert.True(plan.ListSteps);
        Assert.True(plan.Windowed);
    }

    [Theory]
    [InlineData("steps")]
    [InlineData("capture")]
    [InlineData("watch")]
    [InlineData("background")]
    public void ListStepsIsRefusedWithAnOptionThatPlaysAStep(string key)
    {
        ScratchOptions options = key switch
        {
            "steps" => new ScratchOptions(ListSteps: true, Steps: Tokens("[0]")),
            "capture" => new ScratchOptions(ListSteps: true, Windowed: true, Capture: new ScratchCaptureOptions(Screenshot: true)),
            "watch" => new ScratchOptions(ListSteps: true, Watch: new ScratchWatchOptions()),
            _ => new ScratchOptions(ListSteps: true, Background: true),
        };

        Assert.Equal($"options.listSteps plays no step, so it takes no options.{key}", Refused(() => Plan(["res://Other.tscn"], options)));
    }

    [Fact]
    public void ListStepsNamesTheFirstOfItsOffendingOptionsInItsOwnOrder()
    {
        ScratchOptions options = new(ListSteps: true, Steps: Tokens("[0]"), Watch: new ScratchWatchOptions(), Background: true);

        Assert.Equal("options.listSteps plays no step, so it takes no options.steps", Refused(() => Plan(["res://Other.tscn"], options)));
    }

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
    [InlineData("""{ "Other": true }""", "steps for Other: it is a true; it must be a step index, name or range, or an array of them.")]
    public void ABadListInAStepsMapIsRefusedNamingItsScene(string steps, string refusal) =>
        Assert.Equal(refusal, Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(steps)))));

    [Theory]
    [InlineData("true")]
    public void StepsNeitherAnArrayNorAnObjectIsRefused(string steps) =>
        Assert.Equal(
            "steps is a true; it must be a step index, name or range, an array of them, or an object mapping a scene name to one or " + "an array.",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(steps))))
        );

    [Theory]
    [InlineData("\"2-5\"", "[\"2-5\"]")]
    [InlineData("3", "[3]")]
    [InlineData("\"name\"", "[\"name\"]")]
    public void ALoneStepTokenPlaysAsAOneItemList(string lone, string list)
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan one = Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(lone)));
        ScratchPlan many = Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens(list)));

        Assert.Equal(many.Steps, one.Steps);
        Assert.Single(one.Steps!);
        Assert.Empty(one.StepsByScene);
    }

    [Fact]
    public void ALoneTokenInAStepsMapIsThatScenesList()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        ScratchPlan one = Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("""{ "Other": "1-2" }""")));
        ScratchPlan many = Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("""{ "Other": ["1-2"] }""")));

        Assert.Equal(many.StepsFor("Other"), one.StepsFor("Other"));
        Assert.Null(one.Steps);
    }

    [Fact]
    public void ABadLoneTokenIsRefusedAsInAList()
    {
        string list = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("""["5-2"]"""))));
        string lone = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Steps: Tokens("\"5-2\""))));

        Assert.Equal(list, lone);
        Assert.Equal("options.steps token \"5-2\" is a range whose start is after its end; write it \"2-5\".", lone);
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

    [Fact]
    public void AWatchWithTracksAWindowAndAUnitIsPlanned()
    {
        ScratchWatchOptions watch = new(
            new WatchTracks(Properties: [new WatchPropertyTrack("/root/Other", "position")]),
            new WatchWindow(GameMs: 500),
            new WatchOptions(Unit: "physics")
        );

        Assert.Same(watch, Plan(["res://Other.tscn"], new ScratchOptions(Watch: watch)).Watch);
        Assert.Null(Plan(["res://Other.tscn"]).Watch);
    }

    [Fact]
    public void AWatchCallIsRefusedSinceTheRunnerPlaysTheSteps()
    {
        ScratchWatchOptions watch = new(
            new WatchTracks(Properties: [new WatchPropertyTrack("/root/Other", "position")]),
            Options: new WatchOptions(Call: new MethodCall("/root/Other", "PlayStep"))
        );

        string message = Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Watch: watch)));

        Assert.Equal("options.watch takes no options.call: the runner plays the steps", message);
    }

    [Fact]
    public void AWatchTheWatchToolRefusesIsRefusedWithItsMessageNamingTheOption()
    {
        ScratchWatchOptions noTrack = new(new WatchTracks());
        ScratchWatchOptions badWindow = new(new WatchTracks(Monitors: ["frame_ms"]), new WatchWindow(Frames: 0));

        Assert.StartsWith(
            "options.watch: tracks needs at least one track:",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Watch: noTrack))),
            StringComparison.Ordinal
        );
        Assert.Equal(
            "options.watch: frames must be between 1 and 7200; got 0.",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Watch: badWindow)))
        );
    }

    [Fact]
    public void WindowedIsPlannedAndHeadlessIsTheDefault()
    {
        Assert.True(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true)).Windowed);
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: false)).Windowed);
        Assert.False(Plan(["res://Other.tscn"]).Windowed);
    }

    [Fact]
    public void AudibleIsPlannedOnAWindowedRunAndOffByDefault()
    {
        Assert.True(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true, Audible: true)).Audible);
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true)).Audible);
        Assert.False(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true, Audible: false)).Audible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void AnAudibleRunWithoutWindowedIsRefused(bool? windowed)
    {
        Assert.Equal(
            ScratchTools.AudibleNeedsWindowedRefusal,
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Windowed: windowed, Audible: true)))
        );
        Assert.Equal(
            "options.audible needs options.windowed: true, since a headless scene's game always runs Godot's Dummy audio driver",
            ScratchTools.AudibleNeedsWindowedRefusal
        );
    }

    [Fact]
    public void ACaptureWithoutWindowedIsRefused() =>
        Assert.Equal(
            "options.capture needs options.windowed: true, since a headless scene draws no frames",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Capture: new ScratchCaptureOptions(Screenshot: true))))
        );

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ACaptureWhoseScreenshotIsNotTrueIsRefused(bool? screenshot) =>
        Assert.Equal(
            "options.capture.screenshot must be true",
            Refused(() => Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true, Capture: new ScratchCaptureOptions(Screenshot: screenshot))))
        );

    [Fact]
    public void ACaptureKeyOtherThanStepsOrScreenshotIsRefused() =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ScratchOptions>("""{"windowed": true, "capture": {"screenshot": true, "frames": 3}}""", ToolJson.Options)
        );

    [Fact]
    public void ACaptureStepTheSceneLacksIsRefusedAsAnUnknownStepIs()
    {
        ScratchStepNames names = new(["open", "fight"], new HashSet<int>());

        ScratchPlan named = Plan(["res://Other.tscn"], CaptureOf("""["nope"]"""));
        ScratchPlan indexed = Plan(["res://Other.tscn"], CaptureOf("[2]"));

        Assert.StartsWith(
            "options.capture.steps token \"nope\" of Other matches no step; its steps are ",
            ScratchRun.CaptureSteps(named.Capture!, "Other", names).Error,
            StringComparison.Ordinal
        );
        Assert.StartsWith(
            "options.capture.steps token 2 of Other matches no step; its steps are ",
            ScratchRun.CaptureSteps(indexed.Capture!, "Other", names).Error,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void CaptureStepsTakeIndexesAndNamesAndLeftOutCaptureEveryPlayedStep()
    {
        ScratchStepNames names = new(["open", "fight", "close"], new HashSet<int>());

        ScratchPlan given = Plan(["res://Other.tscn"], CaptureOf("""[2, "open"]"""));
        ScratchPlan every = Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true, Capture: new ScratchCaptureOptions(Screenshot: true)));

        (int[]? steps, string? error) = ScratchRun.CaptureSteps(given.Capture!, "Other", names);
        Assert.Null(error);
        Assert.Equal([0, 2], steps!);
        Assert.Equal(((int[]?)null, (string?)null), ScratchRun.CaptureSteps(every.Capture!, "Other", names));
        Assert.Null(Plan(["res://Other.tscn"], new ScratchOptions(Windowed: true)).Capture);
    }

    [Theory]
    [InlineData("true", "options.capture.steps is a true; it must be a step index or name, or an array of them.")]
    [InlineData("[]", "options.capture.steps is empty; list the steps to shoot by index or name, or leave it out to shoot every played step.")]
    [InlineData("[-1]", "options.capture.steps token -1 is not a step; a token is a step index, a whole number from 0 up, or a step's exact name.")]
    [InlineData("[1.5]", "options.capture.steps token 1.5 is not a step; a token is a step index, a whole number from 0 up, or a step's exact name.")]
    [InlineData(
        "[true]",
        "options.capture.steps token true is not a step; a token is a step index, a whole number from 0 up, or a step's exact name."
    )]
    public void CaptureStepsThatAreNoListOfStepsAreRefused(string steps, string refusal) =>
        Assert.Equal(refusal, Refused(() => Plan(["res://Other.tscn"], CaptureOf(steps))));

    [Theory]
    [InlineData("\"x\"", "[\"x\"]")]
    [InlineData("0", "[0]")]
    public void ALoneCaptureStepIsShotAsAOneItemList(string lone, string list)
    {
        ScratchPlan one = Plan(["res://Other.tscn"], CaptureOf(lone));
        ScratchPlan many = Plan(["res://Other.tscn"], CaptureOf(list));

        Assert.Equal(many.Capture!.Steps, one.Capture!.Steps);
        Assert.Single(one.Capture!.Steps!);
    }

    [Fact]
    public void ACaptureStepGivenAsANumberAndAsItsTextIsShotOnce()
    {
        ScratchStepNames names = new(["open", "fight"], new HashSet<int>());

        (int[]? steps, string? error) = ScratchRun.CaptureSteps(Plan(["res://Other.tscn"], CaptureOf("""[0, "0", 0]""")).Capture!, "Other", names);

        Assert.Null(error);
        Assert.Equal([0], steps!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    [InlineData(-1)]
    public async Task AWaitSecondsOutsideOneToSixHundredIsRefused(int seconds)
    {
        await using RegistryHarness harness = new();
        ScratchTools tools = new(harness.Sessions, TestCSharp.Unused());

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.ScratchStatusAsync(_project, "20261019T222144-3f2a", waitSeconds: seconds, cancellationToken: TestContext.Current.CancellationToken)
        );

        Assert.Equal("waitSeconds must be a whole number of seconds from 1 to 600; call scratch_status again to keep waiting.", refused.Message);
    }

    [Fact]
    public async Task AWaitSecondsWithAStopIsRefused()
    {
        await using RegistryHarness harness = new();
        ScratchTools tools = new(harness.Sessions, TestCSharp.Unused());

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            tools.ScratchStatusAsync(
                _project,
                "20261019T222144-3f2a",
                stop: true,
                waitSeconds: 5,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("waitSeconds and stop: true cannot be combined: stop ends the run at once, so there is nothing to wait for.", refused.Message);
    }

    [Fact]
    public void LeftOutUserDataKeepsTheRealOne()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");

        Assert.Null(Plan(null).UserData);
    }

    [Fact]
    public void TheSectionsScratchUserDataGivesEveryScene()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a scratch user://.");
        WriteProfile("""{ "scratch": { "folder": "Scratch", "userData": "scratch" } }""");

        ScratchUserDataRequest? userData = Plan(null).UserData;

        Assert.NotNull(userData);
        Assert.Null(userData.SeedFolder);
    }

    [Fact]
    public void TheTopLevelUserDataIsUsedWhenTheSectionLeavesItOut()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a scratch user://.");
        WriteProfile("""{ "userData": "scratch", "scratch": { "folder": "Scratch" } }""");

        Assert.NotNull(Plan(null).UserData);
    }

    [Fact]
    public void TheSectionsRealBeatsTheTopLevelScratch()
    {
        WriteProfile("""{ "userData": "scratch", "scratch": { "folder": "Scratch", "userData": "real" } }""");

        Assert.Null(Plan(null).UserData);
    }

    [Fact]
    public void OptionsUserDataBeatsTheSections()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a scratch user://.");
        WriteProfile("""{ "scratch": { "folder": "Scratch", "userData": "scratch" } }""");
        var real = new ScratchOptions(UserData: JsonSerializer.SerializeToElement("real"));

        Assert.Null(Plan(null, real).UserData);

        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        var scratch = new ScratchOptions(UserData: JsonSerializer.SerializeToElement("scratch"));

        Assert.NotNull(Plan(null, scratch).UserData);
    }

    [Fact]
    public void ASeedIsResolvedInTheProject()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a scratch user://.");
        string seed = Path.Combine(_project, "seed");
        Directory.CreateDirectory(seed);
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        var options = new ScratchOptions(UserData: JsonSerializer.SerializeToElement(new { seed = "seed" }));

        ScratchUserDataRequest? userData = Plan(null, options).UserData;

        Assert.NotNull(userData);
        Assert.Equal(Path.GetFullPath(seed), Path.GetFullPath(userData.SeedFolder!));
    }

    [Fact]
    public void AMissingSeedIsRefusedBeforeAnySceneLaunches()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a scratch user://.");
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        var options = new ScratchOptions(UserData: JsonSerializer.SerializeToElement(new { seed = "nowhere" }));

        string refused = Refused(() => Plan(null, options));

        Assert.Equal("userData.seed names nowhere, which is not a folder.", refused);
    }

    [Fact]
    public void ABadOptionsUserDataIsRefused()
    {
        WriteProfile("""{ "scratch": { "folder": "Scratch" } }""");
        var options = new ScratchOptions(UserData: JsonSerializer.SerializeToElement(5));

        string refused = Refused(() => Plan(null, options));

        Assert.Equal("""options.userData must be "real", "scratch" or {"seed": "<folder in the project>"}, not 5.""", refused);
    }

    private static ScratchOptions CaptureOf(string steps) =>
        new(Windowed: true, Capture: new ScratchCaptureOptions(Steps: Tokens(steps), Screenshot: true));

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
