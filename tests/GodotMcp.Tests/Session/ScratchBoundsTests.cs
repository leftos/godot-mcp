using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The bounds a scene's walk holds its game to under the registry's <see cref="SessionRegistry.ScratchBoundFloor"/>: each the
/// longer of its own value and the floor, a ceiling the call or the profile gives excepted, and today's values at the default.
/// </summary>
public sealed class ScratchBoundsTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(10);

    private static readonly ScratchWalk Walk = new([new(0.5, null), new(2, null)], [0, 1], 1);

    [Fact]
    public void EachBoundIsAtLeastTheFloor()
    {
        ScratchBounds bounds = new(Floor);

        Assert.True(bounds.Call >= Floor, $"the call timeout is {bounds.Call}");
        Assert.True(bounds.Markers >= Floor, $"the marker wait is {bounds.Markers}");
        Assert.True(bounds.Release(5000, 1) >= Floor, $"the pace release is {bounds.Release(5000, 1)}");
        Assert.True(bounds.Release(20000, 0.5) >= Floor, $"the scaled pace release is {bounds.Release(20000, 0.5)}");
        Assert.True((Walk with { Floor = Floor }).Ceiling >= Floor, $"the walk's ceiling is {(Walk with { Floor = Floor }).Ceiling}");
    }

    [Fact]
    public void AFlooredBoundsTextNamesTheBoundUsed()
    {
        ScratchBounds bounds = new(Floor);

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
        ScratchBounds bounds = new(floor);

        Assert.Equal(TimeSpan.FromSeconds(10), bounds.Call);
        Assert.Equal(TimeSpan.FromSeconds(10), bounds.Markers);
        Assert.Equal(TimeSpan.FromSeconds(15), bounds.Release(5000, 1));
        Assert.Equal(TimeSpan.FromSeconds(50), bounds.Release(20000, 0.5));
        Assert.Equal(ScratchRun.Ceiling([0.5, 2], 1, null), (Walk with { Floor = floor }).Ceiling);
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
}
