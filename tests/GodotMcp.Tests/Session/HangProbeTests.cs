using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using GodotMcp.Tests.Wire;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A timed-out request told apart as a busy or a stuck main thread, through an attached session whose game is a
/// <see cref="FakeBridge"/>. The stuck game's hello names this test process, so the probe has a real process to sample.
/// </summary>
public sealed partial class HangProbeTests : IAsyncDisposable
{
    private const string Script = "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\treturn 1\n";
    private const int TimeoutMs = 300;
    private static readonly TimeSpan AttachWait = TimeSpan.FromSeconds(10);
    private readonly TempDirectory _temp = new();
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private FakeBridge? _game;

    public HangProbeTests() => _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);

    public ValueTask DisposeAsync()
    {
        _game?.Dispose();
        _sessions.Dispose();
        _listener.Dispose();
        _temp.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AGameThatAnswersPingsButNotTheCommandIsBusy()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeBridge game = await AttachAsync(Environment.ProcessId, cancellation);
        using var stopAnswering = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task answering = game.AnswerPingsOnlyAsync(stopAnswering.Token);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));
        await stopAnswering.CancelAsync();
        await answering;

        Assert.Equal(
            "'run_script' timed out after 300 ms, but the game answered a ping, so its main thread is running; "
                + "a script that needs longer can raise timeoutMs.",
            timedOut.Message
        );
    }

    [Fact]
    public async Task AGameThatAnswersNothingIsStuckAndItsHellosProcessIsDescribed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AttachAsync(Environment.ProcessId, cancellation);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));

        string[] lines = timedOut.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("'run_script' timed out after 300 ms and the game did not answer a ping within 2 s: its main thread is stuck.", lines[0]);
        Assert.Matches(ProcessLine(), lines[1]);
        Assert.StartsWith($"Process {Environment.ProcessId}: ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStuckGameWhoseHelloHasNoPidSaysItsProcessIsUnknown()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await AttachAsync(null, cancellation);

        McpException timedOut = await Assert.ThrowsAsync<McpException>(() => RunScriptAsync(cancellation));

        Assert.EndsWith(
            "its main thread is stuck.\nThe game's process id is unknown: its bridge's hello carried none.",
            timedOut.Message,
            StringComparison.Ordinal
        );
    }

    [GeneratedRegex(@"^Process \d+: \d+ ms CPU over 1 s, \d+ threads, main thread \w+(/\w+)?\.$")]
    private static partial Regex ProcessLine();

    private Task<string> RunScriptAsync(CancellationToken cancellation) =>
        new RuntimeTools(_sessions).RunScriptAsync(Script, TimeoutMs, cancellationToken: cancellation);

    /// <summary>Attaches a session to a fake game whose hello carries <paramref name="processId"/>.</summary>
    private async Task<FakeBridge> AttachAsync(int? processId, CancellationToken cancellation)
    {
        string projectDir = _temp.Combine("game");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
        string attachFile = AttachFile.PathIn(projectDir);
        Task<AttachResult> attach = _sessions.AttachAsync(projectDir, null, AttachWait, false, cancellation);
        DateTime deadline = DateTime.UtcNow + AttachWait;
        while (!File.Exists(attachFile))
        {
            Assert.True(DateTime.UtcNow < deadline, "the attach file did not appear within 10 s");
            await Task.Delay(20, cancellation);
        }

        string token = JsonNode.Parse(File.ReadAllText(attachFile))!["token"]!.GetValue<string>();
        _game = await FakeBridge.DialAsync(_listener.Port, token, projectDir, processId, cancellation);
        await attach;
        return _game;
    }
}
