using System.Text.Json.Nodes;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The server's input captures, keyed by session name: events appended in order, the 2000-event cap, a capture that ends with its
/// game kept until one stop takes it, and a new start replacing an ended capture.
/// </summary>
public sealed class CaptureStoreTests
{
    [Fact]
    public void FramesAppendInArrivalOrder()
    {
        CaptureStore store = new();
        Assert.True(store.Begin("game"));

        store.Receive("game", Frame(Key("A"), Key("B")));
        store.Receive("game", Frame(Key("C")));
        CapturedInput taken = store.Take("game")!;

        Assert.Equal(["A", "B", "C"], taken.Events.Select(item => item!["key"]!.GetValue<string>()));
        Assert.False(taken.Truncated);
        Assert.Null(taken.Ended);
    }

    [Fact]
    public void SessionNamesMatchInAnyCase()
    {
        CaptureStore store = new();
        store.Begin("Game");

        store.Receive("game", Frame(Key("A")));

        Assert.True(store.IsRunning("GAME"));
        Assert.Single(store.Take("gAmE")!.Events);
    }

    [Fact]
    public void TheCapAtTwoThousandEventsSetsTruncated()
    {
        CaptureStore store = new();
        store.Begin("game");

        store.Receive("game", Frame([.. Enumerable.Range(0, CaptureStore.MaxEvents - 1).Select(index => Key($"K{index}"))]));
        Assert.False(store.Take("game") is { Truncated: true });
        store.Begin("game");
        store.Receive("game", Frame([.. Enumerable.Range(0, CaptureStore.MaxEvents - 1).Select(index => Key($"K{index}"))]));
        store.Receive("game", Frame(Key("last"), Key("over")));
        CapturedInput taken = store.Take("game")!;

        Assert.Equal(CaptureStore.MaxEvents, taken.Events.Count);
        Assert.Equal("last", taken.Events[^1]!["key"]!.GetValue<string>());
        Assert.True(taken.Truncated);
    }

    [Fact]
    public void AFrameMarkedTruncatedSetsTruncated()
    {
        CaptureStore store = new();
        store.Begin("game");
        JsonObject frame = Frame(Key("A"));
        frame["truncated"] = true;

        store.Receive("game", frame);

        Assert.True(store.Take("game")!.Truncated);
    }

    [Fact]
    public void AnEndedCaptureIsKeptAndReturnedOnceThenGone()
    {
        CaptureStore store = new();
        store.Begin("game");
        store.Receive("game", Frame(Key("A")));

        store.End("game", CaptureStore.EndedByRestart);
        store.Receive("game", Frame(Key("late")));
        store.End("game", CaptureStore.EndedByExit);

        Assert.False(store.IsRunning("game"));
        CapturedInput taken = store.Take("game")!;
        Assert.Equal(CaptureStore.EndedByRestart, taken.Ended);
        Assert.Equal(["A"], taken.Events.Select(item => item!["key"]!.GetValue<string>()));
        Assert.Null(store.Take("game"));
    }

    [Fact]
    public void AStartReplacesAnEndedCaptureButNotARunningOne()
    {
        CaptureStore store = new();
        store.Begin("game");
        store.Receive("game", Frame(Key("old")));

        Assert.False(store.Begin("game"));
        store.End("game", CaptureStore.EndedByStop);
        Assert.True(store.Begin("game"));
        CapturedInput taken = store.Take("game")!;

        Assert.Empty(taken.Events);
        Assert.Null(taken.Ended);
    }

    [Fact]
    public void FramesForANameWithNoCaptureAreDropped()
    {
        CaptureStore store = new();

        store.Receive("game", Frame(Key("A")));

        Assert.Null(store.Take("game"));
        Assert.False(store.IsRunning("game"));
    }

    [Fact]
    public void ADiscardedCaptureIsGone()
    {
        CaptureStore store = new();
        store.Begin("game");

        store.Discard("game");

        Assert.Null(store.Take("game"));
    }

    private static JsonObject Frame(params JsonObject[] events) => new() { ["type"] = "captured", ["events"] = new JsonArray(events) };

    private static JsonObject Key(string name) =>
        new()
        {
            ["type"] = "key",
            ["key"] = name,
            ["pressed"] = true,
        };
}
