using System.Globalization;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;

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
        Assert.True(bounds.UntilReleaseOf(bounds.UntilGuard) >= Floor, $"the until release is {bounds.UntilReleaseOf(bounds.UntilGuard)}");
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
        Assert.Equal(ScratchRun.Ceiling([0.5, 2], [null, null], 1, null), (Walk with { Floor = floor }).Ceiling);
        Assert.Equal(ScratchRun.UntilHangGuard, harness.Sessions.ScratchUntilGuard);
        Assert.Equal(ScratchRun.UntilHangGuard, bounds.UntilReleaseOf(bounds.UntilGuard));
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
        Assert.Equal(ceiling, ScratchRun.Ceiling([0.5, 2], [null, ScratchRun.UntilHangGuard], 1, null));
        Assert.Equal(
            $"the scene passed its ceiling of {Seconds(ceiling.TotalSeconds)} s of load-adjusted time (each of its 2 steps' pace + 10 s, "
                + $"an until step's guard of {Seconds(guard)} s in place of its pace, the last step's pace once more, and 10 s for the "
                + "markers), so its game was stopped",
            until.CeilingReason(string.Empty)
        );
        Assert.Equal(0.5 + guard + 2, until.RemainingSecondsAtMost(0));
        Assert.Equal(guard + 2, until.RemainingSecondsAtMost(1));
    }

    // Step 1's until has a profile guard of 40 s, step 2's none, so it keeps the default.
    [Fact]
    public void EachUntilStepCountsItsOwnGuardInTheCeilingAndTheRemainingTimeAndOneWithoutAGuardTheDefault()
    {
        var guarded = new ScratchWalk([new(0.5, null), new(2, null) { Guard = 40 }, new(1, null)], [0, 1, 2], 1)
        {
            UntilSteps = new HashSet<int> { 1, 2 },
            UntilGuard = ScratchRun.UntilHangGuard,
        };

        Assert.Equal((TimeSpan.FromSeconds(40), ScratchRun.UntilHangGuard), (guarded.GuardOf(1), guarded.GuardOf(2)));
        // 0.5 s + 10 s for the paced step, 40 s + 10 s and 15 s + 10 s for the until steps, 1 s for the last step's pace once more
        // and 10 s for the markers.
        var ceiling = TimeSpan.FromSeconds(96.5);
        Assert.Equal(ceiling, guarded.Ceiling);
        Assert.Equal(ceiling, ScratchRun.Ceiling([0.5, 2, 1], [null, TimeSpan.FromSeconds(40), ScratchRun.UntilHangGuard], 1, null));
        Assert.Equal(TimeSpan.FromSeconds(50 + 25 + 10), ScratchRun.Ceiling([2, 1], [TimeSpan.FromSeconds(40), ScratchRun.UntilHangGuard], 1, 0));
        Assert.Equal(0.5 + 40 + 15 + 1, guarded.RemainingSecondsAtMost(0));
        Assert.Equal(40 + 15 + 1, guarded.RemainingSecondsAtMost(1));
        Assert.Equal(15 + 1, guarded.RemainingSecondsAtMost(2));
        Assert.Equal(
            "the scene passed its ceiling of 96.5 s of load-adjusted time (each of its 3 steps' pace + 10 s, an until step's guard of 40 s "
                + "in place of its pace, the last step's pace once more, and 10 s for the markers), so its game was stopped",
            guarded.CeilingReason(string.Empty)
        );
    }

    // The case table of a step's guard: its own, else its scene's, else none, which the walk takes as the default guard.
    [Theory]
    [InlineData(40.0, 30.0, 40.0)]
    [InlineData(null, 30.0, 30.0)]
    [InlineData(40.0, null, 40.0)]
    [InlineData(null, null, null)]
    public void AStepsGuardIsItsOwnElseItsScenesElseNone(double? stepGuard, double? sceneGuard, double? guard)
    {
        ScratchScenePlan scene = new("Guarded", "res://scratch/Guarded.tscn", 0.5, [])
        {
            Guard = sceneGuard,
            StepPaces = new Dictionary<string, ScratchPace> { ["batch"] = new(2, "the batch lands late") { Guard = stepGuard } },
            TimeScale = 1,
            EngineArgs = [],
        };

        (ScratchPace[] paces, string? error, _) = ScratchRun.StepPaces(scene, ["open", "batch"]);

        Assert.Null(error);
        Assert.Equal((sceneGuard, guard), (paces[0].Guard, paces[1].Guard));
        Assert.Equal(2, paces[1].Seconds);
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

    // A step's pace at the scene's time scale against its until's hang guard: the default one, equal to the pace, a step's own
    // guard of 40 s, and a pace divided by the time scale, since the pace is game time and the guard load-adjusted wall time, so a
    // scale above 1 shortens the pace and one below 1 lengthens it.
    [Theory]
    [InlineData(35.0, null, 1.0, true)]
    [InlineData(15.0, null, 1.0, false)]
    [InlineData(15.5, null, 1.0, true)]
    [InlineData(35.0, 40.0, 1.0, false)]
    [InlineData(20.0, null, 2.0, false)]
    [InlineData(10.0, null, 0.5, true)]
    public void AnUntilPaceAboveItsGuardIsListed(double pace, double? stepGuard, double timeScale, bool listed)
    {
        var walk = new ScratchWalk([new(pace, null) { Guard = stepGuard }], [0], timeScale) { UntilGuard = TimeSpan.FromSeconds(15) };

        int[] above = [.. walk.UntilPacesAboveGuard([0])];

        int[] expected = listed ? [0] : [];
        Assert.Equal(expected, above);
    }

    [Fact]
    public void AnUntilPaceAboveItsGuardIsWarnedOfWithThePaceAndTheGuard()
    {
        Assert.Equal(
            "step 26 'stick-tilts-the-chase-view': its pace of 35 s is longer than its until's hang guard of 15 s of "
                + "load-adjusted time, which ends the step in its pace's place; give the step a \"guard\" above 35 if its "
                + "condition can take that long",
            ScratchVerdict.UntilPaceAboveGuard(26, "stick-tilts-the-chase-view", 35, 1, 15)
        );
    }

    [Fact]
    public void AnUntilPaceWarningAtAnotherTimeScaleNamesTheWallTimeTheGuardMustExceed()
    {
        Assert.Equal(
            "step 3 'slow': its pace of 10 s at time scale 0.5 (20 s of wall time) is longer than its until's hang guard of "
                + "15 s of load-adjusted time, which ends the step in its pace's place; give the step a \"guard\" above 20 if "
                + "its condition can take that long",
            ScratchVerdict.UntilPaceAboveGuard(3, "slow", 10, 0.5, 15)
        );
    }

    private static string Seconds(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture);
}
