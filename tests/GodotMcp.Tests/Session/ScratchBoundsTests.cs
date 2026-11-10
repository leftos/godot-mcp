using System.Globalization;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The bounds a scene's walk holds its game to under the registry's <see cref="SessionRegistry.ScratchBoundFloor"/>: each the
/// longer of its own value and the floor, a ceiling the call or the profile gives and an until's hang guard excepted, and today's
/// values at the default.
/// </summary>
public sealed class ScratchBoundsTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(10);

    private static readonly ScratchWalk Walk = new([new(0.5, null), new(2, null)], [0, 1], 1);

    [Fact]
    public void EachBoundIsAtLeastTheFloorButAnUntilsGuard()
    {
        ScratchBounds bounds = new(Floor, ScratchRun.UntilHangGuard);

        Assert.True(bounds.Call >= Floor, $"the call timeout is {bounds.Call}");
        Assert.True(bounds.Markers >= Floor, $"the marker wait is {bounds.Markers}");
        Assert.True(bounds.Release(5000, 1) >= Floor, $"the pace release is {bounds.Release(5000, 1)}");
        Assert.True(bounds.UntilRelease >= Floor, $"the until release is {bounds.UntilRelease}");
        Assert.Equal(ScratchRun.UntilHangGuard, bounds.UntilGuard);
        Assert.True(bounds.Release(20000, 0.5) >= Floor, $"the scaled pace release is {bounds.Release(20000, 0.5)}");
        Assert.True((Walk with { Floor = Floor }).Ceiling >= Floor, $"the walk's ceiling is {(Walk with { Floor = Floor }).Ceiling}");
    }

    [Fact]
    public void AFlooredBoundsTextNamesTheBoundUsed()
    {
        ScratchBounds bounds = new(Floor, ScratchRun.UntilHangGuard);

        Assert.Equal(
            "the step 2 end marker reached stdout and stderr not both within 600 s",
            ScratchRun.MarkerTimeout("step 2", bounds.Markers, stdoutArrived: true, stderrArrived: false)
        );
        Assert.StartsWith(
            "the scene passed its ceiling of 600 s of load-adjusted time",
            (Walk with { Floor = Floor }).CeilingReason(string.Empty),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task TheRegistrysDefaultFloorKeepsEachBound()
    {
        await using RegistryHarness harness = new();
        TimeSpan floor = harness.Sessions.ScratchBoundFloor;
        ScratchBounds bounds = new(floor, harness.Sessions.ScratchUntilGuard);

        Assert.Equal(TimeSpan.FromSeconds(10), bounds.Call);
        Assert.Equal(TimeSpan.FromSeconds(10), bounds.Markers);
        Assert.Equal(TimeSpan.FromSeconds(15), bounds.Release(5000, 1));
        Assert.Equal(TimeSpan.FromSeconds(50), bounds.Release(20000, 0.5));
        Assert.Equal(ScratchRun.Ceiling([0.5, 2], [false, false], TimeSpan.Zero, 1, null), (Walk with { Floor = floor }).Ceiling);
        Assert.Equal(ScratchRun.UntilHangGuard, harness.Sessions.ScratchUntilGuard);
        Assert.Equal(ScratchRun.UntilHangGuard, bounds.UntilRelease);
    }

    [Fact]
    public void AnUntilStepCountsItsGuardAndTheStepAllowanceInTheCeilingInPlaceOfItsPace()
    {
        ScratchWalk until = Walk with
        {
            UntilSteps = new HashSet<int> { 1 },
            UntilGuard = ScratchRun.UntilHangGuard,
        };

        // 0.5 s + 10 s for the paced step, the guard + 10 s for the until step, 2 s for the last step's pace once more and 10 s
        // for the markers.
        double guard = ScratchRun.UntilHangGuard.TotalSeconds;
        var ceiling = TimeSpan.FromSeconds(47.5);
        Assert.Equal(ceiling, until.Ceiling);
        Assert.Equal(ceiling, ScratchRun.Ceiling([0.5, 2], [false, true], ScratchRun.UntilHangGuard, 1, null));
        Assert.Equal(
            $"the scene passed its ceiling of {Seconds(ceiling.TotalSeconds)} s of load-adjusted time (each of its 2 steps' pace + 10 s, "
                + $"an until step's guard of {Seconds(guard)} s in place of its pace, the last step's pace once more, and 10 s for the "
                + "markers), so its game was stopped",
            until.CeilingReason(string.Empty)
        );
        Assert.Equal(0.5 + guard + 2, until.RemainingSecondsAtMost(0));
        Assert.Equal(guard + 2, until.RemainingSecondsAtMost(1));
    }

    [Fact]
    public void AGivenCeilingIsNotFloored()
    {
        ScratchWalk given = Walk with { Given = new(5, ScratchCeiling.OptionsSource), Floor = Floor };

        Assert.Equal(TimeSpan.FromSeconds(5), given.Ceiling);
        Assert.Equal(
            "the scene passed its ceiling of 5 s of load-adjusted time, set by options.ceilings, so its game was stopped",
            given.CeilingReason(string.Empty)
        );
    }

    private static string Seconds(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture);
}
