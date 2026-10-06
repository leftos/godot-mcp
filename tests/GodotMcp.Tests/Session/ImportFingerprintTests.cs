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

        ImportFingerprints.Save(game, [icon, face], "not-needed", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

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

        ImportFingerprints.Save(game, [icon], "done", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

        KeyValuePair<string, string> entry = Assert.Single(ImportFingerprints.Read(game, NullLogger.Instance));
        Assert.Equal("icon.png.import", entry.Key);
        Assert.Equal(ImportFingerprints.Md5Of(icon), entry.Value);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("stopped")]
    [InlineData("skipped")]
    public void AnythingButADoneOrNotNeededPrepWritesNoRecord(string state)
    {
        string game = Game();

        ImportFingerprints.Save(game, [Path.Combine(game, "icon.png.import")], state, ImportFingerprints.None, NullLogger.Instance);

        Assert.False(File.Exists(ImportFingerprints.PathIn(game)));
    }

    [Fact]
    public void AnUnchangedRecordIsNotRewritten()
    {
        string game = Game();
        string icon = Path.Combine(game, "icon.png.import");
        ImportFingerprints.Save(game, [icon], "done", ImportFingerprints.None, NullLogger.Instance);
        string path = ImportFingerprints.PathIn(game);
        ProjectPrepFixtures.WriteFile(path, File.ReadAllText(path), ProjectPrepFixtures.Old);

        ImportFingerprints.Save(game, [icon], "done", ImportFingerprints.Read(game, NullLogger.Instance), NullLogger.Instance);

        Assert.Equal(ProjectPrepFixtures.Old, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ARefusedImportLeavesTheRecordAsItWas()
    {
        string game = Game();
        WriteRecord(game, ("icon.png.import", "the md5 the prep saw before"));
        string before = File.ReadAllText(ImportFingerprints.PathIn(game));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            ProjectPrep.RunAsync(new PrepContext(game, NullLogger.Instance, () => ["other"], () => { }), TestContext.Current.CancellationToken)
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

    /// <summary>Writes the record's own JSON with the entries given.</summary>
    private static void WriteRecord(string game, params (string Sidecar, string Md5)[] entries) =>
        ProjectPrepFixtures.WriteFile(
            ImportFingerprints.PathIn(game),
            "{" + string.Join(',', entries.Select(entry => $"\"{entry.Sidecar}\":\"{entry.Md5}\"")) + "}",
            ProjectPrepFixtures.Old
        );
}
