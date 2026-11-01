using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol.Protocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Looking at a scratch scene as run_scratches walks it, in the real Godot: options.watch, the watch the runner arms on a
/// scene's session before its first step and returns as the scene's watch; and options.windowed, whose session a frame tool
/// reads while a background run walks it.
/// </summary>
public sealed class ScratchLookRunTests : ScratchRunBase
{
    private const string Green = "ScratchGreen";
    private const string Released = "ScratchUntilReleased";
    private const string QuitSound = "res://quit_sound/ScratchQuitSound.tscn";

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWatchArmedBeforeTheFirstStepSeesTheNoteBeforeItAndEveryStepsNote()
    {
        // The window outlasts any walk, so the runner's stop after the last step ends it, however slowly the steps play.
        ScratchWatchOptions watch = new(
            new WatchTracks(Properties: [new WatchPropertyTrack($"/root/{Green}", "_note")]),
            new WatchWindow(GameMs: 120000)
        );

        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, [Green], new ScratchOptions(Watch: watch), TestContext.Current.CancellationToken)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["warnings"]);
        JsonArray points = scene["watch"]!["tracks"]![0]!["points"]!.AsArray();
        Assert.Equal(["", "opened", "moved", "closed"], points.Select(point => point![2]!.GetValue<string>()));
        Assert.Equal(0, points[0]![0]!.GetValue<int>());
        int[] frames = [.. points.Select(point => point![0]!.GetValue<int>())];
        Assert.Equal(frames.Order(), frames);
        Assert.Equal(frames.Length, frames.Distinct().Count());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWatchSeesAVoiceTheGameStartsInItsQuitWork()
    {
        // The window ends long before the quit, so the watch is held there and only the bridge's last sample at the exit sees
        // the voice the scene's _exit_tree starts. The verdict is not asserted: a voice held at exit may be reported leaked
        // (the fixture script says why). The Node3D's global_position track is out of the tree by the last sample, which must
        // not read it: a read there logs an ERROR line at the exit.
        WatchTracks tracks = new(
            Properties: [new WatchPropertyTrack("Spot", "global_position")],
            Audio: new WatchAudioTrack(Players: new WatchAudioPlayers())
        );
        ScratchWatchOptions watch = new(tracks, new WatchWindow(GameMs: 200));
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "quit_sound"), Path.Combine(_probe.Directory, "quit_sound"));

        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, [QuitSound], new ScratchOptions(Watch: watch, Windowed: true), TestContext.Current.CancellationToken)
        );

        string[] warnings = [.. (scene["warnings"]?.AsArray() ?? []).Select(warning => warning!.GetValue<string>())];
        Assert.DoesNotContain(warnings, warning => warning.Contains("could not be collected at the game's exit", StringComparison.Ordinal));
        JsonArray voices = scene["watch"]!["audio"]!["voices"]!.AsArray();
        Assert.Contains(voices, voice => voice![2]!.GetValue<string>() == "start" && voice[3]!.GetValue<string>() == "/root/QuitSound");
        Assert.Null(scene["watch"]!["stopped"]);
        string[] exitLines = [.. (scene["exit"]?["lines"]?.AsArray() ?? []).Select(line => line!.GetValue<string>())];
        Assert.DoesNotContain(exitLines, line => line.StartsWith("ERROR:", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWatchOnANodeTheSceneLacksIsAWarningAndTheSceneStillPasses()
    {
        ScratchWatchOptions watch = new(new WatchTracks(Properties: [new WatchPropertyTrack("/root/Nowhere", "position")]));

        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, [Green], new ScratchOptions(Watch: watch), TestContext.Current.CancellationToken)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["watch"]);
        string warning = Assert.Single(scene["warnings"]!.AsArray())!.GetValue<string>();
        Assert.StartsWith("options.watch did not start: ", warning, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowedBackgroundScenesSessionTakesAScreenshotWhileItWalks()
    {
        // ScratchUntilReleased holds its first step until the test writes its release file, so the shot is taken mid-walk.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        foreach (string file in new[] { "scratch_until_released.gd", $"{Released}.tscn" })
        {
            File.Copy(Path.Combine(RepoPaths.InputProbe, "scratch_until", file), Path.Combine(_probe.Directory, "scratch", file));
        }

        JsonObject started = JsonNode
            .Parse(
                await _scratch.RunScratchesAsync(_probe.Directory, [Released], new ScratchOptions(Background: true, Windowed: true), cancellation)
            )!
            .AsObject();
        string runId = started["runId"]!.GetValue<string>();
        await WaitForStatusAsync(runId, status => status["scenes"]![0]!["step"] is not null, cancellation);
        RuntimeTools runtime = new(_harness.Sessions, TestCSharp.Unused());

        ContentBlock[] shot =
        [
            .. await runtime.TakeScreenshotAsync("path_only", session: $"InputProbe.scratch-{Released}", cancellationToken: cancellation),
        ];
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ".godot", "scratch_released.marker"), "released", cancellation);
        JsonObject done = await WaitForStatusAsync(runId, status => status["state"]!.GetValue<string>() != "running", cancellation);

        Assert.NotEmpty(shot);
        Assert.Equal("done", done["state"]!.GetValue<string>());
        Assert.True(done["result"]!["passed"]!.GetValue<bool>(), done.ToJsonString());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowedRunTakesAShotAtEachCapturedStep()
    {
        // ScratchUntilReleased's first step has an until, met at once since its release file is written first; its second is paced.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        foreach (string file in new[] { "scratch_until_released.gd", $"{Released}.tscn" })
        {
            File.Copy(Path.Combine(RepoPaths.InputProbe, "scratch_until", file), Path.Combine(_probe.Directory, "scratch", file));
        }

        Directory.CreateDirectory(Path.Combine(_probe.Directory, ".godot"));
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ".godot", "scratch_released.marker"), "released", cancellation);
        ScratchCaptureOptions capture = new(Steps: JsonSerializer.SerializeToElement(new object[] { 0, "done" }), Screenshot: true);

        JsonObject scene = ScratchRunnerTests.Single(
            await RunAsync(_probe.Directory, [Released], new ScratchOptions(Windowed: true, Capture: capture), cancellation)
        );

        Assert.Equal("green", scene["verdict"]!.GetValue<string>());
        Assert.Null(scene["warnings"]);
        string runId = Path.GetFileNameWithoutExtension(scene["log"]!.GetValue<string>())[^"yyyyMMddTHHmmss-xxxx".Length..];
        string folder = Path.Combine(_probe.Directory, ".godot", "godot-mcp", $"scratch-{runId}", Released);
        JsonArray details = scene["details"]!.AsArray();
        Assert.Equal(2, details.Count);
        foreach ((JsonNode? step, string name) in details.Zip(["0-hold.png", "1-done.png"]))
        {
            string shot = step!["screenshot"]!.GetValue<string>();
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, name)), Path.GetFullPath(shot));
            Assert.True(File.Exists(shot), shot);
        }
    }

    /// <summary>Reads the run's status until <paramref name="holds"/>, failing once the run ends without it.</summary>
    private async Task<JsonObject> WaitForStatusAsync(string runId, Func<JsonObject, bool> holds, CancellationToken cancellation)
    {
        while (true)
        {
            JsonObject status = JsonNode
                .Parse(await _scratch.ScratchStatusAsync(_probe.Directory, runId, stop: null, only: null, cancellation))!
                .AsObject();
            if (holds(status))
            {
                return status;
            }

            Assert.True(status["state"]!.GetValue<string>() == "running", $"the run ended before the status held what the test waits for: {status}");
            await Task.Delay(20, cancellation);
        }
    }
}
