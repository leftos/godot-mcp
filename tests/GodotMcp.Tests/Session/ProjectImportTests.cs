using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>Whether a Godot import is needed: missing dest files, the uid and class caches, and the probe copy.</summary>
public sealed class ProjectImportTests : IDisposable
{
    private const string LevelScene = "[gd_scene load_steps=2 format=3 uid=\"uid://dlevel\"]\n\n[node name=\"Level\" type=\"Node\"]\n";

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

        WriteUidCache(game, ProjectPrepFixtures.Old, ("uid://b1234567", "res://player.gd"));

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenANewerUidCacheLacksAUidFilesId()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567\n", ProjectPrepFixtures.Old);
        WriteUidCache(game, ProjectPrepFixtures.Later, ("uid://cother", "res://other.gd"));

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenTheUidCacheLacksASceneHeadersUid()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "level.tscn"), LevelScene, ProjectPrepFixtures.Old);
        WriteUidCache(game, ProjectPrepFixtures.Later, ("uid://cother", "res://other.tscn"));

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheUidCacheHoldsEveryIdHoweverOld()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        WriteUidCache(game, ProjectPrepFixtures.Old, ("uid://b1234567", "res://player.gd"), ("uid://dlevel", "res://level.tscn"));
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567\n", ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "level.tscn"), LevelScene, ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, "plain.tscn"),
            "[gd_scene format=3]\n\n[node name=\"Plain\" type=\"Node\"]\n",
            ProjectPrepFixtures.Later
        );
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, "bad.tres"),
            "[gd_resource type=\"Resource\" format=3 uid=\"uid://Bad\"]\n",
            ProjectPrepFixtures.Later
        );

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("")]
    public void ImportNeededWhenTheUidCacheDoesNotParse(string content)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567\n", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "uid_cache.bin"), content, ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    // Each expected id is what Godot 4.7.2.stable.mono.official.ed1daf0bf printed for ResourceUID.text_to_id(text) in a
    // throwaway --headless --script run; the engine's -1 (INVALID_ID) is the port's null.
    [Theory]
    [InlineData("uid://b", 1L)]
    [InlineData("uid://ba", 34L)]
    [InlineData("uid://0", 25L)]
    [InlineData("uid://", 0L)]
    [InlineData("uid://b1234567", 93953614398L)]
    [InlineData("uid://bfafmgig4vudg", 2737690244184705140L)]
    [InlineData("uid://Bad", null)]
    [InlineData("res://x", null)]
    public void TextToIdMatchesTheEngine(string text, long? expected) => Assert.Equal(expected, PrepScan.TextToId(text));

    [Theory]
    [InlineData(".hidden", null)]
    [InlineData("ignored", ".gdignore")]
    [InlineData("nested", "project.godot")]
    public void ASceneWhereTheScanNeverGoesNeedsNoImport(string folder, string? marker)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        WriteUidCache(game, ProjectPrepFixtures.Later, ("uid://cother", "res://other.tscn"));
        if (marker is not null)
        {
            ProjectPrepFixtures.WriteFile(Path.Combine(game, folder, marker), string.Empty, ProjectPrepFixtures.Old);
        }

        ProjectPrepFixtures.WriteFile(Path.Combine(game, folder, "deep", "level.tscn"), LevelScene, ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, folder, "deep", "player.gd.uid"), "uid://b1234567\n", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWithNoUidCacheAndNoUidFiles()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "level.tscn"), LevelScene, ProjectPrepFixtures.Old);

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

    /// <summary>
    /// Writes game's .godot/uid_cache.bin in the engine's layout: a little-endian 32-bit entry count, then per entry a 64-bit
    /// id, a 32-bit byte length and the UTF-8 path.
    /// </summary>
    private static void WriteUidCache(string game, DateTime modified, params (string Uid, string Path)[] entries)
    {
        string cache = Path.Combine(game, ".godot", "uid_cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        using (BinaryWriter writer = new(File.Create(cache)))
        {
            writer.Write((uint)entries.Length);
            foreach ((string uid, string path) in entries)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(path);
                writer.Write(PrepScan.TextToId(uid)!.Value);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        File.SetLastWriteTimeUtc(cache, modified);
    }
}
