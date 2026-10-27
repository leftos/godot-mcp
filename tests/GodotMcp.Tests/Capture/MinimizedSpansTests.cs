using GodotMcp.Capture;

namespace GodotMcp.Tests.Capture;

/// <summary>The spans a run's window was minimized over, from the minimized state read on each frame tick and the clip's clock.</summary>
public sealed class MinimizedSpansTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void AWindowNeverMinimizedHasNoSpan()
    {
        MinimizedSpans spans = new();

        List<MinimizedSpan> found = Run(spans, [false, false, false, false]);

        Assert.Empty(found);
        Assert.Null(spans.Finish(At(4)));
    }

    [Fact]
    public void ARestoreClosesTheSpanAtTheTickThatSawIt()
    {
        MinimizedSpans spans = new();

        List<MinimizedSpan> found = Run(spans, [false, true, true, true, false, false]);

        Assert.Equal([new MinimizedSpan(At(1), At(4))], found);
        Assert.Null(spans.Finish(At(6)));
    }

    [Fact]
    public void ASpanStillOpenWhenTheRunEndsIsClosedByTheFinish()
    {
        MinimizedSpans spans = new();

        List<MinimizedSpan> found = Run(spans, [false, false, true, true]);

        Assert.Empty(found);
        Assert.Equal(new MinimizedSpan(At(2), At(5)), spans.Finish(At(5)));
        Assert.Null(spans.Finish(At(6)));
    }

    [Fact]
    public void TwoMinimizesAreTwoSpans()
    {
        MinimizedSpans spans = new();

        List<MinimizedSpan> found = Run(spans, [true, true, false, false, true, true, true, false]);

        Assert.Equal([new MinimizedSpan(At(0), At(2)), new MinimizedSpan(At(4), At(7))], found);
    }

    [Fact]
    public void AMinimizeSeenOnOneTickOnlyIsNotReported()
    {
        MinimizedSpans spans = new();

        List<MinimizedSpan> found = Run(spans, [false, true, false, false, true]);

        Assert.Empty(found);
        Assert.Null(spans.Finish(At(5)));
    }

    [Fact]
    public void TheLineGivesTheSpanInTenthsOfASecond()
    {
        MinimizedSpan span = new(TimeSpan.FromSeconds(3.21), TimeSpan.FromSeconds(7.86));

        Assert.Equal("minimized from 3.2 s to 7.9 s of the clip", span.Line);
    }

    private static TimeSpan At(int tick) => tick * Tick;

    /// <summary>Feeds one minimized state per tick, the first at the clip's time zero, and keeps the spans the ticks closed.</summary>
    private static List<MinimizedSpan> Run(MinimizedSpans spans, bool[] minimizedPerTick)
    {
        List<MinimizedSpan> found = [];
        for (int tick = 0; tick < minimizedPerTick.Length; tick++)
        {
            if (spans.Tick(minimizedPerTick[tick], At(tick)) is { } span)
            {
                found.Add(span);
            }
        }

        return found;
    }
}
