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

    private static ErrorEntry Entry(string message, string type = ErrorFeed.ErrorType) =>
        new(0, type, message, "res://x.gd", 1, "f", [], string.Empty);
}
