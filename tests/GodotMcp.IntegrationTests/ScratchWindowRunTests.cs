using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using static GodotMcp.IntegrationTests.ScratchRunnerTests;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the InputProbe fixture's ScratchWindowCut scene in the real Godot, for where a step's window ends: at
/// its pace wait's end in game time, so what the runner's own calls between steps make the game print or raise (GetStatus here)
/// is the next step's, or the exit's after the last step. The order is the runner's own call order, never a time.
/// </summary>
public sealed class ScratchWindowRunTests : ScratchRunBase
{
    private const string Scene = "ScratchWindowCut";
    private const string PlayedLine = "[scratch] first played";
    private const string StatusLine = "[scratch] status after first";
    private const string StatusError = "status error after first";

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFixtureCallTheRunnerMakesBetweenStepsIsTheNextStepsNotTheLast()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, [Scene], new ScratchOptions(Details: FullDetails), TestContext.Current.CancellationToken)
        );

        AssertGetStatusIsTheSecondSteps(scene);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnUntilStepsGetStatusIsTheNextStepsNotItsOwn()
    {
        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchWindowCutUntil"],
                new ScratchOptions(Details: FullDetails),
                TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(scene["details"]![0]!["until"]);
        AssertGetStatusIsTheSecondSteps(scene);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACapturedShotConditionStepsGetStatusIsTheNextStepsNotItsOwn()
    {
        ScratchOptions options = new(
            Windowed: true,
            Capture: new ScratchCaptureOptions(Steps: JsonSerializer.SerializeToElement(new object[] { 0 }), Screenshot: true),
            Details: FullDetails
        );

        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchWindowCutShot"], options, TestContext.Current.CancellationToken));

        Assert.NotNull(scene["details"]![0]!["screenshot"]);
        AssertGetStatusIsTheSecondSteps(scene);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APacedStepWhosePlayStepUnpausesAPausedTreeRunsItsPace()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, ["ScratchPauseUnpause"], new ScratchOptions(Details: FullDetails), TestContext.Current.CancellationToken)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(["paused", "unpaused"], scene["details"]!.AsArray().Select(step => Status(step!)));
    }

    private static void AssertGetStatusIsTheSecondSteps(JsonObject scene)
    {
        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "second"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.Contains(StatusError, failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonArray details = scene["details"]!.AsArray();
        Assert.Contains(PlayedLine, Lines(details[0]));
        Assert.DoesNotContain(StatusLine, Lines(details[0]));
        Assert.Empty(ErrorMessages(details[0]));
        Assert.Contains(StatusLine, Lines(details[1]));
        Assert.Contains(StatusError, ErrorMessages(details[1]));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheLastStepsGetStatusLineIsTheExitsNotItsOwn()
    {
        ScratchOptions options = new(Details: FullDetails, Steps: JsonSerializer.Deserialize<JsonElement>("[0]"));

        JsonObject scene = Single(await RunAsync(_probe.Directory, [Scene], options, TestContext.Current.CancellationToken));

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["failedAt"]);
        JsonNode step = Assert.Single(scene["details"]!.AsArray())!;
        Assert.Contains(PlayedLine, Lines(step));
        Assert.DoesNotContain(StatusLine, Lines(step));
        Assert.DoesNotContain(StatusError, ErrorMessages(step));
        // The exit lists only the lines a failure pattern matches: GetStatus's error line, not its plain line.
        Assert.Contains(Lines(scene["exit"]), line => line.Contains(StatusError, StringComparison.Ordinal));
        Assert.Contains(StatusError, scene["exit"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static string[] Lines(JsonNode? holder) => [.. (holder?["lines"]?.AsArray() ?? []).Select(line => line!.GetValue<string>())];

    private static string[] ErrorMessages(JsonNode? step) =>
        [.. (step?["errors"]?.AsArray() ?? []).Select(error => error!["message"]!.GetValue<string>())];
}
