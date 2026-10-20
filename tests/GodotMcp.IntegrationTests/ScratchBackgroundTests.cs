using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_scratches with options.background and scratch_status, in the real Godot, on the InputProbe fixture's ScratchGreen scene:
/// a run that returns at once and finishes done, a stop mid-run, a second run refused while one is live, and the server's exit
/// mid-run. Each fact is judged by the order of the run's states and lines, never by its times. It shares
/// <see cref="ScratchRunnerTests"/>' helpers.
/// </summary>
public sealed partial class ScratchBackgroundTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 180_000;
    private const string Scene = "ScratchGreen";

    private static readonly TimeSpan WaitCeiling = TimeSpan.FromSeconds(60);

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

    [Fact(Timeout = TestTimeoutMs)]
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

        Assert.Equal($"godot-mcp scratch run {runId} finished: done, passed true, green 1, red 0, killed 0, known 0", completion);
        Assert.Equal(("done", false), (status["state"]!.GetValue<string>(), status["live"]!.GetValue<bool>()));
        JsonObject result = status["result"]!.AsObject();
        Assert.Equal(Keys(synchronous), Keys(result));
        Assert.Equal(Keys(ScratchRunnerTests.Single(synchronous)), Keys(ScratchRunnerTests.Single(result)));
        Assert.Equal("green", ScratchRunnerTests.Single(result)["verdict"]!.GetValue<string>());
        string[] lines = LogLines(started);
        Assert.Equal($"godot-mcp scratch run {runId} scene {Scene} start", lines[0]);
        Assert.Contains($"godot-mcp scratch run {runId} scene {Scene} step 0/3 open", lines);
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [Fact(Timeout = TestTimeoutMs)]
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

    [GeneratedRegex(@"^godot-mcp scratch run \S+ finished:")]
    private static partial Regex Completion();

    private async Task<JsonObject> StartAsync(double pace, CancellationToken cancellation) =>
        JsonNode
            .Parse(await _scratch.RunScratchesAsync(_probe.Directory, [Scene], new ScratchOptions(Pace: pace, Background: true), cancellation))!
            .AsObject();

    private async Task<JsonObject> StatusAsync(string runId, bool? stop, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.ScratchStatusAsync(_probe.Directory, runId, stop, cancellation))!.AsObject();

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

    private static string[] Keys(JsonObject value) => [.. value.Select(entry => entry.Key).Order(StringComparer.Ordinal)];
}
