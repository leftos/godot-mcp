using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The prep's own record of every sidecar's md5: what it reads, and what a finished prep writes to it.</summary>
public sealed class ImportFingerprintTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NoRecordReadsAsEmpty() => Assert.Empty(ImportFingerprints.Read(Game(), NullLogger.Instance));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"icon.png.import\": 3}")]
    [InlineData("{\"icon.png.import\": \"\"}")]
    public void AnUnreadableRecordIsTreatedAsAbsent(string text)
    {
        string game = Game();
        ProjectPrepFixtures.WriteFile(ImportFingerprints.PathIn(game), text, ProjectPrepFixtures.Old);

        Assert.Empty(ImportFingerprints.Read(game, NullLogger.Instance));
    }

    [Fact]
    public void TheRecordsKeyForASidecarIsItsProjectRelativePathWithForwardSlashes()
    {
        string game = Game();

        Assert.Equal("icon.png.import", ImportFingerprints.KeyFor(game, Path.Combine(game, "icon.png.import")));
        Assert.Equal("Art/Theme/stone-face.png.import", ImportFingerprints.KeyFor(game, Path.Combine(game, "Art", "Theme", "stone-face.png.import")));
    }

    [Fact]
    public void ANotNeededPrepBaselinesTheSidecarsItHasNoneFor()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        string face = Path.Combine(game, "Art", "Theme", "stone-face.png.import");
        WriteRecord(game, ("icon.png.import", "the md5 the prep saw before"));

        ImportFingerprints.Save(
            game,
            new ImportPass([icon, face], []),
            "not-needed",
            ImportFingerprints.Read(game, NullLogger.Instance),
            NullLogger.Instance
        );

        IReadOnlyDictionary<string, string> record = ImportFingerprints.Read(game, NullLogger.Instance);
        Assert.Equal("the md5 the prep saw before", record["icon.png.import"]);
        Assert.Equal(ImportFingerprints.Md5Of(face), record["Art/Theme/stone-face.png.import"]);
    }

    [Fact]
    public void AnImportRewritesEveryEntryAndDropsASidecarThatIsGone()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        WriteRecord(game, ("gone.png.import", "a stale md5"), ("icon.png.import", "a stale md5"));

        ImportFingerprints.Save(game, new ImportPass([icon], []), "done", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

        KeyValuePair<string, string> entry = Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance));
        Assert.Equal("icon.png.import", entry.Key);
        Assert.Equal(ImportFingerprints.Md5Of(icon), entry.Value);
    }

    [Fact]
    public void ASourceStillWithoutASidecarAfterAnImportIsRecordedAndNotDueAgain()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        Assert.True(PrepAssertions.IsImportNeeded(game));

        ImportFingerprints.Save(game, new ImportPass([], [blend]), "done", ImportFingerprints.None, NullLogger.Instance);

        Assert.Equal(ImportFingerprints.Md5Of(blend), ImportFingerprints.Read(game, NullLogger.Instance)["block.blend"]);
        Assert.False(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ARecordedSourceIsDueAgainWhenItsBytesChange()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        ImportFingerprints.Save(game, new ImportPass([], [blend]), "done", ImportFingerprints.None, NullLogger.Instance);

        ProjectPrepFixtures.WriteFile(blend, "changed", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ARecordedSourceIsForgottenWhenItsSidecarAppears()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        ImportFingerprints.Save(game, new ImportPass([], WithoutSidecars(game)), "done", ImportFingerprints.None, NullLogger.Instance);
        Assert.Equal("block.blend", Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance).Keys));
        string sidecar = blend + ".import";
        ProjectPrepFixtures.WriteFile(sidecar, "importer=\"scene\"\n", ProjectPrepFixtures.Built);

        ImportFingerprints.Save(
            game,
            new ImportPass([sidecar], WithoutSidecars(game)),
            "done",
            ImportFingerprints.Read(game, NullLogger.Instance),
            NullLogger.Instance
        );

        IReadOnlyDictionary<string, string> record = ImportFingerprints.Read(game, NullLogger.Instance);
        Assert.DoesNotContain("block.blend", record.Keys);
        Assert.Equal(ImportFingerprints.Md5Of(sidecar), record["block.blend.import"]);
    }

    [Fact]
    public void ADoneImportRecordsOnlyTheBlendSourcesStillWithoutASidecar()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "block.blend"), "blend", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "fresh.png"), "png", ProjectPrepFixtures.Old);

        ImportFingerprints.Save(game, new ImportPass([], WithoutSidecars(game)), "done", ImportFingerprints.None, NullLogger.Instance);

        Assert.Equal("block.blend", Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance).Keys));
        Assert.True(PrepAssertions.IsImportNeeded(game));
    }

    [Fact]
    public void ABlendIsNeitherDueNorRecordedWhenBlenderImportIsDisabled()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        File.AppendAllText(Path.Combine(game, "project.godot"), "\n[filesystem]\n\nimport/blender/enabled=false\n");
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);

        ImportFingerprints.Save(game, new ImportPass([], WithoutSidecars(game)), "done", ImportFingerprints.None, NullLogger.Instance);

        Assert.False(PrepAssertions.IsImportNeeded(game));
        Assert.False(PrepScan.AssetNeedsImport(game, blend, ImportFingerprints.None));
        Assert.Empty(ImportFingerprints.Read(game, NullLogger.Instance));
    }

    [Fact]
    public void ASourceDeletedBeforeTheRecordIsSkipped()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        IReadOnlyList<string> withoutSidecars = WithoutSidecars(game);
        File.Delete(blend);

        ImportFingerprints.Save(game, new ImportPass([], withoutSidecars), "done", ImportFingerprints.None, NullLogger.Instance);

        Assert.Empty(ImportFingerprints.Read(game, NullLogger.Instance));
    }

    [Fact]
    public void AnUnreadableRecordedSourceIsNotTakenAsRecorded()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        Dictionary<string, string> record = new() { ["block.blend"] = ImportFingerprints.Md5Of(blend) };

        using FileStream locked = new(blend, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(ImportFingerprints.RecordedWithoutSidecar(game, blend, record));
    }

    [Fact]
    public void ANotNeededPrepDropsASourceEntryOnceItsSidecarAppearsOrItIsGone()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        string imported = Path.Combine(game, "imported.blend");
        string kept = Path.Combine(game, "kept.blend");
        ProjectPrepFixtures.WriteFile(imported, "imported", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(kept, "kept", ProjectPrepFixtures.Old);
        WriteRecord(
            game,
            ("imported.blend", ImportFingerprints.Md5Of(imported)),
            ("kept.blend", ImportFingerprints.Md5Of(kept)),
            ("gone.blend", "the md5 of a source that is gone")
        );
        ProjectPrepFixtures.WriteFile(imported + ".import", "importer=\"scene\"\n", ProjectPrepFixtures.Built);

        ImportFingerprints.Save(game, new ImportPass([], []), "not-needed", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

        Assert.Equal("kept.blend", Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance).Keys));
    }

    [Fact]
    public void ADroppedImportKeepsTheOldRecordAndAddsOnlyTheBlendSources()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        string face = Path.Combine(game, "Art", "Theme", "stone-face.png.import");
        string blend = Path.Combine(game, "block.blend");
        string fresh = Path.Combine(game, "fresh.png");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(fresh, "png", ProjectPrepFixtures.Old);
        WriteRecord(game, ("icon.png.import", "the md5 the prep saw before"));

        ImportFingerprints.Save(
            game,
            new ImportPass([icon, face], [blend, fresh]) { Dropped = true },
            "done",
            ImportFingerprints.Read(game, NullLogger.Instance),
            NullLogger.Instance
        );

        IReadOnlyDictionary<string, string> record = ImportFingerprints.Read(game, NullLogger.Instance);
        Assert.Equal(["block.blend", "icon.png.import"], record.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("the md5 the prep saw before", record["icon.png.import"]);
        Assert.Equal(ImportFingerprints.Md5Of(blend), record["block.blend"]);
    }

    [Fact]
    public void ANotNeededPrepSkipsASidecarItCannotRead()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        string face = Path.Combine(game, "Art", "Theme", "stone-face.png.import");

        using (new FileStream(face, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ImportFingerprints.Save(game, new ImportPass([icon, face], []), "not-needed", ImportFingerprints.None, NullLogger.Instance);
        }

        Assert.Equal("icon.png.import", Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance).Keys));
    }

    [Fact]
    public void ANotNeededRecordKeepsTheGuardsEntries()
    {
        string game = Game();
        string blend = Path.Combine(game, "block.blend");
        ProjectPrepFixtures.WriteFile(blend, "blend", ProjectPrepFixtures.Old);
        WriteRecord(game, ("block.blend", ImportFingerprints.Md5Of(blend)));
        string icon = Path.Combine(game, "icon.png.import");
        string face = Path.Combine(game, "Art", "Theme", "stone-face.png.import");

        ImportFingerprints.Save(
            game,
            new ImportPass([icon, face], []),
            "not-needed",
            ImportFingerprints.Read(game, NullLogger.Instance),
            NullLogger.Instance
        );

        IReadOnlyDictionary<string, string> record = ImportFingerprints.Read(game, NullLogger.Instance);
        Assert.Equal(ImportFingerprints.Md5Of(blend), record["block.blend"]);
        Assert.Equal(ImportFingerprints.Md5Of(icon), record["icon.png.import"]);
        Assert.Equal(ImportFingerprints.Md5Of(face), record["Art/Theme/stone-face.png.import"]);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("stopped")]
    [InlineData("skipped")]
    public void AnythingButADoneOrNotNeededPrepWritesNoRecord(string state)
    {
        string game = Game();

        ImportFingerprints.Save(
            game,
            new ImportPass([Path.Combine(game, "icon.png.import")], []),
            state,
            ImportFingerprints.None,
            NullLogger.Instance
        );

        Assert.False(File.Exists(ImportFingerprints.PathIn(game)));
    }

    [Fact]
    public void AnUnchangedRecordIsNotRewritten()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        ImportFingerprints.Save(game, new ImportPass([icon], []), "done", ImportFingerprints.None, NullLogger.Instance);
        string path = ImportFingerprints.PathIn(game);
        ProjectPrepFixtures.WriteFile(path, File.ReadAllText(path), ProjectPrepFixtures.Old);

        ImportFingerprints.Save(game, new ImportPass([icon], []), "done", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

        Assert.Equal(ProjectPrepFixtures.Old, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ARefusedImportLeavesTheRecordAsItWas()
    {
        string game = Game();
        WriteRecord(game, ("icon.png.import", "the md5 the prep saw before"));
        string before = File.ReadAllText(ImportFingerprints.PathIn(game));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            ProjectPrep.RunAsync(
                new PrepContext(game, NullLogger.Instance, () => ["other"], () => { }) { RetryBootCrashOnce = false },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("needs a Godot import", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ImportFingerprints.PathIn(game)));
    }

    /// <summary>A game folder with an asset and its sidecar at its root, and one nested.</summary>
    private string Game()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png"), "png", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), "importer=\"texture\"\n", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Art", "Theme", "stone-face.png"), "png", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Art", "Theme", "stone-face.png.import"), "importer=\"texture\"\n", ProjectPrepFixtures.Old);
        return game;
    }

    /// <summary>The game's scanned importable sources with no sidecar, as the prep computes them.</summary>
    private static IReadOnlyList<string> WithoutSidecars(string game) =>
        PrepScan.SourcesWithoutSidecars(game, PrepScan.Scan(game, NullLogger.Instance));

    /// <summary>Writes the record's own JSON with the entries given.</summary>
    private static void WriteRecord(string game, params (string Sidecar, string Md5)[] entries) =>
        ProjectPrepFixtures.WriteFile(
            ImportFingerprints.PathIn(game),
            "{" + string.Join(',', entries.Select(entry => $"\"{entry.Sidecar}\":\"{entry.Md5}\"")) + "}",
            ProjectPrepFixtures.Old
        );
}
