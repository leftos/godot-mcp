using System.Text.Json.Nodes;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>get_game_state's argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class StateValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public StateValidationTests()
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
    [InlineData(-1)]
    [InlineData(501)]
    public async Task MaxNodesOutsideOneToFiveHundredIsRefused(int maxNodes)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetGameStateAsync(options: new StateOptions(MaxNodes: maxNodes), cancellationToken: Token)
        );

        Assert.Equal($"maxNodes must be 1 to 500; got {maxNodes}.", refused.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task MaxDepthOutsideOneToEightIsRefused(int maxDepth)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetGameStateAsync(options: new StateOptions(MaxDepth: maxDepth), cancellationToken: Token)
        );

        Assert.Equal($"maxDepth must be 1 to 8; got {maxDepth}.", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyKeyIsRefused(string key)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetGameStateAsync(options: new StateOptions(Keys: ["hp", key]), cancellationToken: Token)
        );

        Assert.Equal(
            "keys holds an empty key. Pass keys of the nodes' state or dotted paths into it (seats[0].hp), or leave keys out.",
            refused.Message
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AnEmptyNodeIsRefused(string node)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.GetGameStateAsync(node, cancellationToken: Token));

        Assert.StartsWith("node is empty.", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(500, 8)]
    public void TheRangesEndsAreTaken(int maxNodes, int maxDepth)
    {
        RuntimeTools.StateRequest request = RuntimeTools.CheckStateRequest("Main", new StateOptions(MaxNodes: maxNodes, MaxDepth: maxDepth));

        Assert.Equal(("Main", maxNodes, maxDepth), (request.Node, request.MaxNodes, request.MaxDepth));
    }

    [Fact]
    public void TheDefaultsAreEveryKeyFiftyNodesAndFourLevels()
    {
        RuntimeTools.StateRequest request = RuntimeTools.CheckStateRequest(null, null);

        Assert.Equal(new RuntimeTools.StateRequest(null, null, 50, 4), request);
    }

    [Fact]
    public void AnEmptyKeysListIsEveryKey()
    {
        RuntimeTools.StateRequest request = RuntimeTools.CheckStateRequest(null, new StateOptions(Keys: []));

        Assert.Null(request.Keys);
    }

    [Fact]
    public async Task WithGoodArgumentsTheSessionIsLookedUp()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.GetGameStateAsync("Main", new StateOptions(["hp"], 10, 2), cancellationToken: Token)
        );

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolRunsAsABatchStep() => Assert.Contains("get_game_state", RuntimeTools.BatchableTools);

    [Fact]
    public void WhyTheHelperCannotRunGoesToTheBridgeAsCsharpError()
    {
        JsonObject parameters = RuntimeTools.StateParameters(Defaults, new StateHelper(null, "The C# helper is not built."));

        Assert.Equal("""{"node":"","maxNodes":50,"maxDepth":4,"csharpError":"The C# helper is not built."}""", parameters.ToJsonString());
    }

    [Fact]
    public void TheHelpersCopyGoesToTheBridgeAsExtension()
    {
        JsonObject parameters = RuntimeTools.StateParameters(Defaults, new StateHelper(@"C:\cache\a\godot_mcp_dotnet.gdextension", null));

        Assert.Equal(@"C:\cache\a\godot_mcp_dotnet.gdextension", parameters["extension"]?.GetValue<string>());
        Assert.False(parameters.ContainsKey("csharpError"), parameters.ToJsonString());
    }

    [Fact]
    public void AProjectWithoutCSharpSendsNeither() =>
        Assert.Equal("""{"node":"","maxNodes":50,"maxDepth":4}""", RuntimeTools.StateParameters(Defaults, StateHelper.None).ToJsonString());

    private static RuntimeTools.StateRequest Defaults => RuntimeTools.CheckStateRequest(null, null);
}
