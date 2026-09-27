using GodotMcp.Server.Tools;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// The seeded draw stress_input picks its pool entries with: the SplitMix64 sequence itself, the indices drawn from it,
/// and how evenly 30000 of them spread over three entries. No Godot runs here.
/// </summary>
public sealed class SeededDrawTests
{
    // The published SplitMix64 reference outputs for state 0: the first five calls of next(), in order.
    private static readonly ulong[] ReferenceValues =
    [
        0xE220A8397B1DCDAF,
        0x6E789E6AA1B965F4,
        0x06C45D188009454F,
        0xF88BB8A8724C81EC,
        0x1B39896A51A8749B,
    ];

    [Fact]
    public void TheFirstFiveOutputsAreTheReferenceSequenceOfSeedZero()
    {
        SeededDraw draw = new(0);
        ulong[] actual = [.. ReferenceValues.Select(_ => draw.Next())];

        Assert.Equal(ReferenceValues, actual);
    }

    [Fact]
    public void TheSameSeedDrawsTheSameFiftyIndices() => Assert.Equal(FiftyIndices(7), FiftyIndices(7));

    [Fact]
    public void DifferentSeedsDrawDifferentIndices() => Assert.NotEqual(FiftyIndices(7), FiftyIndices(8));

    [Fact]
    public void ThirtyThousandDrawsOverThreeEntriesStayEven()
    {
        SeededDraw draw = new(20260926);
        int[] counts = new int[3];
        for (int drawNumber = 0; drawNumber < 30_000; drawNumber++)
        {
            counts[draw.NextIndex(3)]++;
        }

        Assert.All(counts, count => Assert.InRange(count, 9_000, 11_000));
    }

    private static int[] FiftyIndices(int seed)
    {
        SeededDraw draw = new((ulong)seed);
        return [.. Enumerable.Range(0, 50).Select(_ => draw.NextIndex(7))];
    }
}
