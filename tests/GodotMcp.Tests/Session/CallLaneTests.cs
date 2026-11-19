using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The call lane: one entry at a time, a second refused with the first named, and an old entry that frees nothing.</summary>
public sealed class CallLaneTests
{
    [Fact]
    public void AFreeLaneHasNoHolder() => Assert.Null(new CallLane().Holder);

    [Fact]
    public void ASecondEntryIsRefusedNamingTheHolder()
    {
        CallLane lane = new();

        using IDisposable? first = lane.TryEnter("call_method /root/A.f()", out CallLaneHolder? none);

        Assert.NotNull(first);
        Assert.Null(none);
        Assert.Null(lane.TryEnter("run_script", out CallLaneHolder? held));
        Assert.Equal("call_method /root/A.f()", held?.What);
    }

    [Fact]
    public void AReleasedLaneCanBeEnteredAgain()
    {
        CallLane lane = new();
        IDisposable first = lane.TryEnter("call_method /root/A.f()", out _)!;

        first.Dispose();

        Assert.Null(lane.Holder);
        using IDisposable second = lane.TryEnter("run_script", out CallLaneHolder? none)!;
        Assert.Null(none);
        Assert.Equal("run_script", lane.Holder);
    }

    [Fact]
    public void DisposingAnOldEntryLeavesANewerHolder()
    {
        CallLane lane = new();
        IDisposable first = lane.TryEnter("call_method /root/A.f()", out _)!;
        first.Dispose();
        using IDisposable second = lane.TryEnter("run_csharp", out _)!;

        first.Dispose();

        Assert.Equal("run_csharp", lane.Holder);
        Assert.Null(lane.TryEnter("run_script", out CallLaneHolder? held));
        Assert.Equal("run_csharp", held?.What);
    }
}
