using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>cs_members' argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class CSharpValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public CSharpValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public async Task ATargetWithNothingIsRefused()
    {
        McpException refused = await RefusedAsync(new CSharpTarget());

        Assert.Equal("target takes exactly one of node, type or handle; got none.", refused.Message);
    }

    [Fact]
    public async Task ATargetWithTwoKindsIsRefusedNamingThem()
    {
        McpException named = await RefusedAsync(new CSharpTarget(Node: "Probe", Type: "CsProbe.CsTargets"));
        McpException other = await RefusedAsync(new CSharpTarget(Handle: "h1", Type: "CsProbe.CsTargets"));

        // The given ones come in the order node, type, handle however they were passed.
        Assert.Equal("target takes exactly one of node, type or handle; got node, type.", named.Message);
        Assert.Equal("target takes exactly one of node, type or handle; got type, handle.", other.Message);
    }

    [Fact]
    public async Task AnEmptyStringDoesNotCountAsATarget()
    {
        McpException refused = await RefusedAsync(new CSharpTarget(Node: "  "));

        Assert.Equal("target takes exactly one of node, type or handle; got none.", refused.Message);
        await AssertNoSessionAsync(() =>
            _tools.CsMembersAsync(new CSharpTarget(Node: string.Empty, Type: "CsProbe.CsTargets"), cancellationToken: Token)
        );
    }

    [Theory]
    [InlineData(-1, null, "offset must be 0 or more; got -1.")]
    [InlineData(null, 0, "limit must be 1 to 500; got 0.")]
    [InlineData(null, 501, "limit must be 1 to 500; got 501.")]
    public async Task APageOutOfRangeIsRefused(int? offset, int? limit, string message)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsMembersAsync(new CSharpTarget(Node: "Probe"), new MembersOptions(Offset: offset, Limit: limit), cancellationToken: Token)
        );

        Assert.Equal(message, refused.Message);
    }

    [Fact]
    public Task AValidCallWithNoSessionAsksForASession() =>
        AssertNoSessionAsync(() => _tools.CsMembersAsync(new CSharpTarget(Type: "CsProbe.CsTargets"), cancellationToken: Token));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<McpException> RefusedAsync(CSharpTarget target) =>
        Assert.ThrowsAsync<McpException>(() => _tools.CsMembersAsync(target, cancellationToken: Token));

    private static async Task AssertNoSessionAsync(Func<Task<string>> call)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }
}
