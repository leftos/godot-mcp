using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

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
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Built);

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
        CommittedFixture.CopyTo(RepoPaths.InputProbe, probe);

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
        File.WriteAllText(Path.Combine(game, "icon.png"), "png");
        File.WriteAllText(
            Path.Combine(game, "icon.png.import"),
            "[deps]\n\nsource_file=\"res://icon.png\"\n"
                + "dest_files=[\"res://.godot/imported/icon.png-0123.s3tc.ctex\", \"res://.godot/imported/icon.png-0123.etc2.ctex\"]\n"
        );
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.s3tc.ctex"), "ctex", ProjectPrepFixtures.Old);
        Assert.True(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.etc2.ctex"), "ctex", ProjectPrepFixtures.Old);
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenTheSourcesBytesChangedSinceItsMd5()
    {
        string game = ImportedIcon();
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "jpg", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheSourceIsNewerButItsBytesAreTheSame()
    {
        string game = ImportedIcon();
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "png", ProjectPrepFixtures.Later);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenOnlyTheSidecarIsNewer()
    {
        string game = ImportedIcon();
        File.SetLastWriteTimeUtc(Path.Combine(game, "icon.png.import"), ProjectPrepFixtures.Later);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenTheImportLeftNoMd5()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", ProjectPrepFixtures.Old);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheSourceIsOlderThanItsMd5HoweverDifferentItsBytes()
    {
        string game = ImportedIcon();
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Latest);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "jpg", ProjectPrepFixtures.Old);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededForASidecarWhoseSourceIsGone()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "ghost.png.import"), "[remap]\n\nimporter=\"texture\"\n", ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededForAnOrphanSidecarWhoseRecordedSettingsDiffer()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "ghost.png.import"), "[remap]\n\nimporter=\"texture\"\n", ProjectPrepFixtures.Built);
        WriteFingerprints(game, ("ghost.png.import", new string('0', 32)));

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ASidecarAnImportCreatedIsRecordedAfterADoneImport()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        IReadOnlyList<string> scanned = PrepScan.Scan(game, NullLogger.Instance).ImportFiles;
        string created = Path.Combine(game, "new_asset.png.import");
        ProjectPrepFixtures.WriteFile(created, "[remap]\n\nimporter=\"texture\"\n", ProjectPrepFixtures.Built);

        IReadOnlyList<string> current = PrepScan.ImportFiles(game, NullLogger.Instance);
        ImportFingerprints.Save(game, current, "done", ImportFingerprints.None, NullLogger.Instance);

        Assert.DoesNotContain(created, scanned);
        Assert.Contains(created, current);
        Assert.Equal(ImportFingerprints.Md5Of(created), ImportFingerprints.Read(game, NullLogger.Instance)["new_asset.png.import"]);
    }

    [Fact]
    public void ImportNeededWhenTheSidecarsImportSettingsChanged()
    {
        string game = ImportedIcon();
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Latest);
        WriteFingerprints(game, ("icon.png.import", new string('0', 32)));

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheSidecarHasNoRecord()
    {
        string game = ImportedIcon();
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Latest);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheSidecarsRecordMatches()
    {
        string game = ImportedIcon();
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Latest);
        string sidecar = Path.Combine(game, "icon.png.import");
        WriteFingerprints(game, ("icon.png.import", ImportFingerprints.Md5Of(sidecar)));

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("keep", null)]
    [InlineData("skip", null)]
    [InlineData("texture", "valid=false\n")]
    public void ASidecarGodotNeverImportsIsNeverDue(string importer, string? extra)
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), IconSidecar(importer, extra), ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenANestedSourcesBytesChangedSinceItsMd5()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string source = Path.Combine(game, "Art", "Theme", "stone-face.png");
        ProjectPrepFixtures.WriteFile(source, "png", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(source + ".import", "[remap]\n\nimporter=\"texture\"\n", ProjectPrepFixtures.Built);
        WriteImportedMd5(game, "res://Art/Theme/stone-face.png", ProjectPrepFixtures.Old);
        Assert.False(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(source, "jpg", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
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
    /// A built repository whose icon is imported: its dest file present, and the .md5 Godot's import leaves for it, holding the
    /// source's own md5. The source and its sidecar sit at <see cref="ProjectPrepFixtures.Built"/> and the .md5 at
    /// <see cref="ProjectPrepFixtures.Old"/>, so the prep reads the source.
    /// </summary>
    private string ImportedIcon()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "png", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), IconSidecar("texture", null), ProjectPrepFixtures.Built);
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Old);
        return game;
    }

    /// <summary>
    /// Writes the prep's record of every sidecar's md5 — .godot/godot-mcp/import-fingerprints.json — with the entries given.
    /// </summary>
    private static void WriteFingerprints(string game, params (string Sidecar, string Md5)[] entries) =>
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, ".godot", "godot-mcp", "import-fingerprints.json"),
            "{" + string.Join(',', entries.Select(entry => $"\"{entry.Sidecar}\":\"{entry.Md5}\"")) + "}",
            ProjectPrepFixtures.Built
        );

    /// <summary>A texture sidecar for the icon: its importer, any extra line, and the dest file its import wrote.</summary>
    private static string IconSidecar(string importer, string? extra) =>
        $"[remap]\n\nimporter=\"{importer}\"\ntype=\"CompressedTexture2D\"\n{extra}path=\"res://.godot/imported/icon.png-0123.ctex\"\n\n"
        + "[deps]\n\nsource_file=\"res://icon.png\"\ndest_files=[\"res://.godot/imported/icon.png-0123.ctex\"]\n";

    /// <summary>
    /// Writes the .md5 Godot's import leaves for the source a res:// path names:
    /// .godot/imported/&lt;source file name&gt;-&lt;md5 hex of the res:// path&gt;.md5, holding source_md5 of the source's
    /// bytes (4.7.2 editor/file_system/editor_file_system.cpp L3040-3046).
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "The .md5 an import writes is named and hashed by md5; nothing here is a security use."
    )]
    private static void WriteImportedMd5(string game, string resPath, DateTime modified)
    {
        string source = Path.Combine(game, resPath["res://".Length..].Replace('/', Path.DirectorySeparatorChar));
        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(resPath)));
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, ".godot", "imported", $"{Path.GetFileName(source)}-{hash}.md5"),
            $"source_md5=\"{Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(source)))}\"\n"
                + "dest_md5=\"00000000000000000000000000000000\"\n",
            modified
        );
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
