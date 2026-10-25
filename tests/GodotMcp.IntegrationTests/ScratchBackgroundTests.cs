using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches with options.background and scratch_status, in the real Godot, on the InputProbe fixture's ScratchGreen scene
/// (and its scratch_until ScratchUntilReleased, for a windowed drive beside a run the test holds live):
/// a run that returns at once and finishes done, a stop mid-run, a second run refused while one is live, and the server's exit
/// mid-run. Each fact is judged by the order of the run's states and lines, never by its times. It shares
/// <see cref="ScratchRunnerTests"/>' helpers.
/// </summary>
public sealed partial class ScratchBackgroundTests : IAsyncDisposable
{
    private const string Scene = "ScratchGreen";

    /// <summary>The InputProbe fixture's scratch_until scene that holds its first step until the test writes its release file.</summary>
    private const string Released = "ScratchUntilReleased";

    // A hang guard for each wait on the run's files, never reached by a passing run: on a loaded machine the run's launch and
    // its scene's ceiling may each run to their backstops, 5 times their load-adjusted budgets in wall time, about 6 minutes.
    private static readonly TimeSpan WaitCeiling = TimeSpan.FromMinutes(10);

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ScratchTools _scratch;

    public ScratchBackgroundTests()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(_probe.Directory, "scratch"));
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABackgroundRunReturnsAtOnceShowsItsPlayingSceneAndEndsWithItsCompletionLineAndTheSyncResultsShape()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject started = await StartAsync(pace: 5, cancellation);
        string runId = started["runId"]!.GetValue<string>();
        Assert.Equal([Scene], started["scenes"]!.AsArray().Select(scene => scene!.GetValue<string>()));
        Assert.Equal(ScratchBackgroundRun.StatusPath(_probe.Directory, runId), started["statusFile"]!.GetValue<string>());

        JsonObject playing = await WaitForStatusAsync(started, status => SceneState(status) == "playing", cancellation);
        Assert.Equal("running", playing["state"]!.GetValue<string>());
        string completion = await WaitForCompletionAsync(started, cancellation);
        JsonObject status = await StatusAsync(runId, stop: null, cancellation);
        using (var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            ceiling.CancelAfter(WaitCeiling);
            while (status["live"]!.GetValue<bool>())
            {
                Assert.False(ceiling.IsCancellationRequested, $"the run stayed live past its completion line: {status.ToJsonString()}");
                await Task.Delay(20, cancellation);
                status = await StatusAsync(runId, stop: null, cancellation);
            }
        }

        JsonObject synchronous = JsonNode
            .Parse(await _scratch.RunScratchesAsync(_probe.Directory, [Scene], new ScratchOptions(Pace: 1), cancellation))!
            .AsObject();

        string expected = $"godot-mcp scratch run {runId} finished: done, passed true, green 1, red 0, killed 0, known 0";
        Assert.True(completion == expected, $"expected \"{expected}\", got \"{completion}\"; the run's status: {status.ToJsonString()}");
        Assert.Equal(("done", false), (status["state"]!.GetValue<string>(), status["live"]!.GetValue<bool>()));
        JsonObject result = status["result"]!.AsObject();
        Assert.Equal(Keys(synchronous), Keys(result));
        Assert.Equal(Keys(ScratchRunnerTests.Single(synchronous)), Keys(ScratchRunnerTests.Single(result)));
        Assert.Equal("green", ScratchRunnerTests.Single(result)["verdict"]!.GetValue<string>());
        string[] lines = LogLines(started);
        Assert.Equal($"godot-mcp scratch run {runId} scene {Scene} start", lines[0]);
        Assert.Contains($"godot-mcp scratch run {runId} scene {Scene} step 0/3 open", lines);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AStopMidRunEndsItStoppedWithItsCompletionLineAndNoGameLeftRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject started = await StartAsync(pace: 5, cancellation);
        string runId = started["runId"]!.GetValue<string>();
        await WaitForStatusAsync(started, status => SceneState(status) == "playing" && status["scenes"]![0]!["step"] is not null, cancellation);

        JsonObject stopped = await StatusAsync(runId, stop: true, cancellation);

        Assert.Equal(("stopped", false), (stopped["state"]!.GetValue<string>(), stopped["live"]!.GetValue<bool>()));
        Assert.Equal("stopped", SceneState(stopped));
        Assert.Null(stopped["scenes"]![0]!["verdict"]);
        Assert.Equal($"godot-mcp scratch run {runId} finished: stopped", LogLines(started)[^1]);
        Assert.Empty(_harness.Sessions.List(includeStopped: false));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ASecondRunWhileOneIsLiveIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject started = await StartAsync(pace: 5, cancellation);
        string runId = started["runId"]!.GetValue<string>();

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _scratch.RunScratchesAsync(_probe.Directory, [Scene], options: null, cancellation)
        );
        McpException background = await Assert.ThrowsAsync<McpException>(() => StartAsync(pace: 1, cancellation));

        Assert.Equal(ScratchRuns.StillRunning(runId), refused.Message);
        Assert.Equal(ScratchRuns.StillRunning(runId), background.Message);
        Assert.Equal("stopped", (await StatusAsync(runId, stop: true, cancellation))["state"]!.GetValue<string>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TheServersExitMidRunEndsItInterrupted()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        JsonObject started = await StartAsync(pace: 5, cancellation);
        string runId = started["runId"]!.GetValue<string>();
        await WaitForStatusAsync(started, status => SceneState(status) == "playing" && status["scenes"]![0]!["step"] is not null, cancellation);

        _harness.Sessions.Shutdown();

        JsonObject status = ReadStatus(started);
        Assert.Equal("interrupted", status["state"]!.GetValue<string>());
        Assert.Equal("interrupted", SceneState(status));
        Assert.Equal($"godot-mcp scratch run {runId} finished: interrupted", LogLines(started)[^1]);
        Assert.Equal("interrupted", (await StatusAsync(runId, stop: null, cancellation))["state"]!.GetValue<string>());
    }

    // ScratchUntilReleased holds its first step until the test writes its release file, so its quiet scratch session is live
    // through the drive's start and restart, which a scratch session no longer binds to its quiet and pad settings. Its step's
    // 120 s pace is a cap that turns the run red, never green, should the release not come.
    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AWindowedDriveRunsBesideABackgroundScratchRun()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string scratch = Path.Combine(_probe.Directory, "scratch");
        foreach (string file in new[] { "scratch_until_released.gd", "ScratchUntilReleased.tscn" })
        {
            File.Copy(Path.Combine(RepoPaths.InputProbe, "scratch_until", file), Path.Combine(scratch, file));
        }

        JsonObject started = JsonNode
            .Parse(await _scratch.RunScratchesAsync(_probe.Directory, [Released], new ScratchOptions(Background: true), cancellation))!
            .AsObject();
        string runId = started["runId"]!.GetValue<string>();
        await WaitForStatusAsync(started, status => SceneState(status) == "playing", cancellation);
        ProjectTools project = new(_harness.Sessions);

        JsonObject launched = JsonNode
            .Parse(
                await project.RunProjectAsync(
                    _probe.Directory,
                    options: new RunOptions(Quiet: false, Session: "drive"),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();
        JsonObject afterLaunch = await StatusAsync(runId, stop: null, cancellation);
        JsonObject restarted = JsonNode.Parse(await project.RestartProjectAsync(session: "drive", cancellationToken: cancellation))!.AsObject();
        JsonObject afterRestart = await StatusAsync(runId, stop: null, cancellation);
        await File.WriteAllTextAsync(Path.Combine(_probe.Directory, ".godot", "scratch_released.marker"), "released", cancellation);
        await project.StopProjectAsync("drive", cancellationToken: cancellation);
        string completion = await WaitForCompletionAsync(started, cancellation);

        Assert.Equal(("drive", false), (launched["session"]!.GetValue<string>(), launched["quiet"]!.GetValue<bool>()));
        Assert.Equal("drive", restarted["session"]!.GetValue<string>());
        Assert.All([afterLaunch, afterRestart], status => Assert.Equal((Released, "playing"), PlayingScene(status)));
        Assert.Equal($"godot-mcp scratch run {runId} finished: done, passed true, green 1, red 0, killed 0, known 0", completion);
    }

    [GeneratedRegex(@"^godot-mcp scratch run \S+ finished:")]
    private static partial Regex Completion();

    private async Task<JsonObject> StartAsync(double pace, CancellationToken cancellation) =>
        JsonNode
            .Parse(await _scratch.RunScratchesAsync(_probe.Directory, [Scene], new ScratchOptions(Pace: pace, Background: true), cancellation))!
            .AsObject();

    private async Task<JsonObject> StatusAsync(string runId, bool? stop, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.ScratchStatusAsync(_probe.Directory, runId, stop, only: null, cancellation))!.AsObject();

    /// <summary>
    /// Reads the status file until <paramref name="holds"/>, failing as soon as the run is no longer running without it, or once
    /// the wait's ceiling passes.
    /// </summary>
    private static async Task<JsonObject> WaitForStatusAsync(JsonObject started, Func<JsonObject, bool> holds, CancellationToken cancellation)
    {
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(WaitCeiling);
        JsonObject status = ReadStatus(started);
        while (!holds(status))
        {
            Assert.True(
                status["state"]!.GetValue<string>() == "running",
                $"the run ended before the status held what the test waits for: {status.ToJsonString()}"
            );
            Assert.False(ceiling.IsCancellationRequested, $"the status never held what the test waits for: {status.ToJsonString()}");
            await Task.Delay(20, cancellation);
            status = ReadStatus(started);
        }

        return status;
    }

    /// <summary>Reads the log until its completion line, which must be its last, failing once the wait's ceiling passes.</summary>
    private static async Task<string> WaitForCompletionAsync(JsonObject started, CancellationToken cancellation)
    {
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ceiling.CancelAfter(WaitCeiling);
        string[] lines = LogLines(started);
        while (!lines.Any(line => Completion().IsMatch(line)))
        {
            Assert.False(ceiling.IsCancellationRequested, $"the log never got its completion line: {string.Join(" | ", lines)}");
            await Task.Delay(20, cancellation);
            lines = LogLines(started);
        }

        Assert.Single(lines, line => Completion().IsMatch(line));
        return lines[^1];
    }

    private static JsonObject ReadStatus(JsonObject started) => JsonNode.Parse(ReadShared(started["statusFile"]!.GetValue<string>()))!.AsObject();

    private static string[] LogLines(JsonObject started) =>
        ReadShared(started["logFile"]!.GetValue<string>()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A file's text, opened, read and closed with every sharing, as an outside reader should.</summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string SceneState(JsonObject status) => status["scenes"]![0]!["state"]!.GetValue<string>();

    private static (string Scene, string State) PlayingScene(JsonObject status) =>
        (status["scenes"]![0]!["scene"]!.GetValue<string>(), SceneState(status));

    private static string[] Keys(JsonObject value) => [.. value.Select(entry => entry.Key).Order(StringComparer.Ordinal)];
}
