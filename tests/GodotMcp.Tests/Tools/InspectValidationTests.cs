using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The inspection tools' argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class InspectValidationTests : IDisposable
{
    private static readonly JsonElement One = JsonSerializer.SerializeToElement(1);
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public InspectValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 501)]
    public async Task TreeOptionsOutOfRangeAreRefused(int? maxDepth, int? offset, int? limit)
    {
        TreeOptions options = new(maxDepth, offset, limit);

        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.GetSceneTreeAsync(options: options, cancellationToken: Token));

        Assert.DoesNotContain("No Godot session", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TreeWithoutASessionIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetSceneTreeAsync(options: new TreeOptions(0, 0, 500), cancellationToken: Token)
        );

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyNodeIsRefused(string node)
    {
        await AssertRefusedBeforeSessionAsync(() => _tools.InspectNodeAsync(node, cancellationToken: Token), "node");
        await AssertRefusedBeforeSessionAsync(() => _tools.SetPropertyAsync(node, "count", One, cancellationToken: Token), "node");
        await AssertRefusedBeforeSessionAsync(() => _tools.CallMethodAsync(node, "probe_add", cancellationToken: Token), "node");
    }

    [Fact]
    public async Task AnEmptyPropertyIsRefused()
    {
        await AssertRefusedBeforeSessionAsync(() => _tools.SetPropertyAsync("Probe", "", One, cancellationToken: Token), "property");
        await AssertRefusedBeforeSessionAsync(() => _tools.InspectNodeAsync("Probe", ["count", " "], cancellationToken: Token), "properties");
    }

    [Fact]
    public Task AnEmptyMethodIsRefused() =>
        AssertRefusedBeforeSessionAsync(() => _tools.CallMethodAsync("Probe", "", cancellationToken: Token), "method");

    [Theory]
    [InlineData(0)]
    [InlineData(120_001)]
    public Task ACallTimeoutOutOfRangeIsRefused(int timeoutMs) =>
        AssertRefusedBeforeSessionAsync(
            () => _tools.CallMethodAsync("Probe", "probe_add", options: new CallOptions(timeoutMs), cancellationToken: Token),
            "timeoutMs"
        );

    [Fact]
    public async Task ValidCallsWithoutASessionAreRefused()
    {
        JsonElement[] args = [One, One];

        await AssertNoSessionAsync(() => _tools.InspectNodeAsync("Probe", ["count"], cancellationToken: Token));
        await AssertNoSessionAsync(() => _tools.SetPropertyAsync("Probe", "count", One, cancellationToken: Token));
        await AssertNoSessionAsync(() => _tools.CallMethodAsync("Probe", "probe_add", args, new CallOptions(120_000), cancellationToken: Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task AssertRefusedBeforeSessionAsync(Func<Task<string>> call, string argument)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.StartsWith(argument, refused.Message, StringComparison.Ordinal);
    }

    private static async Task AssertNoSessionAsync(Func<Task<string>> call)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }
}
