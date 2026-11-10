using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The launch request a scratch scene's game starts with: its engine arguments before -- and its user arguments after.</summary>
public sealed class ScratchLaunchRequestTests
{
    private static readonly ScratchScenePlan Scene = new("A", "res://scratch/A.tscn", 1, ["u"]) { TimeScale = 4, EngineArgs = ["--a", "--b"] };

    [Fact]
    public void AScenesEngineArgsGoBeforeTheSeparatorAndTheUserArgsAfter()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(
            Plan(windowed: false, audible: false),
            Scene,
            quiet: false,
            shutOutRealGamepads: false,
            mute: false
        );

        Assert.Equal(
            ["--headless", "--time-scale", "4", "--a", "--b", "--", "u"],
            [.. GodotCommandLine.BuildArguments(request, moviePath: null).TakeLast(7)]
        );
    }

    [Fact]
    public void AWindowedScenesGameLaunchesQuietWithoutHeadless()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(
            Plan(windowed: true, audible: false),
            Scene,
            quiet: true,
            shutOutRealGamepads: false,
            mute: false
        );
        string[] arguments = [.. GodotCommandLine.BuildArguments(request, moviePath: null)];

        Assert.Equal(["--time-scale", "4", "--a", "--b", "--", "u"], arguments[^6..]);
        Assert.DoesNotContain("--headless", arguments);
        Assert.True(request.Quiet);
    }

    [Fact]
    public void AScenesRequestCarriesThePlansUserData()
    {
        var userData = new ScratchUserDataRequest(null);

        LaunchRequest carried = ScratchRun.LaunchRequestFor(
            Plan(windowed: false, audible: false) with
            {
                UserData = userData,
            },
            Scene,
            quiet: false,
            shutOutRealGamepads: false,
            mute: false
        );
        LaunchRequest plain = ScratchRun.LaunchRequestFor(
            Plan(windowed: false, audible: false),
            Scene,
            quiet: false,
            shutOutRealGamepads: false,
            mute: false
        );

        Assert.Same(userData, carried.ScratchUserData);
        Assert.Null(plain.ScratchUserData);
    }

    [Fact]
    public void AnAudibleWindowedScenesGameKeepsTheProjectsAudioDriver()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(
            Plan(windowed: true, audible: true),
            Scene,
            quiet: true,
            shutOutRealGamepads: false,
            mute: false
        );

        Assert.True(request.Audible);
        Assert.DoesNotContain("--audio-driver", GodotCommandLine.BuildArguments(request, moviePath: null));
    }

    [Fact]
    public void AQuietWindowedScenesGameRunsTheDummyDriverWithoutAudible()
    {
        LaunchRequest request = ScratchRun.LaunchRequestFor(
            Plan(windowed: true, audible: false),
            Scene,
            quiet: true,
            shutOutRealGamepads: false,
            mute: false
        );
        string[] arguments = [.. GodotCommandLine.BuildArguments(request, moviePath: null)];
        int driver = Array.IndexOf(arguments, "--audio-driver");

        Assert.False(request.Audible);
        Assert.True(driver >= 0, "--audio-driver is missing from the arguments");
        Assert.Equal([.. GodotCommandLine.QuietAudioDriverArgs], arguments[driver..(driver + GodotCommandLine.QuietAudioDriverArgs.Count)]);
    }

    private static ScratchPlan Plan(bool windowed, bool audible) =>
        new("/game", [Scene], [], false, ScratchDetails.Default)
        {
            DetailChars = ScratchVerdict.DefaultDetailChars,
            Parallel = 1,
            Listed = true,
            SessionPrefix = "game",
            KeepGoing = false,
            WarningsRed = false,
            Steps = null,
            TimeScale = 4,
            Windowed = windowed,
            Audible = audible,
        };
}
