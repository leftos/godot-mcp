using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches with scenes whose root gives GetStepUntil, in the real Godot: a step played inside one wait on its until, met
/// however far past its pace, hung at its hang guard, or refused. The probe's scratch folder holds only the InputProbe fixture's
/// scratch_until scenes and the scratch_probe.gd they extend, so <see cref="ScratchRunnerTests"/>' folder runs never play them;
/// their paces stay in the fixture's godot-mcp.json. It shares that class's helpers.
/// </summary>
public sealed class ScratchUntilRunTests : IAsyncDisposable
{
    private const int TestTimeoutMs = TestTimeouts.OwnLaunchMs;

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
    // game time, 1.2 s of real time at time_scale 0.5, and the step ends there.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASignalUntilEndsAtItsSignalEvenOneItsPlayStepEmitsAtOnceAndAScaledExpressionUntilEndsAtItsCondition()
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
        Assert.All(signalled, step => ScratchRunnerTests.AssertUntilMetInGameTime(step!, GuardMs));

        JsonNode scene = scenes[1]!;
        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(["scaled finished", "first finished", "second finished"], details.Select(step => ScratchRunnerTests.Status(step!)));
        ScratchRunnerTests.AssertUntilMetInGameTime(details[0]!, GuardMs);
        Assert.InRange(details[0]!["until"]!["elapsedMs"]!.GetValue<int>(), 1000, int.MaxValue);
        Assert.All(details.Skip(1), step => ScratchRunnerTests.AssertUntilMetInGameTime(step!, GuardMs));
    }

    // ScratchUntilSlow's until holds 1 s of game time after its step starts, ten times its 0.1 s pace: the step ends at its
    // condition, and the pace is only the scene's default wait and exit hold.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilEndsAtItsConditionHoweverFarPastItsPaceItHolds()
    {
        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchUntilSlow"],
                new ScratchOptions(Details: ScratchRunnerTests.FullDetails),
                TestContext.Current.CancellationToken
            )
        );

        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        JsonNode step = Assert.Single(scene["details"]!.AsArray())!;
        Assert.Equal("slow done", ScratchRunnerTests.Status(step));
        ScratchRunnerTests.AssertUntilMetInGameTime(step, GuardMs);
        Assert.InRange(step["until"]!["gameMs"]!.GetValue<int>(), 1000, int.MaxValue);
    }

    // ScratchUntilNever's until never holds, so its first step hangs at its until's hang guard, on a harness of the test's own
    // whose guard is 3 s: the bridge ends the wait at 15 s of game time, 5 times the guard, while the server's release stays on
    // the harness's 10-minute floor, so only the bridge's event ends it. Every other case runs on the class harness's guard.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilThatNeverHoldsHangsAtItsGuardIsRedAndTheSceneStopsThereEvenWhenKeepingGoing()
    {
        await using SessionHarness harness = new(launchesUnfocused: false, productionGraces: false, TimeSpan.FromSeconds(3));
        ScratchTools scratch = new(harness.Sessions, TestCSharp.Unused());
        const string Error =
            "step 0 'wait': its until hung at its guard of 3 s of load-adjusted time (its condition never held); check the "
            + "condition GetStepUntil gives";
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject stopped = ScratchRunnerTests.Single(await RunAsync(scratch, ["ScratchUntilNever"], options: null, cancellation));
        JsonObject kept = ScratchRunnerTests.Single(
            await RunAsync(scratch, ["ScratchUntilNever"], new ScratchOptions(KeepGoing: true), cancellation)
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
        ScratchRunnerTests.AssertUntilMetInGameTime(details[0]!, GuardMs);
        Assert.False(details[0]!["ok"]!.GetValue<bool>(), details[0]!.ToJsonString());
        Assert.Contains("scratch step raised", details[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(details[1]!["ok"]!.GetValue<bool>(), details[1]!.ToJsonString());
        Assert.Equal("after done", ScratchRunnerTests.Status(details[1]!));
    }

    // ScratchUntilReadRaises' GetStepUntil logs an error each time its second step's until is read: the read before the walk
    // keeps no answer for that step, so the error lands in the second step's window, from its read at its play, and the first
    // step stays green.
    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnUntilReadThatLogsAnErrorBeforeTheWalkFailsItsOwnStepNotTheFirst()
    {
        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchUntilReadRaises"],
                new ScratchOptions(Details: ScratchRunnerTests.FullDetails),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        JsonNode failedAt = scene["failedAt"]!;
        Assert.True((1, "second") == (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()), scene.ToJsonString());
        Assert.Contains("scratch until read raised", failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonArray details = scene["details"]!.AsArray();
        Assert.True(details[0]!["ok"]!.GetValue<bool>(), details[0]!.ToJsonString());
        Assert.Equal("first done", ScratchRunnerTests.Status(details[0]!));
    }

    /// <summary>The class harness's until guard in game milliseconds: the hang guard an until met in game time stays under.</summary>
    private int GuardMs => (int)_harness.Sessions.ScratchUntilGuard.TotalMilliseconds;

    private async Task<JsonObject> RunAsync(string projectDir, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(projectDir, scenes, options, cancellation))!.AsObject();

    /// <summary>The probe's scenes played by <paramref name="scratch"/>, on a harness of a test's own.</summary>
    private async Task<JsonObject> RunAsync(ScratchTools scratch, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await scratch.RunScratchesAsync(_probe.Directory, scenes, options, cancellation))!.AsObject();
}
