using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A scratch step's until, with no Godot: how a GetStepUntil answer becomes the wait the runner sends or the step's error, how a
/// failed wait is judged, and how a step played inside its until is listed.
/// </summary>
public sealed class ScratchUntilTests
{
    private const string Root = "/root/ScratchScene";
    private static readonly ScratchRun.UntilPlay Play = new(Root, Screenshot: false, ScratchRun.UntilHangGuard, TimeScale: 1);
    private static readonly double GuardSeconds = ScratchRun.UntilHangGuard.TotalSeconds;
    private static readonly int GuardMs = (int)ScratchRun.UntilHangGuard.TotalMilliseconds;

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void AGetStepHiddenAnswerOfTrueMarksTheStepHiddenAndFalseOrNullLeavesItShown(string answer, bool hidden) =>
        Assert.Equal((hidden, (string?)null), ScratchRun.HiddenAnswer(JsonNode.Parse(answer), 2));

    [Theory]
    [InlineData("1", "GetStepHidden(2) returned 1, not true or false")]
    [InlineData("\"yes\"", "GetStepHidden(2) returned \"yes\", not true or false")]
    public void AGetStepHiddenAnswerNeitherTrueFalseNorNullIsTheScenesRefusal(string answer, string error) =>
        Assert.Equal((false, error), ScratchRun.HiddenAnswer(JsonNode.Parse(answer), 2));

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void NullOrAnEmptyDictionaryIsAPacedStep(string answer) =>
        Assert.Equal(((JsonObject?)null, (string?)null), ScratchRun.UntilWait(JsonNode.Parse(answer), 0, "roll", 500, Play));

    [Theory]
    [InlineData("\"soon\"", "GetStepUntil(2) returned \"soon\", not a wait_for condition")]
    [InlineData("[1]", "GetStepUntil(2) returned [1], not a wait_for condition")]
    [InlineData("true", "GetStepUntil(2) returned true, not a wait_for condition")]
    public void AnAnswerNeitherADictionaryNorNullIsTheStepsError(string answer, string error)
    {
        (JsonObject? wait, string? refusal) = ScratchRun.UntilWait(JsonNode.Parse(answer), 2, "roll", 500, Play);

        Assert.Null(wait);
        Assert.Equal(error, refusal);
    }

    [Fact]
    public void AConditionIsSentAsOneWaitWithThePaceAsItsTimeoutFiveTimesTheGuardAsItsGameLimitAndPlayStepAsItsCall()
    {
        (JsonObject? wait, string? error) = ScratchRun.UntilWait(
            JsonNode.Parse("{\"node\": \"Dice\", \"signal\": \"Settled\"}"),
            2,
            "roll",
            5000,
            Play
        );

        Assert.Null(error);
        Assert.Equal("signal", wait!["kind"]!.GetValue<string>());
        Assert.Equal(("Dice", "Settled"), (wait["node"]!.GetValue<string>(), wait["signal"]!.GetValue<string>()));
        Assert.Equal(5000, wait["timeoutMs"]!.GetValue<int>());
        Assert.Equal(GuardMs * LoadClock.BackstopFactor, wait["limitGameMs"]!.GetValue<int>());
        Assert.Equal("{\"node\":\"/root/ScratchScene\",\"method\":\"PlayStep\",\"args\":[2]}", wait["call"]!.ToJsonString());
    }

    [Fact]
    public void TheUntilsWaitAsksForAScreenshotOnlyForACapturedStep()
    {
        JsonNode condition = JsonNode.Parse("{\"node\": \"Dice\", \"signal\": \"Settled\"}")!;

        (JsonObject? captured, _) = ScratchRun.UntilWait(
            condition,
            2,
            "roll",
            5000,
            new ScratchRun.UntilPlay(Root, Screenshot: true, ScratchRun.UntilHangGuard, TimeScale: 1)
        );
        (JsonObject? plain, _) = ScratchRun.UntilWait(condition, 2, "roll", 5000, Play);

        Assert.True(captured!["screenshot"]!.GetValue<bool>());
        Assert.False(captured.ContainsKey("previewMaxWidth"), captured.ToJsonString());
        Assert.False(plain!.ContainsKey("screenshot"), plain.ToJsonString());
    }

