using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>The order run_scratches starts a run's scenes in: a folder run's longest pace first, a listed run's own order.</summary>
public sealed class ScratchRunOrderTests
{
    private static readonly ScratchScenePlan[] Mixed = [Scene("A", 0.5), Scene("B", 3), Scene("C", 10), Scene("D", 0.5)];

    [Fact]
    public void AFolderRunStartsTheSlowestPaceFirst() => Assert.Equal([2, 1, 0, 3], ScratchRun.StartOrder(Mixed, listed: false));

    [Fact]
    public void AListedRunKeepsItsOrder() => Assert.Equal([0, 1, 2, 3], ScratchRun.StartOrder(Mixed, listed: true));

    [Fact]
    public void EqualPacesKeepNameOrder()
    {
        ScratchScenePlan[] scenes = [Scene("A", 1), Scene("B", 2), Scene("C", 1), Scene("D", 2), Scene("E", 1)];

        Assert.Equal([1, 3, 0, 2, 4], ScratchRun.StartOrder(scenes, listed: false));
    }

    [Fact]
    public void AFolderRunOrdersByTheLargestPaceASceneHoldsStepPacesIncluded()
    {
        ScratchScenePlan[] scenes =
        [
            Scene("A", 0.5),
            Scene("B", 3),
            Scene("C", 0.5) with
            {
                StepPaces = new Dictionary<string, ScratchPace> { ["2"] = new(25, "the batch"), ["fight"] = new(1, null) },
            },
            Scene("D", 4) with
            {
                StepPaces = new Dictionary<string, ScratchPace> { ["0"] = new(0.25, null) },
            },
        ];

        Assert.Equal([2, 3, 1, 0], ScratchRun.StartOrder(scenes, listed: false));
    }

    [Fact]
    public void AFolderRunCountsAScenesExitHoldAmongItsPaces()
    {
        ScratchScenePlan[] scenes = [Scene("A", 2), Scene("B", 0.5) with { ExitHold = 5 }, Scene("C", 3)];

        Assert.Equal([1, 2, 0], ScratchRun.StartOrder(scenes, listed: false));
    }

    [Fact]
    public void AFolderRunOrdersByTheWallTimeAPaceTakesAtItsScenesTimeScale()
    {
        ScratchScenePlan[] scenes = [Scene("A", 8) with { TimeScale = 4 }, Scene("B", 3), Scene("C", 1) with { TimeScale = 4 }];

        Assert.Equal([1, 0, 2], ScratchRun.StartOrder(scenes, listed: false));
    }

