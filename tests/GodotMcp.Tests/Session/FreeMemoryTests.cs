using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The machine's free memory read before a scene's game launches, and the warning naming it: a figure this run can compare with
/// the machine's own, and the sentence a failed scene's warnings carry.
/// </summary>
public sealed class FreeMemoryTests
{
    [Fact]
    public void TheMachinesFreeMemoryIsPositiveAndNoMoreThanItsTotal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GlobalMemoryStatusEx is Windows'");
        int? total = FreeMemory.TotalMb();
        int? available = FreeMemory.AvailableMb();

        Assert.NotNull(total);
        Assert.NotNull(available);
        Assert.InRange(available!.Value, 1, total!.Value);
    }

    [Fact]
    public void TheWarningNamesTheFreeMemoryInWholeMegabytes() =>
        Assert.Equal("the machine had 7800 MB of memory free when this scene's game launched", ScratchVerdict.FreeMemoryWarning(7800));
}
