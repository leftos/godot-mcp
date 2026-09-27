using System.Text.Json;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// cs_members', cs_get's, cs_set's, cs_call's and run_csharp's argument checks, which refuse before anything reaches a game; no
/// Godot runs here.
/// </summary>
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

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public async Task RunCSharpRefusesEmptyCode(string code)
    {
        // An out-of-range option too: the empty code is refused first.
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RunCSharpAsync(code, new RunCSharpOptions(MaxDepth: 0), cancellationToken: Token)
        );

        Assert.Equal("code is empty. Pass a method body: statements, or one expression whose value is returned.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task RunCSharpRefusesAMaxDepthOutOfRange(int maxDepth)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RunCSharpAsync("1 + 1", new RunCSharpOptions(MaxDepth: maxDepth), cancellationToken: Token)
        );

        Assert.Equal($"maxDepth must be 1 to 32; got {maxDepth}.", refused.Message);
    }

    [Fact]
    public async Task RunCSharpRefusesATimeoutAbove120000()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RunCSharpAsync("1 + 1", new RunCSharpOptions(TimeoutMs: 120_001), cancellationToken: Token)
        );

        Assert.Equal("timeoutMs must be 1 to 120000; got 120001.", refused.Message);
    }

    [Fact]
    public async Task RunCSharpRefusesANullUsing()
    {
        // The JSON array ["System.Text", null] arrives as a string array holding a null.
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.RunCSharpAsync("1 + 1", new RunCSharpOptions(Usings: ["System.Text", null!]), cancellationToken: Token)
        );

        Assert.Equal("usings[1] is empty", refused.Message);
    }

    [Fact]
    public void ACompileFailureListsTwentyErrorsThenCountsTheRest()
    {
        SnippetDiagnostic[] errors =
        [
            new(0, 0, "CS1000", "outside"),
            .. Enumerable.Range(1, 22).Select(line => new SnippetDiagnostic(line, 5, "CS0103", $"missing {line}")),
        ];

        string[] lines = RuntimeTools.CompileFailure(errors).Split('\n');

        Assert.Equal(22, lines.Length);
        Assert.Equal("run_csharp failed: the snippet does not compile:", lines[0]);
        Assert.Equal("(wrapper): error CS1000: outside", lines[1]);
        Assert.Equal("snippet(19,5): error CS0103: missing 19", lines[20]);
        Assert.Equal("… and 3 more", lines[21]);
    }

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
