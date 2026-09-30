using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>A copy of the InputProbe fixture in a fresh git repository with everything committed.</summary>
internal sealed class ProbeProject : IDisposable
{
    private readonly TempDirectory _temp = new();

    public ProbeProject()
    {
        Directory = _temp.Combine("InputProbe");
        CommittedFixture.CopyTo(RepoPaths.InputProbe, Directory);
    }

    public string Directory { get; }

    public string ProjectFile => Path.Combine(Directory, "project.godot");

    public string OverrideFile => Path.Combine(Directory, "override.cfg");

    public void Dispose() => _temp.Dispose();
}
