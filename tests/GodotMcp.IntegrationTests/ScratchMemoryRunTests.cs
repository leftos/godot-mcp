using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches' memory figures in the real Godot: a green scene's entry names the peak private memory its game ran at, so a
/// memory ceiling can be sized from a run that never came near one. Its ceilings are <see cref="ScratchRunnerTests"/>'.
/// </summary>
public sealed class ScratchMemoryRunTests : ScratchRunBase
{
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGreenSceneCarriesThePeakPrivateMemoryOfItsGame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch_memory"), Path.Combine(_probe.Directory, "scratch_memory"));

        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, ["res://scratch_memory/ScratchMemory.tscn"], new ScratchOptions(Pace: 1), cancellation)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.True(scene["peakPrivateMb"] is JsonValue peak && peak.GetValue<int>() >= 1536, scene.ToJsonString());
    }
}
