using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The launch request a scratch scene's game starts with: its engine arguments before -- and its user arguments after.</summary>
public sealed class ScratchLaunchRequestTests
{
    private static readonly ScratchScenePlan Scene = new("A", "res://scratch/A.tscn", 1, ["u"]) { TimeScale = 4, EngineArgs = ["--a", "--b"] };

    [Fact]
    public void AScenesEngineArgsGoBeforeTheSeparatorAndTheUserArgsAfter()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(Plan(windowed: false), Scene, quiet: false, shutOutRealGamepads: false, mute: false);

        Assert.Equal(
            ["--headless", "--time-scale", "4", "--a", "--b", "--", "u"],
            [.. GodotCommandLine.BuildArguments(request, moviePath: null).TakeLast(7)]
        );
    }

    [Fact]
    public void AWindowedScenesGameLaunchesQuietWithoutHeadless()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(Plan(windowed: true), Scene, quiet: true, shutOutRealGamepads: false, mute: false);
        string[] arguments = [.. GodotCommandLine.BuildArguments(request, moviePath: null)];

        Assert.Equal(["--time-scale", "4", "--a", "--b", "--", "u"], arguments[^6..]);
        Assert.DoesNotContain("--headless", arguments);
        Assert.True(request.Quiet);
    }

    private static ScratchPlan Plan(bool windowed) =>
        new("/game", [Scene], [], false, ScratchDetails.Default)
        {
            DetailChars = ScratchVerdict.DefaultDetailChars,
            Parallel = 1,
            Listed = true,
            SessionPrefix = "game",
            KeepGoing = false,
            Steps = null,
            TimeScale = 4,
            Windowed = windowed,
        };
}
