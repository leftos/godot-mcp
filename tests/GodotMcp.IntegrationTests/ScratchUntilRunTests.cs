using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches with scenes whose root gives GetStepUntil, in the real Godot: a step played inside one wait on its until, met
/// within its pace of load-adjusted time, not met, or refused. The probe's scratch folder holds only the InputProbe fixture's
/// scratch_until scenes and the scratch_probe.gd they extend, so <see cref="ScratchRunnerTests"/>' folder runs never play them;
/// their paces stay in the fixture's godot-mcp.json. It shares that class's helpers.
/// </summary>
public sealed class ScratchUntilRunTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 180_000;

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchUntilRunTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        string scratch = Path.Combine(_probe.Directory, "scratch");
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_until"), scratch);
        File.Copy(Path.Combine(RepoPaths.InputProbe, "scratch", "scratch_probe.gd"), Path.Combine(scratch, "scratch_probe.gd"));
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    // One run of both scenes, to keep the group inside its ceiling. ScratchUntilExpression's scaled step's until holds at 0.6 s of
    // game time, 1.2 s of real time at time_scale 0.5: its 1 s pace is load-adjusted time at that time scale, 2 s of it, so the
    // step is green.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASignalUntilEndsAtItsSignalEvenOneItsPlayStepEmitsAtOnceAndAnExpressionUntilsPaceCountsLoadAdjustedTime()
    {
        JsonArray scenes = (
            await RunAsync(
                _probe.Directory,
                ["ScratchUntilSignal", "ScratchUntilExpression"],
                new ScratchOptions(Details: ScratchRunnerTests.FullDetails),
                TestContext.Current.CancellationToken
            )
        )["scenes"]!.AsArray();

        JsonNode signal = scenes[0]!;
        Assert.Equal("green", signal["verdict"]!.GetValue<string>());
        Assert.Equal(2, signal["pace"]!.GetValue<double>());
        JsonArray signalled = signal["details"]!.AsArray();
        Assert.Equal(["at once done", "timer done", "timer again done"], signalled.Select(step => ScratchRunnerTests.Status(step!)));
        Assert.All(signalled, step => ScratchRunnerTests.AssertUntilMetInGameTime(step!, 2000));

        JsonNode scene = scenes[1]!;
        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(["scaled finished", "first finished", "second finished"], details.Select(step => ScratchRunnerTests.Status(step!)));
        ScratchRunnerTests.AssertUntilMetInGameTime(details[0]!, 1000);
        Assert.InRange(details[0]!["until"]!["elapsedMs"]!.GetValue<int>(), 1000, int.MaxValue);
        Assert.All(details.Skip(1), step => ScratchRunnerTests.AssertUntilMetInGameTime(step!, 2000));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilNotMetWithinItsPaceIsRedAndTheSceneStopsThereEvenWhenKeepingGoing()
    {
        const string Error =
            "step 0 'wait': its until was not met within its pace of 1 s of load-adjusted time; give it a longer scratch.pace, or "
            + "check the condition GetStepUntil gives";
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject stopped = ScratchRunnerTests.Single(await RunAsync(_probe.Directory, ["ScratchUntilNever"], options: null, cancellation));
        JsonObject kept = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, ["ScratchUntilNever"], new ScratchOptions(KeepGoing: true), cancellation)
        );

        foreach (JsonObject scene in new[] { stopped, kept })
        {
            Assert.Equal("red", scene["verdict"]!.GetValue<string>());
            JsonNode failedAt = scene["failedAt"]!;
            Assert.Equal((0, "wait"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
            Assert.Equal(Error, failedAt["error"]!.GetValue<string>());
            Assert.Equal((1, 2), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
            JsonNode step = Assert.Single(scene["details"]!.AsArray())!;
            Assert.False(step["until"]!["met"]!.GetValue<bool>(), step.ToJsonString());
            Assert.Null(step["gameMs"]);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilTheBridgeRefusesOrAnAnswerThatIsNoConditionIsRedAtThatStep()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject refused = ScratchRunnerTests.Single(await RunAsync(_probe.Directory, ["ScratchUntilRefused"], options: null, cancellation));
        JsonObject worded = ScratchRunnerTests.Single(await RunAsync(_probe.Directory, ["ScratchUntilNotDict"], options: null, cancellation));

        Assert.Equal("red", refused["verdict"]!.GetValue<string>());
        Assert.StartsWith(
            "step 0 'missing': its until was refused: No node named 'NoSuchNode' anywhere under /root in the running game",
            refused["failedAt"]!["error"]!.GetValue<string>(),
            StringComparison.Ordinal
        );
        Assert.Equal("red", worded["verdict"]!.GetValue<string>());
        Assert.Equal("GetStepUntil(0) returned \"soon\", not a wait_for condition", worded["failedAt"]!["error"]!.GetValue<string>());
        Assert.All([refused, worded], scene => Assert.Empty(ScratchRunnerTests.Status(scene["failedAt"]!)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnErrorAStepRaisesInsidePlayStepIsRedButItsUntilStillRunsAndKeepGoingPlaysOn()
    {
        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, ["ScratchUntilRaises"], new ScratchOptions(KeepGoing: true), TestContext.Current.CancellationToken)
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal((0, "raises"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.Equal(2, scene["steps"]!["played"]!.GetValue<int>());
        JsonArray details = scene["details"]!.AsArray();
        ScratchRunnerTests.AssertUntilMetInGameTime(details[0]!, 2000);
        Assert.False(details[0]!["ok"]!.GetValue<bool>(), details[0]!.ToJsonString());
        Assert.Contains("scratch step raised", details[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(details[1]!["ok"]!.GetValue<bool>(), details[1]!.ToJsonString());
        Assert.Equal("after done", ScratchRunnerTests.Status(details[1]!));
    }

    private async Task<JsonObject> RunAsync(string projectDir, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(projectDir, scenes, options, cancellation))!.AsObject();
}
