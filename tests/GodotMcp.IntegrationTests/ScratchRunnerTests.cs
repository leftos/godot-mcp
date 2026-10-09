using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the scratch scenes of the InputProbe fixture (its scratch folder and godot-mcp.json) and a C# one of
/// CsProbe's, in the real Godot: each verdict, the step list, the sessions the scenes ran in, and a C# exception reported as
/// the failing step's error; an error at the boot, GDScript's or a C# _Ready's, is red before any step. The fixtures' copies
/// take top-level files only, so each test copies the scratch folder in.
/// </summary>
public sealed class ScratchRunnerTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 180_000;
    private const int CSharpTestTimeoutMs = 300_000;

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchRunnerTests()
    {
        _scratch = new ScratchTools(_harness.Sessions);
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(_probe.Directory, "scratch"));
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    private const int ParallelTestTimeoutMs = 300_000;

    private static readonly string[] FolderScenes =
    [
        "ScratchBeside",
        "ScratchBootError",
        "ScratchBusy",
        "ScratchGreen",
        "ScratchLateError",
        "ScratchLateLine",
        "ScratchLaunchNoise",
        "ScratchLeak",
        "ScratchMarker",
        "ScratchNoProtocol",
        "ScratchNoSteps",
        "ScratchPushError",
        "ScratchQuit",
        "ScratchStepPace",
        "ScratchStepPaceAfter",
        "ScratchTwoRed",
    ];

    private static readonly string[] FolderVerdicts =
    [
        "green",
        "red",
        "killed",
        "green",
        "red",
        "red",
        "green",
        "known",
        "green",
        "red",
        "no-steps",
        "red",
        "red",
        "green",
        "red",
        "red",
    ];

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheScratchFolderPlaysEachSceneToItsVerdict()
    {
        JsonObject result = await RunAsync(_probe.Directory, scenes: null, options: null, TestContext.Current.CancellationToken);

        AssertFolderVerdicts(result);
        JsonNode prep = result["prep"]!;
        Assert.False(string.IsNullOrEmpty(prep["build"]!.GetValue<string>()), result.ToJsonString());
        Assert.False(string.IsNullOrEmpty(prep["import"]!.GetValue<string>()), result.ToJsonString());
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.All(scenes, scene => Assert.Null(scene!["alone"]));
        Assert.Equal(
            [1, 0.5, 0.1, 0.5, 2, 0.5, 0.5, 0.5, 2, 0.5, 0.5, 0.5, 0.5, 0.25, 0.1, 0.5],
            scenes.Select(scene => scene!["pace"]!.GetValue<double>())
        );
        Assert.Equal("boot", scenes[1]!["failedAt"]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = ParallelTestTimeoutMs)]
    public async Task ThreeAtOnceTheFolderPlaysToTheSameVerdictsInTheListedOrder()
    {
        JsonObject result = await RunAsync(_probe.Directory, scenes: null, new ScratchOptions(Parallel: 3), TestContext.Current.CancellationToken);

        AssertFolderVerdicts(result);
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Null(scenes[1]!["alone"]);
        Assert.False(scenes[2]!["alone"]!.GetValue<bool>());
        Assert.Null(scenes[9]!["alone"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    private static void AssertFolderVerdicts(JsonObject result)
    {
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(FolderScenes, scenes.Select(scene => scene!["scene"]!.GetValue<string>()));
        Assert.Equal(FolderVerdicts, scenes.Select(scene => scene!["verdict"]!.GetValue<string>()));
        Assert.Equal(FolderScenes.Select(name => "InputProbe.scratch-" + name), scenes.Select(scene => scene!["session"]!.GetValue<string>()));
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal((5, 8, 1, 1, 1), Counts(result));
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LaunchLinesAreNeverJudgedAndARenamedRootIsFound()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLaunchNoise"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Equal(1, scene["steps"]!["played"]!.GetValue<int>());
        DebugOutput output = _harness.Sessions.GetDebugOutput("InputProbe.scratch-ScratchLaunchNoise", 100, before: null);
        Assert.Contains("ERROR: launch noise", output.Stderr);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ARootWithoutTheProtocolIsRedBeforeAnyStep()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchNoProtocol"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal(-1, failedAt["index"]!.GetValue<int>());
        Assert.Contains("lacks GetStatus", failedAt["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, scene["steps"]!["played"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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
        Assert.Contains("[scratch] calm", details[0]!["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
        JsonNode error = Assert.Single(details[1]!["errors"]!.AsArray())!;
        Assert.Equal("scratch step two failed", error["message"]!.GetValue<string>());
        Assert.EndsWith("scratch_push_error.gd", error["file"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ALineATimerPrintsFailsTheStepItPrintedIn()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLateLine"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal((1, "wait"), (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>()));
        Assert.Equal("ERROR: late failure", failedAt["error"]!.GetValue<string>());
        Assert.Empty(scene["details"]![1]!["errors"]!.AsArray());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ALeakAtExitIsFoundAndAKnownSceneReportsItsReason()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLeak"], options: null, TestContext.Current.CancellationToken));

        Assert.Equal("known", scene["verdict"]!.GetValue<string>());
        Assert.Equal("1 ObjectDB instance", scene["exit"]!["leaked"]!.GetValue<string>());
        Assert.StartsWith("leaks a node at exit on purpose", scene["known"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(scene["failedAt"]);
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = CSharpTestTimeoutMs)]
    public async Task ACSharpStepThatThrowsIsRedWithItsExceptionAndAReadyThatThrowsIsRedAtTheBoot()
    {
        using var csProbe = CsProbeProject.Unbuilt();
        CopyFolder(Path.Combine(RepoPaths.Root, "tests", "fixtures", "CsProbe", "Scratch"), Path.Combine(csProbe.Directory, "Scratch"));

        JsonObject result = await RunAsync(
            csProbe.Directory,
            ["res://Scratch/ScratchThrow.tscn", "res://Scratch/ScratchBootThrow.tscn"],
            options: null,
            TestContext.Current.CancellationToken
        );

        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(2, scenes.Count);
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

    [Fact(Timeout = TestTimeoutMs)]
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

    private async Task<JsonObject> RunAsync(string projectDir, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(projectDir, scenes, options, cancellation))!.AsObject();

    private static JsonObject Single(JsonObject result) => Assert.Single(result["scenes"]!.AsArray())!.AsObject();

    private static string Status(JsonNode step) => step["status"]!.GetValue<string>();

    private static (int Green, int Red, int Known, int NoSteps, int Killed) Counts(JsonObject result) =>
        (
            result["green"]!.GetValue<int>(),
            result["red"]!.GetValue<int>(),
            result["known"]!.GetValue<int>(),
            result["noSteps"]!.GetValue<int>(),
            result["killed"]!.GetValue<int>()
        );

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
