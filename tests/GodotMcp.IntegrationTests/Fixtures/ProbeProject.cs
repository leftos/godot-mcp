using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>A copy of the InputProbe fixture in a fresh git repository with everything committed.</summary>
internal sealed class ProbeProject : IDisposable
{
    private readonly TempDirectory _temp = new();

    public ProbeProject()
    {
        Directory = _temp.Combine("InputProbe");
        CopyFixture(RepoPaths.InputProbe, Directory);
        Git.InitAndCommitAll(Directory);
    }

    public string Directory { get; }

    public string ProjectFile => Path.Combine(Directory, "project.godot");

    public string OverrideFile => Path.Combine(Directory, "override.cfg");

    public void Dispose() => _temp.Dispose();

    private static void CopyFixture(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (string file in System.IO.Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
