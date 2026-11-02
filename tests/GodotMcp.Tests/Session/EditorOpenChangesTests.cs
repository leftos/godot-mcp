using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The warning run_editor adds when the editor's open changed files in the project: the project's git status after the open
/// against the one read before it.
/// </summary>
public sealed class EditorOpenChangesTests
{
    private const string Prefix = "The editor's open changed files in the project: ";
    private const string Suffix = ". Commit or revert them before committing other work.";

    [Fact]
    public void AStatusThatDidNotChangeIsNoWarning() => Assert.Null(EditorOpenChanges.Describe(" M notes.txt\n", " M notes.txt\n"));

    [Fact]
    public void AnEmptyStatusOnBothSidesIsNoWarning() => Assert.Null(EditorOpenChanges.Describe("", ""));

    [Fact]
    public void TheFilesAnOpenChangedAreNamedInAftersOrder() =>
        Assert.Equal(Prefix + "project.godot, a.gd.uid" + Suffix, EditorOpenChanges.Describe("", " M project.godot\n?? a.gd.uid\n"));

    [Fact]
    public void ALineTheStatusAlreadyHadIsIgnored() =>
        Assert.Equal(Prefix + "a.gd.uid" + Suffix, EditorOpenChanges.Describe(" M project.godot\n", " M project.godot\n?? a.gd.uid\n"));

    [Fact]
    public void AChangeUnderGodotsOwnFolderIsNoWarning() => Assert.Null(EditorOpenChanges.Describe("", "?? .godot/imported/x.ctex\n"));

    [Fact]
    public void AGodotFolderPathIsLeftOutOfTheNamedFiles() =>
        Assert.Equal(Prefix + "project.godot" + Suffix, EditorOpenChanges.Describe("", "?? .godot/imported/x.ctex\n M project.godot\n"));

    [Fact]
    public void OnlyTheFirstTenFilesAreNamed() =>
        Assert.Equal(
            Prefix + string.Join(", ", Enumerable.Range(1, 10).Select(number => $"f{number:00}.gd.uid")) + " and 2 more" + Suffix,
            EditorOpenChanges.Describe("", string.Concat(Enumerable.Range(1, 12).Select(number => $"?? f{number:00}.gd.uid\n")))
        );

    [Fact]
    public void ARenameIsNamedByItsNewPath() => Assert.Equal(Prefix + "new.gd" + Suffix, EditorOpenChanges.Describe("", "R  old.gd -> new.gd\n"));

    [Fact]
    public void AStatusGitCouldNotReadIsNoWarning()
    {
        Assert.Null(EditorOpenChanges.Describe(null, " M project.godot\n"));
        Assert.Null(EditorOpenChanges.Describe("", null));
    }
}
