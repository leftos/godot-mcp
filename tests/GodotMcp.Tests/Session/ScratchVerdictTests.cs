using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>run_scratches' verdict of a scene from what the runner saw of it, with no Godot.</summary>
public sealed class ScratchVerdictTests
{
    private static readonly IReadOnlyList<ScratchPattern> Default = ScratchProfile.DefaultPatterns;
    private static readonly ScratchRules Plain = new(Default, Known: null, PaceReason: null, Details: false);

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
    public void DetailsListAGreenScenesSteps()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0) with { Lines = ["[scratch] opened"] }), Plain with { Details = true });

        ScratchStepResult step = Assert.Single(result.Details!);
        Assert.True(step.Ok);
        Assert.Equal(500, step.GameMs);
        Assert.Empty(step.Lines);
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
    public void LinesBeforeTheFirstStepAreNeverJudged()
    {
        ScratchSceneResult result = ScratchVerdict.Judge(Seen(Step(0)) with { LaunchLines = ["ERROR: launch noise", "SCRIPT ERROR: early"] }, Plain);

        Assert.Equal(ScratchVerdict.Green, result.Verdict);
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

        ScratchSceneResult result = ScratchVerdict.Judge(seen, Plain with { Details = true });

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

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped);

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

        ScratchRunResult run = ScratchVerdict.Summarise([killed], PrepResult.Skipped);

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

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped);

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

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped);

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

        ScratchRunResult run = ScratchVerdict.Summarise([result], PrepResult.Skipped);

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

        ScratchRunResult run = ScratchVerdict.Summarise([green, known, empty], PrepResult.Skipped);

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
        ScratchRunResult run = ScratchVerdict.Summarise([entry], PrepResult.Skipped);

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
        Assert.Equal(1, ScratchVerdict.Summarise([entry], PrepResult.Skipped).Red);
    }

    [Fact]
    public void AScenePlayedOnceHasNoAloneFields()
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(ScratchVerdict.Judge(Seen(Step(0)), Plain), ToolJson.Options))!.AsObject();

        Assert.False(json.ContainsKey("alone"), json.ToJsonString());
        Assert.False(json.ContainsKey("aloneFailedAt"), json.ToJsonString());
    }

    [Fact]
    public void AStepListsAtMostTwentyLinesAndCountsTheRest()
    {
        ScratchStep noisy = Step(0) with { Lines = [.. Enumerable.Range(0, 25).Select(n => $"ERROR: {n}")] };

        IReadOnlyList<string> lines = ScratchVerdict.Judge(Seen(noisy), Plain).Details![0].Lines;

        Assert.Equal(21, lines.Count);
        Assert.Equal("ERROR: 19", lines[19]);
        Assert.Equal("… 5 more", lines[20]);
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

        ScratchRunResult run = ScratchVerdict.Summarise([ScratchVerdict.Judge(Seen(Step(0)), Plain)], prep);
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
            ScratchRun.MarkerTimeout("step 2", stdoutArrived, stderrArrived)
        );

    [Fact]
    public void TheCeilingBudgetsEachStepThePaceAfterAndTheMarkers() =>
        Assert.Equal(TimeSpan.FromSeconds((3 * 10.5) + 0.5 + 10), ScratchRun.Ceiling([0.5, 0.5, 0.5]));

    [Fact]
    public void TheCeilingSumsEachStepsOwnPaceAndWaitsTheLastStepsPaceAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 40 + 30 + 10), ScratchRun.Ceiling([0.5, 30]));
        Assert.Equal(TimeSpan.FromSeconds(10.5 + 40 + 10.5 + 0.5 + 10), ScratchRun.Ceiling([0.5, 30, 0.5]));
    }

    [Fact]
    public void EachStepTakesItsPaceByIndexElseByNameElseTheScenes()
    {
        ScratchScenePlan scene = PacedScene(new() { ["1"] = new ScratchPace(30, "the fight plays long"), ["close"] = new ScratchPace(2, null) });

        (ScratchPace[] paces, string? error) = ScratchRun.StepPaces(scene, ["open", "fight", "close", "rest"]);

        Assert.Null(error);
        Assert.Equal([0.5, 30, 2, 0.5], paces.Select(pace => pace.Seconds));
        Assert.Equal(["the scene settles", "the fight plays long", null, "the scene settles"], paces.Select(pace => pace.Reason));
    }

    [Fact]
    public void AKeyWithALeadingZeroIsAStepNameNotAnIndex()
    {
        ScratchScenePlan scene = PacedScene(new() { ["03"] = new ScratchPace(3, null) });

        (ScratchPace[] paces, string? error) = ScratchRun.StepPaces(scene, ["a", "b", "c", "d", "03"]);

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

        (ScratchPace[] paces, string? error) = ScratchRun.StepPaces(scene, ["first", "0", "7"]);

        Assert.Null(error);
        Assert.Equal([3, 0.5, 4], paces.Select(pace => pace.Seconds));
    }

    [Theory]
    [InlineData("5", "pace.steps key \"5\" of Paced matches no step; its steps are 0: open, 1: hit, 2: hit")]
    [InlineData("shut", "pace.steps key \"shut\" of Paced matches no step; its steps are 0: open, 1: hit, 2: hit")]
    [InlineData("-1", "pace.steps key \"-1\" of Paced matches no step; its steps are 0: open, 1: hit, 2: hit")]
    [InlineData("hit", "pace.steps key \"hit\" of Paced names 2 steps (1, 2); key it by index")]
    public void AStepPaceKeyThatMatchesNoSingleStepIsRefused(string key, string refusal)
    {
        ScratchScenePlan scene = PacedScene(new() { ["open"] = new ScratchPace(1, null), [key] = new ScratchPace(2, null) });

        Assert.Equal(refusal, ScratchRun.StepPaces(scene, ["open", "hit", "hit"]).Error);
    }

    [Fact]
    public void AStepPaceKeyOnASceneWithoutStepsIsRefused() =>
        Assert.Equal(
            "pace.steps key \"0\" of Paced matches no step; it has no steps",
            ScratchRun.StepPaces(PacedScene(new() { ["0"] = new ScratchPace(1, null) }), []).Error
        );

    [Fact]
    public void TwoStepPaceKeysForOneStepAreRefused() =>
        Assert.Equal(
            "pace.steps keys \"0\" and \"open\" of Paced both name step 0; keep one",
            ScratchRun.StepPaces(PacedScene(new() { ["0"] = new ScratchPace(1, null), ["open"] = new ScratchPace(2, null) }), ["open", "hit"]).Error
        );

    private static ScratchScenePlan PacedScene(Dictionary<string, ScratchPace> stepPaces) =>
        new("Paced", "res://scratch/Paced.tscn", 0.5, []) { PaceReason = "the scene settles", StepPaces = stepPaces };

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
        ScratchSceneResult green = ScratchVerdict.Judge(Seen(Step(0)), Plain with { Details = true });

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

    private static List<string> Window(OutputPage stdout, OutputPage stderr, LineMark after, LineMark before) =>
        ScratchRun.Window(stdout, stderr, after, before, "n1");

    private static ScratchObservation Seen(params ScratchStep[] steps) =>
        new("Scene", "probe.scratch-Scene", 0.5, steps.Length) { Steps = steps, ExitCode = 0 };

    private static ScratchStep Step(int index) => new(index, $"step{index}", 500);

    private static ErrorEntry Error(string message, string file, int line) => new(1, ErrorFeed.ErrorType, message, file, line, "", [], "");
}