    [Theory]
    [InlineData("{\"node\": \"Dice\", \"signal\": \"Settled\", \"expression\": \"true\"}", "condition needs exactly one of: ")]
    [InlineData(
        "{\"output\": \"rolled\"}",
        "options.call is taken only by a gameMs, frames, exists, property, expression, text, settled, state or signal wait; this condition is output."
    )]
    [InlineData("{\"uiChanged\": true}", "options.call is taken only by ")]
    [InlineData("{\"nope\": 1}", "")]
    public void AConditionWaitForRefusesIsTheStepsRefusal(string answer, string refusal)
    {
        (JsonObject? wait, string? error) = ScratchRun.UntilWait(JsonNode.Parse(answer), 1, "roll", 500, Play);

        Assert.Null(wait);
        Assert.StartsWith("step 1 'roll': its until was refused: " + refusal, error, StringComparison.Ordinal);
    }

    // The server's release is the guard, floored by the registry's floor as the harness floors it to 10 minutes; the guard
    // itself, and so the bridge's game-time limit, never is.
    [Fact]
    public void AnUntilsReleaseIsItsHangGuardNotItsPaceFlooredOnlyOnTheServer()
    {
        Assert.Equal(
            ScratchRun.UntilHangGuard,
            new ScratchBounds(TimeSpan.Zero, ScratchRun.UntilHangGuard).UntilReleaseOf(ScratchRun.UntilHangGuard)
        );
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            new ScratchBounds(TimeSpan.FromMinutes(10), ScratchRun.UntilHangGuard).UntilReleaseOf(ScratchRun.UntilHangGuard)
        );
        Assert.Equal(ScratchRun.UntilHangGuard, new ScratchBounds(TimeSpan.FromMinutes(10), ScratchRun.UntilHangGuard).UntilGuard);
    }

    // The bridge's game-time limit is a backstop behind the load-adjusted release: 5 times the guard in game time at the scene's
    // time scale, so neither a loaded machine nor a time scale above 1 reaches it first. 15 s is the server's guard; 3 s the
    // never-met itests' harness guard.
    [Theory]
    [InlineData(15, 1, 75000)]
    [InlineData(15, 10, 750000)]
    [InlineData(15, 0.5, 37500)]
    [InlineData(3, 1, 15000)]
    public void AnUntilsGameLimitIsFiveTimesItsGuardInGameTimeAtTheScenesTimeScale(double guardSeconds, double timeScale, int limitGameMs)
    {
        (JsonObject? wait, string? error) = ScratchRun.UntilWait(
            JsonNode.Parse("{\"node\": \"Dice\", \"signal\": \"Settled\"}"),
            0,
            "roll",
            500,
            new ScratchRun.UntilPlay(Root, Screenshot: false, TimeSpan.FromSeconds(guardSeconds), timeScale)
        );

        Assert.Null(error);
        Assert.Equal(limitGameMs, wait!["limitGameMs"]!.GetValue<int>());
        Assert.Equal(500, wait["timeoutMs"]!.GetValue<int>());
    }

    // Step 0's profile guard is 40 s, step 1 has none: step 0's bridge limit is 5 times 40 s and its release 40 s, unfloored at a
    // floor of 0, and its not-met text names 40 s; step 1 keeps the default guard of 15 s.
    [Fact]
    public void AStepsOwnGuardSetsItsGameLimitReleaseAndNotMetTextAndAStepWithoutOneKeepsTheDefault()
    {
        ScratchWalk walk = new([new(0.5, null) { Guard = 40 }, new(0.5, null)], [0, 1], 1)
        {
            UntilSteps = new HashSet<int> { 0, 1 },
            UntilGuard = ScratchRun.UntilHangGuard,
        };
        ScratchBounds bounds = new(TimeSpan.Zero, ScratchRun.UntilHangGuard);
        JsonNode condition = JsonNode.Parse("{\"node\": \"Dice\", \"signal\": \"Settled\"}")!;

        (JsonObject? guarded, _) = ScratchRun.UntilWait(condition, 0, "batch", 500, Play with { Guard = walk.GuardOf(0) });
        (JsonObject? plain, _) = ScratchRun.UntilWait(condition, 1, "roll", 500, Play with { Guard = walk.GuardOf(1) });

        Assert.Equal(200000, guarded!["limitGameMs"]!.GetValue<int>());
        Assert.Equal(75000, plain!["limitGameMs"]!.GetValue<int>());
        Assert.Equal(TimeSpan.FromSeconds(40), bounds.UntilReleaseOf(walk.GuardOf(0)));
        Assert.Equal(ScratchRun.UntilHangGuard, bounds.UntilReleaseOf(walk.GuardOf(1)));
        Assert.Equal(
            "step 0 'batch': its until hung at its guard of 40 s of load-adjusted time (its condition never held); check the condition "
                + "GetStepUntil gives",
            ScratchRun.UntilMissed(0, "batch", new ScratchUntil(false, 40000, 40010, 900), walk.GuardOf(0).TotalSeconds, 1)
        );
    }

    // A paced step options.capture shoots, whose GetStepShotUntil gives a condition, is played inside one wait_for on it, shot at
    // the met frame and bounded by its pace in game time, counted from PlayStep, as a paced step's wait is.
    [Fact]
    public void AShotConditionIsSentAsOneShotWaitWithPlayStepAsItsCallAndThePaceAsItsTimeoutAndGameLimit()
    {
        (JsonObject? wait, string? refusal) = ScratchRun.ShotUntilWait(
            JsonNode.Parse("{\"node\": \"Globe\", \"property\": \"shot_ready\", \"equals\": true}"),
            2,
            2000,
            Root
        );

        Assert.Null(refusal);
        Assert.Equal("property", wait!["kind"]!.GetValue<string>());
        Assert.Equal(("Globe", "shot_ready"), (wait["node"]!.GetValue<string>(), wait["property"]!.GetValue<string>()));
        Assert.True(wait["screenshot"]!.GetValue<bool>());
        Assert.False(wait.ContainsKey("previewMaxWidth"), wait.ToJsonString());
        Assert.Equal(2000, wait["timeoutMs"]!.GetValue<int>());
        Assert.Equal(2000, wait["limitGameMs"]!.GetValue<int>());
        Assert.Equal("{\"node\":\"/root/ScratchScene\",\"method\":\"PlayStep\",\"args\":[2]}", wait["call"]!.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void NullOrAnEmptyDictionaryShotConditionLeavesTheStepShotAtTheEndOfItsPace(string answer) =>
        Assert.Equal(((JsonObject?)null, (string?)null), ScratchRun.ShotUntilWait(JsonNode.Parse(answer), 0, 2000, Root));

    [Theory]
    [InlineData("\"soon\"", "the screenshot at step 2 'roll' failed: GetStepShotUntil(2) returned \"soon\", not a wait_for condition")]
    [InlineData("[1]", "the screenshot at step 2 'roll' failed: GetStepShotUntil(2) returned [1], not a wait_for condition")]
    [InlineData("true", "the screenshot at step 2 'roll' failed: GetStepShotUntil(2) returned true, not a wait_for condition")]
    public void AShotConditionAnswerNeitherADictionaryNorNullIsTheShotsWarning(string answer, string warning)
    {
        (JsonObject? wait, string? refusal) = ScratchRun.ShotUntilWait(JsonNode.Parse(answer), 2, 2000, Root);

        Assert.Null(wait);
        Assert.Equal(warning, ScratchVerdict.ShotFailed(2, "roll", refusal!));
    }

    [Fact]
    public void AShotConditionWaitForRefusesIsTheShotsWarning()
    {
        (JsonObject? wait, string? refusal) = ScratchRun.ShotUntilWait(
            JsonNode.Parse("{\"node\": \"Globe\", \"signal\": \"Settled\", \"expression\": \"true\"}"),
            1,
            2000,
            Root
        );

        Assert.Null(wait);
        Assert.StartsWith(
            "the screenshot at step 1 'roll' failed: GetStepShotUntil(1) was refused: condition needs exactly one of: ",
            ScratchVerdict.ShotFailed(1, "roll", refusal!),
            StringComparison.Ordinal
        );
    }

    // A step with an until options.capture shoots, whose GetStepShotUntil gives a condition, carries it in its until's wait as
    // shotWhen: the condition alone, checked at each drawn frame, with no timeout, preview or call of its own.
    [Fact]
    public void AShotConditionForAStepWithAnUntilIsTheConditionAloneWithoutATimeoutPreviewOrCall()
    {
        (JsonObject? shotWhen, string? refusal) = ScratchRun.UntilShotWhen(
            JsonNode.Parse("{\"node\": \"Globe\", \"property\": \"shot_ready\", \"equals\": true}"),
            2,
            2000
        );

        Assert.Null(refusal);
        Assert.Equal("property", shotWhen!["kind"]!.GetValue<string>());
        Assert.Equal(("Globe", "shot_ready"), (shotWhen["node"]!.GetValue<string>(), shotWhen["property"]!.GetValue<string>()));
        Assert.True(shotWhen["equals"]!.GetValue<bool>());
        Assert.False(shotWhen.ContainsKey("timeoutMs"), shotWhen.ToJsonString());
        Assert.False(shotWhen.ContainsKey("previewMaxWidth"), shotWhen.ToJsonString());
        Assert.False(shotWhen.ContainsKey("call"), shotWhen.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void NullOrAnEmptyDictionaryShotConditionLeavesAStepWithAnUntilShotAtItsMetFrame(string answer) =>
        Assert.Equal(((JsonObject?)null, (string?)null), ScratchRun.UntilShotWhen(JsonNode.Parse(answer), 0, 2000));

    [Theory]
    [InlineData("\"soon\"", "GetStepShotUntil(2) returned \"soon\", not a wait_for condition")]
    [InlineData("[1]", "GetStepShotUntil(2) returned [1], not a wait_for condition")]
    [InlineData("true", "GetStepShotUntil(2) returned true, not a wait_for condition")]
    public void AShotConditionForAStepWithAnUntilNeitherADictionaryNorNullIsRefused(string answer, string refused) =>
        Assert.Equal(((JsonObject?)null, refused), ScratchRun.UntilShotWhen(JsonNode.Parse(answer), 2, 2000));

    [Theory]
    [InlineData("{\"node\": \"Globe\", \"signal\": \"Settled\"}", "signal")]
    [InlineData("{\"gameMs\": 100}", "gameMs")]
    [InlineData("{\"frames\": 3}", "frames")]
    public void AShotConditionForAStepWithAnUntilNotCheckedAtADrawnFrameIsRefused(string answer, string kind) =>
        Assert.Equal(
            (
                (JsonObject?)null,
                $"GetStepShotUntil(1) returned a {kind} condition; a step with an until is shot on a condition checked at a drawn frame, "
                    + "not signal, gameMs or frames"
            ),
            ScratchRun.UntilShotWhen(JsonNode.Parse(answer), 1, 2000)
        );

    [Fact]
    public void AShotConditionForAStepWithAnUntilThatWaitForRefusesIsRefused()
    {
        (JsonObject? shotWhen, string? refusal) = ScratchRun.UntilShotWhen(
            JsonNode.Parse("{\"node\": \"Globe\", \"signal\": \"Settled\", \"expression\": \"true\"}"),
            1,
            2000
        );

        Assert.Null(shotWhen);
        Assert.StartsWith("GetStepShotUntil(1) was refused: condition needs exactly one of: ", refusal, StringComparison.Ordinal);
    }

    // A step with an until whose wait carried shotWhen: the bridge's refusal of the shot condition, its hold, or why it is unshot.
    [Theory]
    [InlineData("{\"met\": true, \"shotHeld\": false, \"shotError\": \"unknown condition kind 'bogus'\"}", "refused")]
    [InlineData("{\"met\": true, \"shotHeld\": true}", null)]
    [InlineData("{\"met\": true, \"shotHeld\": false}", "not held")]
    [InlineData("{\"met\": true}", "not held")]
    [InlineData("null", "not held")]
    public void AStepWithAnUntilsShotFailureIsTheShotConditionsRefusalOrThatItDidNotHold(string reply, string? failure) =>
        Assert.Equal(
            failure switch
            {
                "refused" => "GetStepShotUntil(3) was refused: unknown condition kind 'bogus'",
                "not held" => ScratchRun.ShotNotHeldBeforeUntil,
                _ => null,
            },
            ScratchRun.UntilShotFailure(JsonNode.Parse(reply), 3)
        );

    [Fact]
    public void AShotConditionThatDidNotHoldWithinItsPaceIsTheShotsWarning() =>
        Assert.Equal(
            "the screenshot at step 3 'spin' failed: its shot condition did not hold within its pace",
            ScratchVerdict.ShotFailed(3, "spin", ScratchRun.ShotNotHeld)
        );

    [Fact]
    public void TheServersUntilGuardIsFifteenSeconds() => Assert.Equal(TimeSpan.FromSeconds(15), ScratchRun.UntilHangGuard);

    [Fact]
    public void AnUntilThatHungSaysWhichStepItsGuardAndWhatToDo() =>
        Assert.Equal(
            "step 0 'wait': its until hung at its guard of 20 s of load-adjusted time (its condition never held); check the condition "
                + "GetStepUntil gives",
            ScratchRun.UntilNotMet(0, "wait", 20)
        );

    [Fact]
    public void AnUnmetUntilEndedByItsGuardWithGameTimePastItsPaceHungAtItsGuard() =>
        Assert.Equal(
            ScratchRun.UntilNotMet(0, "wait", GuardSeconds),
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, GuardMs, GuardMs + 10, 60), GuardSeconds, 1)
        );

    [Fact]
    public void AnUnmetUntilWithGameTimeAtHalfItsGuardHungAtItsGuard() =>
        Assert.Equal(
            ScratchRun.UntilNotMet(0, "wait", GuardSeconds),
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 7500, 15010, 900), GuardSeconds, 1)
        );

    [Fact]
    public void AnUnmetUntilWithGameTimeJustUnderHalfItsGuardIsTheStoodStillText() =>
        Assert.Equal(
            "step 0 'wait': its until was not met within 15 s: the game's clock stood still (is it paused?)",
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 7499, 15010, 900), GuardSeconds, 1)
        );

    // The pace is a step's default wait, not its until's bound: a step paced 120 s whose until ran its 15 s guard in game time
    // hung, however short of its pace the game time is.
    [Fact]
    public void AnUnmetUntilWithAPaceLongerThanItsGuardHungAtItsGuard() =>
        Assert.Equal(
            "step 0 'wait': its until hung at its guard of 15 s of load-adjusted time (its condition never held); check the condition "
                + "GetStepUntil gives",
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 15000, 15010, 900), 15, 1)
        );

    [Fact]
    public void AnUnmetUntilThatRanPastHalfTheHarnessGuardHungAtThatGuard() =>
        Assert.Equal(ScratchRun.UntilNotMet(0, "wait", 3), ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 15000, 15010, 900), 3, 1));

    [Fact]
    public void AnUnmetUntilWhoseGamesClockStoodStillIsTheStoodStillText() =>
        Assert.Equal(
            "step 0 'wait': its until was not met within 10.5 s: the game's clock stood still (is it paused?)",
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 16, 10520, 600), 10.5, 1)
        );

    [Fact]
    public void ABridgeErrorThatTheStepsFeedAlsoLoggedReadsAsThatFeedEntryWithItsFileAndLine()
    {
        IReadOnlyList<ErrorEntry> errors = [Entry("error", "scratch step threw")];

        Assert.Equal(
            ("scratch step threw (res://scene.gd:3)", errors[0]),
            ScratchRun.UntilFailure(0, "throws", "The bridge refused 'wait_for': scratch step threw", errors)
        );
    }

    [Fact]
    public void ALongFeedEntryAsAStepsUntilErrorKeepsItsFileAndLineWithinTheFailuresBudget()
    {
        const string Suffix = " (res://scene.gd:3)";
        ErrorEntry entry = Entry("error", new string('x', 600));
        (string error, ErrorEntry? raised) = ScratchRun.UntilFailure(0, "throws", "The bridge refused 'wait_for': " + entry.Message, [entry]);
        ScratchRules rules = new(
            ScratchProfile.DefaultPatterns,
            Known: null,
            PaceReason: null,
            Details: ScratchDetails.Default,
            DetailChars: ScratchVerdict.DefaultDetailChars
        );
        ScratchObservation seen = new("Scene", "probe.scratch-Scene", 5, 1)
        {
            Steps =
            [
                new(0, "throws", 5000)
                {
                    CallError = error,
                    CallErrorEntry = raised,
                    Errors = [entry],
                },
            ],
            ExitCode = 0,
        };

        ScratchSceneResult result = ScratchVerdict.Judge(seen, rules);

        Assert.Equal("red", result.Verdict);
        Assert.Equal(new string('x', ScratchVerdict.MaxFailureIndexChars) + "… (+400 chars)" + Suffix, Assert.Single(result.Failures!).Error);
    }

    [Fact]
    public void AnyOtherBridgeErrorIsTheUntilsRefusalWithoutTheBridgesWrapper()
    {
        IReadOnlyList<ErrorEntry> errors = [Entry("warning", "No node 'Dice'"), Entry("error", "something else")];

        Assert.Equal(
            ("step 3 'roll': its until was refused: No node 'Dice'", (ErrorEntry?)null),
            ScratchRun.UntilFailure(3, "roll", "The bridge refused 'wait_for': No node 'Dice'", errors)
        );
        Assert.Equal(
            ("step 3 'roll': its until was refused: the node left", (ErrorEntry?)null),
            ScratchRun.UntilFailure(3, "roll", "the node left", [])
        );
    }

    [Fact]
    public void AShotConditionsWaitTheBridgeRefusedIsTheShotConditionsRefusalWithoutTheBridgesWrapper()
    {
        IReadOnlyList<ErrorEntry> errors = [Entry("warning", "No node 'Globe'"), Entry("error", "something else")];

        Assert.Equal(
            ("step 3 'spin': its shot condition's wait was refused: No node 'Globe'", (ErrorEntry?)null),
            ScratchRun.ShotWaitFailure(3, "spin", "The bridge refused 'wait_for': No node 'Globe'", errors)
        );
        Assert.Equal("step 3 'spin': its shot condition's wait was refused: no", ScratchRun.ShotRefused(3, "spin", "no"));
    }

    [Fact]
    public void AShotConditionsWaitRefusalThatTheStepsFeedAlsoLoggedReadsAsThatFeedEntryWithItsFileAndLine()
    {
        IReadOnlyList<ErrorEntry> errors = [Entry("error", "scratch step threw")];

        Assert.Equal(
            ("scratch step threw (res://scene.gd:3)", errors[0]),
            ScratchRun.ShotWaitFailure(0, "throws", "The bridge refused 'wait_for': scratch step threw", errors)
        );
    }

    [Fact]
    public void AConnectionOrSessionFailureDuringTheShotConditionsWaitIsItsWaitsFailure() =>
        Assert.Equal(
            "step 3 'spin': its shot condition's wait failed: the connection ended",
            ScratchRun.ShotWaitFailed(3, "spin", "the connection ended")
        );

    [Fact]
    public void ADisposedConnectionIsTheShotConditionsWaitFailureAndAnyOtherInvalidOperationIsARefusal()
    {
        Assert.Equal(
            ((string?)"step 3 'spin': its shot condition's wait failed: Cannot access a disposed object.", (string?)null),
            ScratchRun.ShotCaught(3, "spin", new ObjectDisposedException(null, "Cannot access a disposed object."))
        );
        Assert.Equal(
            ((string?)null, (string?)"The bridge refused 'wait_for': no"),
            ScratchRun.ShotCaught(3, "spin", new InvalidOperationException("The bridge refused 'wait_for': no"))
        );
    }

    [Fact]
    public void ADisposedConnectionIsTheUntilsWaitFailureAndAnyOtherInvalidOperationIsARefusal()
    {
        Assert.Equal(
            ((string?)"step 3 'roll': its until's wait failed: Cannot access a disposed object.", (string?)null),
            ScratchRun.UntilCaught(3, "roll", new ObjectDisposedException(null, "Cannot access a disposed object."))
        );
        Assert.Equal(
            ((string?)null, (string?)"The bridge refused 'wait_for': no"),
            ScratchRun.UntilCaught(3, "roll", new InvalidOperationException("The bridge refused 'wait_for': no"))
        );
    }

    [Fact]
    public void AConnectionOrSessionFailureDuringTheUntilsWaitIsItsWaitsFailure() =>
        Assert.Equal("step 3 'roll': its until's wait failed: the connection ended", ScratchRun.UntilWaitFailed(3, "roll", "the connection ended"));

    [Fact]
    public void AWaitReplysFiguresAreTheUntilsAndAnUnmetOneIsNotMet()
    {
        Assert.Equal(
            new ScratchUntil(true, 300, 312, 19),
            ScratchRun.UntilFigures(JsonNode.Parse("{\"met\":true,\"gameMs\":300,\"elapsedMs\":312,\"frames\":19,\"args\":[]}"))
        );
        Assert.Equal(
            new ScratchUntil(false, 1000, 1004, 60),
            ScratchRun.UntilFigures(JsonNode.Parse("{\"met\":false,\"gameMs\":1000,\"elapsedMs\":1004,\"frames\":60}"))
        );
    }

    [Fact]
    public void AnUntilNotMetStopsTheSceneEvenWhenKeepingGoing()
    {
        ScratchStep step = new(0, "wait", 1000)
        {
            CallError = ScratchRun.UntilNotMet(0, "wait", GuardSeconds),
            Until = new ScratchUntil(false, 1000, 1004, 60),
        };

        Assert.True(ScratchRun.StopsAfter(step, ScratchProfile.DefaultPatterns, keepGoing: true));
    }

    [Fact]
    public void AStepPlayedInsideItsUntilListsTheUntilInPlaceOfGameMsAndAPacedOneIsUnchanged()
    {
        ScratchRules rules = new(
            ScratchProfile.DefaultPatterns,
            Known: null,
            PaceReason: null,
            Details: ScratchDetails.Full,
            DetailChars: ScratchVerdict.DefaultDetailChars
        );
        ScratchObservation seen = new("Scene", "probe.scratch-Scene", 5, 2)
        {
            Steps = [new(0, "paced", 5000), new(1, "until", 5000) { Until = new ScratchUntil(true, 300, 312, 19) }],
            ExitCode = 0,
        };

        JsonArray details = JsonSerializer.SerializeToNode(ScratchVerdict.Judge(seen, rules), ToolJson.Options)!["details"]!.AsArray();

        Assert.Equal(5000, details[0]!["gameMs"]!.GetValue<int>());
        Assert.Null(details[0]!["until"]);
        Assert.Equal(["index", "name", "ok", "gameMs", "status"], details[0]!.AsObject().Select(field => field.Key).Take(5));
        Assert.Null(details[1]!["gameMs"]);
        Assert.Equal("{\"met\":true,\"gameMs\":300,\"elapsedMs\":312,\"frames\":19}", details[1]!["until"]!.ToJsonString());
        Assert.Equal(["index", "name", "ok", "until", "status"], details[1]!.AsObject().Select(field => field.Key).Take(5));
    }

    [Fact]
    public void AStepWhoseUntilWaitGaveNoFiguresListsUntilRanFalseWhileAPacedOrMetStepListsNeither()
    {
        ScratchRules rules = new(
            ScratchProfile.DefaultPatterns,
            Known: null,
            PaceReason: null,
            Details: ScratchDetails.Full,
            DetailChars: ScratchVerdict.DefaultDetailChars
        );
        ScratchObservation seen = new("Scene", "probe.scratch-Scene", 5, 3)
        {
            Steps =
            [
                new(0, "paced", 5000),
                new(1, "until", 5000) { UntilWaited = true, Until = new ScratchUntil(true, 300, 312, 19) },
                new(2, "refused", 5000) { UntilWaited = true, CallError = ScratchRun.UntilRefused(2, "refused", "no") },
            ],
            ExitCode = 0,
        };

        JsonArray details = JsonSerializer.SerializeToNode(ScratchVerdict.Judge(seen, rules), ToolJson.Options)!["details"]!.AsArray();

        Assert.Equal(5000, details[0]!["gameMs"]!.GetValue<int>());
        Assert.Null(details[0]!["untilRan"]);
        Assert.Null(details[1]!["gameMs"]);
        Assert.NotNull(details[1]!["until"]);
        Assert.Null(details[1]!["untilRan"]);
        Assert.Null(details[2]!["gameMs"]);
        Assert.False(details[2]!["untilRan"]!.GetValue<bool>());
    }

    [Fact]
    public void AnUnmetUntilWhoseStepLoggedAnErrorFailsWithThatFeedEntryAndOneWithNoErrorWithTheUntilsText()
    {
        ErrorEntry raised = Entry(ErrorFeed.ErrorType, "scratch until step threw");
        ScratchUntil unmet = new(false, 2000, 2004, 60);
        string notMet = ScratchRun.UntilMissed(0, "throws", unmet, GuardSeconds, 1);

        (string? error, ErrorEntry? entry) = ScratchRun.UnmetFailure(unmet, notMet, [raised]);

        Assert.Equal("scratch until step threw (res://scene.gd:3)", error);
        Assert.Same(raised, entry);

        (error, entry) = ScratchRun.UnmetFailure(unmet, notMet, []);

        Assert.Equal(notMet, error);
        Assert.Null(entry);
    }

    [Fact]
    public void AnUnmetUntilWhoseStepLoggedAWarningTakenAsJudgedFailsWithThatWarning()
    {
        ErrorEntry warning = Entry(ErrorFeed.WarningType, "slow frame");
        ScratchUntil unmet = new(false, 2000, 2004, 60);
        string notMet = ScratchRun.UntilMissed(0, "throws", unmet, GuardSeconds, 1);

        (string? error, ErrorEntry? entry) = ScratchRun.UnmetFailure(unmet, notMet, [warning]);

        Assert.Equal("warning: slow frame (res://scene.gd:3)", error);
        Assert.Same(warning, entry);
    }

    // The entries a step's read before the walk passed over are matched to the step's own read by what each says, not by its
    // sequence number: a raise the step's own read logs again is the same entry and is not charged twice, and one only the read
    // before the walk logged is not reproduced.
    [Fact]
    public void AnEntryTheStepsWindowHoldsAgainWithANewSequenceNumberIsNotLeftOver()
    {
        Assert.Empty(ScratchRun.Unreproduced([Raised(1)], [Raised(2)]));
        Assert.Empty(ScratchRun.Unreproduced([Raised(1), Raised(4)], [Raised(2), Raised(5)]));
    }

    [Fact]
    public void AnEntryTheStepsWindowDoesNotHoldIsLeftOver()
    {
        ErrorEntry early = Raised(1);

        Assert.Same(early, Assert.Single(ScratchRun.Unreproduced([early], [Entry(ErrorFeed.ErrorType, "another")])));
    }

    [Fact]
    public void TwoIdenticalEarlyEntriesAndOneWindowEntryLeaveOneOverAsOneWindowEntryMatchesOneOfThem()
    {
        ErrorEntry first = Raised(1);
        ErrorEntry second = Raised(2);

        Assert.Same(second, Assert.Single(ScratchRun.Unreproduced([first, second], [Raised(3)])));
    }

    [Fact]
    public void AnEntryMatchingTheWindowsMessageButNotItsLineIsLeftOver()
    {
        ErrorEntry early = Raised(1);

        Assert.Same(early, Assert.Single(ScratchRun.Unreproduced([early], [Raised(2, line: 4)])));
    }

    [Fact]
    public void AnEmptyWindowLeavesEveryEntryOverInTheOrderSkipped() =>
        Assert.Collection(
            ScratchRun.Unreproduced([Raised(3, line: 3), Raised(1, line: 4)], []),
            entry => Assert.Equal(("res://scene.gd", 3), (entry.File, entry.Line)),
            entry => Assert.Equal(("res://scene.gd", 4), (entry.File, entry.Line))
        );

    [Fact]
    public void ASkippedWarningIsNotReproducedByAWindowErrorOfTheSameText()
    {
        ErrorEntry warning = Raised(1, ErrorFeed.WarningType);

        Assert.Same(warning, Assert.Single(ScratchRun.Unreproduced([warning], [Raised(2)])));
    }

    private static ErrorEntry Entry(string type, string message) => new(1, type, message, "res://scene.gd", 3, "f", [], "");

    private static ErrorEntry Raised(long seq, string type = ErrorFeed.ErrorType, int line = 3) =>
        new(seq, type, "scratch until read raised", "res://scene.gd", line, "GetStepUntil", [], string.Empty);
}
