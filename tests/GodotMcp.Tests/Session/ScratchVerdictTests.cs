using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>run_scratches' verdict of a scene from what the runner saw of it, with no Godot.</summary>
public sealed class ScratchVerdictTests
{
    private static readonly IReadOnlyList<ScratchPattern> Default = ScratchProfile.DefaultPatterns;
    private static readonly ScratchRules Plain = new(
        Default,
        Known: null,
        PaceReason: null,
        Details: ScratchDetails.Default,
        DetailChars: ScratchVerdict.DefaultDetailChars
    );

    [Fact]
    public void EveryStepCleanAndACleanExitIsGreenWithoutSteps()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0), Step(1)), Plain);

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
        Assert.Equal(new ScratchStepCount(2, 2), result.Steps);
        Assert.Null(result.FailedAt);
        Assert.Null(result.Details);
        Assert.Equal(0, result.Exit.Code);
        Assert.Null(result.Exit.Leaked);
    }

    [Fact]
    public void DetailsListAGreenScenesStepsWithTheirNotes()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0) with { Lines = ["[scratch] opened"] }),
            Plain with
            {
                Details = ScratchDetails.Full,
            }
        );

        ScratchStepResult step = Assert.Single(result.Details!);
        Assert.True(step.Ok);
        Assert.Equal(500, step.GameMs);
        Assert.Equal(["[scratch] opened"], step.Lines);
    }

    [Fact]
    public void AFeedErrorFailsItsStepWithItsFileAndLine()
    {
        ScratchStep failing = Step(1) with { Status = "note", Errors = [Error("boom", "res://a.gd", 7)], Lines = ["[scratch] before"] };

        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0), failing), Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(new ScratchFailure(1, "step1", "boom (res://a.gd:7)", "note"), result.FailedAt);
        ScratchStepResult listed = result.Details![1];
        Assert.False(listed.Ok);
        Assert.Equal([new ScratchError("boom", "res://a.gd", 7)], listed.Errors);
        Assert.Equal(["[scratch] before"], listed.Lines);
    }

    [Fact]
    public void AWarningInTheFeedDoesNotFailAStep()
    {
        ScratchStep warned = Step(0) with { Errors = [Error("careful", "", 0) with { Type = ErrorFeed.WarningType }] };

        Assert.Equal(ScratchVerdict.Green, ScratchVerdict.Judge(Seen(warned), Plain).Verdict);
    }

    [Fact]
    public void ACallErrorFailsItsStep()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "PlayStep failed: gone" }), Plain);

        Assert.Equal("PlayStep failed: gone", result.FailedAt!.Error);
    }

    [Fact]
    public void ALineMatchingAPatternInTheStepsWindowFailsThatStep()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0), Step(1) with { Lines = ["ok", "ERROR: late failure"] }), Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(1, result.FailedAt!.Index);
        Assert.Equal("ERROR: late failure", result.FailedAt.Error);
    }

    [Fact]
    public void ALineMatchingNoPatternDoesNotFailAStep()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["error: lower case", "  ERROR: indented"] }), Plain);

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
    }

    [Fact]
    public void ABootLineMatchingAPatternTurnsTheSceneRedAtBoot()
    {
        IReadOnlyList<ScratchPattern> patterns = [new ScratchPattern(ScratchProfile.Compile(@"^\[Warning\] slow"), "boot warnings fail")];
        ScratchObservation seen = Seen() with { Total = 2, LaunchLines = ["loaded", "[Warning] slow shader", "[Warning] slow again"] };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Patterns = patterns, Details = ScratchDetails.Full, Known = "flaky" });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(
            new ScratchFailure(-1, "boot", "[Warning] slow shader", "") { Pattern = new ScratchMatch(@"^\[Warning\] slow", "boot warnings fail") },
            result.FailedAt
        );
        Assert.Equal(new ScratchStepCount(0, 2), result.Steps);
        Assert.Null(result.Details);
        Assert.Null(result.Known);
        Assert.False(ScratchVerdict.PlaysAgainAlone(result, parallel: 2));
    }

    [Fact]
    public void ABootLineMatchingNoPatternLeavesTheBootGreen()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { LaunchLines = ["error: lower case", "  ERROR: indented"] }, Plain);

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Null(result.Exit.Lines);
    }

    [Fact]
    public void ABootFeedErrorIsRedAtTheBoot()
    {
        ScratchObservation seen = Seen() with
        {
            Total = 2,
            BootErrors = [Error("boot failure", "res://s.gd", 4)],
            LaunchLines = ["ERROR: launch noise"],
        };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Details = ScratchDetails.Full });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(new ScratchFailure(-1, "boot", "boot failure (res://s.gd:4)", ""), result.FailedAt);
        Assert.Equal(new ScratchStepCount(0, 2), result.Steps);
        Assert.Null(result.Details);
    }

    [Fact]
    public void AWarningAtTheBootIsNotRed()
    {
        ScratchObservation seen = Seen(Step(0)) with { BootErrors = [Error("careful", "", 0) with { Type = ErrorFeed.WarningType }] };

        Assert.Equal(1, seen.Total);
        Assert.Equal(ScratchVerdict.Green, ScratchVerdict.Judge(seen, Plain).Verdict);
    }

    [Fact]
    public void ABootErrorInASceneWithNoStepsIsRedNotNoSteps()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen() with { BootErrors = [Error("boot failure", "", 0)] }, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(new ScratchFailure(-1, "boot", "boot failure", ""), result.FailedAt);
    }

    [Fact]
    public void AKnownSceneRedAtBootStaysRed()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen() with
            {
                Total = 2,
                BootErrors = [Error("boot failure", "", 0)],
            },
            Plain with
            {
                Known = "flaky tray",
            }
        );

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.Known);
        Assert.Equal((false, 1, 0), (run.Passed, run.Red, run.Known));
    }

    [Fact]
    public void ASceneRedAtBootIsNotReplayedAlone()
    {
        ScratchSceneResult booted = ScratchVerdict.Judge(Seen() with { Total = 2, BootErrors = [Error("boot failure", "", 0)] }, Plain);

        Assert.Equal((ScratchVerdict.Red, 0, "boot"), (booted.Verdict, booted.Steps.Played, booted.FailedAt!.Name));
        Assert.False(ScratchVerdict.PlaysAgainAlone(booted, parallel: 2));
        Assert.False(ScratchVerdict.PlaysAgainAlone(booted, parallel: 4));
    }

    [Fact]
    public void ACatastrophicPatternIsItsStepsErrorNotAKill()
    {
        IReadOnlyList<ScratchPattern> slow = [new ScratchPattern(ScratchProfile.Compile("(a+)+$"), null)];
        ScratchStep step = Step(0) with { Lines = [new string('a', 40) + "b"] };

        ScratchSceneResult result = ScratchVerdict.Judge(Seen(step), Plain with { Patterns = slow });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal("pattern '(a+)+$' took over 1 s on a line; simplify it in scratch.patterns", result.FailedAt!.Error);
        Assert.Equal(new ScratchMatch("(a+)+$", null), result.FailedAt.Pattern);
    }

    [Fact]
    public void ALineMatchingAReasonedPatternNamesItOnFailedAt()
    {
        ScratchPattern pattern = new(ScratchProfile.Compile("^FAIL"), "the scenes' own assertion prefix");

        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0) with { Lines = ["FAIL: the tray did not drop"] }),
            Plain with
            {
                Patterns = [pattern],
            }
        );

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal("FAIL: the tray did not drop", result.FailedAt!.Error);
        Assert.Equal(new ScratchMatch("^FAIL", "the scenes' own assertion prefix"), result.FailedAt.Pattern);
    }

    [Fact]
    public void ABareStringPatternGivesAPatternWithNoReasonInTheJson()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["ERROR: late failure"] }), Plain);

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, ToolJson.Options))!.AsObject();
        JsonObject match = json["failedAt"]!["pattern"]!.AsObject();

        Assert.Equal("^SCRIPT ERROR|^ERROR:|ObjectDB instances? (was|were) leaked", match["pattern"]!.GetValue<string>());
        Assert.False(match.ContainsKey("reason"), json.ToJsonString());
    }

    [Fact]
    public void ThePatternAfterAnotherIsNamedWhenItMatches()
    {
        IReadOnlyList<ScratchPattern> patterns =
        [
            new ScratchPattern(ScratchProfile.Compile("^A"), "first"),
            new(ScratchProfile.Compile("boom$"), "second"),
        ];
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["boom"] }), Plain with { Patterns = patterns });

        Assert.Equal(new ScratchMatch("boom$", "second"), result.FailedAt!.Pattern);
    }

    [Fact]
    public void FailuresThatAreNotLineMatchesCarryNoPattern()
    {
        ScratchSceneResult feed = ScratchVerdict.Judge(Seen(Step(0) with { Errors = [Error("boom", "", 0)] }), Plain);
        ScratchSceneResult call = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "gone" }), Plain);
        ScratchSceneResult killed = ScratchVerdict.Judge(Seen(Step(0)) with { Kill = new ScratchFailure(0, "step0", "ceiling", "") }, Plain);

        foreach (ScratchSceneResult result in new[] { feed, call, killed })
        {
            JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, ToolJson.Options))!.AsObject();
            Assert.False(json["failedAt"]!.AsObject().ContainsKey("pattern"), json.ToJsonString());
        }
    }

    [Fact]
    public void AnErrorAfterTheLastStepIsRedWithNoFailedStep()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { AfterError = "late (res://a.gd:3)" }, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Equal("late (res://a.gd:3)", result.Exit.Error);
    }

    [Fact]
    public void AKillByTheStopTurnsAGreenSceneRed()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0)) with
            {
                ExitCode = null,
                StopKillReason = "the game did not quit within 3 s",
                StopWarning = "a debugger was attached",
            },
            Plain
        );

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Equal(
            (true, "the game did not quit within 3 s", "a debugger was attached"),
            (result.Exit.Killed, result.Exit.KillReason, result.Exit.Warning)
        );
    }

    [Fact]
    public void AKilledSceneFailsTheRun()
    {
        ScratchSceneResult killed = ScratchVerdict.Judge(Seen(Step(0)) with { Kill = new ScratchFailure(0, "step0", "ceiling", "") }, Plain);

        ScratchRunResult run = ScratchVerdict.Summarise([killed], PrepResult.Skipped, timeScale: 1);

        Assert.Equal((false, 0, 0, 1), (run.Passed, run.Green, run.Red, run.Killed));
    }

    [Fact]
    public void AKnownSceneThatIsKilledStaysKilledAndFailsTheRun()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0)) with
            {
                Kill = new ScratchFailure(0, "step0", "ceiling", ""),
            },
            Plain with
            {
                Known = "flaky tray",
            }
        );

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.Killed, result.Verdict);
        Assert.Equal((false, 0, 1), (run.Passed, run.Known, run.Killed));
    }

    [Fact]
    public void AKnownSceneRefusedBeforeAnyStepIsRed()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen() with
            {
                Refusal = "res://s/S.tscn did not start: gone",
            },
            Plain with
            {
                Known = "flaky tray",
            }
        );

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(-1, result.FailedAt!.Index);
        Assert.Null(result.Known);
        Assert.Equal((false, 1, 0), (run.Passed, run.Red, run.Known));
    }

    [Theory]
    [InlineData("WARNING: 1 ObjectDB instance was leaked at exit", "1 ObjectDB instance")]
    [InlineData("WARNING: 3 ObjectDB instances were leaked at exit", "3 ObjectDB instances")]
    public void ALeakAtExitTurnsAGreenSceneRed(string line, string leaked)
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { ExitLines = ["bye", line] }, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Equal(leaked, result.Exit.Leaked);
        Assert.Equal([line], result.Exit.Lines);
    }

    [Fact]
    public void ANonZeroExitTurnsAGreenSceneRed()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { ExitCode = 3 }, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(3, result.Exit.Code);
    }

    [Fact]
    public void AnExpectedLineAnywhereInTheScenesOutputKeepsItGreen()
    {
        ScratchRules rules = Plain with { Expect = [Expected("^launched$"), Expected("^\\[scratch\\] ready$"), Expected("^removed: ")] };
        ScratchObservation seen = Seen(Step(0) with { Lines = ["[scratch] ready"] }) with
        {
            LaunchLines = ["launched"],
            ExitLines = ["removed: user://store"],
        };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, rules);

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
        Assert.Null(result.Exit.Missing);
        Assert.False(Json(result)["exit"]!.AsObject().ContainsKey("missing"));
    }

    [Fact]
    public void AnExpectedLineNeverPrintedTurnsAGreenSceneRedNamingItInTheExit()
    {
        ScratchRules rules = Plain with
        {
            Expect = [Expected("^removed: ", "the store folder is left behind"), Expected("^bye$"), Expected("^ready$")],
        };

        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["ready"] }) with { ExitLines = ["later"] }, rules);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Equal(
            ["`^removed: ` never appeared in the scene's output (the store folder is left behind)", "`^bye$` never appeared in the scene's output"],
            result.Exit.Missing
        );
        Assert.Equal(2, Json(result)["exit"]!["missing"]!.AsArray().Count);
    }

    [Fact]
    public void ARedStepKeepsItsVerdictAndTheExitStillListsTheMissingLines()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "gone" }), Plain with { Expect = [Expected("^bye$")] });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal((0, "gone"), (result.FailedAt!.Index, result.FailedAt.Error));
        Assert.Equal(["`^bye$` never appeared in the scene's output"], result.Exit.Missing);
    }

    [Fact]
    public void AKilledSceneStaysKilledAndListsNoMissingLines()
    {
        ScratchObservation seen = Seen(Step(0)) with { Kill = new ScratchFailure(0, "step0", "ceiling", "") };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Expect = [Expected("^bye$")] });

        Assert.Equal(ScratchVerdict.Killed, result.Verdict);
        Assert.Null(result.Exit.Missing);
    }

    [Fact]
    public void APartialStepsRunSkipsTheExpectedLines()
    {
        ScratchObservation seen = Seen(Step(0)) with { Total = 2, Selected = [0] };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Expect = [Expected("^bye$")] });

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
        Assert.Null(result.Exit.Missing);
    }

    [Fact]
    public void ARefusedOrStepLessSceneIsNotJudgedForExpectedLines()
    {
        ScratchRules rules = Plain with { Expect = [Expected("^bye$")] };

        ScratchSceneResult refused = ScratchVerdict.Judge(Seen() with { Total = 2, Refusal = "the scene root lacks PlayStep" }, rules);
        ScratchSceneResult booted = ScratchVerdict.Judge(Seen() with { Total = 2, BootErrors = [Error("boot failure", "", 0)] }, rules);
        ScratchSceneResult empty = ScratchVerdict.Judge(Seen(), rules);

        Assert.Equal((ScratchVerdict.Red, -1), (refused.Verdict, refused.FailedAt!.Index));
        Assert.Equal((ScratchVerdict.Red, "boot"), (booted.Verdict, booted.FailedAt!.Name));
        Assert.Equal(ScratchVerdict.NoSteps, empty.Verdict);
        Assert.All([refused, booted, empty], result => Assert.Null(result.Exit.Missing));
    }

    [Fact]
    public void AnExpectedPatternPastItsTimeoutIsNotSatisfiedAndSaysSo()
    {
        ScratchStep step = Step(0) with { Lines = [new string('a', 40) + "b"] };

        ScratchSceneResult result = ScratchVerdict.Judge(Seen(step), Plain with { Expect = [Expected("(a+)+$")] });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Null(result.FailedAt);
        Assert.Equal(["`(a+)+$` never appeared in the scene's output; it took over 1 s on a line, so simplify it"], result.Exit.Missing);
    }

    [Fact]
    public void AMissingLineNotesTheOutputLinesTheBufferDropped()
    {
        ScratchObservation seen = Seen(Step(0) with { Dropped = 2 }, Step(1) with { Dropped = 1 });

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Expect = [Expected("^bye$", "the game says goodbye")] });

        Assert.Equal(
            ["`^bye$` never appeared in the scene's output (the game says goodbye); 3 output lines were dropped, so it may have been printed"],
            result.Exit.Missing
        );
    }

    [Fact]
    public void AnUnknownExitCodeDoesNotTurnASceneRed() =>
        Assert.Equal(ScratchVerdict.Green, ScratchVerdict.Judge(Seen(Step(0)) with { ExitCode = null }, Plain).Verdict);

    [Fact]
    public void NoStepsIsItsOwnVerdict()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(), Plain);

        Assert.Equal(ScratchVerdict.NoSteps, result.Verdict);
        Assert.Equal(new ScratchStepCount(0, 0), result.Steps);
    }

    [Fact]
    public void ARefusalBeforeAnyStepIsRedAtIndexMinusOne()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen() with { Refusal = "the scene root /root/X lacks PlayStep" }, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(new ScratchFailure(-1, "", "the scene root /root/X lacks PlayStep", ""), result.FailedAt);
        Assert.Null(result.Details);
    }

    [Fact]
    public void AKilledSceneNamesTheStepInFlightAndListsTheStepsPlayed()
    {
        ScratchFailure inFlight = new(1, "step1", "the scene passed its ceiling", "");

        ScratchSceneResult result = ScratchVerdict.Judge(
            new ScratchObservation("S", "p.scratch-S", 0.5, 3) { Steps = [Step(0)], Kill = inFlight },
            Plain
        );

        Assert.Equal(ScratchVerdict.Killed, result.Verdict);
        Assert.Equal(inFlight, result.FailedAt);
        Assert.Equal(new ScratchStepCount(1, 3), result.Steps);
        Assert.Single(result.Details!);
    }

    [Fact]
    public void AKnownSceneThatGoesRedIsKnownWithItsReason()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain with { Known = "flaky tray" });

        Assert.Equal(ScratchVerdict.KnownRed, result.Verdict);
        Assert.Equal("flaky tray", result.Known);
        Assert.NotNull(result.Details);
    }

    [Fact]
    public void AKnownSceneThatGoesGreenIsKnownNowGreenAndCountsAsRed()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)), Plain with { Known = "flaky tray" });

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.KnownNowGreen, result.Verdict);
        Assert.Equal("flaky tray", result.Known);
        Assert.False(run.Passed);
        Assert.Equal(1, run.Red);
    }

    [Fact]
    public void TheRunPassesWithOnlyGreenKnownAndNoStepsScenes()
    {
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0)), Plain);
        ScratchSceneResult known = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain with { Known = "why" });
        ScratchSceneResult empty = ScratchVerdict.Judge(Seen(), Plain);

        ScratchRunResult run = ScratchVerdict.Summarise([green, known, empty], PrepResult.Skipped, timeScale: 1);

        Assert.Equal((true, 1, 0, 1, 1, 0), (run.Passed, run.Green, run.Red, run.Known, run.NoSteps, run.Killed));
        Assert.Equal([green, known, empty], run.Scenes);
    }

    [Fact]
    public void ARedSceneCarriesTheProfilesReasonForItsPace()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain with { PaceReason = "the tray lies at rest" });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal("the tray lies at rest", result.PaceReason);
    }

    [Fact]
    public void AKilledSceneCarriesTheProfilesReasonForItsPace()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0)) with
            {
                Kill = new ScratchFailure(0, "step0", "ceiling", ""),
            },
            Plain with
            {
                PaceReason = "the tray lies at rest",
            }
        );

        Assert.Equal(ScratchVerdict.Killed, result.Verdict);
        Assert.Equal("the tray lies at rest", result.PaceReason);
    }

    [Fact]
    public void AGreenSceneCarriesNoReasonAndOmitsItFromTheJson()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)), Plain with { PaceReason = "the tray lies at rest" });

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, ToolJson.Options))!.AsObject();

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
        Assert.Null(result.PaceReason);
        Assert.False(json.ContainsKey("paceReason"), json.ToJsonString());
    }

    [Fact]
    public void AKnownSceneCarriesNoReason()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(
            Seen(Step(0) with { CallError = "x" }),
            Plain with
            {
                Known = "flaky tray",
                PaceReason = "the tray lies at rest",
            }
        );

        Assert.Equal(ScratchVerdict.KnownRed, result.Verdict);
        Assert.Null(result.PaceReason);
    }

    [Fact]
    public void AKnownNowGreenSceneCarriesNoReason()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)), Plain with { Known = "flaky tray", PaceReason = "the tray lies at rest" });

        Assert.Equal(ScratchVerdict.KnownNowGreen, result.Verdict);
        Assert.Null(result.PaceReason);
    }

    [Fact]
    public void ARedSceneWithoutAProfileReasonCarriesNone() =>
        Assert.Null(ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain).PaceReason);

    [Fact]
    public void ARedOrKilledScenePlaysAgainAloneOnlyWhenItRanBesideOthers()
    {
        ScratchSceneResult red = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain);
        ScratchSceneResult killed = ScratchVerdict.Judge(Seen(Step(0)) with { Kill = new ScratchFailure(0, "step0", "ceiling", "") }, Plain);
        ScratchSceneResult exitRed = ScratchVerdict.Judge(Seen(Step(0)) with { ExitCode = 1 }, Plain);

        Assert.True(ScratchVerdict.PlaysAgainAlone(red, parallel: 2));
        Assert.True(ScratchVerdict.PlaysAgainAlone(killed, parallel: 4));
        Assert.True(ScratchVerdict.PlaysAgainAlone(exitRed, parallel: 2));
        Assert.False(ScratchVerdict.PlaysAgainAlone(red, parallel: 1));
        Assert.False(ScratchVerdict.PlaysAgainAlone(killed, parallel: 1));
    }

    [Fact]
    public void GreenKnownNoStepsAndARefusalBeforeAnyStepNeverPlayAgain()
    {
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0)), Plain);
        ScratchSceneResult known = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }), Plain with { Known = "why" });
        ScratchSceneResult empty = ScratchVerdict.Judge(Seen(), Plain);
        ScratchSceneResult refused = ScratchVerdict.Judge(Seen() with { Refusal = "the scene root lacks GetStatus", Total = 2 }, Plain);

        Assert.Equal((ScratchVerdict.Red, 0, -1), (refused.Verdict, refused.Steps.Played, refused.FailedAt!.Index));
        Assert.All([green, known, empty, refused], scene => Assert.False(ScratchVerdict.PlaysAgainAlone(scene, parallel: 4), scene.Verdict));
    }

    [Fact]
    public void AKilledScenePlaysAgainAloneBeforeItsFirstStepOrInThePaceAfterItsLast()
    {
        ScratchSceneResult inPace = ScratchVerdict.Judge(Seen(Step(0)) with { Kill = new ScratchFailure(-1, "", "ceiling", "") }, Plain);
        ScratchSceneResult atLaunch = ScratchVerdict.Judge(Seen() with { Kill = new ScratchFailure(-1, "", "timed out", ""), Total = 2 }, Plain);

        Assert.Equal((ScratchVerdict.Killed, 1, 1, -1), (inPace.Verdict, inPace.Steps.Played, inPace.Steps.Total, inPace.FailedAt!.Index));
        Assert.Equal((ScratchVerdict.Killed, 0, -1), (atLaunch.Verdict, atLaunch.Steps.Played, atLaunch.FailedAt!.Index));
        Assert.True(ScratchVerdict.PlaysAgainAlone(inPace, parallel: 2));
        Assert.True(ScratchVerdict.PlaysAgainAlone(atLaunch, parallel: 2));
        Assert.False(ScratchVerdict.PlaysAgainAlone(inPace, parallel: 1));
    }

    [Fact]
    public void AReplayThatPassesAloneIsTheEntryMarkedAloneAndCountsAsGreen()
    {
        ScratchSceneResult first = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "x" }) with { Seconds = 2.0 }, Plain);
        ScratchSceneResult replay = ScratchVerdict.Judge(Seen(Step(0)) with { Seconds = 1.5 }, Plain);

        ScratchSceneResult entry = ScratchVerdict.Alone(first, replay);
        ScratchRunResult run = ScratchVerdict.Summarise([entry], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.Green, entry.Verdict);
        Assert.True(entry.Alone);
        Assert.Null(entry.FailedAt);
        Assert.Null(entry.AloneFailedAt);
        Assert.Equal(3.5, entry.Seconds);
        Assert.Equal((true, 1, 0), (run.Passed, run.Green, run.Red));
    }

    [Fact]
    public void AReplayThatFailsAgainKeepsTheFirstEntryWithTheReplaysFailure()
    {
        ScratchSceneResult first = ScratchVerdict.Judge(Seen(Step(0) with { CallError = "beside" }) with { Seconds = 2.0 }, Plain);
        ScratchSceneResult replay = ScratchVerdict.Judge(Seen(Step(0), Step(1) with { CallError = "alone" }) with { Seconds = 1.0 }, Plain);

        ScratchSceneResult entry = ScratchVerdict.Alone(first, replay);
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(entry, ToolJson.Options))!.AsObject();

        Assert.Equal(ScratchVerdict.Red, entry.Verdict);
        Assert.False(entry.Alone);
        Assert.Equal("beside", entry.FailedAt!.Error);
        Assert.Equal(new ScratchFailure(1, "step1", "alone", ""), entry.AloneFailedAt);
        Assert.Equal(3.0, entry.Seconds);
        Assert.False(json["alone"]!.GetValue<bool>());
        Assert.Equal(1, json["aloneFailedAt"]!["index"]!.GetValue<int>());
        Assert.Equal(1, ScratchVerdict.Summarise([entry], PrepResult.Skipped, timeScale: 1).Red);
    }

    [Fact]
    public void AScenePlayedOnceHasNoAloneFields()
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(ScratchVerdict.Judge(Seen(Step(0)), Plain), ToolJson.Options))!.AsObject();

        Assert.False(json.ContainsKey("alone"), json.ToJsonString());
        Assert.False(json.ContainsKey("aloneFailedAt"), json.ToJsonString());
    }

    [Fact]
    public void AStepListsAtMostTwentyLinesWithDetailsAndFiveWithoutAndCountsTheRest()
    {
        ScratchStep noisy = Step(0) with { Lines = [.. Enumerable.Range(0, 25).Select(n => $"ERROR: {n}")] };

        IReadOnlyList<string> full = ScratchVerdict.Judge(Seen(noisy), Plain with { Details = ScratchDetails.Full }).Details![0].Lines!;
        IReadOnlyList<string> compact = ScratchVerdict.Judge(Seen(noisy), Plain).Details![0].Lines!;

        Assert.Equal(21, full.Count);
        Assert.Equal("ERROR: 19", full[19]);
        Assert.Equal("… 5 more", full[20]);
        Assert.Equal(["ERROR: 0", "ERROR: 1", "ERROR: 2", "ERROR: 3", "ERROR: 4", "… 20 more"], compact);
    }

    [Fact]
    public void TheResultSerialisesWithoutItsAbsentFieldsAndRoundsSeconds()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { Seconds = 1.26 }, Plain);

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, ToolJson.Options))!.AsObject();

        Assert.Equal(1.3, json["seconds"]!.GetValue<double>());
        Assert.False(json.ContainsKey("failedAt"), json.ToJsonString());
        Assert.False(json.ContainsKey("details"), json.ToJsonString());
        Assert.False(json.ContainsKey("known"), json.ToJsonString());
        Assert.False(json["exit"]!.AsObject().ContainsKey("leaked"), json.ToJsonString());
        Assert.Equal(1, json["steps"]!["played"]!.GetValue<int>());
    }

    [Fact]
    public void TheRunResultCarriesItsPrepWithoutItsAbsentFields()
    {
        PrepResult prep = new()
        {
            Build = "no-csproj",
            Import = "done",
            ImportMs = 1200,
        };

        ScratchRunResult run = ScratchVerdict.Summarise([ScratchVerdict.Judge(Seen(Step(0)), Plain)], prep, timeScale: 1);
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(run, ToolJson.Options))!.AsObject();
        JsonObject shown = json["prep"]!.AsObject();

        Assert.Equal(["build", "import", "importMs"], shown.Select(pair => pair.Key));
        Assert.Equal(("no-csproj", "done"), (shown["build"]!.GetValue<string>(), shown["import"]!.GetValue<string>()));
        Assert.Equal(1200, shown["importMs"]!.GetValue<long>());
    }

    [Fact]
    public void AStepsWindowEndsAtEachStreamsMarkerNotAtTheOtherStreamsCount()
    {
        const string Marker = ScratchRun.MarkerPrefix + "step 0 end n1";
        OutputBuffer stdout = new(100);
        OutputBuffer stderr = new(100);
        LineMark start = new(1, 0);
        stdout.Add("[scratch] launch");
        stdout.Add("[scratch] step out");
        stdout.Add(Marker);
        stderr.Add("ERROR: step err");

        Assert.Null(ScratchRun.Markers(stdout.Page(100, null), stderr.Page(100, null), start, Marker));

        stderr.Add("ERROR: late step err");
        stderr.Add(Marker);
        stdout.Add("[scratch] next step out");
        LineMark end = ScratchRun.Markers(stdout.Page(100, null), stderr.Page(100, null), start, Marker)!.Value;

        Assert.Equal(new LineMark(3, 3), end);
        Assert.Equal(
            ["[scratch] step out", "ERROR: step err", "ERROR: late step err"],
            Window(stdout.Page(100, null), stderr.Page(100, null), start, end)
        );
        Assert.Equal(
            ["[scratch] next step out"],
            Window(stdout.Page(100, null), stderr.Page(100, null), end, new LineMark(long.MaxValue, long.MaxValue))
        );
    }

    [Fact]
    public void AMarkerBeforeTheWindowsStartOrFromAnotherWindowIsNotItsEnd()
    {
        const string Marker = ScratchRun.MarkerPrefix + "step 1 end n1";
        OutputBuffer stdout = new(100);
        OutputBuffer stderr = new(100);
        stdout.Add(Marker);
        stderr.Add(Marker);
        stdout.Add(ScratchRun.MarkerPrefix + "step 0 end n1");
        stderr.Add(ScratchRun.MarkerPrefix + "step 0 end n1");

        Assert.Null(ScratchRun.Markers(stdout.Page(100, null), stderr.Page(100, null), new LineMark(1, 1), Marker));
        Assert.Empty(Window(stdout.Page(100, null), stderr.Page(100, null), new LineMark(0, 0), new LineMark(9, 9)));
    }

    [Fact]
    public void OnlyThisRunsMarkersAreDroppedFromAWindow()
    {
        OutputBuffer stdout = new(100);
        OutputBuffer stderr = new(100);
        stdout.Add(ScratchRun.MarkerPrefix + "hello from the game");
        stdout.Add(ScratchRun.MarkerPrefix + "step 0 end n1");
        stderr.Add(ScratchRun.MarkerPrefix + "step 0 end other");

        List<string> lines = Window(stdout.Page(100, null), stderr.Page(100, null), new LineMark(0, 0), new LineMark(9, 9));

        Assert.Equal([ScratchRun.MarkerPrefix + "hello from the game", ScratchRun.MarkerPrefix + "step 0 end other"], lines);
    }

    [Fact]
    public void AnErrorFlushedBetweenTwoWindowsLandsInExactlyTheNextOne()
    {
        ErrorFeed feed = new();
        long mark = feed.Mark();
        feed.Add([Error("in step 0", "", 0)], 0);

        IReadOnlyList<ErrorEntry> first = ScratchRun.TakeErrors(feed, ref mark);
        feed.Add([Error("between the windows", "", 0), Error("warned", "", 0) with { Type = ErrorFeed.WarningType }], 0);
        IReadOnlyList<ErrorEntry> second = ScratchRun.TakeErrors(feed, ref mark);
        IReadOnlyList<ErrorEntry> third = ScratchRun.TakeErrors(feed, ref mark);

        Assert.Equal(["in step 0"], first.Select(entry => entry.Message));
        Assert.Equal(["between the windows"], second.Select(entry => entry.Message));
        Assert.Empty(third);
    }

    [Fact]
    public void LinesTheBufferNoLongerHoldsAreCountedAndListedFirst()
    {
        OutputBuffer stdout = new(3);
        OutputBuffer stderr = new(3);
        foreach (int number in Enumerable.Range(1, 7))
        {
            stdout.Add($"line {number}");
        }

        stderr.Add("err 1");

        long dropped = ScratchRun.Dropped(stdout.Page(3, null), stderr.Page(3, null), new LineMark(1, 0), new LineMark(long.MaxValue, long.MaxValue));
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Dropped = dropped, CallError = "x" }), Plain);

        Assert.Equal(3, dropped);
        Assert.Equal(["… 3 earlier lines were no longer in the output buffer"], result.Details![0].Lines);
    }

    [Theory]
    [InlineData(false, true, "; stdout may be buffered: is application/run/flush_stdout_on_print.debug off in project.godot?")]
    [InlineData(true, false, "")]
    [InlineData(false, false, "")]
    public void AMissingStdoutMarkerPointsAtBuffering(bool stdoutArrived, bool stderrArrived, string hint) =>
        Assert.Equal(
            "the step 2 end marker reached stdout and stderr not both within 10 s" + hint,
            ScratchRun.MarkerTimeout("step 2", TimeSpan.FromSeconds(10), stdoutArrived, stderrArrived)
        );

    [Fact]
    public void TheCeilingBudgetsEachStepThePaceAfterAndTheMarkers() =>
        Assert.Equal(TimeSpan.FromSeconds((3 * 10.5) + 0.5 + 10), ScratchRun.Ceiling([0.5, 0.5, 0.5], 1, null));

    [Fact]
    public void TheCeilingSumsEachStepsOwnPaceAndWaitsTheLastStepsPaceAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 40 + 30 + 10), ScratchRun.Ceiling([0.5, 30], 1, null));
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 40 + 10.5 + 0.5 + 10), ScratchRun.Ceiling([0.5, 30, 0.5], 1, null));
    }

    [Fact]
    public void AtATimeScaleTheCeilingDividesTheStepPacesButNotThePaceAfter() =>
        Assert.Equal(TimeSpan.FromSeconds(72), ScratchRun.Ceiling([8, 32], 4, null));

    [Fact]
    public void TheCeilingCountsAnExitHoldInPlaceOfTheLastStepsPaceAfterAtEachTimeScale()
    {
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 40 + 2 + 10), ScratchRun.Ceiling([0.5, 30], 1, 2));
        Assert.Equal(TimeSpan.FromSeconds(12 + 18 + 2 + 10), ScratchRun.Ceiling([8, 32], 4, 2));
        Assert.Equal(TimeSpan.FromSeconds(14 + 2 + 10), ScratchRun.Ceiling([2], 0.5, 1));
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 5 + 10), ScratchRun.Ceiling([0.5], 1, 5));
    }

    [Fact]
    public void AWatchLeftWithoutAWindowSamplesThePlayedStepsPacesAndTheWaitAfterInGameMs()
    {
        ScratchWalk held = new([new(0.5, null), new(10, null), new(2, null)], [0, 2], 1) { ExitHold = 1 };
        ScratchWalk paced = new([new(0.5, null), new(2, null)], [0, 1], 1);
        ScratchWalk scaled = new([new(8, null), new(32, null)], [0, 1], 4);

        Assert.Equal(new WatchWindow(GameMs: 500 + 2000 + 1000), held.DefaultWatchWindow);
        Assert.Equal(new WatchWindow(GameMs: 500 + 2000 + 2000), paced.DefaultWatchWindow);
        Assert.Equal(new WatchWindow(GameMs: 120000), scaled.DefaultWatchWindow);
    }

    [Fact]
    public void AtTimeScaleOneTheWaitAfterTheLastStepIsTheExitHoldAndTheKillReasonNamesIt()
    {
        ScratchWalk walk = new([new(0.5, null), new(10, null)], [0, 1], 1) { ExitHold = 2 };

        Assert.Equal(2000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 20 + 2 + 10), walk.Ceiling);
        Assert.Equal(
            "the scene passed its ceiling of 42.5 s of load-adjusted time (each of its 2 steps' pace + 10 s, the exit hold of 2 s, "
                + "and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AScaledWalksExitHoldIsHeldInWallTime()
    {
        ScratchWalk walk = new([new(8, null), new(32, null)], [0, 1], 4) { ExitHold = 2 };

        Assert.Equal(8000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(42), walk.Ceiling);
        Assert.Equal(
            "the scene passed its ceiling of 42 s of load-adjusted time (each of its 2 steps' pace at time scale 4 + 10 s, the exit "
                + "hold of 2 s in wall time, and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AtAHalfTimeScaleTheExitHoldIsHeldInGameTime()
    {
        ScratchWalk walk = new([new(2, null)], [0], 0.5) { ExitHold = 1 };

        Assert.Equal(1000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(26), walk.Ceiling);
    }

    [Fact]
    public void AnExitHoldLongerThanTheLastStepsPaceIsWaitedWhole()
    {
        ScratchWalk walk = new([new(0.5, null), new(2, null)], [0, 1], 1) { ExitHold = 5 };

        Assert.Equal(5000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 12 + 5 + 10), walk.Ceiling);
    }

    [Fact]
    public void AScaledWalksKillReasonGivesItsWallFiguresAtItsTimeScaleAndHoldsItsPaceAfterInWallTime()
    {
        ScratchWalk walk = new([new(8, null), new(32, null)], [0, 1], 4);

        Assert.Equal(TimeSpan.FromSeconds(72), walk.Ceiling);
        Assert.Equal(128000, walk.AfterGameMs);
        Assert.Equal(
            "the scene passed its ceiling of 72 s of load-adjusted time (each of its 2 steps' pace at time scale 4 + 10 s, the last "
                + "step's pace once more in wall time, and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AtTimeScaleOneThePaceAfterIsTheLastStepsPace()
    {
        ScratchWalk walk = new([new(0.5, null), new(2, null)], [0, 1], 1);

        Assert.Equal(2000, walk.AfterGameMs);
        Assert.Equal(
            "the scene passed its ceiling of 34.5 s of load-adjusted time (each of its 2 steps' pace + 10 s, the last step's pace "
                + "once more, and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AtAHalfTimeScaleThePaceAfterKeepsItsPaceInGameTime()
    {
        ScratchWalk walk = new([new(2, null)], [0], 0.5);

        Assert.Equal(2000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(28), walk.Ceiling);
        Assert.Equal(
            "the scene passed its ceiling of 28 s of load-adjusted time (its 1 step's pace at time scale 0.5 + 10 s, the last "
                + "step's pace once more, and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AWaitIsReleasedAtItsGameTimeAtTheScenesTimeScalePlusTheStepAllowance()
    {
        Assert.Equal(TimeSpan.FromSeconds(50), ScratchRun.Release(20000, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(15), ScratchRun.Release(20000, 4));
        Assert.Equal(TimeSpan.FromSeconds(30), ScratchRun.Release(20000, 1));
    }

    [Fact]
    public void AScaledScenesGameLaunchesWithItsTimeScaleAndANormalOneAsBefore()
    {
        Assert.Equal(["--headless", "--time-scale", "4"], ScratchRun.EngineArgs(4, [], windowed: false));
        Assert.Equal(["--headless", "--time-scale", "2.5"], ScratchRun.EngineArgs(2.5, [], windowed: false));
        Assert.Equal(["--headless"], ScratchRun.EngineArgs(1, [], windowed: false));
    }

    [Fact]
    public void AScenesOwnEngineArgsComeAfterItsHeadlessTimeScale()
    {
        Assert.Equal(["--headless", "--time-scale", "4", "--fixed-fps", "30"], ScratchRun.EngineArgs(4, ["--fixed-fps", "30"], windowed: false));
        Assert.Equal(["--headless", "--fixed-fps", "30"], ScratchRun.EngineArgs(1, ["--fixed-fps", "30"], windowed: false));
    }

    [Fact]
    public void TheRunsResultNamesItsTimeScaleOnlyWhenItIsNotOne()
    {
        ScratchSceneResult scene = ScratchVerdict.Judge(Seen(Step(0)), Plain);

        JsonObject scaled = JsonNode
            .Parse(JsonSerializer.Serialize(ScratchVerdict.Summarise([scene], PrepResult.Skipped, 4), ToolJson.Options))!
            .AsObject();
        JsonObject normal = JsonNode
            .Parse(JsonSerializer.Serialize(ScratchVerdict.Summarise([scene], PrepResult.Skipped, 1), ToolJson.Options))!
            .AsObject();

        Assert.Equal(4, scaled["timeScale"]!.GetValue<double>());
        Assert.False(normal.ContainsKey("timeScale"));
        Assert.False(scaled["scenes"]![0]!.AsObject().ContainsKey("timeScale"));
    }

    [Fact]
    public void ASceneCarriesItsExitHoldOnlyWhenTheProfileSetsOne()
    {
        JsonObject held = Json(ScratchVerdict.Judge(Seen(Step(0)) with { ExitHold = 2 }, Plain));
        JsonObject plain = Json(ScratchVerdict.Judge(Seen(Step(0)), Plain));

        Assert.Equal(2, held["exitHold"]!.GetValue<double>());
        Assert.False(plain.ContainsKey("exitHold"), plain.ToJsonString());
    }

    [Fact]
    public void ASceneThatRanAtAnotherScaleThanTheCallsCarriesItRefusedOrKilledToo()
    {
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0)) with { TimeScale = 1 }, Plain);
        ScratchSceneResult refused = ScratchVerdict.Judge(Seen() with { TimeScale = 1, Refusal = "the scene root lacks GetStatus" }, Plain);
        ScratchSceneResult killed = ScratchVerdict.Judge(
            Seen(Step(0)) with
            {
                TimeScale = 1,
                Kill = new ScratchFailure(0, "step0", "ceiling", ""),
            },
            Plain
        );

        Assert.Equal(1, Json(green)["timeScale"]!.GetValue<double>());
        Assert.Equal((1.0, 1.0), (refused.TimeScale, killed.TimeScale));
    }

    [Fact]
    public void EachStepTakesItsPaceByIndexElseByNameElseTheScenes()
    {
        ScratchScenePlan scene = PacedScene(new() { ["1"] = new ScratchPace(30, "the fight plays long"), ["close"] = new ScratchPace(2, null) });

        (ScratchPace[] paces, string? error, _) = ScratchRun.StepPaces(scene, ["open", "fight", "close", "rest"]);

        Assert.Null(error);
        Assert.Equal([0.5, 30, 2, 0.5], paces.Select(pace => pace.Seconds));
        Assert.Equal(["the scene settles", "the fight plays long", null, "the scene settles"], paces.Select(pace => pace.Reason));
    }

    [Fact]
    public void AKeyWithALeadingZeroIsAStepNameNotAnIndex()
    {
        ScratchScenePlan scene = PacedScene(new() { ["03"] = new ScratchPace(3, null) });

        (ScratchPace[] paces, string? error, _) = ScratchRun.StepPaces(scene, ["a", "b", "c", "d", "03"]);

        Assert.Null(error);
        Assert.Equal([0.5, 0.5, 0.5, 0.5, 3], paces.Select(pace => pace.Seconds));
    }

    [Fact]
    public void ARedStepWithItsOwnPaceCarriesThatStepsReasonAndAnotherStepTheScenes()
    {
        ScratchObservation seen = Seen(Step(0), Step(1) with { CallError = "x" }) with { StepPaceReasons = ["the scene settles", "the batch"] };
        ScratchObservation first = Seen(Step(0) with { CallError = "x" }, Step(1)) with { StepPaceReasons = ["the scene settles", "the batch"] };
        ScratchRules rules = Plain with { PaceReason = "the scene settles" };

        Assert.Equal("the batch", ScratchVerdict.Judge(seen, rules).PaceReason);
        Assert.Equal("the scene settles", ScratchVerdict.Judge(first, rules).PaceReason);
    }

    [Fact]
    public void AKilledStepWithItsOwnPaceCarriesThatStepsReasonAndABareOneNone()
    {
        ScratchObservation seen = Seen(Step(0), Step(1)) with
        {
            StepPaceReasons = ["the scene settles", "the batch"],
            Kill = new ScratchFailure(1, "step1", "ceiling", ""),
        };
        ScratchRules rules = Plain with { PaceReason = "the scene settles" };

        Assert.Equal("the batch", ScratchVerdict.Judge(seen, rules).PaceReason);
        Assert.Null(ScratchVerdict.Judge(seen with { StepPaceReasons = ["the scene settles", null] }, rules).PaceReason);
    }

    [Fact]
    public void ARedSceneOutsideAnyStepCarriesTheScenesReason()
    {
        ScratchObservation seen = Seen(Step(0)) with { StepPaceReasons = ["the batch"], ExitCode = 1 };

        Assert.Equal("the scene settles", ScratchVerdict.Judge(seen, Plain with { PaceReason = "the scene settles" }).PaceReason);
    }

    [Fact]
    public void AnIndexKeyIsTriedBeforeANameAndANameOnlyPastTheLastIndex()
    {
        ScratchScenePlan scene = PacedScene(new() { ["0"] = new ScratchPace(3, null), ["7"] = new ScratchPace(4, null) });

        (ScratchPace[] paces, string? error, _) = ScratchRun.StepPaces(scene, ["first", "0", "7"]);

        Assert.Null(error);
        Assert.Equal([3, 0.5, 4], paces.Select(pace => pace.Seconds));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("shut")]
    [InlineData("-1")]
    public void AStalePaceKeyIsNamedInTheScenesWarningsAndThePlayGoesOn(string key)
    {
        ScratchScenePlan scene = PacedScene(new() { ["open"] = new ScratchPace(1, null), [key] = new ScratchPace(2, null) });

        (ScratchPace[] paces, string? error, IReadOnlyList<string> warnings) = ScratchRun.StepPaces(scene, ["open", "hit", "hit"]);

        Assert.Null(error);
        Assert.Equal([1, 0.5, 0.5], paces.Select(pace => pace.Seconds));
        Assert.Equal([$"scratch.pace.steps key \"{key}\" matches no step of Paced, so it sets no step's pace"], warnings);
    }

    [Fact]
    public void AStepPaceKeyNamingTwoStepsIsRefused()
    {
        ScratchScenePlan scene = PacedScene(new() { ["open"] = new ScratchPace(1, null), ["hit"] = new ScratchPace(2, null) });

        (_, string? error, IReadOnlyList<string> warnings) = ScratchRun.StepPaces(scene, ["open", "hit", "hit"]);

        Assert.Equal("pace.steps key \"hit\" of Paced names 2 steps (1, 2); key it by index", error);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AStepPaceKeyOnASceneWithoutStepsIsAWarning() =>
        Assert.Equal(
            ["scratch.pace.steps key \"0\" matches no step of Paced, so it sets no step's pace"],
            ScratchRun.StepPaces(PacedScene(new() { ["0"] = new ScratchPace(1, null) }), []).Warnings
        );

    [Fact]
    public void ASceneCarriesItsWarningsAndOmitsThemWhenItHasNone()
    {
        ScratchSceneResult warned = ScratchVerdict.Judge(Seen(Step(0)) with { Warnings = ["a stale key"] }, Plain);
        ScratchSceneResult plain = ScratchVerdict.Judge(Seen(Step(0)), Plain);

        Assert.Equal(ScratchVerdict.Green, warned.Verdict);
        Assert.Equal(["a stale key"], Json(warned)["warnings"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(Json(plain).ContainsKey("warnings"));
    }

    [Fact]
    public void TwoStepPaceKeysForOneStepAreRefused() =>
        Assert.Equal(
            "pace.steps keys \"0\" and \"open\" of Paced both name step 0; keep one",
            ScratchRun.StepPaces(PacedScene(new() { ["0"] = new ScratchPace(1, null), ["open"] = new ScratchPace(2, null) }), ["open", "hit"]).Error
        );

    [Theory]
    [InlineData("[3]", new[] { 3 })]
    [InlineData("[\"last\"]", new[] { 3 })]
    [InlineData("[\"3\"]", new[] { 3 })]
    [InlineData("[\"1-2\"]", new[] { 1, 2 })]
    [InlineData("[3, \"calm\"]", new[] { 0, 3 })]
    [InlineData("[\"0-1\", 1]", new[] { 0, 1 })]
    [InlineData("[\"2-3\", \"0-2\"]", new[] { 0, 1, 2, 3 })]
    public void StepsPlayTheUnionOfTheirTokensInAscendingOrderEachOnce(string steps, int[] played)
    {
        (int[] indices, string? error) = ScratchRun.ResolveSteps(Tokens(steps), "TwoRed", TwoRed);

        Assert.Null(error);
        Assert.Equal(played, indices);
    }

    [Theory]
    [InlineData("[\"03\"]", "options.steps token \"03\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"+3\"]", "options.steps token \"+3\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"4\"]", "options.steps token \"4\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[4]", "options.steps token 4 of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"nope\"]", "options.steps token \"nope\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"2-4\"]", "options.steps token \"2-4\" of TwoRed reaches past its last step; " + TwoRedSteps)]
    [InlineData("[\"0-99999999999\"]", "options.steps token \"0-99999999999\" of TwoRed reaches past its last step; " + TwoRedSteps)]
    [InlineData("[0, \"nope\"]", "options.steps token \"nope\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"1-2\\n\"]", "options.steps token \"1-2\n\" of TwoRed matches no step; " + TwoRedSteps)]
    [InlineData("[\"١-٢\"]", "options.steps token \"١-٢\" of TwoRed matches no step; " + TwoRedSteps)]
    public void AStepsTokenThatNamesNoStepRefusesTheSceneListingItsSteps(string steps, string refusal)
    {
        (int[] indices, string? error) = ScratchRun.ResolveSteps(Tokens(steps), "TwoRed", TwoRed);

        Assert.Equal(refusal, error);
        Assert.Empty(indices);
    }

    [Theory]
    [InlineData("hit")]
    [InlineData("1-2")]
    public void AStepsNameThatNamesTwoStepsIsRefusedEvenWhenItReadsAsARange(string name) =>
        Assert.Equal(
            $"options.steps token \"{name}\" of Paced names 2 steps (1, 2); key it by index",
            ScratchRun.ResolveSteps(Tokens($"[\"{name}\"]"), "Paced", ["open", name, name]).Error
        );

    [Fact]
    public void AStepsTokenOnASceneWithoutStepsIsRefused() =>
        Assert.Equal("options.steps token 0 of Paced matches no step; it has no steps", ScratchRun.ResolveSteps(Tokens("[0]"), "Paced", []).Error);

    [Fact]
    public void AStepsStringIsAnIndexFirstThenANameThenARange()
    {
        ScratchStepNames names = ["first", "0", "1-2", "7", "+3"];

        Assert.Equal([0], ScratchRun.ResolveSteps(Tokens("""["0"]"""), "Odd", names).Indices);
        Assert.Equal([3], ScratchRun.ResolveSteps(Tokens("""["7"]"""), "Odd", names).Indices);
        Assert.Equal([2], ScratchRun.ResolveSteps(Tokens("""["1-2"]"""), "Odd", names).Indices);
        Assert.Equal([4], ScratchRun.ResolveSteps(Tokens("""["+3"]"""), "Odd", names).Indices);
        Assert.Equal([0, 1, 2, 3], ScratchRun.ResolveSteps(Tokens("""["0-3"]"""), "Odd", names).Indices);
    }

    [Fact]
    public void ASelectionsCeilingPaceAfterAndKillReasonCountOnlyThePlayedSteps()
    {
        ScratchPace[] paces = [new(0.5, null), new(30, null), new(2, null), new(0.5, null)];

        ScratchWalk walk = new(paces, [0, 2], 1);

        Assert.Equal(TimeSpan.FromSeconds(10.5 + 12 + 2 + 10), walk.Ceiling);
        Assert.Equal(2000, walk.AfterGameMs);
        Assert.Equal(
            "the scene passed its ceiling of 34.5 s of load-adjusted time (each of its 2 steps' pace + 10 s, the last step's pace once "
                + "more, and 10 s for the markers), so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AOneStepWalksKillReasonNamesItsOneStep() =>
        Assert.Equal(
            "the scene passed its ceiling of 21 s of load-adjusted time (its 1 step's pace + 10 s, the last step's pace once more, and "
                + "10 s for the markers), so its game was stopped",
            new ScratchWalk([new(30, null), new(0.5, null)], [1], 1).CeilingReason(string.Empty)
        );

    [Fact]
    public void ASelectionsExitHoldFollowsItsLastPlayedStep()
    {
        ScratchPace[] paces = [new(0.5, null), new(30, null), new(2, null), new(0.5, null)];

        ScratchWalk walk = new(paces, [0, 2], 1) { ExitHold = 1 };

        Assert.Equal(1000, walk.AfterGameMs);
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 12 + 1 + 10), walk.Ceiling);
    }

    [Fact]
    public void AGivenCeilingIsKeptBesideAnExitHold()
    {
        ScratchWalk walk = new([new(0.5, null), new(30, null)], [0, 1], 1)
        {
            ExitHold = 2,
            Given = new ScratchCeiling(90.5, ScratchCeiling.OptionsSource),
        };

        Assert.Equal(TimeSpan.FromSeconds(90.5), walk.Ceiling);
        Assert.Equal(2000, walk.AfterGameMs);
    }

    [Fact]
    public void ASceneCeilingGivenByTheCallReplacesTheDerivedOne()
    {
        ScratchWalk walk = new([new(0.5, null), new(30, null)], [0, 1], 1);

        Assert.Equal(TimeSpan.FromSeconds(90.5), walk.Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(5), (walk with { Given = new(5, ScratchCeiling.OptionsSource) }).Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(500), (walk with { Given = new(500, ScratchCeiling.ProfileSource) }).Ceiling);
    }

    [Fact]
    public void AGivenCeilingsReasonNamesItsSource()
    {
        ScratchWalk walk = new([new(0.5, null), new(30, null)], [0, 1], 1) { Given = new(5, ScratchCeiling.OptionsSource) };

        Assert.Equal(
            "the scene passed its ceiling of 5 s of load-adjusted time, set by options.ceilings, so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
        Assert.Equal(
            "the scene passed its ceiling of 5 s of load-adjusted time, set by the profile's scratch.ceilings, so its game was stopped",
            (walk with { Given = new(5, ScratchCeiling.ProfileSource) }).CeilingReason(string.Empty)
        );
        Assert.Equal(
            "the scene passed its ceiling of 2.5 s of load-adjusted time, set by options.ceilings, so its game was stopped",
            (walk with { Given = new(2.5, ScratchCeiling.OptionsSource) }).CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AGivenCeilingsReasonKeepsItsDigits()
    {
        ScratchWalk walk = new([new(0.5, null), new(30, null)], [0, 1], 1) { Given = new(0.25, ScratchCeiling.OptionsSource) };

        Assert.Equal(TimeSpan.FromSeconds(0.25), walk.Ceiling);
        Assert.Equal(
            "the scene passed its ceiling of 0.25 s of load-adjusted time, set by options.ceilings, so its game was stopped",
            walk.CeilingReason(string.Empty)
        );
    }

    [Fact]
    public void AGivenCeilingHoldsForAPartialRun()
    {
        ScratchWalk paced = new([new(0.5, null), new(30, null), new(2, null)], [0, 2], 1);

        Assert.Equal(TimeSpan.FromSeconds(34.5), paced.Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(7.5), (paced with { Given = new(7.5, ScratchCeiling.ProfileSource) }).Ceiling);
    }

    [Fact]
    public void AKnownScenePlayedInPartStaysKnownAndNeverKnownNowGreen()
    {
        ScratchObservation partial = new("Combat", "probe.scratch-Combat", 0.5, 6)
        {
            Steps = [Step(2)],
            ExitCode = 0,
            Selected = [2],
        };

        ScratchSceneResult result = ScratchVerdict.Judge(partial, Plain with { Known = "step 5 crashes until the pool fix" });
        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped, timeScale: 1);

        Assert.Equal(ScratchVerdict.KnownRed, result.Verdict);
        Assert.Equal("step 5 crashes until the pool fix", result.Known);
        Assert.True(run.Passed);
        Assert.Equal((0, 1), (run.Red, run.Known));
    }

    [Fact]
    public void AJudgedSceneCarriesItsSetupStepsAndOmitsThemWhenItHasNone()
    {
        ScratchObservation seen = new("Combat", "probe.scratch-Combat", 0.5, 8)
        {
            Steps = [Step(0), Step(5), Step(6), Step(7)],
            ExitCode = 0,
            Selected = [5, 6, 7],
            Setup = [0],
        };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain);
        JsonObject json = Json(result);
        JsonObject plain = Json(ScratchVerdict.Judge(Seen(Step(0)), Plain));

        Assert.Equal([5, 6, 7], result.Selected);
        Assert.Equal([0], result.Setup);
        Assert.Equal([5, 6, 7], json["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.Equal([0], json["setup"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.False(plain.ContainsKey("setup"), plain.ToJsonString());
    }

    [Fact]
    public void AKnownScenePlayedWhollyThroughASelectionIsKnownNowGreen()
    {
        ScratchObservation whole = new("Combat", "probe.scratch-Combat", 0.5, 2)
        {
            Steps = [Step(0), Step(1)],
            ExitCode = 0,
            Selected = [0, 1],
        };

        Assert.Equal(ScratchVerdict.KnownNowGreen, ScratchVerdict.Judge(whole, Plain with { Known = "flaky" }).Verdict);
    }

    [Fact]
    public void ASelectionRidesOnTheSceneAsSelectedAndNoSelectionLeavesItOut()
    {
        ScratchObservation partial = new("Scene", "probe.scratch-Scene", 0.5, 4)
        {
            Steps = [Step(3)],
            ExitCode = 0,
            Selected = [3],
        };

        JsonObject selected = Json(ScratchVerdict.Judge(partial, Plain));
        JsonObject whole = Json(ScratchVerdict.Judge(Seen(Step(0)), Plain));

        Assert.Equal("green", selected["verdict"]!.GetValue<string>());
        Assert.Equal((1, 4), (selected["steps"]!["played"]!.GetValue<int>(), selected["steps"]!["total"]!.GetValue<int>()));
        Assert.Equal([3], selected["selected"]!.AsArray().Select(index => index!.GetValue<int>()));
        Assert.False(whole.ContainsKey("selected"));
    }

    [Fact]
    public void AStepsRefusalMarksAHiddenStepAndKeepsItsIndex() =>
        Assert.Equal(
            "options.steps token \"nope\" of Base matches no step; its steps are 0 (hidden) fight setup, 1 first view",
            ScratchRun.ResolveSteps(Tokens("""["nope"]"""), "Base", new ScratchStepNames(["fight setup", "first view"], new HashSet<int> { 0 })).Error
        );

    [Fact]
    public void AHiddenStepStaysPlayableByIndexAndName()
    {
        ScratchStepNames names = new(["fight setup", "first view"], new HashSet<int> { 0 });

        Assert.Equal([0, 1], ScratchRun.ResolveSteps(Tokens("""[0, "first view"]"""), "Base", names).Indices);
        Assert.Equal([0], ScratchRun.ResolveSteps(Tokens("""["fight setup"]"""), "Base", names).Indices);
    }

    [Fact]
    public void ASceneRunInPartListsEveryStepByIndexAndName()
    {
        ScratchObservation partial = new("Scene", "probe.scratch-Scene", 0.5, 3)
        {
            Steps = [Step(1)],
            ExitCode = 0,
            Selected = [1],
            StepNames = ["open", "fight", "close"],
        };

        JsonObject json = Json(ScratchVerdict.Judge(partial, Plain));

        Assert.Equal(["0 open", "1 fight", "2 close"], json["stepList"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    [Fact]
    public void AWholeRunListsItsStepsOnlyWhenOneIsHidden()
    {
        ScratchObservation hidden = Seen(Step(0), Step(1)) with { StepNames = new(["fight setup", "first view"], new HashSet<int> { 0 }) };
        ScratchObservation shown = Seen(Step(0), Step(1)) with { StepNames = ["fight setup", "first view"] };

        JsonObject withHidden = Json(ScratchVerdict.Judge(hidden, Plain));
        JsonObject without = Json(ScratchVerdict.Judge(shown, Plain));

        Assert.Equal(["0 (hidden) fight setup", "1 first view"], withHidden["stepList"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.False(without.ContainsKey("stepList"), without.ToJsonString());
    }

    private const string TwoRedSteps = "its steps are 0 calm, 1 first, 2 second, 3 last";

    private static readonly ScratchStepNames TwoRed = ["calm", "first", "second", "last"];

    private static IReadOnlyList<ScratchStepToken> Tokens(string json) => ScratchTools.StepTokens(JsonSerializer.Deserialize<JsonElement[]>(json))!;

    private static ScratchScenePlan PacedScene(Dictionary<string, ScratchPace> stepPaces) =>
        new("Paced", "res://scratch/Paced.tscn", 0.5, [])
        {
            PaceReason = "the scene settles",
            StepPaces = stepPaces,
            TimeScale = 1,
            EngineArgs = [],
        };

    [Fact]
    public void TwoRedStepsAreBothFailuresWithFailedAtTheFirstAndEachDetailsItsError()
    {
        ScratchObservation seen = Seen(
            Step(0),
            Step(1) with
            {
                Errors = [Error("first failure", "res://s.gd", 4)],
            },
            Step(2) with
            {
                Lines = ["ERROR: second failure"],
            },
            Step(3)
        );

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain);

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal((1, "step1"), (result.FailedAt!.Index, result.FailedAt.Name));
        Assert.Equal(
            [new ScratchStepFailure(1, "step1", "first failure (res://s.gd:4)"), new ScratchStepFailure(2, "step2", "ERROR: second failure")],
            result.Failures
        );
        Assert.Equal(new ScratchStepCount(4, 4), result.Steps);
        Assert.Equal([null, "first failure (res://s.gd:4)", "ERROR: second failure", null], result.Details!.Select(step => step.Error));
    }

    [Fact]
    public void ARedStepIsTheOnlyFailureAndAGreenSceneHasNoneAndNeitherWritesNulls()
    {
        ScratchSceneResult red = ScratchVerdict.Judge(Seen(Step(0), Step(1) with { CallError = "PlayStep failed: gone" }), Plain);
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0)), Plain with { Details = ScratchDetails.Full });

        Assert.Equal([new ScratchStepFailure(1, "step1", "PlayStep failed: gone")], red.Failures);
        Assert.Null(green.Failures);
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(green, ToolJson.Options))!.AsObject();
        Assert.False(json.ContainsKey("failures"), json.ToJsonString());
        Assert.False(json["details"]![0]!.AsObject().ContainsKey("error"), json.ToJsonString());
    }

    [Theory]
    [InlineData("green", false, false)]
    [InlineData("green", true, false)]
    [InlineData("feed", false, true)]
    [InlineData("feed", true, false)]
    [InlineData("line", false, true)]
    [InlineData("line", true, false)]
    [InlineData("call", false, true)]
    [InlineData("call", true, true)]
    [InlineData("call and feed", false, true)]
    [InlineData("call and feed", true, true)]
    [InlineData("call and line", true, true)]
    public void KeepGoingPlaysPastAPushedErrorOrAMatchingLineButNeverPastACallError(string failure, bool keepGoing, bool stops)
    {
        const string CallError = "PlayStep failed: boom";
        ScratchStep step = failure switch
        {
            "feed" => Step(0) with { Errors = [Error("boom", "res://s.gd", 1)] },
            "line" => Step(0) with { Lines = ["ERROR: boom"] },
            "call" => Step(0) with { CallError = "wait_for 500 game ms failed: gone" },
            "call and feed" => Step(0) with { CallError = CallError, Errors = [Error("boom", "res://s.gd", 1)] },
            "call and line" => Step(0) with { CallError = CallError, Lines = ["ERROR: boom"] },
            _ => Step(0),
        };

        Assert.Equal(stops, ScratchRun.StopsAfter(step, Default, keepGoing));
    }

    [Fact]
    public void AKilledStepIsTheLastFailureAndFailedAtNamesTheKill()
    {
        ScratchObservation seen = Seen(
            Step(0),
            Step(1) with
            {
                Errors = [Error("first failure", "res://s.gd", 4)],
            },
            Step(2) with
            {
                Lines = ["ERROR: second failure"],
            }
        ) with
        {
            Total = 4,
            Kill = new ScratchFailure(3, "step3", "ceiling", ""),
        };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain);

        Assert.Equal(ScratchVerdict.Killed, result.Verdict);
        Assert.Equal((3, "step3", "ceiling"), (result.FailedAt!.Index, result.FailedAt.Name, result.FailedAt.Error));
        Assert.Equal(
            [
                new ScratchStepFailure(1, "step1", "first failure (res://s.gd:4)"),
                new ScratchStepFailure(2, "step2", "ERROR: second failure"),
                new ScratchStepFailure(3, "step3", "ceiling"),
            ],
            result.Failures
        );
    }

    [Fact]
    public void AKillOutsideAnyStepIsNoFailureAndAFailureWritesItsIndex()
    {
        ScratchSceneResult inPace = ScratchVerdict.Judge(Seen(Step(0)) with { Kill = new ScratchFailure(-1, "", "ceiling", "") }, Plain);
        ScratchSceneResult red = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["ERROR: boom"] }), Plain);

        Assert.Null(inPace.Failures);
        JsonObject failure = JsonNode.Parse(JsonSerializer.Serialize(red, ToolJson.Options))!["failures"]![0]!.AsObject();
        Assert.Equal((0, "step0"), (failure["index"]!.GetValue<int>(), failure["name"]!.GetValue<string>()));
        Assert.False(failure.ContainsKey("step"), failure.ToJsonString());
    }

    [Fact]
    public void TwoHundredIdenticalErrorsFoldIntoOneEntryWithTheirCount()
    {
        ScratchStep flooding = Step(0) with { Errors = [.. Enumerable.Repeat(Error("same every frame", "res://s.gd", 3), 200)] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(flooding), Plain))["details"]![0]!.AsObject();

        JsonObject error = Assert.Single(step["errors"]!.AsArray())!.AsObject();
        Assert.Equal(("same every frame", 200), (error["message"]!.GetValue<string>(), error["count"]!.GetValue<int>()));
        Assert.False(step.ContainsKey("errorsOmitted"), step.ToJsonString());
    }

    [Fact]
    public void AHundredThousandCharacterMessageIsCutInEveryCopy()
    {
        string message = new('m', 100_000);

        JsonObject json = Json(
            ScratchVerdict.Judge(Seen(Step(0) with { Errors = [Error(message, "", 0)] }), Plain with { Details = ScratchDetails.Full })
        );

        JsonNode step = json["details"]![0]!;
        string copy = OutputBuffer.Cut(message, 500);
        Assert.Equal(OutputBuffer.Cut(message, 2000), step["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(
            [copy, copy, copy],
            [step["error"]!.GetValue<string>(), json["failedAt"]!["error"]!.GetValue<string>(), json["failures"]![0]!["error"]!.GetValue<string>()]
        );
    }

    [Fact]
    public void AFailuresErrorIsCutAtTwoHundredWithoutDetailsAndFiveHundredWith()
    {
        string message = new('m', 100_000);
        ScratchStep failing = Step(0) with { Errors = [Error(message, "res://s.gd", 3)] };

        JsonObject compact = Json(ScratchVerdict.Judge(Seen(failing), Plain));
        JsonObject full = Json(ScratchVerdict.Judge(Seen(failing), Plain with { Details = ScratchDetails.Full }));

        Assert.Equal($"{OutputBuffer.Cut(message, 200)} (res://s.gd:3)", compact["failures"]![0]!["error"]!.GetValue<string>());
        Assert.Equal($"{OutputBuffer.Cut(message, 500)} (res://s.gd:3)", full["failures"]![0]!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 3)]
    public void FiftyDistinctErrorsListTwentyWithDetailsOrThreeWithoutAndCountTheRest(bool details, int listed)
    {
        ScratchStep noisy = Step(0) with { Errors = [.. Enumerable.Range(0, 50).Select(n => Error($"error {n}", "res://s.gd", n + 1))] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(noisy), Plain with { Details = details ? ScratchDetails.Full : ScratchDetails.Default }))[
            "details"
        ]![0]!.AsObject();

        JsonArray errors = step["errors"]!.AsArray();
        Assert.Equal(listed, errors.Count);
        Assert.Equal(
            ("error 0", $"error {listed - 1}"),
            (errors[0]!["message"]!.GetValue<string>(), errors[listed - 1]!["message"]!.GetValue<string>())
        );
        Assert.All(errors, error => Assert.False(error!.AsObject().ContainsKey("count"), error.ToJsonString()));
        Assert.Equal(50 - listed, step["errorsOmitted"]!.GetValue<int>());
    }

    [Fact]
    public void ATwentyOneStepRedScenePlayedOnWhereEveryStepFailsLongSerialisesUnderFortyThousandCharactersWithoutDetails()
    {
        string tail = new('t', 20_000);
        ScratchStep[] steps =
        [
            .. Enumerable
                .Range(0, 21)
                .Select(index =>
                    Step(index) with
                    {
                        Status = $"step {index} played",
                        Errors = [.. Enumerable.Range(0, 5).Select(n => Error($"step {index} error {n} {tail}", "res://s.gd", n + 1))],
                        Lines = [.. Enumerable.Range(0, 10).Select(n => $"[scratch] step {index} note {n} {tail}")],
                    }
                ),
        ];

        string json = JsonSerializer.Serialize(ScratchVerdict.Judge(Seen(steps), Plain), ToolJson.Options);

        Assert.True(json.Length < 40_000, $"{json.Length} characters");
    }

    [Fact]
    public void ARedScenePlayedOnListsItsGreenStepsByTheirStatusLineOnly()
    {
        ScratchObservation seen = Seen(
            Step(0) with
            {
                Status = "calm",
                Lines = ["[scratch] calm"],
            },
            Step(1) with
            {
                Status = "fails",
                Errors = [Error("boom", "res://s.gd", 4)],
                Lines = ["[scratch] fails"],
            },
            Step(2) with
            {
                Status = "after",
                Lines = ["[scratch] after"],
            }
        );

        JsonArray details = Json(ScratchVerdict.Judge(seen, Plain))["details"]!.AsArray();

        string[] statusLine = ["index", "name", "ok", "gameMs", "status"];
        Assert.Equal(statusLine, details[0]!.AsObject().Select(field => field.Key));
        Assert.Equal(statusLine, details[2]!.AsObject().Select(field => field.Key));
        Assert.Equal([.. statusLine, "errors", "lines", "error"], details[1]!.AsObject().Select(field => field.Key));
    }

    [Fact]
    public void NotesAppearOnlyOnTheFailingStepUnlessDetailsAsksForEveryStep()
    {
        ScratchObservation seen = Seen(
            Step(0) with
            {
                Lines = ["[scratch] calm"],
            },
            Step(1) with
            {
                Lines = ["[scratch] fails", "ERROR: boom"],
            },
            Step(2) with
            {
                Lines = ["[scratch] after"],
            }
        );

        ScratchSceneResult plain = ScratchVerdict.Judge(seen, Plain);
        ScratchSceneResult detailed = ScratchVerdict.Judge(seen, Plain with { Details = ScratchDetails.Full });

        Assert.Equal([null, ["[scratch] fails", "ERROR: boom"], null], plain.Details!.Select(step => step.Lines));
        Assert.Equal(
            [
                ["[scratch] calm"],
                ["[scratch] fails", "ERROR: boom"],
                ["[scratch] after"],
            ],
            detailed.Details!.Select(step => step.Lines)
        );
    }

    [Fact]
    public void AFortyNineStepRedSceneWithAFloodingStepSerialisesUnderSixtyThousandCharacters()
    {
        string flood = new('f', 5000);
        ScratchStep flooding = Step(10) with
        {
            Status = "flooding\n" + flood,
            Errors = [.. Enumerable.Range(0, 500).Select(n => Error($"{n % 50} {flood}", "res://s.gd", 9))],
            Lines = [.. Enumerable.Range(0, 500).Select(n => $"ERROR: {n} {flood}")],
        };
        ScratchStep[] steps =
        [
            .. Enumerable
                .Range(0, 49)
                .Select(index =>
                    index == 10
                        ? flooding
                        : Step(index) with
                        {
                            Status = $"step {index} played",
                            Lines = [.. Enumerable.Range(0, 30).Select(n => $"[scratch] note {n} {flood}")],
                        }
                ),
        ];

        string json = JsonSerializer.Serialize(ScratchVerdict.Judge(Seen(steps), Plain), ToolJson.Options);

        Assert.True(json.Length < 60_000, $"{json.Length} characters");
    }

    [Theory]
    [InlineData(true, 4000)]
    [InlineData(false, 1500)]
    public void AStepsDetailOverItsBudgetIsCutAndNamesTheBudgetAndTheSessionsLog(bool details, int budget)
    {
        ScratchStep verbose = Step(0) with { Lines = [.. Enumerable.Range(0, 20).Select(n => $"ERROR: {n} {new string('x', 1000)}")] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(verbose), Plain with { Details = details ? ScratchDetails.Full : ScratchDetails.Default }))[
            "details"
        ]![0]!.AsObject();

        JsonArray lines = step["lines"]!.AsArray();
        int detail =
            step["error"]!.ToJsonString(ToolJson.Options).Length
            + step["errors"]!.ToJsonString(ToolJson.Options).Length
            + lines.ToJsonString(ToolJson.Options).Length;
        Assert.InRange(detail, 1, budget);
        Assert.InRange(lines.Count, 1, 19);
        Assert.Equal(OutputBuffer.Cut($"ERROR: 0 {new string('x', 1000)}", 500), lines[0]!.GetValue<string>());
        Assert.Equal($"detail over {budget} characters; get_debug_output probe.scratch-Scene holds the full log", step["cut"]!.GetValue<string>());
    }

    [Fact]
    public void AStatusAndALineOverFiveHundredCharactersAreKeptWholeUnderDetailChars()
    {
        (ScratchStep step, string status, string note) = LongStep();

        JsonObject listed = Json(ScratchVerdict.Judge(Seen(step), Plain with { Details = ScratchDetails.Full, DetailChars = 2000 }))["details"]![
            0
        ]!.AsObject();

        Assert.Equal(status, listed["status"]!.GetValue<string>());
        Assert.Equal([note], listed["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.False(listed.ContainsKey("cut"), listed.ToJsonString());
    }

    [Fact]
    public void AStatusAndItsLinesAreCutAtTheCallsDetailChars()
    {
        (ScratchStep step, string status, string note) = LongStep();

        JsonObject listed = Json(ScratchVerdict.Judge(Seen(step), Plain with { Details = ScratchDetails.Full, DetailChars = 100 }))["details"]![
            0
        ]!.AsObject();

        Assert.Equal(OutputBuffer.Cut(status, 100), listed["status"]!.GetValue<string>());
        Assert.Equal([OutputBuffer.Cut(note, 100)], listed["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    [Fact]
    public void AFailedStepsStatusIsCutAtDetailChars()
    {
        string status = new('s', 1500);
        ScratchStep failing = Step(0) with { Status = status, Lines = ["ERROR: boom"] };

        ScratchSceneResult result = ScratchVerdict.Judge(Seen(failing), Plain with { DetailChars = 2000 });

        Assert.Equal(ScratchVerdict.Red, result.Verdict);
        Assert.Equal(status, result.FailedAt!.Status);
    }

    [Fact]
    public void TheBudgetGrowsByTheCharactersDetailCharsAddsAndTheCutNoteNamesIt()
    {
        ScratchStep verbose = Step(0) with { Lines = [.. Enumerable.Range(0, 20).Select(n => $"ERROR: {n} {new string('x', 1000)}")] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(verbose), Plain with { Details = ScratchDetails.Full, DetailChars = 2000 }))["details"]![
            0
        ]!.AsObject();

        JsonArray lines = step["lines"]!.AsArray();
        Assert.InRange(lines.ToJsonString(ToolJson.Options).Length, ScratchVerdict.MaxStepDetailChars + 1, 5500);
        Assert.Equal($"ERROR: 0 {new string('x', 1000)}", lines[0]!.GetValue<string>());
        Assert.Equal("detail over 5500 characters; get_debug_output probe.scratch-Scene holds the full log", step["cut"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ScratchVerdict.DefaultDetailChars, OutputBuffer.MaxLineLength)]
    [InlineData(2000, 2000)]
    public void ARawLineIsCutAtTheBufferPageCutOrTheCallsLongerDetailCharsAndTheMarksComeFromTheSnapshot(int detailChars, int kept)
    {
        string line = "[scratch] " + new string('n', 1500);

        (OutputPage stdout, OutputPage stderr, LineMark total) = ScratchRun.Bounded(
            new OutputPage(7, ["short", line]),
            new OutputPage(null, []),
            detailChars
        );

        Assert.Equal(["short", OutputBuffer.Cut(line, kept)], stdout.Lines);
        Assert.Equal(7, stdout.FirstLine);
        Assert.Empty(stderr.Lines);
        Assert.Equal(new LineMark(8, 0), total);
    }

    /// <summary>A step whose status and [scratch] note are each 1500 characters past their prefix.</summary>
    private static (ScratchStep Step, string Status, string Note) LongStep()
    {
        string status = new('s', 1500);
        string note = "[scratch] " + new string('n', 1500);
        return (Step(0) with { Status = status, Lines = [note] }, status, note);
    }

    [Fact]
    public void ErrorsTheBudgetDropsAreCountedInErrorsOmitted()
    {
        ScratchStep failing = Step(0) with
        {
            Errors = [.. Enumerable.Range(0, 3).Select(n => Error($"{n} {new string('e', 598)}", "res://s.gd", n + 1))],
        };

        JsonObject listed = Json(ScratchVerdict.Judge(Seen(failing), Plain))["details"]![0]!.AsObject();

        Assert.Single(listed["errors"]!.AsArray());
        Assert.Equal(2, listed["errorsOmitted"]?.GetValue<int>());
    }

    [Fact]
    public void AFailingStepOverItsBudgetWithNothingDroppableCarriesNoCut()
    {
        string file = "res://" + new string('f', 3000) + ".gd";
        ScratchStep failing = Step(0) with { Errors = [Error("boom", file, 7)] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(failing), Plain))["details"]![0]!.AsObject();

        Assert.True(step["error"]!.GetValue<string>().Length > ScratchVerdict.MaxStepDetailCharsCompact, step.ToJsonString());
        Assert.Single(step["errors"]!.AsArray());
        Assert.False(step.ContainsKey("cut"), step.ToJsonString());
    }

    [Fact]
    public void TheFirstErrorsEntrySurvivesTheBudgetWhenTheErrorAloneIsFarOverIt()
    {
        string file = "res://" + new string('f', 3000) + ".gd";
        ScratchStep failing = Step(0) with { Errors = [Error("first", file, 1), Error("second", file, 2), Error("third", file, 3)] };

        JsonObject step = Json(ScratchVerdict.Judge(Seen(failing), Plain))["details"]![0]!.AsObject();

        JsonObject listed = Assert.Single(step["errors"]!.AsArray())!.AsObject();
        Assert.Equal("first", listed["message"]!.GetValue<string>());
        Assert.Equal(2, step["errorsOmitted"]!.GetValue<int>());
        Assert.NotNull(step["cut"]);
    }

    [Fact]
    public void RedOnlyKeepsARedScenesRedStepsWithTheirReadbackAndItsFailures()
    {
        ScratchStep failing = Step(1) with { Status = "note", Errors = [Error("boom", "res://a.gd", 7)], Lines = ["[scratch] before"] };
        JsonObject run = RunJson(
            ScratchVerdict.Judge(Seen(Step(0), failing, Step(2) with { CallError = "gone" }), Plain with { Details = ScratchDetails.Full })
        );
        JsonObject whole = run["scenes"]![0]!.AsObject();

        JsonObject scene = ScratchView.Result(run, redOnly: true)["scenes"]![0]!.AsObject();

        JsonArray steps = scene["details"]!.AsArray();
        Assert.Equal([1, 2], steps.Select(step => step!["index"]!.GetValue<int>()));
        Assert.True(JsonNode.DeepEquals(whole["details"]![1], steps[0]), steps[0]!.ToJsonString());
        Assert.Equal("note", steps[0]!["status"]!.GetValue<string>());
        Assert.Equal("boom", steps[0]!["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(["[scratch] before"], steps[0]!["lines"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.NotNull(steps[1]!["error"]);
        Assert.True(JsonNode.DeepEquals(whole["failures"], scene["failures"]));
        Assert.True(JsonNode.DeepEquals(whole["failedAt"], scene["failedAt"]));
        Assert.Equal(3, whole["details"]!.AsArray().Count);
    }

    [Fact]
    public void RedOnlyListsNoStepsOfAGreenSceneAndKeepsItsVerdictSecondsAndTimeScale()
    {
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0), Step(1)), Plain with { Details = ScratchDetails.Full }) with { TimeScale = 1 };
        JsonObject run = RunJson(green);

        JsonObject scene = ScratchView.Result(run, redOnly: true)["scenes"]![0]!.AsObject();

        Assert.False(scene.ContainsKey("details"), scene.ToJsonString());
        Assert.Equal(ScratchVerdict.Green, scene["verdict"]!.GetValue<string>());
        Assert.Equal(green.Seconds, scene["seconds"]!.GetValue<double>());
        Assert.Equal(1, scene["timeScale"]!.GetValue<double>());
        Assert.Equal(2, scene["steps"]!["played"]!.GetValue<int>());
    }

    [Fact]
    public void RedOnlyKeepsTheFailedAtAndExitOfARedSceneWithNoRedStep()
    {
        ScratchSceneResult booted = ScratchVerdict.Judge(Seen() with { Total = 2, BootErrors = [Error("boot failure", "", 0)] }, Plain);
        ScratchSceneResult exited = ScratchVerdict.Judge(Seen(Step(0)) with { ExitCode = 3 }, Plain);
        JsonObject run = RunJson(booted, exited);

        JsonArray scenes = ScratchView.Result(run, redOnly: true)["scenes"]!.AsArray();

        Assert.NotNull(run["scenes"]![1]!["details"]);
        Assert.NotNull(run["scenes"]![0]!["failedAt"]);
        foreach ((JsonNode? before, JsonNode? after) in run["scenes"]!.AsArray().Zip(scenes))
        {
            Assert.Equal(ScratchVerdict.Red, after!["verdict"]!.GetValue<string>());
            Assert.False(after.AsObject().ContainsKey("details"), after.ToJsonString());
            Assert.True(JsonNode.DeepEquals(before!["failedAt"], after["failedAt"]), after.ToJsonString());
            Assert.True(JsonNode.DeepEquals(before["exit"], after["exit"]), after.ToJsonString());
        }
    }

    [Theory]
    [InlineData("""{"runId":"20261019T222144-3f2a","state":"running","scenes":[{"scene":"A","state":"playing","step":1,"total":3}]}""")]
    [InlineData("""{"runId":"20261019T222144-3f2a","state":"done","result":{"passed":false}}""")]
    [InlineData("""{"state":"done","result":{"scenes":[{"scene":"A","verdict":"red"},{"scene":"B","details":"odd"},7]}}""")]
    public void RedOnlyPassesAPartialStatusThroughUnchanged(string json)
    {
        JsonObject status = JsonNode.Parse(json)!.AsObject();

        JsonObject read = ScratchView.Status(status, redOnly: true);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), read), read.ToJsonString());
    }

    [Fact]
    public void RedOnlyPrunesAStatusResultAndLeavesTheStatusItReadWhole()
    {
        JsonObject status = new()
        {
            ["state"] = "done",
            ["result"] = RunJson(ScratchVerdict.Judge(Seen(Step(0), Step(1) with { CallError = "gone" }), Plain)),
        };
        string before = status.ToJsonString();

        JsonObject read = ScratchView.Status(status, redOnly: true);

        Assert.Equal([1], read["result"]!["scenes"]![0]!["details"]!.AsArray().Select(step => step!["index"]!.GetValue<int>()));
        Assert.Equal(before, status.ToJsonString());
    }

    [Fact]
    public void OnlyAllReturnsTheInputUnchanged()
    {
        JsonObject run = RunJson(ScratchVerdict.Judge(Seen(Step(0), Step(1) with { CallError = "gone" }), Plain));
        JsonObject status = new() { ["state"] = "done", ["result"] = run.DeepClone() };
        string before = (run.ToJsonString(), status.ToJsonString()).ToString();

        Assert.Same(run, ScratchView.Result(run, redOnly: false));
        Assert.Same(status, ScratchView.Status(status, redOnly: false));
        Assert.Equal(before, (run.ToJsonString(), status.ToJsonString()).ToString());
    }

    [Fact]
    public void CompactListsAGreenStepAsOneLineWithItsNotes()
    {
        ScratchRules compact = Plain with { Details = ScratchDetails.Compact };
        ScratchObservation seen = Seen(
            Step(0) with
            {
                Lines = ["[scratch] opened", "plain line", "[scratch] ready"],
            },
            Step(1) with
            {
                Until = new ScratchUntil(Met: true, GameMs: 300, ElapsedMs: 312, Frames: 19),
            },
            Step(2) with
            {
                Until = new ScratchUntil(Met: false, GameMs: 900, ElapsedMs: 905, Frames: 54),
            }
        );
        ScratchObservation chatty = Seen(Step(0) with { Lines = ["[scratch] " + new string('n', 100)] });

        JsonArray details = Json(ScratchVerdict.Judge(seen, compact))["details"]!.AsArray();
        JsonNode cut = Json(ScratchVerdict.Judge(chatty, compact with { DetailChars = 20 }))["details"]![0]!;

        Assert.Equal(
            ["0 ok step0 500ms | [scratch] opened / [scratch] ready", "1 ok step1 until met 300ms", "2 ok step2 until unmet 900ms"],
            details.Select(step => step!.GetValue<string>())
        );
        Assert.Equal("0 ok step0 500ms | [… (+109 chars)", cut.GetValue<string>());
    }

    [Fact]
    public void CompactKeepsAFailingStepsObject()
    {
        ScratchStep failing = Step(1) with { Status = "note", Errors = [Error("boom", "res://a.gd", 7)], Lines = ["[scratch] before"] };
        ScratchObservation seen = Seen(Step(0), failing);

        JsonArray compact = Json(ScratchVerdict.Judge(seen, Plain with { Details = ScratchDetails.Compact }))["details"]!.AsArray();
        JsonArray plain = Json(ScratchVerdict.Judge(seen, Plain))["details"]!.AsArray();

        Assert.Equal("0 ok step0 500ms", compact[0]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(plain[1], compact[1]), compact[1]!.ToJsonString());
    }

    [Fact]
    public void OnlyRedDropsCompactGreenLines()
    {
        ScratchRules compact = Plain with { Details = ScratchDetails.Compact };
        ScratchSceneResult red = ScratchVerdict.Judge(Seen(Step(0), Step(1) with { CallError = "gone" }, Step(2)), compact);
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0), Step(1)), compact);
        JsonObject run = RunJson(red, green);

        JsonArray scenes = ScratchView.Result(run, redOnly: true)["scenes"]!.AsArray();

        JsonObject kept = Assert.Single(scenes[0]!["details"]!.AsArray())!.AsObject();
        Assert.Equal(1, kept["index"]!.GetValue<int>());
        Assert.False(scenes[1]!.AsObject().ContainsKey("details"), scenes[1]!.ToJsonString());
        Assert.Equal(2, run["scenes"]![1]!["details"]!.AsArray().Count);
    }

    private static JsonObject RunJson(params ScratchSceneResult[] scenes) =>
        JsonSerializer.SerializeToNode(ScratchVerdict.Summarise(scenes, PrepResult.Skipped, timeScale: 1), ToolJson.Options)!.AsObject();

    private static JsonObject Json(ScratchSceneResult result) => JsonNode.Parse(JsonSerializer.Serialize(result, ToolJson.Options))!.AsObject();

    private static List<string> Window(OutputPage stdout, OutputPage stderr, LineMark after, LineMark before) =>
        ScratchRun.Window(stdout, stderr, after, before, "n1");

    private static ScratchObservation Seen(params ScratchStep[] steps) =>
        new("Scene", "probe.scratch-Scene", 0.5, steps.Length) { Steps = steps, ExitCode = 0 };

    private static ScratchStep Step(int index) => new(index, $"step{index}", 500);

    private static ScratchPattern Expected(string pattern, string? reason = null) => new(ScratchProfile.Compile(pattern), reason);

    private static ErrorEntry Error(string message, string file, int line) => new(1, ErrorFeed.ErrorType, message, file, line, "", [], "");
}
