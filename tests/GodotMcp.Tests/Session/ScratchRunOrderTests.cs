using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The order run_scratches starts a run's scenes in: a folder run's longest pace first, a listed run's own order.</summary>
public sealed class ScratchRunOrderTests
{
    private static readonly ScratchScenePlan[] Mixed = [Scene("A", 0.5), Scene("B", 3), Scene("C", 10), Scene("D", 0.5)];

    [Fact]
    public void AFolderRunStartsTheSlowestPaceFirst() => Assert.Equal([2, 1, 0, 3], ScratchRun.StartOrder(Mixed, listed: false));

    [Fact]
    public void AListedRunKeepsItsOrder() => Assert.Equal([0, 1, 2, 3], ScratchRun.StartOrder(Mixed, listed: true));

    [Fact]
    public void EqualPacesKeepNameOrder()
    {
        ScratchScenePlan[] scenes = [Scene("A", 1), Scene("B", 2), Scene("C", 1), Scene("D", 2), Scene("E", 1)];

        Assert.Equal([1, 3, 0, 2, 4], ScratchRun.StartOrder(scenes, listed: false));
    }

    private static ScratchScenePlan Scene(string name, double pace) => new(name, $"res://scratch/{name}.tscn", pace, []);
}
