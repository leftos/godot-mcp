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
    public void AFolderRunOrdersByTheWallTimeAPaceTakesAtItsScenesTimeScale()
    {
        ScratchScenePlan[] scenes = [Scene("A", 8) with { TimeScale = 4 }, Scene("B", 3), Scene("C", 1) with { TimeScale = 4 }];

        Assert.Equal([1, 0, 2], ScratchRun.StartOrder(scenes, listed: false));
    }

    [Fact]
    public void SetupStepsResolveByIndexOrName()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = ["3", "build", "1", "build"] };

        (int[] steps, string? error) = ScratchRun.SetupSteps(scene, ["open", "build", "close", "rest"]);
        (int[] none, string? noError) = ScratchRun.SetupSteps(Scene("B", 0.5), ["open"]);

        Assert.Null(error);
        Assert.Equal([1, 3], steps);
        Assert.Empty(none);
        Assert.Null(noError);
    }

    [Theory]
    [InlineData("5", "setup \"5\" of A matches no step; its steps are 0: open, 1: hit, 2: hit")]
    [InlineData("shut", "setup \"shut\" of A matches no step; its steps are 0: open, 1: hit, 2: hit")]
    [InlineData("hit", "setup \"hit\" of A names 2 steps (1, 2); key it by index")]
    public void ASetupKeyNamingNoStepOrASharedNameIsAnError(string key, string refusal)
    {
        (int[] steps, string? error) = ScratchRun.SetupSteps(Scene("A", 0.5) with { Setup = ["open", key] }, ["open", "hit", "hit"]);

        Assert.Equal(refusal, error);
        Assert.Empty(steps);
    }

    private static IReadOnlyList<ScratchStepToken> Steps(string json) => ScratchTools.StepTokens(JsonSerializer.Deserialize<JsonElement[]>(json))!;

    [Fact]
    public void ASetupStepTheCallAlsoSelectedIsLeftOutOfSetup()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = ["build"] };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, Steps("""["build", "use"]"""), ["build", "use", "note"]);

        Assert.Null(error);
        Assert.Equal([0, 1], selection.Selected!);
        Assert.Empty(selection.Setup);
        Assert.Equal([0, 1], selection.Played);
    }

    [Fact]
    public void AFullRunIgnoresSetup()
    {
        ScratchScenePlan scene = Scene("A", 0.5) with { Setup = ["build"] };

        (ScratchSelection selection, string? error) = ScratchRun.SelectSteps(scene, tokens: null, ["build", "use", "note"]);

        Assert.Null(error);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Setup);
        Assert.Equal([0, 1, 2], selection.Played);
    }

    private static ScratchScenePlan Scene(string name, double pace) =>
        new(name, $"res://scratch/{name}.tscn", pace, []) { TimeScale = 1, EngineArgs = [] };
}
