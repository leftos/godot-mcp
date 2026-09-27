using System.Text.Json;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>cs_members', cs_get's, cs_set's and cs_call's argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
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

    [Fact]
    public async Task GetAndSetRefuseATargetWithNothingOrTwoKinds()
    {
        CSharpTarget[] targets = [new CSharpTarget(), new CSharpTarget(Node: "Probe", Handle: "h1")];
        string[] messages =
        [
            "target takes exactly one of node, type or handle; got none.",
            "target takes exactly one of node, type or handle; got node, handle.",
        ];

        for (int i = 0; i < targets.Length; i++)
        {
            CSharpTarget target = targets[i];
            McpException get = await Assert.ThrowsAsync<McpException>(() => _tools.CsGetAsync(target, "Mood", cancellationToken: Token));
            McpException set = await Assert.ThrowsAsync<McpException>(() => _tools.CsSetAsync(target, "Mood", Value, cancellationToken: Token));

            Assert.Equal(messages[i], get.Message);
            Assert.Equal(messages[i], set.Message);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetAndSetRefuseAnEmptyMember(string member)
    {
        CSharpTarget target = new(Node: "Probe");

        McpException get = await Assert.ThrowsAsync<McpException>(() => _tools.CsGetAsync(target, member, cancellationToken: Token));
        McpException set = await Assert.ThrowsAsync<McpException>(() => _tools.CsSetAsync(target, member, Value, cancellationToken: Token));

        Assert.Equal(EmptyMember, get.Message);
        Assert.Equal(EmptyMember, set.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task GetRefusesAMaxDepthOutOfRange(int maxDepth)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsGetAsync(new CSharpTarget(Node: "Probe"), "Mood", new GetOptions(MaxDepth: maxDepth), cancellationToken: Token)
        );

        Assert.Equal($"maxDepth must be 1 to 32; got {maxDepth}.", refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(32)]
    public Task AValidGetWithNoSessionAsksForASession(int? maxDepth) =>
        AssertNoSessionAsync(() =>
            _tools.CsGetAsync(new CSharpTarget(Type: "CsProbe.Tally"), "Count", new GetOptions(maxDepth, Keep: true), cancellationToken: Token)
        );

    [Fact]
    public Task AValidSetWithNoSessionAsksForASession() =>
        AssertNoSessionAsync(() => _tools.CsSetAsync(new CSharpTarget(Type: "CsProbe.Tally"), "Count", Value, cancellationToken: Token));

    [Fact]
    public async Task CallRefusesATargetWithNothingOrTwoKinds()
    {
        McpException none = await Assert.ThrowsAsync<McpException>(() => _tools.CsCallAsync(new CSharpTarget(), "Hit", cancellationToken: Token));
        McpException two = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsCallAsync(new CSharpTarget(Type: "CsProbe.Tally", Handle: "h1"), "Bump", cancellationToken: Token)
        );

        Assert.Equal("target takes exactly one of node, type or handle; got none.", none.Message);
        Assert.Equal("target takes exactly one of node, type or handle; got type, handle.", two.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task CallRefusesAnEmptyMember(string member)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsCallAsync(new CSharpTarget(Node: "Probe"), member, cancellationToken: Token)
        );

        Assert.Equal("member is empty. Pass a method name, or .ctor with a {type} target; cs_members lists them.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120_001)]
    public async Task CallRefusesATimeoutOutOfRange(int timeoutMs)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsCallAsync(new CSharpTarget(Node: "Probe"), "Hit", options: new CsCallOptions(TimeoutMs: timeoutMs), cancellationToken: Token)
        );

        Assert.Equal($"timeoutMs must be 1 to 120000; got {timeoutMs}.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task CallRefusesAMaxDepthOutOfRange(int maxDepth)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CsCallAsync(new CSharpTarget(Node: "Probe"), "Hit", options: new CsCallOptions(MaxDepth: maxDepth), cancellationToken: Token)
        );

        Assert.Equal($"maxDepth must be 1 to 32; got {maxDepth}.", refused.Message);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120_000, 32)]
    public Task AValidCsCallWithNoSessionAsksForASession(int timeoutMs, int maxDepth) =>
        AssertNoSessionAsync(() =>
            _tools.CsCallAsync(
                new CSharpTarget(Type: "CsProbe.Greeter"),
                ".ctor",
                [Value],
                new CsCallOptions(Signature: ["int"], TypeArgs: ["int"], Keep: true, TimeoutMs: timeoutMs, MaxDepth: maxDepth),
                cancellationToken: Token
            )
        );

    private const string EmptyMember =
        "member is empty. Pass a property or field name, or a dotted path (Pending.Options[0]); cs_members lists them.";

    private static JsonElement Value => JsonSerializer.SerializeToElement(3);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<McpException> RefusedAsync(CSharpTarget target) =>
        Assert.ThrowsAsync<McpException>(() => _tools.CsMembersAsync(target, cancellationToken: Token));

    private static async Task AssertNoSessionAsync(Func<Task<string>> call)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }
}
