using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches over the whole scratch folder of the InputProbe fixture, in the real Godot, one scene at a time: every scene to
/// its verdict in the folder's order, with what the prep did and each scene's pace. The fact plays all 21 scenes, so the class
/// is a group of its own, apart from <see cref="ScratchRunnerTests"/>, whose helpers it shares. It holds the folder's scenes and
/// verdicts, their assertion and the probe's setup and teardown, which <see cref="ScratchFolderParallelTests"/> shares.
/// </summary>
public sealed class ScratchFolderTests : IAsyncDisposable
{
    internal static readonly string[] FolderScenes =
    [
        "ScratchBeside",
        "ScratchBootError",
        "ScratchBusy",
        "ScratchExitHold",
        "ScratchExitLine",
        "ScratchFlood",
        "ScratchGreen",
        "ScratchHidden",
        "ScratchLateError",
        "ScratchLateLine",
        "ScratchLaunchNoise",
        "ScratchLeak",
        "ScratchLongNote",
        "ScratchMarker",
        "ScratchNoProtocol",
        "ScratchNoSteps",
        "ScratchPushError",
        "ScratchQuit",
        "ScratchStepPace",
        "ScratchStepPaceAfter",
        "ScratchTwoRed",
    ];

    internal static readonly string[] FolderVerdicts =
    [
        "green",
        "red",
        "killed",
        "green",
        "green",
        "red",
        "green",
        "green",
        "red",
        "red",
        "red",
        "known",
        "green",
        "green",
        "red",
        "no-steps",
        "red",
        "red",
        "green",
        "red",
        "red",
    ];

    /// <summary>
    /// The folder run's ceilings: ScratchBusy's spinning step is killed at a ceiling it is given, since the harness floors the
    /// ceiling its pace gives and its calls' timeouts at a hang guard in minutes.
    /// </summary>
    internal static Dictionary<string, double> FolderCeilings => new() { ["ScratchBusy"] = 5 };

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchFolderTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        CopyScratchFolder(_probe);
    }

    public ValueTask DisposeAsync() => DisposeProbeAsync(_harness, _probe);

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheScratchFolderPlaysEachSceneToItsVerdict()
    {
        JsonObject result = await RunFolderAsync(
            _scratch,
            _probe,
            new ScratchOptions(Ceilings: FolderCeilings),
            TestContext.Current.CancellationToken
        );

        AssertFolderVerdicts(result);
        JsonNode prep = result["prep"]!;
        Assert.False(string.IsNullOrEmpty(prep["build"]!.GetValue<string>()), result.ToJsonString());
        Assert.False(string.IsNullOrEmpty(prep["import"]!.GetValue<string>()), result.ToJsonString());
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.All(scenes, scene => Assert.Null(scene!["alone"]));
        Assert.Equal(
            [1, 0.5, 0.1, 0.1, 0.5, 0.5, 0.5, 0.5, 2, 0.5, 0.5, 0.5, 0.5, 2, 0.5, 0.5, 0.5, 0.5, 0.25, 0.1, 0.5],
            scenes.Select(scene => scene!["pace"]!.GetValue<double>())
        );
        Assert.Equal("boot", scenes[1]!["failedAt"]!["name"]!.GetValue<string>());
    }

    internal static void CopyScratchFolder(ProbeProject probe) =>
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(probe.Directory, "scratch"));

    internal static async ValueTask DisposeProbeAsync(SessionHarness harness, ProbeProject probe)
    {
        // A game holds its project's folder until it exits, and the harness kills and waits for every game still running, so
        // the copy is deleted only once it is done.
        try
        {
            await harness.DisposeAsync();
        }
        finally
        {
            probe.Dispose();
        }
    }

    internal static async Task<JsonObject> RunFolderAsync(
        ScratchTools scratch,
        ProbeProject probe,
        ScratchOptions? options,
        CancellationToken cancellation
    ) => JsonNode.Parse(await scratch.RunScratchesAsync(probe.Directory, scenes: null, options, cancellation))!.AsObject();

    internal static void AssertFolderVerdicts(JsonObject result)
    {
        JsonArray scenes = result["scenes"]!.AsArray();
        Assert.Equal(FolderScenes, scenes.Select(scene => scene!["scene"]!.GetValue<string>()));
        string[] verdicts = [.. scenes.Select(scene => scene!["verdict"]!.GetValue<string>())];
        if (!verdicts.SequenceEqual(FolderVerdicts))
        {
            Assert.Fail($"verdicts {string.Join(", ", verdicts)}; the run: {result.ToJsonString()}");
        }

        Assert.Equal(FolderScenes.Select(name => "InputProbe.scratch-" + name), scenes.Select(scene => scene!["session"]!.GetValue<string>()));
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal((8, 10, 1, 1, 1), Counts(result));
    }

    internal static (int Green, int Red, int Known, int NoSteps, int Killed) Counts(JsonObject result) =>
        (
            result["green"]!.GetValue<int>(),
            result["red"]!.GetValue<int>(),
            result["known"]!.GetValue<int>(),
            result["noSteps"]!.GetValue<int>(),
            result["killed"]!.GetValue<int>()
        );
}
