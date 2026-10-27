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
        Assert.Equal(((JsonObject?)null, (string?)null), ScratchRun.UntilWait(JsonNode.Parse(answer), 0, "roll", 500, Root));

    [Theory]
    [InlineData("\"soon\"", "GetStepUntil(2) returned \"soon\", not a wait_for condition")]
    [InlineData("[1]", "GetStepUntil(2) returned [1], not a wait_for condition")]
    [InlineData("true", "GetStepUntil(2) returned true, not a wait_for condition")]
    public void AnAnswerNeitherADictionaryNorNullIsTheStepsError(string answer, string error)
    {
        (JsonObject? wait, string? refusal) = ScratchRun.UntilWait(JsonNode.Parse(answer), 2, "roll", 500, Root);

        Assert.Null(wait);
        Assert.Equal(error, refusal);
    }

    [Fact]
    public void AConditionIsSentAsOneWaitWithThePaceAsItsTimeoutAndGameLimitAndPlayStepAsItsCall()
    {
        (JsonObject? wait, string? error) = ScratchRun.UntilWait(
            JsonNode.Parse("{\"node\": \"Dice\", \"signal\": \"Settled\"}"),
            2,
            "roll",
            5000,
            Root
        );

        Assert.Null(error);
        Assert.Equal("signal", wait!["kind"]!.GetValue<string>());
        Assert.Equal(("Dice", "Settled"), (wait["node"]!.GetValue<string>(), wait["signal"]!.GetValue<string>()));
        Assert.Equal(5000, wait["timeoutMs"]!.GetValue<int>());
        Assert.Equal(5000, wait["limitGameMs"]!.GetValue<int>());
        Assert.Equal("{\"node\":\"/root/ScratchScene\",\"method\":\"PlayStep\",\"args\":[2]}", wait["call"]!.ToJsonString());
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
        (JsonObject? wait, string? error) = ScratchRun.UntilWait(JsonNode.Parse(answer), 1, "roll", 500, Root);

        Assert.Null(wait);
        Assert.StartsWith("step 1 'roll': its until was refused: " + refusal, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntilNotMetSaysWhichStepItsPaceAndWhatToDo() =>
        Assert.Equal(
            "step 0 'wait': its until was not met within its pace of 0.25 s; give it a longer scratch.pace, or check the condition "
                + "GetStepUntil gives",
            ScratchRun.UntilNotMet(0, "wait", 0.25)
        );

    [Fact]
    public void AnUnmetUntilIsNotMetWithinItsPaceOnceItsGameTimeReachedItElseTheGamesClockStoodStill()
    {
        Assert.Equal(ScratchRun.UntilNotMet(0, "wait", 0.5), ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 500, 510, 30), 0.5, 10.5));
        Assert.Equal(
            "step 0 'wait': its until was not met within 10.5 s: the game's clock stood still (is it paused?)",
            ScratchRun.UntilMissed(0, "wait", new ScratchUntil(false, 16, 10520, 600), 0.5, 10.5)
        );
    }

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
        ScratchStep step = new(0, "wait", 1000) { CallError = ScratchRun.UntilNotMet(0, "wait", 1), Until = new ScratchUntil(false, 1000, 1004, 60) };

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

    private static ErrorEntry Entry(string type, string message) => new(1, type, message, "res://scene.gd", 3, "f", [], "");
}
