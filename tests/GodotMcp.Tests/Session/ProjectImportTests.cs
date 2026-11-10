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
    private const string BlenderLine =
        "ERROR: Blender path is invalid or not set, check your Editor Settings. Cannot configure blender path in headless mode.\n";

    private const string LevelScene = "[gd_scene load_steps=2 format=3 uid=\"uid://dlevel\"]\n\n[node name=\"Level\" type=\"Node\"]\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("box.xres")]
    [InlineData("BOX.XRES")]
    public void AnXresNeverNeedsAnImport(string name)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string resource = Path.Combine(game, name);
        File.WriteAllText(resource, string.Empty);

        Assert.False(PrepScan.AssetNeedsImport(game, resource, ImportFingerprints.None));
    }

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
    public void ATrackedSidecarDeletedFromTheWorkingTreeIsImportedAgain()
    {
        string game = ProjectPrepFixtures.CreateRepositoryWithImportedIcon(_temp);
        File.Delete(Path.Combine(game, "icon.png.import"));

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("block.glb")]
    [InlineData("BARLOW.WOFF2")]
    [InlineData("Fonts/Barlow/Barlow-Medium.woff2")]
    [InlineData("icon.png")]
    public void ImportNeededWhenASourceHasNoImportFile(string name)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, name.Replace('/', Path.DirectorySeparatorChar)), "asset", ProjectPrepFixtures.Built);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("ignored", ".gdignore")]
    [InlineData(".hidden", null)]
    [InlineData("nested", "project.godot")]
    public void ImportNotNeededWhenASourceIsInAnUnscannedFolder(string folder, string? marker)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        if (marker is not null)
        {
            ProjectPrepFixtures.WriteFile(Path.Combine(game, folder, marker), string.Empty, ProjectPrepFixtures.Old);
        }

        ProjectPrepFixtures.WriteFile(Path.Combine(game, folder, "deep", "block.glb"), "glb", ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("atlas.dds")]
    [InlineData("atlas.ktx")]
    [InlineData("gradient.tres")]
    [InlineData("gradient.res")]
    [InlineData("gradient.xres")]
    [InlineData("player.gd")]
    [InlineData("level.tscn")]
    public void ImportNotNeededForFilesThatLoadWithoutAnImport(string name)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, name), string.Empty, ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("track.mp1")]
    [InlineData("track.mp2")]
    public void Mp1AndMp2AreNotImportExtensions(string name)
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, name), string.Empty, ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData("import/blender/enabled=false", false)]
    [InlineData("import/blender/enabled=0", false)]
    [InlineData("import/blender/enabled=0.0", false)]
    [InlineData("import/blender/enabled=null", false)]
    [InlineData("import/blender/enabled=nil", false)]
    [InlineData("import/blender/enabled=\"\"", false)]
    [InlineData("import/blender/enabled = false", false)]
    [InlineData("import/blender/enabled=false ; off on the build machines", false)]
    [InlineData("import/blender/enabled=true", true)]
    [InlineData("import/blender/enabled=1", true)]
    [InlineData("import/blender/enabled=\"false\"", true)]
    [InlineData("import/blender/enabled.windows=false", true)]
    [InlineData("import/blender/enabled.editor=false", true)]
    [InlineData("", true)]
    public void BlenderImportIsOffOnlyForAValueGodotReadsAsFalse(string setting, bool enabled)
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        File.AppendAllText(Path.Combine(game, "project.godot"), $"\n[filesystem]\n\n{setting}\n");

        Assert.Equal(enabled, PrepScan.BlenderEnabled(game));
    }

    [Fact]
    public void ABlendSidecarWithMissingDestFilesIsNotDueWhenBlenderImportIsDisabled()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        File.AppendAllText(Path.Combine(game, "project.godot"), "\n[filesystem]\n\nimport/blender/enabled=false\n");
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "block.blend"), "blend", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(
            Path.Combine(game, "block.blend.import"),
            "[remap]\n\nimporter=\"scene\"\ntype=\"PackedScene\"\npath=\"res://.godot/imported/block.blend-0123.scn\"\n\n"
                + "[deps]\n\nsource_file=\"res://block.blend\"\ndest_files=[\"res://.godot/imported/block.blend-0123.scn\"]\n",
            ProjectPrepFixtures.Old
        );

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenTheSidecarIsOnDiskButGitIgnored()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string repo = Path.GetDirectoryName(game)!;
        File.AppendAllText(Path.Combine(repo, ".gitignore"), "*.import\n");
        Git.CommitAll(repo);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "png", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), "importer=\"texture\"\n", ProjectPrepFixtures.Built);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenAnImportFileIsOlderThanItsSource()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string source = Path.Combine(game, "icon.png");
        ProjectPrepFixtures.WriteFile(source, "png", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(source + ".import", IconSidecar("texture", null), ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", ProjectPrepFixtures.Old);
        WriteImportedMd5(game, "res://icon.png", ProjectPrepFixtures.Old);
        Assert.False(PrepAssertions.IsImportNeeded(game));

        ProjectPrepFixtures.WriteFile(source, "jpg", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportNotNeededWhenASourceOrAFolderAboveItIsHidden(bool hideFolder)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The hidden attribute is a Windows file attribute.");
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string block = Path.Combine(game, "art", "deep", "block.glb");
        ProjectPrepFixtures.WriteFile(block, "glb", ProjectPrepFixtures.Built);
        string hidden = hideFolder ? Path.Combine(game, "art") : block;

        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ATrackedSourceDeletedFromTheWorkingTreeIsNotDue()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string block = Path.Combine(game, "block.glb");
        File.WriteAllText(block, "glb");
        Git.CommitAll(Path.GetDirectoryName(game)!);
        File.Delete(block);

        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void PrepNoteNamesTheAssetThatMadeTheImportDue()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string block = Path.Combine(game, "block.glb");
        ProjectPrepFixtures.WriteFile(block, "glb", ProjectPrepFixtures.Built);

        Assert.Equal("imported because res://block.glb had no .import file.", ProjectPrep.MissingSidecarNote(game, [block]));
        Assert.Equal(
            "imported because res://block.glb had no .import file (and 2 more).",
            ProjectPrep.MissingSidecarNote(game, [block, Path.Combine(game, "a.png"), Path.Combine(game, "b.png")])
        );
    }

    [Fact]
    public void PrepNoteNamesASourceTheImportLeftWithoutAnImportFile()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string log = Path.Combine(game, ".godot", "godot-mcp", "import.log");
        string fresh = Path.Combine(game, "fresh.png");

        Assert.Equal($"res://fresh.png still has no .import file after the import; see {log}.", ProjectPrep.StillMissingNote(game, [fresh], log));
        Assert.Equal(
            $"res://fresh.png still has no .import file after the import (and 1 more); see {log}.",
            ProjectPrep.StillMissingNote(game, [fresh, Path.Combine(game, "a.png")], log)
        );
        Assert.Null(ProjectPrep.StillMissingNote(game, [], log));
    }

    [Fact]
    public void PrepNoteSaysGodotDroppedTheImportOnlyWhenItsOutputSaysBlendersPathIsNotSet()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string log = Path.Combine(game, ".godot", "godot-mcp", "import.log");
        ProjectPrepFixtures.WriteFile(log, "Godot Engine v4.7.2\nimported res://icon.png\n", ProjectPrepFixtures.Built);
        Assert.Null(ProjectPrep.BlenderPathNote(game, log));

        File.AppendAllText(log, BlenderLine);

        Assert.Equal(BlenderNote(game), ProjectPrep.BlenderPathNote(game, log));
    }

    [Fact]
    public void PrepNotesSplitTheSourcesAnImportLeftWithoutAnImportFile()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string log = Path.Combine(game, ".godot", "godot-mcp", "import.log");
        string blend = Path.Combine(game, "block.blend");
        string fresh = Path.Combine(game, "fresh.png");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(fresh, "png", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(log, "Godot Engine v4.7.2\n", ProjectPrepFixtures.Built);

        Assert.Equal(
            $"imported because res://block.blend had no .import file. res://fresh.png still has no .import file after the import; see {log}.",
            ProjectPrep.MissingSidecarNotes(game, [blend, fresh], log)
        );

        File.AppendAllText(log, BlenderLine);

        Assert.Equal(
            $"res://fresh.png still has no .import file after the import; see {log}. {BlenderNote(game)}",
            ProjectPrep.MissingSidecarNotes(game, [blend, fresh], log)
        );
    }

    [Fact]
    public void TheBlenderLineAloneDoesNotRunTheImportTwice()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectFiles files = PrepScan.Scan(game, NullLogger.Instance);
        string log = Path.Combine(game, ".godot", "godot-mcp", "import.log");
        ProjectPrepFixtures.WriteFile(log, "Godot Engine v4.7.2\n" + BlenderLine, ProjectPrepFixtures.Built);

        Assert.Null(ProjectPrep.SecondPassReason(log, null, game, files));

        File.AppendAllText(log, "ERROR: Cannot open file 'res://icon.png'.\n");

        Assert.Equal("its first pass logged errors", ProjectPrep.SecondPassReason(log, null, game, files));
    }

    [Fact]
    public void ARecordedSidecarThatCannotBeReadCountsAsChanged()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string sidecar = Path.Combine(game, "icon.png.import");
        ProjectPrepFixtures.WriteFile(sidecar, "importer=\"texture\"\n", ProjectPrepFixtures.Built);
        Dictionary<string, string> record = new() { ["icon.png.import"] = ImportFingerprints.Md5Of(sidecar) };
        Assert.False(PrepScan.SettingsChanged(game, sidecar, record));

        using FileStream locked = new(sidecar, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(PrepScan.SettingsChanged(game, sidecar, record));
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
        ImportFingerprints.Save(game, new ImportPass(current, []), "done", ImportFingerprints.None, NullLogger.Instance);

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
    /// <summary>The note the prep adds when Godot dropped the import of <paramref name="game"/> for Blender's path.</summary>
    private static string BlenderNote(string game) =>
        "Godot dropped the import because Blender's path is not set: set [filesystem] import/blender/enabled=false in project.godot, "
        + "or set Blender's path in the editor settings and then open the project in the editor once, "
        + $"or delete {Path.Combine(game, ".godot", "godot-mcp", "import-fingerprints.json")} so the next run imports it.";

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
