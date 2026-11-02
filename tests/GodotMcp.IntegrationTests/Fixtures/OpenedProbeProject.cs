using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A copy of the InputProbe fixture the Godot editor has opened once (run_editor, then stop_project), then every file that first
/// open wrote (project.godot's rewrite, the .uid files) committed, so the tree is clean before the next editor opens it.
/// </summary>
public sealed class OpenedProbeProject : IAsyncLifetime
{
    private readonly ProbeProject _probe = new();

    public string Directory => _probe.Directory;

    public string ProjectFile => _probe.ProjectFile;

    public async ValueTask InitializeAsync()
    {
        await using SessionHarness harness = new();
        _ = await harness.Sessions.LaunchEditorAsync(ProbeRuns.Request(_probe.Directory, null, quiet: true, false), null, CancellationToken.None);
        _ = await harness.Sessions.StopAsync(null, CancellationToken.None);
        Git.CommitAll(_probe.Directory);
    }

    public ValueTask DisposeAsync()
    {
        _probe.Dispose();
        return ValueTask.CompletedTask;
    }
}
