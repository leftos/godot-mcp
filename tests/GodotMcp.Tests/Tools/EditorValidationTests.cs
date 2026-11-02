using GodotMcp.Server.Tools;
using GodotMcp.Tests.Session;
using GodotMcp.Tests.Wire;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>
/// editor's and get_editor_state's checks, which refuse before anything reaches a bridge: an unknown action, and a session that
/// is not an editor's (a fake attached game here; no Godot runs).
/// </summary>
public sealed class EditorValidationTests : IAsyncDisposable
{
    private const string Game = "game";
    private readonly RegistryHarness _harness = new();
    private readonly List<FakeBridge> _games = [];
    private readonly RuntimeTools _tools;

    public EditorValidationTests()
    {
        _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (FakeBridge game in _games)
        {
            game.Dispose();
        }
    }

    [Theory]
    [InlineData("build")]
    [InlineData("Open")]
    [InlineData("")]
    public async Task AnUnknownActionIsRefusedWithTheActionsListed(string action)
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.EditorAsync(action, cancellationToken: Token));

        Assert.Equal($"action must be one of open, save, save_all, close, play, stop, select_file; got '{action}'.", refused.Message);
    }

    [Fact]
    public async Task OpenWithoutASceneIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.EditorAsync("open", cancellationToken: Token));

        Assert.Equal("open needs scene, the res:// path of the scene to open.", refused.Message);
    }

    [Fact]
    public async Task SelectFileWithoutAPathIsRefused()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.EditorAsync("select_file", cancellationToken: Token));

        Assert.Equal("select_file needs path, the res:// file or folder to select.", refused.Message);
    }

    [Fact]
    public async Task BothToolsRefuseASessionThatIsNotAnEditors()
    {
        await AttachAsync();

        McpException action = await Assert.ThrowsAsync<McpException>(() => _tools.EditorAsync("stop", session: Game, cancellationToken: Token));
        McpException state = await Assert.ThrowsAsync<McpException>(() => _tools.GetEditorStateAsync(Game, Token));

        Assert.Equal("editor works only in an editor session; session 'game' is an attach session. Start one with run_editor.", action.Message);
        Assert.Equal(
            "get_editor_state works only in an editor session; session 'game' is an attach session. Start one with run_editor.",
            state.Message
        );
    }

    private async Task AttachAsync() => _games.Add(await _harness.AttachFakeGameAsync(_harness.Project(Game), Game, new FakeHello()));
}
