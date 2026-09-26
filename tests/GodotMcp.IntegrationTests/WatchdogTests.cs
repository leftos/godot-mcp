using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// A timed-out request told apart as a stuck main thread or a busy one, and stop_project killing a stuck game at once, against
/// the InputProbe in the real Godot.
/// </summary>
public sealed partial class WatchdogTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 60_000;
    private const string BlockMainThread = "OS.delay_msec(20000)\n\treturn true";
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;

    public WatchdogTests() => _tools = new RuntimeTools(_harness.Sessions);

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AStuckMainThreadIsReportedAsStuck()
    {
        LaunchResult launched = await LaunchAsync(TestContext.Current.CancellationToken);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(BlockMainThread, 2000));

        Assert.StartsWith(
            "'run_script' timed out after 2000 ms and the game did not answer a ping within 2 s: its main thread is stuck.\nProcess ",
            timedOut.Message,
            StringComparison.Ordinal
        );
        Match state = ProcessLine().Match(timedOut.Message);
        Assert.True(state.Success, timedOut.Message);
        int pid = int.Parse(state.Groups["pid"].Value, CultureInfo.InvariantCulture);
        Assert.NotEqual(launched.ProcessId, pid);
        using var wrapper = Process.GetProcessById(launched.ProcessId);
        using var game = Process.GetProcessById(pid);
        Assert.Equal(wrapper.ProcessName.Replace("_console", string.Empty, StringComparison.OrdinalIgnoreCase), game.ProcessName);
        Assert.InRange(long.Parse(state.Groups["cpu"].Value, CultureInfo.InvariantCulture), 0, 250);
        Assert.Equal("Wait", state.Groups["state"].Value);
        Assert.Contains("\nLast stderr lines:\n", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ASlowScriptOnALiveThreadIsReportedAsBusy()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() =>
            RunScriptAsync("await scene_tree.create_timer(10.0).timeout\n\treturn true", 1000)
        );

        Assert.Equal(
            "'run_script' timed out after 1000 ms, but the game answered a ping, so its main thread is running; "
                + "a script that needs longer can raise timeoutMs.",
            timedOut.Message
        );
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StopKillsAStuckGameAtOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await LaunchAsync(TestContext.Current.CancellationToken);

        // The timed-out call's own probe proves the main thread is inside the delay before stop is asked.
        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(BlockMainThread, 1000));
        Assert.Contains("its main thread is stuck", timedOut.Message, StringComparison.Ordinal);
        var elapsed = Stopwatch.StartNew();
        StopResult stopped = await _harness.Sessions.StopAsync(null, cancellation);
        elapsed.Stop();

        Assert.True(stopped.Killed);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4), $"stop took {elapsed.Elapsed.TotalSeconds:0.00} s");
        Assert.False(_harness.Sessions.GetDebugOutput(null, 1, null).Running);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StopOfAHealthyGameStillQuitsGracefully()
    {
        await LaunchAsync(TestContext.Current.CancellationToken);

        StopResult stopped = await _harness.Sessions.StopAsync(null, TestContext.Current.CancellationToken);

        Assert.False(stopped.Killed);
        Assert.False(_harness.Sessions.GetDebugOutput(null, 1, null).Running);
    }

    [GeneratedRegex(@"\nProcess (?<pid>\d+): (?<cpu>\d+) ms CPU over 1 s, \d+ threads, main thread (?<state>\w+)(/\w+)?\.\n")]
    private static partial Regex ProcessLine();

    private Task<LaunchResult> LaunchAsync(CancellationToken cancellation) =>
        _harness.Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], true, false), null, cancellation);

    private Task<string> RunScriptAsync(string body, int timeoutMs)
    {
        string script = $"extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t{body}\n";
        return _tools.RunScriptAsync(script, timeoutMs, cancellationToken: TestContext.Current.CancellationToken);
    }
}
