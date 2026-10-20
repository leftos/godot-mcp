using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches with a step whose until is a state condition on a C# node's _McpState, in the real Godot: the runner adds the
/// C# helper to the until's wait as wait_for adds it, so the bridge reads the state and the step ends green. It sits in the
/// csharp group, whose run lays out the C# helper. The run's 2 s pace holds the fixture's 0.2 s timer on a loaded machine: a
/// headless frame's delta is its real frame time, and the helper loads in the first frame.
/// </summary>
public sealed class ScratchCSharpStateTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 300_000;

    private readonly SessionHarness _harness = new();
    private readonly TempDirectory _cache = new();
    private readonly ScratchTools _scratch;

    public ScratchCSharpStateTests() =>
        _scratch = new ScratchTools(_harness.Sessions, new CSharpBridge(new HelperCache(_cache.Combine("dotnet")), Installation.FindDotnetExtension));

    public async ValueTask DisposeAsync()
    {
        // The game holds the helper's copy until it exits, so it is stopped before the cache is deleted.
        await _harness.DisposeAsync();
        _cache.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStepWhoseUntilIsACSharpNodesStateEndsGreenWhenTheStateHolds()
    {
        using var csProbe = CsProbeProject.Unbuilt();
        string scratch = Path.Combine(csProbe.Directory, "Scratch");
        Directory.CreateDirectory(scratch);
        foreach (string name in new[] { "ScratchUntilState.cs", "ScratchUntilState.tscn" })
        {
            File.Copy(Path.Combine(RepoPaths.Root, "tests", "fixtures", "CsProbe", "Scratch", name), Path.Combine(scratch, name));
        }

        string json = await _scratch.RunScratchesAsync(
            csProbe.Directory,
            ["res://Scratch/ScratchUntilState.tscn"],
            new ScratchOptions(Pace: 2, Details: true),
            TestContext.Current.CancellationToken
        );

        JsonNode scene = Assert.Single(JsonNode.Parse(json)!["scenes"]!.AsArray())!;
        Assert.True(scene["verdict"]!.GetValue<string>() == "green", scene.ToJsonString());
        JsonNode step = Assert.Single(scene["details"]!.AsArray())!;
        Assert.Equal("counted", step["status"]!.GetValue<string>());
        Assert.True(step["until"]!["met"]!.GetValue<bool>(), step.ToJsonString());
        Assert.InRange(step["until"]!["gameMs"]!.GetValue<int>(), 0, 1999);
    }
}
