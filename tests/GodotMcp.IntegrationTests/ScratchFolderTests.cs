using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches over the whole scratch folder of the InputProbe fixture, in the real Godot: every scene to its verdict in the
/// folder's order, with what the prep did and each scene's pace, one scene at a time and three at once. Each fact plays all 17
/// scenes, so the class is a group of its own, apart from <see cref="ScratchRunnerTests"/>, whose helpers it shares.
/// </summary>
public sealed class ScratchFolderTests : IAsyncDisposable
{
    private static readonly string[] FolderScenes =
    [
        "ScratchBeside",
        "ScratchBootError",
        "ScratchBusy",
        "ScratchFlood",
        "ScratchGreen",
        "ScratchLateError",
        "ScratchLateLine",
        "ScratchLaunchNoise",
        "ScratchLeak",
        "ScratchMarker",
        "ScratchNoProtocol",
        "ScratchNoSteps",
        "ScratchPushError",
        "ScratchQuit",
        "ScratchStepPace",
        "ScratchStepPaceAfter",
        "ScratchTwoRed",
    ];

    private static readonly string[] FolderVerdicts =
    [
        "green",
        "red",
        "killed",
        "red",
        "green",
        "red",
        "red",
        "green",
        "known",
        "green",
        "red",
        "no-steps",
        "red",
        "red",
        "green",
        "red",
        "red",
    ];

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchFolderTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(_probe.Directory, "scratch"));
    }

    public async ValueTask DisposeAsync()
    {
        // A game holds its project's folder until it exits, and the harness kills and waits for every game still running, so
        // the copy is deleted only once it is done.
        try
        {
            await _harness.DisposeAsync();
        }
        finally
        {
            _probe.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheScratchFolderPlaysEachSceneToItsVerdict()
    {
        JsonObject result = await RunAsync(options: null, TestContext.Current.CancellationToken);

        AssertFolderVerdicts(result);
        JsonNode prep = result["prep"]!;
        Assert.False(string.IsNullOrEmpty(prep["build"]!.GetValue<string>()), result.ToJsonString());
        Assert.False(string.IsNullOrEmpty(prep["import"]!.GetValue<string>()), result.ToJsonString());
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.All(scenes, scene => Assert.Null(scene!["alone"]));
        Assert.Equal(
            [1, 0.5, 0.1, 0.5, 0.5, 2, 0.5, 0.5, 0.5, 2, 0.5, 0.5, 0.5, 0.5, 0.25, 0.1, 0.5],
            scenes.Select(scene => scene!["pace"]!.GetValue<double>())
        );
        Assert.Equal("boot", scenes[1]!["failedAt"]!["name"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ThreeAtOnceTheFolderPlaysToTheSameVerdictsInTheListedOrder()
    {
        JsonObject result = await RunAsync(new ScratchOptions(Parallel: 3), TestContext.Current.CancellationToken);

        AssertFolderVerdicts(result);
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Null(scenes[1]!["alone"]);
        Assert.False(scenes[2]!["alone"]!.GetValue<bool>());
        Assert.Null(scenes[10]!["alone"]);
    }

    private static void AssertFolderVerdicts(JsonObject result)
    {
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(FolderScenes, scenes.Select(scene => scene!["scene"]!.GetValue<string>()));
        Assert.Equal(FolderVerdicts, scenes.Select(scene => scene!["verdict"]!.GetValue<string>()));
        Assert.Equal(FolderScenes.Select(name => "InputProbe.scratch-" + name), scenes.Select(scene => scene!["session"]!.GetValue<string>()));
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal((5, 9, 1, 1, 1), Counts(result));
    }

    private async Task<JsonObject> RunAsync(ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(_probe.Directory, scenes: null, options, cancellation))!.AsObject();

    private static (int Green, int Red, int Known, int NoSteps, int Killed) Counts(JsonObject result) =>
        (
            result["green"]!.GetValue<int>(),
            result["red"]!.GetValue<int>(),
            result["known"]!.GetValue<int>(),
            result["noSteps"]!.GetValue<int>(),
            result["killed"]!.GetValue<int>()
        );
}