    [Fact]
    public void SetupStepsResolveByIndexOrName()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = new ScratchSetup(["3", "build", "1", "build"]) };

        (int[] steps, string? error) = ScratchRun.SetupSteps(scene, [0], ["open", "build", "close", "rest"]);
        (int[] none, string? noError) = ScratchRun.SetupSteps(Scene("B", 0.5), [0], ["open"]);

        Assert.Null(error);
        Assert.Equal([1, 3], steps);
        Assert.Empty(none);
        Assert.Null(noError);
    }

    [Theory]
    [InlineData("5", "setup \"5\" of A matches no step; its steps are 0 open, 1 hit, 2 hit")]
    [InlineData("shut", "setup \"shut\" of A matches no step; its steps are 0 open, 1 hit, 2 hit")]
    [InlineData("hit", "setup \"hit\" of A names 2 steps (1, 2); key it by index")]
    public void ASetupKeyNamingNoStepOrASharedNameIsAnError(string key, string refusal)
    {
        (int[] steps, string? error) = ScratchRun.SetupSteps(
            Scene("A", 0.5) with
            {
                Setup = new ScratchSetup(["open", key]),
            },
            [0],
            ["open", "hit", "hit"]
        );

        Assert.Equal(refusal, error);
        Assert.Empty(steps);
    }

    private static IReadOnlyList<ScratchStepToken> Steps(string json) => ScratchTools.StepTokens(JsonSerializer.Deserialize<JsonElement[]>(json))!;

    private static ScratchSetup Setup(string[] always, params (string Step, string[] Keys)[] steps) =>
        new(always, steps.ToDictionary(step => step.Step, step => (IReadOnlyList<string>)step.Keys, StringComparer.Ordinal));

    [Fact]
    public void ASelectedStepsOwnListPlaysBesideTheSceneWideList()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup(["build"], ("fight", ["batch"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["fight"]"""), ["build", "fight", "batch", "note"]);

        Assert.Null(error);
        Assert.Equal([1], selection.Selected!);
        Assert.Equal([0, 2], selection.Setup);
        Assert.Equal([0, 1, 2], selection.Played);
    }

    [Fact]
    public void AnUnselectedStepsOwnListDoesNotPlay()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("fight", ["batch"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["note"]"""), ["build", "fight", "batch", "note"]);

        Assert.Null(error);
        Assert.Empty(selection.Setup);
        Assert.Equal([3], selection.Played);
    }

    [Fact]
    public void TwoSelectedStepsListsUnionAscendingAndDeduplicated()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("fight", ["4", "batch"]), ("note", ["batch"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(
            scene,
            Steps("""["fight", "note"]"""),
            ["build", "fight", "batch", "note", "say"]
        );

        Assert.Null(error);
        Assert.Equal([2, 4], selection.Setup);
        Assert.Equal([1, 2, 3, 4], selection.Played);
    }

    [Fact]
    public void ARangeTokenAppliesEachCoveredStepsOwnList()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("build", ["open"]), ("fight", ["batch"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(
            scene,
            Steps("""["1-2"]"""),
            ["open", "build", "fight", "batch", "note"]
        );

        Assert.Null(error);
        Assert.Equal([0, 3], selection.Setup);
        Assert.Equal([0, 1, 2, 3], selection.Played);
    }

    [Fact]
    public void AStepsOwnListNamingItselfIsIgnored()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("b", ["b", "c"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["b"]"""), ["a", "b", "c"]);

        Assert.Null(error);
        Assert.Equal([2], selection.Setup);
        Assert.Equal([1, 2], selection.Played);
    }

    [Fact]
    public void ASetupStepsOwnListDoesNotApply()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("a", ["b"]), ("b", ["c"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["a"]"""), ["a", "b", "c"]);

        Assert.Null(error);
        Assert.Equal([1], selection.Setup);
        Assert.Equal([0, 1], selection.Played);
    }

    [Fact]
    public void AStepsKeyNamingNoStepIsAnError()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("shut", ["open"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["open"]"""), ["open", "hit", "hit"]);

        Assert.Equal("setup for step \"shut\" of A matches no step; its steps are 0 open, 1 hit, 2 hit", error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Played);
    }

    [Fact]
    public void AStepsKeyNamingSharedStepsIsAnError()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("hit", ["open"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["open"]"""), ["open", "hit", "hit"]);

        Assert.Equal("setup for step \"hit\" of A names 2 steps (1, 2); key it by index", error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Played);
    }

    [Fact]
    public void ASelectedStepsListNamesItsKeysWithItsOwnLabel()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("fight", ["nope"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["fight"]"""), ["build", "fight", "batch", "note"]);

        Assert.Equal("setup for step \"fight\" \"nope\" of A matches no step; its steps are 0 build, 1 fight, 2 batch, 3 note", error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Played);
    }

    [Fact]
    public void AnUnselectedStepsListIsCheckedToo()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup([], ("fight", ["nope"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["note"]"""), ["build", "fight", "batch", "note"]);

        Assert.Equal("setup for step \"fight\" \"nope\" of A matches no step; its steps are 0 build, 1 fight, 2 batch, 3 note", error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Played);
    }

    [Fact]
    public void ASetupStepTheCallAlsoSelectedIsLeftOutOfSetup()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = new ScratchSetup(["build"]) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["build", "use"]"""), ["build", "use", "note"]);

        Assert.Null(error);
        Assert.Equal([0, 1], selection.Selected!);
        Assert.Empty(selection.Setup);
        Assert.Equal([0, 1], selection.Played);
    }

    [Fact]
    public void AFullRunIgnoresSetup()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = new ScratchSetup(["build"]) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, tokens: null, ["build", "use", "note"]);

        Assert.Null(error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Setup);
        Assert.Equal([0, 1, 2], selection.Played);
    }

    [Fact]
    public void AFullRunIgnoresBothFormsOfSetup()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = Setup(["build"], ("shut", ["batch"])) };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, tokens: null, ["build", "fight", "batch", "note"]);

        Assert.Null(error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Setup);
        Assert.Equal([0, 1, 2, 3], selection.Played);
    }

    private static ScratchScenePlan Scene(string name, double pace) =>
        new(name, $"res://scratch/{name}.tscn", pace, []) { TimeScale = 1, EngineArgs = [] };
}
