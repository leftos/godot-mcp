using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches' scratch.expect and scratch.expectFor against the InputProbe fixture's scratch scenes, in the real Godot: a line
/// a scene prints from its _exit_tree satisfies its expectFor entry, and an expected line a scene never prints turns it red with
/// the pattern in exit.missing. A class of its own so the scratch group, <see cref="ScratchRunnerTests"/>', stays inside its
/// ceiling.
/// </summary>
public sealed class ScratchExpectRunTests : IAsyncDisposable
{
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchExpectRunTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        ScratchFolderTests.CopyScratchFolder(_probe);
    }

    public ValueTask DisposeAsync() => ScratchFolderTests.DisposeProbeAsync(_harness, _probe);

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ALineTheSceneExpectsPrintedFromItsExitTreeKeepsItGreen()
    {
        string profile =
            """{ "scratch": { "folder": "scratch", "expectFor": { "ScratchExitLine": ["^\\[scratch\\] store folder removed: exit-line$"] } } }""";
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject scene = ScratchRunnerTests.Single(await RunAsync("ScratchExitLine", TestContext.Current.CancellationToken));

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["exit"]!["missing"]);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ALineTheSceneExpectsButNeverPrintsTurnsItRedInTheExit()
    {
        string profile = """
            { "scratch": { "folder": "scratch", "expect": [
              { "pattern": "^\\[scratch\\] store folder removed: ", "reason": "the store folder is left behind" }
            ] } }
            """;
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), profile, TestContext.Current.CancellationToken);

        JsonObject scene = ScratchRunnerTests.Single(await RunAsync("ScratchGreen", TestContext.Current.CancellationToken));

        Assert.Equal("red", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["failedAt"]);
        Assert.Equal(0, scene["exit"]!["code"]!.GetValue<int>());
        Assert.Equal(
            ["`^\\[scratch\\] store folder removed: ` never appeared in the scene's output (the store folder is left behind)"],
            scene["exit"]!["missing"]!.AsArray().Select(entry => entry!.GetValue<string>())
        );
    }

    private async Task<JsonObject> RunAsync(string scene, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(_probe.Directory, [scene], options: null, cancellation))!.AsObject();
}
