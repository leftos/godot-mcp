using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The .uid and .import files a prep's import created, changed or deleted: the snapshot of a project walk, taken before and
/// after the import, and the diff between the two.
/// </summary>
public sealed class PrepSidecarChangesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ANewUidFileIsWritten()
    {
        string project = Project();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        File.WriteAllText(Path.Combine(project, "a.gd.uid"), "uid://a");

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Equal(["res://a.gd.uid"], written);
        Assert.Null(removed);
    }

    [Fact]
    public void AChangedImportSidecarIsWritten()
    {
        string project = Project();
        string sidecar = Path.Combine(project, "icon.png.import");
        File.WriteAllText(sidecar, "[remap]\n");
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        File.WriteAllText(sidecar, "[remap]\n\n[params]\n\ncompress/mode=0\n");
        File.SetLastWriteTimeUtc(sidecar, DateTime.UtcNow.AddSeconds(5));

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Equal(["res://icon.png.import"], written);
        Assert.Null(removed);
    }

    [Fact]
    public void AnUntouchedSidecarIsNotListed()
    {
        string project = Project();
        File.WriteAllText(Path.Combine(project, "b.gd.uid"), "uid://b");
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Null(written);
        Assert.Null(removed);
    }

    [Fact]
    public void ADeletedSidecarIsRemoved()
    {
        string project = Project();
        string sidecar = Path.Combine(project, "old.png.import");
        File.WriteAllText(sidecar, "[remap]\n");
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        File.Delete(sidecar);

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Null(written);
        Assert.Equal(["res://old.png.import"], removed);
    }

    [Fact]
    public void ASidecarInASubfolderIsNamedWithForwardSlashes()
    {
        string project = Project();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        string scripts = Path.Combine(project, "Scripts", "Input");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "KeyboardRamps.cs.uid"), "uid://k");

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Equal(["res://Scripts/Input/KeyboardRamps.cs.uid"], written);
        Assert.Null(removed);
    }

    [Theory]
    [InlineData(".hidden", null)]
    [InlineData("ignored", ".gdignore")]
    [InlineData("nested", "project.godot")]
    [InlineData(".godot", null)]
    public void FoldersTheScanSkipsAreLeftOut(string folderName, string? marker)
    {
        string project = Project();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        string folder = Path.Combine(project, folderName);
        Directory.CreateDirectory(folder);
        if (marker is not null)
        {
            File.WriteAllText(Path.Combine(folder, marker), string.Empty);
        }

        File.WriteAllText(Path.Combine(folder, "a.gd.uid"), "uid://a");

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Null(written);
        Assert.Null(removed);
    }

    [Fact]
    public void OtherFilesAreNotListed()
    {
        string project = Project();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        File.WriteAllText(Path.Combine(project, "a.gd"), "extends Node\n");
        File.WriteAllText(Path.Combine(project, "a.txt"), "notes\n");

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Null(written);
        Assert.Null(removed);
    }

    [Fact]
    public void ListsAreSorted()
    {
        string project = Project();
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before = PrepScan.Sidecars(project);
        File.WriteAllText(Path.Combine(project, "b.gd.uid"), "uid://b");
        File.WriteAllText(Path.Combine(project, "a.gd.uid"), "uid://a");

        (IReadOnlyList<string>? written, IReadOnlyList<string>? removed) = Diff(project, before);

        Assert.Equal(["res://a.gd.uid", "res://b.gd.uid"], written);
        Assert.Null(removed);
    }

    private string Project()
    {
        string project = _temp.Combine("project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.godot"), string.Empty);
        return project;
    }

    private static (IReadOnlyList<string>? Written, IReadOnlyList<string>? Removed) Diff(
        string project,
        IReadOnlyDictionary<string, (long Length, DateTime LastWriteUtc)> before
    ) => PrepScan.SidecarChanges(project, before, PrepScan.Sidecars(project));
}
