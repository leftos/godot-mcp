using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A built copy of the CsProbe fixture the Godot editor has opened once (run_editor with its prep, then stop_project), then every
/// file that first open wrote committed, so the tree is clean before the next editor opens it.
/// </summary>
public class OpenedCsProbeProject : IAsyncLifetime
{
    private readonly CsProbeProject _probe;

    public OpenedCsProbeProject() => _probe = new CsProbeProject();

    /// <summary>A copy with <paramref name="extraSources"/> built in (<see cref="CsProbeProject.BuiltWith"/>), committed on the first open.</summary>
    protected OpenedCsProbeProject(IReadOnlyDictionary<string, string> extraSources) => _probe = CsProbeProject.BuiltWith(extraSources);

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
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An opened CsProbe copy with the fake .xscn format's loader and saver (<see cref="FakeXamlFormat"/>) and a res://x.xscn.</summary>
public sealed class OpenedFakeXamlProject : OpenedCsProbeProject
{
    public OpenedFakeXamlProject()
        : base(FakeXamlFormat.Sources) =>
        File.WriteAllText(
            Path.Combine(Directory, "x.xscn"),
            FakeXamlFormat.CommentPrefix + "a fake-format scene\n[gd_scene format=3]\n\n[node name=\"X\" type=\"Node2D\"]\n"
        );
}
