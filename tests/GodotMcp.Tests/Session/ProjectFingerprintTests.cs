using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>A project folder's fingerprint: which files count, and which changes make two fingerprints differ.</summary>
public sealed class ProjectFingerprintTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _game;

    public ProjectFingerprintTests()
    {
        _game = _temp.Combine("game");
        Write("project.godot", "config_version=5\n");
        Write("main.gd", "extends Node\n");
        Write("scenes/level.tscn", "[gd_scene format=3]\n");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnUnchangedFolderGivesEqualFingerprints()
    {
        var first = ProjectFingerprint.Take(_game);
        var second = ProjectFingerprint.Take(_game);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public void AFileWrittenAgainWithTheSameSizeButALaterTimeChangesIt()
    {
        string level = Path.Combine(_game, "scenes", "level.tscn");
        File.SetLastWriteTimeUtc(level, DateTime.UtcNow.AddMinutes(-5));
        var before = ProjectFingerprint.Take(_game);

        File.SetLastWriteTimeUtc(level, DateTime.UtcNow);

        Assert.NotEqual(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void AFileWhoseSizeChangedChangesIt()
    {
        string main = Path.Combine(_game, "main.gd");
        DateTime written = File.GetLastWriteTimeUtc(main);
        var before = ProjectFingerprint.Take(_game);

        File.WriteAllText(main, "extends Node2D\n");
        File.SetLastWriteTimeUtc(main, written);

        Assert.NotEqual(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void AnAddedFileChangesIt()
    {
        var before = ProjectFingerprint.Take(_game);

        Write("enemy.gd", "extends Node\n");

        Assert.NotEqual(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void ADeletedFileChangesIt()
    {
        var before = ProjectFingerprint.Take(_game);

        File.Delete(Path.Combine(_game, "main.gd"));

        Assert.NotEqual(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void GitAndAGdignoredSubtreeAreLeftOut()
    {
        Write(".gdignore-free/kept.txt", "kept\n");
        Write("docs/.gdignore", string.Empty);
        var before = ProjectFingerprint.Take(_game);

        Write(".git/index", "changed\n");
        Write(".git/objects/ab/cdef", "object\n");
        Write("docs/notes.md", "ignored\n");
        Write("docs/deep/er.md", "ignored too\n");

        Assert.Equal(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void TheImportedFilesAndTheCachesUnderDotGodotCount()
    {
        var bare = ProjectFingerprint.Take(_game);
        Write(".godot/imported/icon.svg-1234.ctex", "texture\n");
        var imported = ProjectFingerprint.Take(_game);
        Write(".godot/uid_cache.bin", "cache\n");
        var uids = ProjectFingerprint.Take(_game);
        Write(".godot/global_script_class_cache.cfg", "list=[]\n");

        Assert.NotEqual(bare, imported);
        Assert.NotEqual(imported, uids);
        Assert.NotEqual(uids, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void TheRestOfDotGodotIsLeftOut()
    {
        var before = ProjectFingerprint.Take(_game);

        Write(".godot/godot-mcp/headless.log", "[godot-mcp] request 1 validate\n");
        Write(".godot/editor/filesystem_cache10", "cache\n");

        Assert.Equal(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void TheServersOwnOverrideComingAndGoingLeavesItUnchanged()
    {
        var before = ProjectFingerprint.Take(_game);

        Write("override.cfg", $"{OverrideFile.Marker}\n; owners: 1234\n[autoload]\n");
        var written = ProjectFingerprint.Take(_game);
        File.Delete(Path.Combine(_game, "override.cfg"));

        Assert.Equal(before, written);
        Assert.Equal(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void AUsersOwnOverrideCountsAndItsEditChangesIt()
    {
        var bare = ProjectFingerprint.Take(_game);
        Write("override.cfg", "[display]\n");
        string path = Path.Combine(_game, "override.cfg");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var before = ProjectFingerprint.Take(_game);

        Write("override.cfg", "[display]\nwindow/size/viewport_width=640\n");

        Assert.NotEqual(bare, before);
        Assert.Equal(4, before.Count);
        Assert.NotEqual(before, ProjectFingerprint.Take(_game));
    }

    [Fact]
    public void AJunctionToAnAncestorIsListedAsItselfAndNotFollowed()
    {
        string link = Path.Combine(_game, "scenes", "loop");
        CreateJunction(link, _game);
        try
        {
            var first = ProjectFingerprint.Take(_game);

            Assert.Equal(4, first.Count);
            Assert.Equal(first, ProjectFingerprint.Take(_game));
        }
        finally
        {
            // Removes the link alone, so the folder's cleanup never walks through it.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void AMissingFolderEqualsNoFingerprintOfAFolderThatWasThere()
    {
        string empty = _temp.Combine("empty");
        Directory.CreateDirectory(empty);
        var taken = ProjectFingerprint.Take(empty);

        var missing = ProjectFingerprint.Take(_temp.Combine("gone"));

        Assert.NotEqual(taken, missing);
        Assert.Equal(0, missing.Count);
    }

    /// <summary>
    /// A junction at <paramref name="link"/> to <paramref name="target"/>: <c>mklink /J</c> on Windows, which needs no privilege
    /// where a symbolic link does, and a symbolic link elsewhere.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        ProcessStartInfo start = new("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process mklink = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        // A line or two each way, which no pipe buffer fills, so reading one after the other cannot block.
        string output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, $"mklink /J exited {mklink.ExitCode}: {output}");
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
