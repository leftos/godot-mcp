using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

public sealed class ErrorFeedTests
{
    [Fact]
    public void SequenceNumbersIncreaseFromOne()
    {
        ErrorFeed feed = new();

        long before = feed.Mark();
        feed.Add([Entry("a"), Entry("b")], 0);
        feed.Add([Entry("c")], 0);

        Assert.Equal(0, before);
        Assert.Equal([1L, 2L, 3L], feed.Since(0, 10).Select(entry => entry.Seq));
        Assert.Equal(["a", "b", "c"], feed.Since(0, 10).Select(entry => entry.Message));
        Assert.Equal(3, feed.Mark());
    }

    [Fact]
    public void TheRingEvictsTheOldestAndCountsThem()
    {
        ErrorFeed feed = new();

        feed.Add([.. Enumerable.Range(1, ErrorFeed.Capacity + 3).Select(index => Entry($"e{index}"))], 0);
        IReadOnlyList<ErrorEntry> kept = feed.Since(0, ErrorFeed.Capacity + 10);

        Assert.Equal(ErrorFeed.Capacity, kept.Count);
        Assert.Equal(4, kept[0].Seq);
        Assert.Equal("e4", kept[0].Message);
        Assert.Equal(ErrorFeed.Capacity + 3, kept[^1].Seq);
        Assert.Equal(3, feed.Dropped);
    }

    [Fact]
    public void SinceReturnsOnlyLaterEntriesUpToTheLimit()
    {
        ErrorFeed feed = new();
        feed.Add([Entry("a"), Entry("b", ErrorFeed.WarningType), Entry("c"), Entry("d"), Entry("e")], 0);

        IReadOnlyList<ErrorEntry> page = feed.Since(1, 2);

        Assert.Equal([2L, 3L], page.Select(entry => entry.Seq));
        Assert.Empty(feed.Since(5, 10));
        Assert.Equal(["c", "d", "e"], feed.ErrorsSince(1).Select(entry => entry.Message));
    }

    [Fact]
    public void JudgedSinceGivesErrorsOnlyWithWarningsOffAndBothInFeedOrderWithWarningsOn()
    {
        ErrorFeed feed = new();
        feed.Add([Entry("a"), Entry("b", ErrorFeed.WarningType), Entry("c"), Entry("d", ErrorFeed.WarningType)], 0);

        Assert.Equal(["c"], feed.JudgedSince(1, warnings: false).Select(entry => entry.Message));
        Assert.Equal(["b", "c", "d"], feed.JudgedSince(1, warnings: true).Select(entry => entry.Message));
        Assert.Equal([2L, 3L, 4L], feed.JudgedSince(1, warnings: true).Select(entry => entry.Seq));
    }

