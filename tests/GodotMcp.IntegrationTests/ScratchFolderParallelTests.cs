using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches over the whole scratch folder of the InputProbe fixture, in the real Godot, three scenes at once: every scene to
/// the same verdict as one at a time, reported in the folder's order, and which scenes ran alone. The fact plays all 17 scenes,
/// so the class is a group of its own, apart from <see cref="ScratchFolderTests"/>, whose folder, verdicts and probe setup it
/// shares.
/// </summary>
public sealed class ScratchFolderParallelTests : IAsyncDisposable
{
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchFolderParallelTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        ScratchFolderTests.CopyScratchFolder(_probe);
    }

    public ValueTask DisposeAsync() => ScratchFolderTests.DisposeProbeAsync(_harness, _probe);

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ThreeAtOnceTheFolderPlaysToTheSameVerdictsInTheListedOrder()
    {
        JsonObject result = await ScratchFolderTests.RunFolderAsync(
            _scratch,
            _probe,
            new ScratchOptions(Parallel: 3),
            TestContext.Current.CancellationToken
        );

        ScratchFolderTests.AssertFolderVerdicts(result);
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Null(scenes[1]!["alone"]);
        Assert.False(scenes[2]!["alone"]!.GetValue<bool>());
        Assert.Null(scenes[10]!["alone"]);
    }
}
