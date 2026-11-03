using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The rules an editor session (run_editor) follows apart from a run: its kind's name and restart.</summary>
public sealed class EditorSessionTests
{
    [Fact]
    public void ListSessionsNamesTheKindEditor() => Assert.Equal("editor", SessionRegistry.KindName(SessionKind.Editor));

    [Fact]
    public async Task EditorSessionNamesIsEmptyForAFolderWithoutAnEditorSession()
    {
        await using RegistryHarness harness = new();
        string project = harness.Project("alpha");

        Assert.Empty(harness.Sessions.EditorSessionNames(project));
    }

    [Fact]
    public async Task RestartProjectRefusesAnEditorSession()
    {
        await using RegistryHarness harness = new();
        using GodotSession editor = new(new SessionSpec("editor", harness.Path, SessionKind.Editor, false, true), harness.Sessions);

        SessionException refused = Assert.Throws<SessionException>(() => harness.Sessions.CheckCanRestart(editor));

        Assert.Equal(
            "restart_project does not restart an editor session; stop it with stop_project and start it again with run_editor.",
            refused.Message
        );
    }
}
