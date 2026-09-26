using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>Launching the real Godot on the InputProbe with the bridge injected, and leaving no trace after.</summary>
public sealed class SessionLifecycleTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 45_000;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task LaunchHandshakesPassesUserArgsAndStopsWithoutATrace()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        byte[] projectBefore = File.ReadAllBytes(_probe.ProjectFile);

        await _harness.Session.LaunchAsync(Request(userArgs: ["--hello", "a b"]), cancellation);
        Assert.True(File.Exists(_probe.OverrideFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
        JsonNode? pong = await _harness.Session.SendAsync("ping", null, PingTimeout, cancellation);
        Assert.True(pong?["pong"]?.GetValue<bool>());
        Assert.True(await StdoutContainsAsync("[probe] ready args=[\"--hello\",\"a b\"]"));
        StopResult stopped = await _harness.Session.StopAsync(cancellation);

        Assert.False(stopped.Killed);
        Assert.True(stopped.OverrideRemoved);
        Assert.False(_harness.Session.GetDebugOutput(1).Running);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(projectBefore, File.ReadAllBytes(_probe.ProjectFile));
        Assert.Equal(string.Empty, Git.Status(_probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RefusesTheProjectsOwnOverrideAndLeavesItByteIdentical()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        File.WriteAllText(_probe.OverrideFile, "[application]\nconfig/name=\"Mine\"\n");
        byte[] before = File.ReadAllBytes(_probe.OverrideFile);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Session.LaunchAsync(Request(), cancellation));

        Assert.Contains("override.cfg", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_probe.OverrideFile));
        Assert.False(_harness.Session.GetDebugOutput(1).Running);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ReplacesAStaleMarkedOverrideAndRemovesItAfter()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        File.WriteAllText(_probe.OverrideFile, $"{OverrideFile.Marker}\n[autoload]\n\nGodotMcpBridge=\"*D:/gone/old_bridge.gd\"\n");

        await _harness.Session.LaunchAsync(Request(), cancellation);
        string injected = File.ReadAllText(_probe.OverrideFile);
        await _harness.Session.StopAsync(cancellation);

        Assert.DoesNotContain("old_bridge.gd", injected, StringComparison.Ordinal);
        Assert.Contains(RepoPaths.BridgeScript.Replace('\\', '/'), injected, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_probe.OverrideFile));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task RefusesASecondLaunchWhileOneIsRunning()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await _harness.Session.LaunchAsync(Request(), cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => _harness.Session.LaunchAsync(Request(), cancellation));
        JsonNode? pong = await _harness.Session.SendAsync("ping", null, PingTimeout, cancellation);
        await _harness.Session.StopAsync(cancellation);

        Assert.Contains("already running", refused.Message, StringComparison.Ordinal);
        Assert.Contains("stop_project first", refused.Message, StringComparison.Ordinal);
        Assert.True(pong?["pong"]?.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task BackgroundRunStillHandshakes()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        LaunchResult launched = await _harness.Session.LaunchAsync(Request(background: true), cancellation);
        JsonNode? pong = await _harness.Session.SendAsync("ping", null, PingTimeout, cancellation);
        StopResult stopped = await _harness.Session.StopAsync(cancellation);

        Assert.True(launched.Background);
        Assert.True(pong?["pong"]?.GetValue<bool>());
        Assert.False(stopped.Killed);
    }

    private LaunchRequest Request(string[]? userArgs = null, bool background = false) =>
        new(_probe.Directory, null, [], userArgs ?? [], background, false);

    private Task<bool> StdoutContainsAsync(string line) =>
        Poll.UntilAsync(
            () => _harness.Session.GetDebugOutput(GodotRun.OutputCapacity).Stdout.Contains(line),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );
}
