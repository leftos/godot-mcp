using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>Whether a Godot import is needed: missing dest files, the uid and class caches, and the probe copy.</summary>
public sealed class ProjectImportTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ImportNeededWhenADestFileIsMissing()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenEveryDestFileIsPresent()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenUidFilesHaveNoUidCache()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567", ProjectPrepFixtures.Old);
        Assert.True(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "uid_cache.bin"), "cache", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenAUidFileIsNewerThanTheUidCache()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "uid_cache.bin"), "cache", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenEveryUidFileIsOlderThanTheUidCache()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "uid_cache.bin"), "cache", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void AnInputProbeCopyNeedsNoImport()
    {
        string probe = _temp.Combine("InputProbe");
        Directory.CreateDirectory(probe);
        foreach (string file in Directory.EnumerateFiles(RepoPaths.InputProbe))
        {
            File.Copy(file, Path.Combine(probe, Path.GetFileName(file)));
        }

        Git.InitAndCommitAll(probe);

        Assert.False(PrepAssertions.IsImportNeeded(probe));
    }

    [Fact]
    public void ATrackedSidecarDeletedFromTheWorkingTreeIsSkipped()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        File.Delete(Path.Combine(game, "icon.png.import"));

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenOnlyTheSecondOfSeveralDestFilesIsMissing()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.WriteAllText(
            Path.Combine(game, "icon.png.import"),
            "[deps]\n\nsource_file=\"res://icon.png\"\n"
                + "dest_files=[\"res://.godot/imported/icon.png-0123.s3tc.ctex\", \"res://.godot/imported/icon.png-0123.etc2.ctex\"]\n"
        );
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.s3tc.ctex"), "ctex", ProjectPrepFixtures.Old);
        Assert.True(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.etc2.ctex"), "ctex", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void AClassNameNewerThanTheCacheIsDueAnImport()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string player = Path.Combine(game, "actors", "player.gd");
        ProjectPrepFixtures.WriteFile(PrepScan.ClassCachePath(game), "list=[]\n", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(player, "extends Node\nclass_name Player\n", ProjectPrepFixtures.Old);
        Assert.False(PrepAssertions.IsImportNeeded(game));

        File.SetLastWriteTimeUtc(player, ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("@tool\nextends Node2D\n\nclass_name Enemy\n")]
    [InlineData("class_name Enemy extends Node2D\n")]
    [InlineData("extends Node class_name Enemy\n")]
    [InlineData("@icon(\"res://enemy.svg\") class_name Enemy\nextends Node2D\n")]
    [InlineData("@tool @icon(\"res://enemy.svg\") class_name Enemy\n")]
    public void AMissingCacheWithAClassNameIsDueAnImport(string source)
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "enemy.gd"), source, ProjectPrepFixtures.Old);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void NoClassNameNeedsNoImportForTheCache()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, "main.gd"),
            "extends Node\n# class_name Main would name it\nvar class_name_text := \"class_name X\"\n",
            ProjectPrepFixtures.Later
        );
        Assert.False(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(PrepScan.ClassCachePath(game), "list=[]\n", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }
}
