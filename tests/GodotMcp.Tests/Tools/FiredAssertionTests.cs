using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// The fired assertion's check of a tool step's result, a pure function over the result's JSON: the node and signal that
/// match, the step's equals against the entry's own arguments, and the text a failure gives.
/// </summary>
public sealed class FiredAssertionTests
{
    private const string Ready = "/root/Main/Ready";
    private const string Other = "/root/Main/Toggle";

    [Fact]
    public void AnExactNodeAndSignalMatches()
    {
        RuntimeTools.FiredCheck check = Check("pressed", Reply(Entry(Ready, "pressed", 1)));

        Assert.Null(check.Reason);
        JsonObject matched = check.Result!["fired"]!.AsObject();
        Assert.Equal(Ready, matched["node"]!.GetValue<string>());
        Assert.Equal("pressed", matched["signal"]!.GetValue<string>());
        Assert.Equal(1, matched["frame"]!.GetValue<int>());
    }

    [Fact]
    public void AnExactAbsolutePathMatches()
    {
        RuntimeTools.FiredCheck check = Check("pressed", Reply(Entry(Ready, "pressed", 0)), node: Ready);

        Assert.Null(check.Reason);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Main/Ready")]
    public void ANodeTailMatchesOnASegmentBoundary(string node)
    {
        RuntimeTools.FiredCheck check = Check("pressed", Reply(Entry(Ready, "pressed", 0)), node: node);

        Assert.Null(check.Reason);
    }

    [Theory]
    [InlineData("eady")]
    [InlineData("in/Ready")]
    [InlineData("/Main/Ready")]
    [InlineData("/root/Main/Ready/Child")]
    public void ANodeThatIsNotThePathOrItsTailDoesNotMatch(string node)
    {
        RuntimeTools.FiredCheck check = Check("pressed", Reply(Entry(Ready, "pressed", 0)), node: node);

        Assert.StartsWith($"the fired assertion wanted {node}.pressed but step 0 fired: ", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignalMustMatch()
    {
        RuntimeTools.FiredCheck check = Check("toggled", Reply(Entry(Ready, "pressed", 0)));

        Assert.StartsWith("the fired assertion wanted Ready.toggled but step 0 fired: ", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEqualsIsComparedWithTheEntrysWholeArgumentsArray()
    {
        JsonObject fired = Reply(Entry(Ready, "pressed", 1, args: "[true]"));

        Assert.Null(Check("pressed", fired, equals: Args("[true]")).Reason);
        Assert.Null(Check("pressed", fired).Reason);
        Assert.NotNull(Check("pressed", fired, equals: Args("[false]")).Reason);
        Assert.NotNull(Check("pressed", fired, equals: Args("[true, true]")).Reason);
        Assert.NotNull(Check("pressed", fired, equals: Args("[]")).Reason);
    }

    [Fact]
    public void AnEntryWithoutArgumentsMatchesAnEmptyEquals()
    {
        JsonObject fired = Reply(Entry(Ready, "pressed", 1));

        Assert.Null(Check("pressed", fired, equals: Args("[]")).Reason);
        Assert.NotNull(Check("pressed", fired, equals: Args("[true]")).Reason);
    }

    [Fact]
    public void AFoldedEntryMatchesWithTheRunsLastArguments()
    {
        JsonObject fired = Reply(Entry(Ready, "text_changed", 1, args: "[\"abc\"]", count: 3));

        RuntimeTools.FiredCheck check = Check("text_changed", fired, equals: Args("[\"abc\"]"));

        Assert.Null(check.Reason);
        Assert.Equal(3, check.Result!["fired"]!["count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(10, "")]
    [InlineData(11, ", … (1 more)")]
    [InlineData(12, ", … (2 more)")]
    public void TheFailureListsAtMostTenEntries(int entries, string more)
    {
        JsonObject fired = Reply([.. Enumerable.Range(0, entries).Select(index => Entry($"/root/N{index}", $"sig{index}", 0))]);
        string expected =
            "the fired assertion wanted Ready.pressed but step 0 fired: "
            + string.Join(", ", Enumerable.Range(0, 10).Select(index => $"/root/N{index}.sig{index}"))
            + more;

        Assert.Equal(expected, Check("pressed", fired).Reason);
    }

    [Fact]
    public void NothingFiredNamesTheChainsListenedOn()
    {
        JsonObject result = new() { ["fired"] = new JsonArray(), ["listenedOn"] = new JsonArray(Ready, Other) };

        Assert.Equal(
            "the fired assertion wanted Ready.pressed but step 0 fired: nothing fired on /root/Main/Ready, /root/Main/Toggle",
            Check("pressed", result).Reason
        );
    }

    [Fact]
    public void NothingFiredAndNothingListenedSaysSo()
    {
        JsonObject result = new() { ["fired"] = new JsonArray(), ["listenedOn"] = new JsonArray() };

        Assert.Equal(
            "the fired assertion wanted Ready.pressed but step 0 fired: nothing fired and nothing was listened on",
            Check("pressed", result).Reason
        );
    }

    [Fact]
    public void AToolResultWithNoFiredListFails()
    {
        const string Expected = "step 1: the fired assertion checks step 0 (click), whose result has no fired list, so there is nothing to check.";

        Assert.Equal(Expected, Check("pressed", new JsonObject { ["pressedOn"] = null }).Reason);
        Assert.Equal(Expected, Check("pressed", null).Reason);
    }

    [Fact]
    public void ANullEqualsReadsAsAbsent()
    {
        JsonObject fired = Reply(Entry(Ready, "pressed", 1, args: "[true]"));

        Assert.Null(Check("pressed", fired, equals: Args("null")).Reason);
        Assert.StartsWith(
            "the fired assertion wanted Ready.toggled but step 0 fired: ",
            Check("toggled", fired, equals: Args("null")).Reason,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AFailureAddsTheDroppedCountWhenTheListWasCut()
    {
        JsonObject result = Reply(Entry("/root/N0", "sig0", 0), Entry("/root/N1", "sig1", 0));
        result["firedDropped"] = 3;

        string expected =
            "the fired assertion wanted Ready.pressed but step 0 fired: /root/N0.sig0, /root/N1.sig1; 3 more were dropped from the fired list";

        Assert.Equal(expected, Check("pressed", result).Reason);
    }

    [Fact]
    public void AnEntryThatMatchedTheSignalButNotTheArgsShowsItsArguments()
    {
        JsonObject fired = Reply(Entry(Ready, "pressed", 1, args: "[false]"), Entry(Other, "pressed", 1, args: "[false]"));

        string expected =
            "the fired assertion wanted Ready.pressed with args [true] but step 0 fired: "
            + "/root/Main/Ready.pressed with args [false], /root/Main/Toggle.pressed";

        Assert.Equal(expected, Check("pressed", fired, equals: Args("[true]")).Reason);
    }

    [Fact]
    public void AnEntryWithNoArgumentsShowsAnEmptyArrayWithThem()
    {
        JsonObject fired = Reply(Entry(Ready, "pressed", 1));

        Assert.Equal(
            "the fired assertion wanted Ready.pressed with args [true] but step 0 fired: /root/Main/Ready.pressed with args []",
            Check("pressed", fired, equals: Args("[true]")).Reason
        );
    }

    [Fact]
    public void TheWantedTextNamesTheEqualsWhenGiven()
    {
        RuntimeTools.FiredCheck check = Check("pressed", Reply(Entry(Ready, "button_down", 0)), equals: Args("[true]"));

        Assert.StartsWith("the fired assertion wanted Ready.pressed with args [true] but step 0 fired: ", check.Reason, StringComparison.Ordinal);
    }

    private static RuntimeTools.FiredCheck Check(string signal, JsonObject? result, string node = "Ready", JsonElement? equals = null) =>
        RuntimeTools.CheckFired(new BatchStep(Assert: "fired", Node: node, Signal: signal, EqualsValue: equals), 1, 0, "click", result);

    private static JsonObject Reply(params JsonObject[] fired) =>
        new() { ["fired"] = new JsonArray([.. fired]), ["listenedOn"] = new JsonArray(Ready) };

    private static JsonObject Entry(string node, string signal, int frame, string? args = null, int? count = null)
    {
        JsonObject entry = new()
        {
            ["node"] = node,
            ["signal"] = signal,
            ["frame"] = frame,
            ["listeners"] = 0,
        };
        if (args is not null)
        {
            entry["args"] = JsonNode.Parse(args);
        }

        if (count is not null)
        {
            entry["count"] = count;
        }

        return entry;
    }

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
