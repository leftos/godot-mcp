using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the scratch scenes of the InputProbe fixture (its scratch folder and godot-mcp.json) and a C# one of
/// CsProbe's, in the real Godot: each verdict, the step list, the sessions the scenes ran in, and a C# exception reported as
/// the failing step's error; an error at the boot, GDScript's or a C# _Ready's, is red before any step. The fixtures' copies
/// take top-level files only, so each test copies the scratch folder in. The facts that play the whole folder are
/// <see cref="ScratchFolderTests"/>'.
/// </summary>
public sealed class ScratchRunnerTests : IAsyncDisposable
{
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;
    private CsProbeProject? _csProbe;

    public ScratchRunnerTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(_probe.Directory, "scratch"));
    }

    public async ValueTask DisposeAsync()
    {
        // A game holds its project's folder until it exits, and the harness kills and waits for every game still running, so
        // the copies are deleted only once it is done.
        try
        {
            await _harness.DisposeAsync();
        }
        finally
        {
            _probe.Dispose();
            _csProbe?.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASceneRedOnlyBesideAnotherIsPlayedAgainAloneAndPasses()
    {
        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchMarker", "ScratchBeside"],
            new ScratchOptions(Parallel: 2),
            TestContext.Current.CancellationToken
        );

        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(["ScratchMarker", "ScratchBeside"], scenes.Select(scene => scene!["scene"]!.GetValue<string>()));
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("green", scenes[0]!["verdict"]!.GetValue<string>());
        Assert.Null(scenes[0]!["alone"]);
        JsonNode beside = scenes[1]!;
        Assert.Equal("green", beside["verdict"]!.GetValue<string>());
        Assert.True(beside["alone"]!.GetValue<bool>(), beside.ToJsonString());
        Assert.Null(beside["aloneFailedAt"]);
        Assert.Equal("InputProbe.scratch-ScratchBeside", beside["session"]!.GetValue<string>());
        Assert.Equal(2, result["green"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnErrorAtTheBootIsRedAtTheBootAndNoStepPlays()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchBootError"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((-1, "boot"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.Contains("boot failure", failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((0, 2), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
        Assert.Null(scene["details"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task LaunchLinesAreNeverJudgedAndARenamedRootIsFound()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLaunchNoise"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(1, scene["steps"]!["played"]!.GetValue<int>());
        DebugOutput output = _harness.Sessions.GetDebugOutput("InputProbe.scratch-ScratchLaunchNoise", 100, before: null);
        Assert.Contains("ERROR: launch noise", output.Stderr);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARootWithoutTheProtocolIsRedBeforeAnyStep()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchNoProtocol"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal(-1, failedAt["index"]!.GetValue<int>());
        Assert.Contains("lacks GetStatus", failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, scene["steps"]!["played"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepPastItsCeilingIsKilledAndItsGameStopped()
    {
        JsonObject result = await RunAsync(_probe.Directory, ["ScratchBusy"], options: null, TestContext.Current.CancellationToken);

        JsonObject scene = Single(result);
        Assert.Equal("killed", scene["verdict"]!.GetValue<string>());
        Assert.False(result["passed"]!.GetValue<bool>());
        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal((0, "spin"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.True(scene["exit"]!["killed"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(scene["exit"]!["killReason"]!.GetValue<string>()));
        Assert.False(_harness.Sessions.GetDebugOutput("InputProbe.scratch-ScratchBusy", 1, before: null).Running);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ACeilingBelowASceneLengthKillsItAtThatCeiling()
    {
        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchStepPace"],
                new ScratchOptions(Pace: 2, Ceilings: new() { ["ScratchStepPace"] = 1 }),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("killed", scene["verdict"]!.GetValue<string>());
        Assert.Contains("set by options.ceilings", scene["failedAt"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnErrorInThePaceAfterTheLastStepIsRedInTheExit()
    {
        string profile = """{ "scratch": { "folder": "scratch", "patterns": [], "pace": { "ScratchLateError": 2 } } }""";
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLateError"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["failedAt"]);
        Assert.Equal(1, scene["steps"]!["played"]!.GetValue<int>());
        Assert.Contains("late error after the last step", scene["exit"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGreenSceneStopsCleanlyAndItsSessionKeepsItsOutput()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchGreen"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(3, scene["steps"]!["played"]!.GetValue<int>());
        Assert.Equal(3, scene["steps"]!["total"]!.GetValue<int>());
        Assert.Equal(0, scene["exit"]!["code"]!.GetValue<int>());
        Assert.Null(scene["exit"]!["leaked"]);
        Assert.Null(scene["failedAt"]);
        Assert.Null(scene["details"]);
        DebugOutput output = _harness.Sessions.GetDebugOutput("InputProbe.scratch-ScratchGreen", 100, before: null);
        Assert.Contains("[scratch] closed", output.Stdout);
        Assert.False(output.Running);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepThatPushesAnErrorIsTheFailedStepAndTheRestNeverPlay()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchPushError"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "fails", "about to fail"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>(), Status(failedAt)));
        Assert.Contains("scratch step two failed", failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, scene["steps"]!["played"]!.GetValue<int>());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(2, details.Count);
        Assert.True(details[0]!["ok"]!.GetValue<bool>());
        Assert.Null(details[0]!["lines"]);
        Assert.Contains("[scratch] about to fail", details[1]!["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
        JsonNode error = Assert.Single(details[1]!["errors"]!.AsArray())!;
        Assert.Equal("scratch step two failed", error["message"]!.GetValue<string>());
        Assert.EndsWith("scratch_push_error.gd", error["file"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task KeepGoingReportsEveryRedStepAndWithoutItTheSceneStopsAtTheFirst()
    {
        JsonObject kept = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(KeepGoing: true), TestContext.Current.CancellationToken)
        );
        JsonObject stopped = Single(await RunAsync(_probe.Directory, ["ScratchTwoRed"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal(("red", "red"), (kept["verdict"]!.GetValue<string>(), stopped["verdict"]!.GetValue<string>()));
        Assert.Equal((1, "first"), (kept["failedAt"]!["index"]!.GetValue<int>(), kept["failedAt"]!["name"]!.GetValue<string>()));
        JsonArray failures = kept["failures"]!.AsArray();
        Assert.Equal([1, 2], failures.Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(["first", "second"], failures.Select(failure => failure!["name"]!.GetValue<string>()));
        Assert.Contains("first independent failure", failures[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("second independent failure", failures[1]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal((4, 4), (kept["steps"]!["played"]!.GetValue<int>(), kept["steps"]!["total"]!.GetValue<int>()));
        JsonArray details = kept["details"]!.AsArray();
        Assert.Equal([true, false, false, true], details.Select(step => step!["ok"]!.GetValue<bool>()));
        Assert.Contains("second independent failure", details[2]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(details[3]!["error"]);

        JsonNode only = Assert.Single(stopped["failures"]!.AsArray())!;
        Assert.Equal((1, "first"), (only["index"]!.GetValue<int>(), only["name"]!.GetValue<string>()));
        Assert.Equal(2, stopped["steps"]!["played"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task OnlyRedListsARedScenesRedStepsAloneWithTheirReadback()
    {
        JsonObject scene = Single(
            await RunAsync(
                _probe.Directory,
                ["ScratchTwoRed"],
                new ScratchOptions(KeepGoing: true, Only: "red"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((4, 4), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal([1, 2], details.Select(step => step!["index"]!.GetValue<int>()));
        Assert.All(details, step => Assert.False(step!["ok"]!.GetValue<bool>()));
        Assert.Contains("first independent failure", details[0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("second independent failure", details[1]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([1, 2], scene["failures"]!.AsArray().Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(1, scene["failedAt"]!["index"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task KeepGoingStillStopsAtAStepThatLeftTheGameUnableToAnswer()
    {
        JsonObject scene = Single(
            await RunAsync(_probe.Directory, ["ScratchQuit"], new ScratchOptions(KeepGoing: true), TestContext.Current.CancellationToken)
        );

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "quit"), (scene["failedAt"]!["index"]!.GetValue<int>(), scene["failedAt"]!["name"]!.GetValue<string>()));
        Assert.Equal((2, 4), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
        JsonNode failure = Assert.Single(scene["failures"]!.AsArray())!;
        Assert.Equal(1, failure["index"]!.GetValue<int>());
        Assert.Equal(["calm", "quit"], scene["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStepThatErrorsEveryFrameListsItOnceWithItsCount()
    {
        string raw = await _scratch.RunScratchesAsync(
            _probe.Directory,
            ["ScratchFlood"],
            new ScratchOptions(Details: true),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(JsonNode.Parse(raw)!.AsObject());
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "floods"), (scene["failedAt"]!["index"]!.GetValue<int>(), scene["failedAt"]!["name"]!.GetValue<string>()));
        JsonNode error = Assert.Single(scene["details"]![1]!["errors"]!.AsArray())!;
        Assert.StartsWith("the same error every frame", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(error["count"]!.GetValue<int>() > 1, error.ToJsonString());
        Assert.True(raw.Length < 60_000, $"{raw.Length} characters");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ALineATimerPrintsFailsTheStepItPrintedIn()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLateLine"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "wait"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.Equal("ERROR: late failure", failedAt["error"]!.GetValue<string>());
        Assert.Empty(scene["details"]![1]!["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ALeakAtExitIsFoundAndAKnownSceneReportsItsReason()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLeak"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("known", scene["verdict"]!.GetValue<string>());
        Assert.Equal("1 ObjectDB instance", scene["exit"]!["leaked"]!.GetValue<string>());
        Assert.StartsWith("leaks a node at exit on purpose", scene["known"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(scene["failedAt"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task DetailsListAGreenScenesStepsAndAKnownGreenSceneCountsAsRed()
    {
        string profile = """{ "scratch": { "folder": "scratch", "known": { "ScratchGreen": "was flaky" } } }""";
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchGreen"],
            new ScratchOptions(Details: true, Pace: 0.25),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(result);
        Assert.Equal("known-now-green", scene["verdict"]!.GetValue<string>());
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal(1, result["red"]!.GetValue<int>());
        Assert.Equal(0.25, scene["pace"]!.GetValue<double>());
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(["open", "move", "close"], details.Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal(["opened", "moved", "closed"], details.Select(step => Status(step!)));
        Assert.All(details, step => Assert.Equal(250, step!["gameMs"]!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APartialRunPlaysTheScenesSetupStepFirst()
    {
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_setup"), Path.Combine(_probe.Directory, "scratch"));
        await File.WriteAllTextAsync(
            Path.Combine(_probe.Directory, ProjectProfile.FileName),
            """{ "scratch": { "folder": "scratch", "setup": { "ScratchSetupNeeded": ["build"] } } }""",
            TestContext.Current.CancellationToken
        );
        ScratchOptions options = new(Details: true, Steps: Tokens("""["use"]"""));

        JsonObject green = await RunAsync(_probe.Directory, ["ScratchSetupNeeded"], options, TestContext.Current.CancellationToken);
        JsonObject scene = Single(green);

        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        Assert.Equal([1], scene["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal([0], scene["setup"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal(["build", "use"], scene["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));
        Assert.Equal(["built", "used"], scene["details"]!.AsArray().Select(step => Status(step!)));

        await File.WriteAllTextAsync(
            Path.Combine(_probe.Directory, ProjectProfile.FileName),
            """{ "scratch": { "folder": "scratch" } }""",
            TestContext.Current.CancellationToken
        );

        JsonObject red = Single(await RunAsync(_probe.Directory, ["ScratchSetupNeeded"], options, TestContext.Current.CancellationToken));

        Assert.True(red["verdict"]!.GetValue<string>() == "red", red.ToJsonString());
        Assert.Equal((1, "use"), (red["failedAt"]!["index"]!.GetValue<int>(), red["failedAt"]!["name"]!.GetValue<string>()));
        Assert.Contains("use ran before build", red["failedAt"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([1], red["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.False(red.ContainsKey("setup"), red.ToJsonString());
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
            "pace.steps key \"shut\" of ScratchGreen matches no step; its steps are 0: open, 1: move, 2: close",
            failedAt["error"]!.GetValue<string>()
        );
        Assert.Equal((0, 3), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task StepsPlaysOnlyTheSelectedStepsAndTheOthersNeverRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject last = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(Steps: Tokens("""["last"]""")), cancellation)
        );
        JsonObject middle = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(KeepGoing: true, Steps: Tokens("""["1-2"]""")), cancellation)
        );
        JsonObject unknown = Single(
            await RunAsync(_probe.Directory, ["ScratchTwoRed"], new ScratchOptions(Steps: Tokens("""["nope"]""")), cancellation)
        );

        Assert.Equal("green", last["verdict"]!.GetValue<string>());
        Assert.Equal((1, 4), (last["steps"]!["played"]!.GetValue<int>(), last["steps"]!["total"]!.GetValue<int>()));
        Assert.Equal([3], Indices(last["selected"]!));

        Assert.Equal("red", middle["verdict"]!.GetValue<string>());
        Assert.Equal((2, 4), (middle["steps"]!["played"]!.GetValue<int>(), middle["steps"]!["total"]!.GetValue<int>()));
        Assert.Equal([1, 2], Indices(middle["selected"]!));
        Assert.Equal([1, 2], middle["failures"]!.AsArray().Select(failure => failure!["index"]!.GetValue<int>()));
        Assert.Equal(["first", "second"], middle["details"]!.AsArray().Select(step => step!["name"]!.GetValue<string>()));

        JsonNode failedAt = unknown["failedAt"]!;
        Assert.Equal("red", unknown["verdict"]!.GetValue<string>());
        Assert.Equal(-1, failedAt["index"]!.GetValue<int>());
        Assert.Equal(
            "options.steps token \"nope\" of ScratchTwoRed matches no step; its steps are 0: calm, 1: first, 2: second, 3: last",
            failedAt["error"]!.GetValue<string>()
        );
        Assert.Equal((0, 4), (unknown["steps"]!["played"]!.GetValue<int>(), unknown["steps"]!["total"]!.GetValue<int>()));
        Assert.Null(unknown["selected"]);
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AScratchRunNamesItsSessionsAfterThePrefixItWasGiven()
    {
        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchGreen"],
            new ScratchOptions(Session: "agent-a"),
            TestContext.Current.CancellationToken
        );

        JsonObject scene = Single(result);
        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal("agent-a.scratch-ScratchGreen", scene["session"]!.GetValue<string>());
        Assert.Contains(_harness.Sessions.List(includeStopped: true), session => session.Name == "agent-a.scratch-ScratchGreen");
    }

    /// A scratch's session is stopped once its scene quits and its game has exited, yet it still names the game's pid: the
    /// harness must not open whatever process Windows has since handed that pid.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheHarnessDoesNotOpenAProcessThatTookAStoppedScratchGamesPid()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Single(await RunAsync(_probe.Directory, ["ScratchQuit"], options: null, cancellation));
        SessionInfo stopped = _harness.Sessions.List(includeStopped: true).Single(s => s.Name == "InputProbe.scratch-ScratchQuit");
        Assert.False(stopped.Live);
        Assert.NotNull(stopped.GameProcessId);
        using Process bystander = Process.Start(
            new ProcessStartInfo("ping.exe", "-n 600 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false }
        )!;
        try
        {
            using Process? opened = SessionHarness.OpenGame(stopped with { GameProcessId = bystander.Id });
            Assert.Null(opened);
        }
        finally
        {
            bystander.Kill();
            await bystander.WaitForExitAsync(cancellation);
        }
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

    private async Task<JsonObject> RunAsync(string projectDir, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(projectDir, scenes, options, cancellation))!.AsObject();

    internal static JsonObject Single(JsonObject result) => Assert.Single(result["scenes"]!.AsArray())!.AsObject();

    internal static string Status(JsonNode step) => step["status"]!.GetValue<string>();

    // The proof a step ended at its until and not its pace: met, with the game time the bridge counted under the pace's
    // milliseconds (each fixture's until holds 0.6 s of game time or less into a pace of 1 s or 2 s); game time, so load-proof.
    internal static void AssertUntilMetInGameTime(JsonNode step, int paceMs)
    {
        Assert.True(step["until"]!["met"]!.GetValue<bool>(), step.ToJsonString());
        Assert.InRange(step["until"]!["gameMs"]!.GetValue<int>(), 0, paceMs - 1);
        Assert.Null(step["gameMs"]);
    }

    private static JsonElement[] Tokens(string json) => JsonSerializer.Deserialize<JsonElement[]>(json)!;

    private static IEnumerable<int> Indices(JsonNode indices) => indices.AsArray().Select(index => index!.GetValue<int>());

    internal static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
