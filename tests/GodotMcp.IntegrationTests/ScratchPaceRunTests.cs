using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using static GodotMcp.IntegrationTests.ScratchRunnerTests;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the InputProbe fixture's scratch scenes and CsProbe's C# ones in the real Godot, for paces and time
/// scales: a step's own pace, a scaled run's game time, a pace that opts out of the time scale, the wait after the last step, a
/// step pace key that matches no step; and a C# exception reported as the failing step's error, a C# _Ready's at the boot, and a
/// C# until ending its step. It shares <see cref="ScratchRunnerTests"/>' helpers.
/// </summary>
public sealed class ScratchPaceRunTests : ScratchRunBase
{
    private CsProbeProject? _csProbe;

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _csProbe?.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepWithItsOwnPaceWaitsItAndTheOtherStepsWaitTheScenes()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, ["ScratchStepPace"], new ScratchOptions(Details: true), TestContext.Current.CancellationToken)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(0.25, scene["pace"]!.GetValue<double>());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(["quick", "slow", "settle", "last"], details.Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal([250, 1000, 500, 250], details.Select(step => step!["gameMs"]!.GetValue<int>()));
        Assert.All(details, step => Assert.Null(step!["until"]));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AScaledRunPlaysEachStepsPaceInGameTimeAndNamesItsTimeScale()
    {
        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchStepPace"],
            new ScratchOptions(Details: true, TimeScale: 4),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(result);
        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(4, result["timeScale"]!.GetValue<double>());
        Assert.Null(scene["timeScale"]);
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal([250, 1000, 500, 250], details.Select(step => step!["gameMs"]!.GetValue<int>()));
        Assert.Equal("quick at time_scale 4.0", Status(details[0]!));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASceneWhosePaceOptsOutOfTheTimeScaleRunsAtNormalSpeedAndSaysSo()
    {
        string profile = """
            {
              "scratch": {
                "folder": "scratch",
                "pace": { "ScratchStepPace": { "seconds": 0.25, "reason": "its steps only leave a note", "timeScale": 1 } }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchStepPace"],
            new ScratchOptions(Details: true, TimeScale: 4),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(result);
        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal("quick at time_scale 1.0", Status(scene["details"]![0]!));
        Assert.Equal(4, result["timeScale"]!.GetValue<double>());
        Assert.Equal(1, scene["timeScale"]!.GetValue<double>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheWaitAfterTheLastStepIsThatStepsOwnPace()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, ["ScratchStepPaceAfter"], new ScratchOptions(Details: true), TestContext.Current.CancellationToken)
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["failedAt"]);
        Assert.Equal([100, 2000], scene["details"]!.AsArray().Select(step => step!["gameMs"]!.GetValue<int>()));
        Assert.Contains("error in the last step's pace after it", scene["exit"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepPaceKeyThatMatchesNoStepIsRedBeforeAnyStepPlays()
    {
        string profile = """
            {
              "scratch": {
                "folder": "scratch",
                "pace": { "ScratchGreen": { "seconds": 0.25, "reason": "quick", "steps": { "open": 1, "shut": 2 } } }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchGreen"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal(-1, failedAt["index"]!.GetValue<int>());
        Assert.Equal(
            "pace.steps key \"shut\" of ScratchGreen matches no step; its steps are 0 open, 1 move, 2 close",
            failedAt["error"]!.GetValue<string>()
        );
        Assert.Equal((0, 3), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACSharpStepThatThrowsIsRedWithItsExceptionAReadyThatThrowsIsRedAtTheBootAndAnUntilEndsAStep()
    {
        CsProbeProject csProbe = _csProbe = CsProbeProject.Unbuilt();
        CopyFolder(Path.Combine(RepoPaths.Root, "tests", "fixtures", "CsProbe", "Scratch"), Path.Combine(csProbe.Directory, "Scratch"));

        JsonObject result = await RunAsync(
            csProbe.Directory,
            [
                "res://Scratch/ScratchThrow.tscn",
                "res://Scratch/ScratchBootThrow.tscn",
                "res://Scratch/ScratchUntil.tscn",
                "res://Scratch/ScratchUntilThrow.tscn",
            ],
            new ScratchOptions(Pace: 2, Details: true),
            TestContext.Current.CancellationToken
        );

        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(4, scenes.Count);
        AssertCSharpUntils(scenes[2]!, scenes[3]!);
        Assert.Null(scenes[0]!["details"]![0]!["until"]);
        JsonNode step = scenes[0]!;
        JsonNode stepFailedAt = step["failedAt"]!;
        Assert.Equal("red", step["verdict"]!.GetValue<string>());
        Assert.Equal("CsProbe.scratch-ScratchThrow", step["session"]!.GetValue<string>());
        Assert.Equal(
            (1, "throws", "about to throw"),
            (stepFailedAt["index"]!.GetValue<int>(), stepFailedAt["name"]!.GetValue<string>(), Status(stepFailedAt))
        );
        JsonNode error = Assert.Single(step["details"]![1]!["errors"]!.AsArray())!;
        Assert.Contains("InvalidOperationException: scratch step threw", error["message"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonNode boot = scenes[1]!;
        JsonNode bootFailedAt = boot["failedAt"]!;
        Assert.Equal("red", boot["verdict"]!.GetValue<string>());
        Assert.Equal("CsProbe.scratch-ScratchBootThrow", boot["session"]!.GetValue<string>());
        Assert.Equal((-1, "boot"), (bootFailedAt["index"]!.GetValue<int>(), bootFailedAt["name"]!.GetValue<string>()));
        Assert.Contains("InvalidOperationException: scratch boot threw", bootFailedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, boot["steps"]!["played"]!.GetValue<int>());
    }

    // ScratchUntil's signal until, a 0.3 s timer, ends its step inside the 2 s pace of game time the run gives every scene: a
    // headless frame's delta is its real frame time, and the C# helper loads in the first frame. ScratchUntilThrow's PlayStep throws
    // inside its until's wait, which reads as its feed entry's message, file and line, and stops the scene before its second step.
    private static void AssertCSharpUntils(JsonNode until, JsonNode thrown)
    {
        Assert.True(until["verdict"]!.GetValue<string>() == "green", until.ToJsonString());
        JsonNode waited = Assert.Single(until["details"]!.AsArray())!;
        Assert.Equal("ended", Status(waited));
        AssertUntilMetInGameTime(waited, 2000);

        Assert.Equal("red", thrown["verdict"]!.GetValue<string>());
        Assert.Equal((1, 2), (thrown["steps"]!["played"]!.GetValue<int>(), thrown["steps"]!["total"]!.GetValue<int>()));
        string error = thrown["failedAt"]!["error"]!.GetValue<string>();
        Assert.Contains("InvalidOperationException: scratch until step threw", error, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(error, @"ScratchUntilThrow\.cs:line \d+"), error);
        Assert.False(error.StartsWith("step 0", StringComparison.Ordinal), error);
    }
}
