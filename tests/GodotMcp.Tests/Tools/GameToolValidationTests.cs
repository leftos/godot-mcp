using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// list_game_tools' and call_game_tool's argument checks, which refuse before anything reaches a game, and the requests they
/// send; no Godot runs here.
/// </summary>
public sealed class GameToolValidationTests : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly SessionRegistry _sessions;
    private readonly RuntimeTools _tools;

    public GameToolValidationTests()
    {
        _sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_sessions, TestCSharp.Unused());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _listener.Dispose();
    }

    [Theory]
    [InlineData(-1, null, "offset must be 0 or more; got -1.")]
    [InlineData(null, 0, "limit must be 1 to 500; got 0.")]
    [InlineData(null, 501, "limit must be 1 to 500; got 501.")]
    public async Task APageOutOfRangeIsRefused(int? offset, int? limit, string message)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ListGameToolsAsync(new GameToolsOptions(Offset: offset, Limit: limit), cancellationToken: Token)
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 500)]
    public Task APageInRangeAsksForASession(int offset, int limit) =>
        AssertNoSessionAsync(() => _tools.ListGameToolsAsync(new GameToolsOptions("heal", offset, limit), cancellationToken: Token));

    [Fact]
    public Task NoOptionsAsksForASession() => AssertNoSessionAsync(() => _tools.ListGameToolsAsync(cancellationToken: Token));

    [Fact]
    public void ANameIsSentAsTheFilter()
    {
        JsonObject request = RuntimeTools.GameToolsRequest(new GameToolsOptions(Name: "Heal"));

        Assert.Equal("""{"op":"tools","name":"Heal"}""", request.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingOrEmptyNameSendsNoFilter(string? name)
    {
        JsonObject request = RuntimeTools.GameToolsRequest(new GameToolsOptions(Name: name));

        Assert.Equal("""{"op":"tools"}""", request.ToJsonString());
        Assert.Equal("""{"op":"tools"}""", RuntimeTools.GameToolsRequest(null).ToJsonString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnEmptyToolNameIsRefused(string name)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.CallGameToolAsync(name, cancellationToken: Token));

        Assert.Equal("name is empty. Pass a tool's name; list_game_tools lists them.", refused.Message);
    }

    [Theory]
    [InlineData("[1, 2]", "array")]
    [InlineData("\"hp\"", "string")]
    [InlineData("6", "number")]
    [InlineData("true", "boolean")]
    [InlineData("false", "boolean")]
    public async Task ArgsThatAreNotAnObjectAreRefusedNamingTheirKind(string args, string kind)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.CallGameToolAsync("SetHp", Json(args), cancellationToken: Token));

        Assert.Equal($"args must be an object keyed by parameter name; got {kind}.", refused.Message);
    }

    [Fact]
    public async Task AnArgumentNamedTwiceIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CallGameToolAsync("SetHp", Json("""{"enemy": true, "hp": 1, "hp": 2}"""), cancellationToken: Token)
        );

        Assert.Equal("args names 'hp' twice.", refused.Message);
    }

    [Theory]
    [InlineData(0, null, "timeoutMs must be 1 to 120000; got 0.")]
    [InlineData(120_001, null, "timeoutMs must be 1 to 120000; got 120001.")]
    [InlineData(null, 0, "maxDepth must be 1 to 32; got 0.")]
    [InlineData(null, 33, "maxDepth must be 1 to 32; got 33.")]
    public async Task ACallOptionOutOfRangeIsRefused(int? timeoutMs, int? maxDepth, string message)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.CallGameToolAsync("Heal", options: new CallGameToolOptions(timeoutMs, maxDepth), cancellationToken: Token)
        );

        Assert.Equal(message, refused.Message);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120_000, 32)]
    public Task CallOptionsInRangeAskForASession(int timeoutMs, int maxDepth) =>
        AssertNoSessionAsync(() =>
            _tools.CallGameToolAsync("Heal", Json("""{"amount": 3}"""), new CallGameToolOptions(timeoutMs, maxDepth), cancellationToken: Token)
        );

    [Fact]
    public Task ACallWithNoArgsOrOptionsAsksForASession() => AssertNoSessionAsync(() => _tools.CallGameToolAsync("Heal", cancellationToken: Token));

    [Fact]
    public void ACallSendsTheNameTheNamedArgsAndTheDepth()
    {
        JsonObject request = RuntimeTools.CallGameToolRequest(
            "SetHp",
            Json("""{"enemy": true, "index": 0, "hp": 6}"""),
            new CallGameToolOptions(MaxDepth: 4)
        );

        Assert.Equal("""{"op":"tool_call","name":"SetHp","args":{"enemy":true,"index":0,"hp":6},"maxDepth":4}""", request.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    public void MissingNullOrEmptyArgsSendAnEmptyObjectAtTheDefaultDepth(string? args)
    {
        JsonObject request = RuntimeTools.CallGameToolRequest("Heal", args is null ? null : Json(args), null);

        Assert.Equal("""{"op":"tool_call","name":"Heal","args":{},"maxDepth":8}""", request.ToJsonString());
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task AssertNoSessionAsync(Func<Task<string>> call)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }
}
