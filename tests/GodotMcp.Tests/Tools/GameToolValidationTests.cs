using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>list_game_tools' option checks, which refuse before anything reaches a game, and the request it sends; no Godot runs here.</summary>
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task AssertNoSessionAsync(Func<Task<string>> call)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(call);

        Assert.Contains("No Godot session", refused.Message, StringComparison.Ordinal);
    }
}
