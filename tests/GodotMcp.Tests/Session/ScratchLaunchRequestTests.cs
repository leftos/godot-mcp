using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The launch request a scratch scene's game starts with: its engine arguments before -- and its user arguments after.</summary>
public sealed class ScratchLaunchRequestTests
{
    [Fact]
    public void AScenesEngineArgsGoBeforeTheSeparatorAndTheUserArgsAfter()
    {
        ScratchScenePlan scene = new("A", "res://scratch/A.tscn", 1, ["u"]) { TimeScale = 4, EngineArgs = ["--a", "--b"] };
        ScratchPlan plan = new("/game", [scene], [], false, ScratchDetails.Default)
        {
            DetailChars = ScratchVerdict.DefaultDetailChars,
            Parallel = 1,
            Listed = true,
            SessionPrefix = "game",
            KeepGoing = false,
            Steps = null,
            TimeScale = 4,
        };

        LaunchRequest request = ScratchRun.LaunchRequestFor(plan, scene, quiet: false, shutOutRealGamepads: false, mute: false);

        Assert.Equal(
            ["--headless", "--time-scale", "4", "--a", "--b", "--", "u"],
            [.. GodotCommandLine.BuildArguments(request, moviePath: null).TakeLast(7)]
        );
    }
}
