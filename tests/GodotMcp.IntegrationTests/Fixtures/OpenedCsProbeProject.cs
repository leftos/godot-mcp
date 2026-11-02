using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A built copy of the CsProbe fixture the Godot editor has opened once (run_editor with its prep, then stop_project), then every
/// file that first open wrote committed, so the tree is clean before the next editor opens it.
/// </summary>
public sealed class OpenedCsProbeProject : IAsyncLifetime
{
    private readonly CsProbeProject _probe = new();

    public string Directory => _probe.Directory;

    /// <summary>The copy's source file's full path.</summary>
    public string SourcePath(string fileName) => _probe.SourcePath(fileName);

    /// <summary>Replaces a source file's text, leaving the change uncommitted.</summary>
    public void WriteSource(string fileName, string content) => _probe.WriteSource(fileName, content);

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