    [Fact]
    public void TheBridgesDroppedCountIsAdded()
    {
        ErrorFeed feed = new();
        JsonObject frame = JsonNode
            .Parse(
                """
                {"type": "errors", "dropped": 7.0, "entries": [
                  {"type": "warning", "message": "careful", "file": "res://a.gd", "line": 12.0, "function": "f", "engine": "scene/main/node.cpp:1800",
                   "stack": ["res://a.gd:12 in f", "res://b.gd:3 in g"]},
                  {"type": "error", "message": "broken"}
                ]}
                """
            )!
            .AsObject();

        feed.Receive(frame);
        IReadOnlyList<ErrorEntry> entries = feed.Since(0, 10);

        Assert.Equal(7, feed.Dropped);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new ErrorEntry(1, "warning", "careful", "res://a.gd", 12, "f", entries[0].Stack, "scene/main/node.cpp:1800"), entries[0]);
        Assert.Equal(["res://a.gd:12 in f", "res://b.gd:3 in g"], entries[0].Stack);
        Assert.True(entries[1].IsError);
        Assert.Equal(0, entries[1].Line);
    }

    [Fact]
    public void AMalformedFrameIsIgnored()
    {
        ErrorFeed feed = new();

        feed.Receive(JsonNode.Parse("""{"type": "errors", "entries": {"message": "not a list"}}""")!.AsObject());
        feed.Receive(
            JsonNode
                .Parse(
                    """
                    {"type": "errors", "dropped": "many", "entries": [
                      5, "text", {"message": "kept", "stack": "not a list", "line": "x", "file": 3}
                    ]}
                    """
                )!
                .AsObject()
        );

        ErrorEntry kept = Assert.Single(feed.Since(0, 10));
        Assert.Equal("kept", kept.Message);
        Assert.Empty(kept.Stack);
        Assert.Equal(0, kept.Line);
        Assert.Equal(string.Empty, kept.File);
        Assert.Equal(0, feed.Dropped);
    }

    [Fact]
    public void AMarkEntryIsNotAnErrorAndRecordsTheSeqBeforeIt()
    {
        ErrorFeed feed = new();

        feed.Receive(Frame(("error", "a"), ("error", "b"), ("mark", "step 0 end"), ("warning", "c")));

        Assert.Equal(["a", "b", "c"], feed.Since(0, 10).Select(entry => entry.Message));
        Assert.Equal([1L, 2L, 3L], feed.Since(0, 10).Select(entry => entry.Seq));
        Assert.Equal(["a", "b"], feed.ErrorsSince(0).Select(entry => entry.Message));
        Assert.Equal(3, feed.Mark());
        (IReadOnlyList<ErrorEntry> taken, long? mark) = feed.TakeThrough(0, "step 0 end", warnings: true);
        Assert.Equal(2, mark);
        Assert.Equal(["a", "b"], taken.Select(entry => entry.Message));
    }

    [Fact]
    public void TakingThroughAMarkLeavesLaterEntriesForTheNextWindow()
    {
        ErrorFeed feed = new();
        feed.Receive(Frame(("error", "mine"), ("mark", "step 0 end"), ("error", "next")));

        (IReadOnlyList<ErrorEntry> taken, long? mark) = feed.TakeThrough(0, "step 0 end", warnings: false);
        IReadOnlyList<ErrorEntry> rest = feed.JudgedSince(mark!.Value, warnings: false);

        Assert.Equal(["mine"], taken.Select(entry => entry.Message));
        Assert.Equal(1, mark);
        Assert.Equal(["next"], rest.Select(entry => entry.Message));
    }

    [Fact]
    public void AMissingMarkTakesNothing()
    {
        ErrorFeed feed = new();
        feed.Receive(Frame(("error", "a"), ("warning", "w"), ("error", "b")));

        (IReadOnlyList<ErrorEntry> taken, long? mark) = feed.TakeThrough(0, "step 0 end", warnings: false);

        Assert.Null(mark);
        Assert.Empty(taken);
        Assert.Equal(["a", "b"], feed.JudgedSince(0, warnings: false).Select(entry => entry.Message));
    }

    [Fact]
    public void TwoMarksCutThreeWindows()
    {
        ErrorFeed feed = new();
        feed.Receive(Frame(("error", "one"), ("mark", "step 0 end"), ("error", "two")));
        feed.Receive(Frame(("warning", "three"), ("mark", "step 1 end"), ("error", "four")));

        (IReadOnlyList<ErrorEntry> first, long? firstMark) = feed.TakeThrough(0, "step 0 end", warnings: true);
        (IReadOnlyList<ErrorEntry> second, long? secondMark) = feed.TakeThrough(firstMark!.Value, "step 1 end", warnings: true);
        IReadOnlyList<ErrorEntry> third = feed.JudgedSince(secondMark!.Value, warnings: true);

        Assert.Equal(["one"], first.Select(entry => entry.Message));
        Assert.Equal(["two", "three"], second.Select(entry => entry.Message));
        Assert.Equal(["four"], third.Select(entry => entry.Message));
        Assert.Equal((1L, 3L), (firstMark.Value, secondMark.Value));
    }

    private static JsonObject Frame(params (string Type, string Message)[] entries) =>
        new()
        {
            ["type"] = "errors",
            ["dropped"] = 0,
            ["entries"] = new JsonArray([
                .. entries.Select(entry => (JsonNode)new JsonObject { ["type"] = entry.Type, ["message"] = entry.Message }),
            ]),
        };

    private static ErrorEntry Entry(string message, string type = ErrorFeed.ErrorType) =>
        new(0, type, message, "res://x.gd", 1, "f", [], string.Empty);
}
