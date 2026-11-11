using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches against the scratch scenes of the InputProbe fixture (its scratch folder and godot-mcp.json), in the real
/// Godot: each verdict, a scene red only beside another played again alone, an error at the boot red before any step, a
/// step's or a scene's ceiling, the exit, and the sessions the scenes ran in. Which steps a scene plays is
/// <see cref="ScratchStepsRunTests"/>', paces, time scales and the C# scenes <see cref="ScratchPaceRunTests"/>', and the facts
/// that play the whole folder <see cref="ScratchFolderTests"/>'. Its static helpers serve the other scratch classes.
/// </summary>
public sealed class ScratchRunnerTests : ScratchRunBase
{
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASceneRedOnlyBesideAnotherIsPlayedAgainAloneAndPasses()
    {
        // ScratchHolder and ScratchBesideHeld wait on each other's files through their first steps' untils, so the first play of
        // ScratchBesideHeld is red beside the holder whatever order the engines boot in; each until's 120 s pace is only its cap.
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_beside"), Path.Combine(_probe.Directory, "scratch"));
        string profile = """
            {
              "scratch": {
                "folder": "scratch",
                "pace": {
                  "ScratchHolder": {
                    "seconds": 0.1,
                    "reason": "its last step only leaves a note",
                    "steps": { "hold": { "seconds": 120, "reason": "a cap only: the until ends the step once the besider has looked" } }
                  },
                  "ScratchBesideHeld": {
                    "seconds": 0.1,
                    "reason": "its last step only judges what the first saw",
                    "steps": { "look": { "seconds": 120, "reason": "a cap only: the until ends the step once the holder is seen" } }
                  }
                }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchHolder", "ScratchBesideHeld"],
            new ScratchOptions(Parallel: 2, Details: FullDetails),
            TestContext.Current.CancellationToken
        );

        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(["ScratchHolder", "ScratchBesideHeld"], scenes.Select(scene => scene!["scene"]!.GetValue<string>()));
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("green", scenes[0]!["verdict"]!.GetValue<string>());
        Assert.Null(scenes[0]!["alone"]);
        JsonNode beside = scenes[1]!;
        Assert.Equal("green", beside["verdict"]!.GetValue<string>());
        Assert.True(beside["alone"]!.GetValue<bool>(), beside.ToJsonString());
        Assert.Null(beside["aloneFailedAt"]);
        Assert.Equal("InputProbe.scratch-ScratchBesideHeld", beside["session"]!.GetValue<string>());
        Assert.Equal(2, result["green"]!.GetValue<int>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AScratchUserDataGivesEachSceneItsOwnSeededFolderGoneAfterTheRun()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The scratch user:// route checked is APPDATA's.");
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_userdata"), Path.Combine(_probe.Directory, "scratch"));
        string seed = Path.Combine(_probe.Directory, "seed-data");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "seeded");
        string profile = """
            { "scratch": { "folder": "scratch", "userData": { "seed": "seed-data" } } }
            """;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchUserData", "ScratchGreen"],
            new ScratchOptions(Parallel: 2, Details: FullDetails),
            TestContext.Current.CancellationToken
        );

        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        JsonArray details = scenes[0]!["details"]!.AsArray();
        string scratchRoot = Path.Combine(Path.GetTempPath(), "godot-mcp").Replace('\\', '/');
        Assert.Contains(
            $"{scratchRoot}/userdata-{Environment.ProcessId}-"
                + OverrideOwner.Current.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-InputProbe.scratch-ScratchUserData-",
            Status(details[0]!),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal("seeded", Status(details[1]!));
        string folder = Path.Combine(_probe.Directory, ".godot", "godot-mcp");
        if (Directory.Exists(folder))
        {
            Assert.Empty(Directory.GetDirectories(folder, "userdata-*"));
        }

        string scratchParent = Path.Combine(Path.GetTempPath(), "godot-mcp");
        if (Directory.Exists(scratchParent))
        {
            Assert.Empty(
                Directory.GetDirectories(
                    scratchParent,
                    $"userdata-{Environment.ProcessId}-"
                        + OverrideOwner.Current.StartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "-InputProbe.scratch-ScratchUserData-*"
                )
            );
        }
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
    public async Task ALaunchLineMatchingAPatternIsRedAtTheBootAndARenamedRootIsFound()
    {
        JsonObject scene = Single(await RunAsync(_probe.Directory, ["ScratchLaunchNoise"], options: null, TestContext.Current.CancellationToken));

        JsonNode failedAt = scene["failedAt"]!;
        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Equal(
            (-1, "boot", "ERROR: launch noise"),
            (failedAt["index"]!.GetValue<int>(), failedAt["name"]!.GetValue<string>(), failedAt["error"]!.GetValue<string>())
        );
        Assert.NotNull(failedAt["pattern"]);
        Assert.Equal((0, 1), (scene["steps"]!["played"]!.GetValue<int>(), scene["steps"]!["total"]!.GetValue<int>()));
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
        JsonObject result = await RunAsync(
            _probe.Directory,
            ["ScratchBusy"],
            new ScratchOptions(Ceilings: JsonSerializer.SerializeToElement(new Dictionary<string, double> { ["ScratchBusy"] = 5 })),
            TestContext.Current.CancellationToken
        );

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
                new ScratchOptions(Pace: 2, Ceilings: JsonSerializer.SerializeToElement(new Dictionary<string, double> { ["ScratchStepPace"] = 1 })),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("killed", scene["verdict"]!.GetValue<string>());
        Assert.Contains("set by options.ceilings", scene["failedAt"]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// The game's only step allocates and keeps 1536 MB: a 1024 MB memory ceiling kills the scene while its steps play, and a
    /// 4096 MB one leaves it green. The step's pace, given by the call, is the wait after it the watch reads memory in; the watch
    /// decides on a reading the unit tests call directly, so no test waits on its second.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AScenePastItsMemoryCeilingIsKilledAndOneUnderItIsGreen()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_memory"), Path.Combine(_probe.Directory, "scratch_memory"));

        JsonObject killed = Single(
            await RunAsync(
                _probe.Directory,
                ["res://scratch_memory/ScratchMemory.tscn"],
                new ScratchOptions(Pace: 30, MemoryCeilings: new() { ["ScratchMemory"] = 1024 }),
                cancellation
            )
        );
        Assert.Equal("killed", killed["verdict"]!.GetValue<string>());
        Assert.StartsWith(
            "the scene's game passed its memory ceiling of 1024 MB (",
            killed["failedAt"]!["error"]!.GetValue<string>(),
            StringComparison.Ordinal
        );

        JsonObject green = Single(
            await RunAsync(
                _probe.Directory,
                ["res://scratch_memory/ScratchMemory.tscn"],
                new ScratchOptions(Pace: 1, MemoryCeilings: new() { ["ScratchMemory"] = 4096 }),
                cancellation
            )
        );
        Assert.True(green["verdict"]!.GetValue<string>() == "green", green.ToJsonString());
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

    /// A scene played twice keeps both games' output: each run's log is named for the run, and the first survives the second.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TwoRunsOfOneSceneKeepTwoLogs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string first = GreenLog(await RunAsync(_probe.Directory, ["ScratchGreen"], options: null, cancellation));
        string second = GreenLog(await RunAsync(_probe.Directory, ["ScratchGreen"], options: null, cancellation));

        Assert.True(File.Exists(first), first);
        Assert.True(File.Exists(second), second);
        Assert.NotEqual(first, second);
        Assert.Contains("[scratch] closed", File.ReadAllLines(first));
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
    public async Task ALateLineFailsTheStepItPrintedIn()
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

    /// <summary>options.details true, every step's full detail.</summary>
    internal static JsonElement FullDetails { get; } = JsonSerializer.SerializeToElement(true);

    internal static JsonObject Single(JsonObject result) => Assert.Single(result["scenes"]!.AsArray())!.AsObject();

    internal static string Status(JsonNode step) => step["status"]!.GetValue<string>();

    /// <summary>The log a green scene's result names, which every scene's log must exist as.</summary>
    private static string GreenLog(JsonObject result)
    {
        JsonObject scene = Single(result);
        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        return scene["log"]!.GetValue<string>();
    }

    // A step ended at its until, met, with the game time the bridge counted under boundMs, the harness's until hang guard in game
    // milliseconds: a hang guard, not a timing assertion, since a met until ends however long past its pace its condition holds.
    internal static void AssertUntilMetInGameTime(JsonNode step, int boundMs)
    {
        Assert.True(step["until"]!["met"]!.GetValue<bool>(), step.ToJsonString());
        Assert.InRange(step["until"]!["gameMs"]!.GetValue<int>(), 0, boundMs - 1);
        Assert.Null(step["gameMs"]);
    }

    internal static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
