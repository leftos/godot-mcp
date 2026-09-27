using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>snapshot_subtree's and diff_snapshots' argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class SnapshotValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public SnapshotValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task MaxNodesUnderOneIsRefused(int maxNodes)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.SnapshotSubtreeAsync(options: new SnapshotOptions(MaxNodes: maxNodes), cancellationToken: Token)
        );

        Assert.Equal($"maxNodes must be at least 1; got {maxNodes}.", refused.Message);
    }

    [Fact]
    public async Task EmptyNamesAreRefused()
    {
        await AssertRefusedAsync(() => _tools.SnapshotSubtreeAsync(" ", cancellationToken: Token), "node");
        await AssertRefusedAsync(
            () => _tools.SnapshotSubtreeAsync(options: new SnapshotOptions(Properties: ["x", ""]), cancellationToken: Token),
            "properties"
        );
        await AssertRefusedAsync(() => _tools.SnapshotSubtreeAsync(options: new SnapshotOptions(Ignore: [" "]), cancellationToken: Token), "ignore");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyBeforeIdIsRefused(string beforeId)
    {
        await AssertRefusedAsync(() => _tools.DiffSnapshotsAsync(beforeId, cancellationToken: Token), "beforeId");
        await AssertRefusedAsync(() => _tools.DiffSnapshotsAsync("s1", beforeId, cancellationToken: Token), "afterId");
    }

    [Fact]
    public async Task WithGoodArgumentsTheSessionIsLookedUp()
    {
        McpException snapshot = await Assert.ThrowsAsync<McpException>(() => _tools.SnapshotSubtreeAsync(cancellationToken: Token));
        McpException diff = await Assert.ThrowsAsync<McpException>(() => _tools.DiffSnapshotsAsync("s1", cancellationToken: Token));

        Assert.Contains("No Godot session", snapshot.Message, StringComparison.Ordinal);
        Assert.Contains("No Godot session", diff.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsAreTheWholeInspectorAndTwoThousandNodes()
    {
        RuntimeTools.SnapshotRequest request = RuntimeTools.CheckSnapshotRequest(null, null);

        Assert.Equal(new RuntimeTools.SnapshotRequest(null, null, null, 2000), request);
    }

    [Fact]
    public void BothToolsRunAsBatchSteps()
    {
        Assert.Contains("snapshot_subtree", RuntimeTools.BatchableTools);
        Assert.Contains("diff_snapshots", RuntimeTools.BatchableTools);
    }

    private static async Task AssertRefusedAsync(Func<Task<string>> call, string argument)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);
        Assert.StartsWith(argument, refused.Message, StringComparison.Ordinal);
    }
}
