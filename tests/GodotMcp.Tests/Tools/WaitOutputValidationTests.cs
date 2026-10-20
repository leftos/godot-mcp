using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>wait_for's output condition: its argument checks and the line scan behind it; no Godot runs here.</summary>
public sealed class WaitOutputValidationTests
{
    private const string ConditionMessage =
        "condition needs exactly one of: {node, exists}, {node, property, equals}, {node, signal}, {expression}, {uiChanged: true | \"now\"}, "
        + "{gameMs}, {frames}, {text}, {node, stable}, {node, state, equals}, {output} (with stream and after optional).";

    [Theory]
    [InlineData(null)]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("any")]
    public void AnOutputConditionIsAcceptedWithEachStream(string? stream)
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined", Stream: stream), 1000);

        Assert.Equal("output", parameters["kind"]!.GetValue<string>());
        Assert.Equal("joined", parameters["output"]!.GetValue<string>());
        Assert.Equal(stream, parameters["stream"]?.GetValue<string>());
    }

    [Fact]
    public void ABlankOutputIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Output: " "), 1000));

        Assert.Equal("condition.output is blank; give a regular expression to match a printed line.", refused.Message);
    }

    [Fact]
    public void AnInvalidPatternIsRefusedWithTheCompilersReason()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "["), 1000));

        Assert.StartsWith("condition.output is not a valid regular expression: ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("..", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith(".", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("log")]
    [InlineData("STDOUT")]
    [InlineData("")]
    public void AStreamOtherThanStdoutStderrAnyIsRefused(string stream)
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined", Stream: stream), 1000)
        );

        Assert.Equal($"condition.stream must be stdout, stderr or any; got {stream}.", refused.Message);
    }

    [Fact]
    public void ANegativeAfterIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined", After: -1), 1000)
        );

        Assert.Equal("condition.after must be 0 or more; got -1.", refused.Message);
    }

    [Fact]
    public void ACheckOnceOutputWaitNeedsAfter()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined"), 0));

        Assert.Equal("timeoutMs 0 checks once, which an output wait needs after for: without it there is nothing new to check.", refused.Message);
    }

    [Fact]
    public void ACheckOnceOutputWaitWithAfterIsAccepted()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined", After: 3), 0);

        Assert.Equal("output", parameters["kind"]!.GetValue<string>());
        Assert.Equal(3L, parameters["after"]!.GetValue<long>());
        Assert.Equal(0, parameters["timeoutMs"]!.GetValue<int>());
    }

    [Fact]
    public void AStreamWithoutOutputIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildWaitParameters(new WaitCondition(Stream: "stdout"), 1000));

        Assert.Equal("condition.stream scopes an output condition; give output with it.", refused.Message);
    }

    [Fact]
    public void AnAfterWithoutOutputIsRefusedOnAnyKind()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Main", Exists: true, After: -1), 1000)
        );

        Assert.Equal("condition.after scopes an output condition; give output with it.", refused.Message);
    }

    [Fact]
    public void AnOutputConditionTakesNoOtherKindsField()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Node: "Main", Output: "joined"), 1000)
        );

        Assert.Equal(ConditionMessage, refused.Message);
    }

    [Fact]
    public void AnOutputWaitRefusesScreenshot()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined"), 1000, new WaitOptions(Screenshot: true))
        );

        Assert.Equal("options.screenshot needs a wait that meets on a game frame; an output wait meets on a printed line.", refused.Message);
    }

    [Fact]
    public void AnOutputWaitAcceptsScreenshotFalse()
    {
        JsonObject parameters = RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined"), 1000, new WaitOptions(Screenshot: false));

        Assert.False(parameters.ContainsKey("screenshot"));
    }

    [Fact]
    public void AnOutputWaitRefusesThen()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined"), 1000, new WaitOptions(Then: new WaitThen(TimeScale: 2)))
        );

        Assert.Equal("options.then needs a wait that meets on a game frame; an output wait meets on a printed line.", refused.Message);
    }

    [Fact]
    public void AnOutputWaitRefusesCallThroughItsKind()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(
                new WaitCondition(Output: "joined"),
                1000,
                new WaitOptions(Call: new MethodCall(Node: "Main", Method: "Go"))
            )
        );

        Assert.Equal(
            "options.call is taken only by a gameMs, frames, exists, property, expression, text, settled, state or signal wait; "
                + "this condition is output.",
            refused.Message
        );
    }

    [Fact]
    public void AnOutputWaitRefusesEdgeThroughItsKind()
    {
        McpException refused = Assert.Throws<McpException>(() =>
            RuntimeTools.BuildWaitParameters(new WaitCondition(Output: "joined"), 1000, new WaitOptions(Edge: true))
        );

        Assert.Equal("options.edge applies to exists, property, expression, text and state waits; this condition is output.", refused.Message);
    }

    [Fact]
    public void ALineMatchingThePatternReportsItsStreamNumberAndGroups()
    {
        OutputPage stdout = new(40, ["skip", "joined room 42"]);

        (OutputMatch? match, string? error) = OutputWait.Scan(stdout, new OutputPage(null, []), "stdout", Pattern(@"joined room (\d+)"), 0, 0);

        Assert.Null(error);
        Assert.NotNull(match);
        Assert.Equal("stdout", match.Value.Stream);
        Assert.Equal(41, match.Value.LineNumber);
        Assert.Equal("joined room 42", match.Value.Line);
        Assert.Equal("42", Assert.Single(match.Value.Groups));
    }

    [Fact]
    public void AfterExcludesItsOwnLine()
    {
        OutputPage stdout = new(1, ["first", "second"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, new OutputPage(null, []), "stdout", Pattern("first"), 1, 1);

        Assert.Null(match);
    }

    [Fact]
    public void AnAfterPastTheNewestLineMatchesNothingUntilALaterLineExists()
    {
        Assert.Null(OutputWait.Scan(new OutputPage(1, ["first"]), new OutputPage(null, []), "stdout", Pattern("first"), 5, 5).Match);

        (OutputMatch? match, _) = OutputWait.Scan(new OutputPage(6, ["sixth"]), new OutputPage(null, []), "stdout", Pattern("sixth"), 5, 5);

        Assert.Equal(6, match!.Value.LineNumber);
    }

    [Fact]
    public void AnAfterBelowTheNewestLineStillSeesTheNextLine()
    {
        OutputPage stdout = new(1, ["first", "second"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, new OutputPage(null, []), "stdout", Pattern("second"), 1, 1);

        Assert.Equal(2, match!.Value.LineNumber);
    }

    [Fact]
    public void AnAfterOlderThanEveryHeldLineClampsToTheOldest()
    {
        OutputPage stdout = new(701, ["held"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, new OutputPage(null, []), "stdout", Pattern("held"), 10, 10);

        Assert.Equal(701, match!.Value.LineNumber);
    }

    [Fact]
    public void AnUnmatchedOptionalGroupIsNull()
    {
        OutputPage stdout = new(1, ["a"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, new OutputPage(null, []), "stdout", Pattern("a(b)?"), 0, 0);

        Assert.Null(Assert.Single(match!.Value.Groups));
    }

    [Fact]
    public void StdoutWinsWhenBothStreamsMatch()
    {
        OutputPage stdout = new(1, ["hit"]);
        OutputPage stderr = new(1, ["hit"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, stderr, "any", Pattern("hit"), 0, 0);

        Assert.Equal("stdout", match!.Value.Stream);
    }

    [Fact]
    public void AStreamFilterScansOnlyThatStream()
    {
        OutputPage stdout = new(1, ["hit"]);
        OutputPage stderr = new(1, ["hit"]);

        (OutputMatch? match, _) = OutputWait.Scan(stdout, stderr, "stderr", Pattern("hit"), 0, 0);

        Assert.Equal("stderr", match!.Value.Stream);
    }

    [Fact]
    public void ACatastrophicPatternFailsTheWaitInsteadOfHanging()
    {
        string line = new('a', 30);

        (OutputMatch? match, string? error) = OutputWait.Scan(new OutputPage(1, [line]), new OutputPage(null, []), "stdout", Pattern("(a+)+b"), 0, 0);

        Assert.Null(match);
        Assert.Equal("condition.output took over 1 s to match a line; simplify the pattern.", error);
    }

    private static Regex Pattern(string pattern) => ScratchProfile.Compile(pattern);
}
