using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>Whether a built assembly is stale against its inputs, the stamp and the ignored folders.</summary>
public sealed class ProjectStalenessTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void StaleWhenTheAssemblyIsMissing()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.Delete(PrepScan.AssemblyPath(game, "Game"));

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void UpToDateWhenEveryInputIsOlderThanTheAssembly()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);

        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void StaleWhenACsFileIsNewerThanTheAssembly()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnEditedMarkdownFileIsNotAnInput()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.SetLastWriteTimeUtc(Path.Combine(game, "README.md"), ProjectPrepFixtures.Later);

        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void ASiblingLibraryUnderTheSameTopLevelIsAnInput()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.SetLastWriteTimeUtc(_temp.Combine("repo", "lib", "Lib.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void FilesUnderIgnoredBinAndObjAreNotInputs()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "obj", "Generated.cs"), "class Generated;", ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "bin", "Copied.cs"), "class Copied;", ProjectPrepFixtures.Later);

        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnUntrackedButNotIgnoredCsFileIsAnInput()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Enemy.cs"), "class Enemy;", ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void TheStampNewerThanAnInputThatLeftTheAssemblyAloneKeepsItUpToDate()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Directory.Build.props"), "<Project />", ProjectPrepFixtures.Later);
        Assert.True(PrepAssertions.IsStale(game));

        ProjectPrepFixtures.RecordBuild(game);

        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnAssemblyThePrepNeverRecordedIsStaleThoughNewerThanEveryInput()
    {
        // A routed run's outputs install put old bytes over the assembly after an input's edit, stamped with a time after it.
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        string stamp = PrepScan.StampPath(game);
        if (File.Exists(stamp))
        {
            File.Delete(stamp);
        }

        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);
        File.SetLastWriteTimeUtc(PrepScan.AssemblyPath(game, "Game"), ProjectPrepFixtures.Latest);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnAssemblyRewrittenAfterThePrepRecordedItIsStale()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(PrepScan.AssemblyPath(game, "Game"), "dll", ProjectPrepFixtures.Latest);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void ALegacyEmptyStampIsStale()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.WriteFile(PrepScan.StampPath(game), string.Empty, ProjectPrepFixtures.Latest);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnInputEditedAfterTheRecordIsStale()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void ARecordMatchingTheAssemblyAndInputsIsUpToDate()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        ProjectPrepFixtures.RecordBuild(game);

        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void OutsideGitTheProjectFolderIsWalkedSkippingBuildFolders()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Game.csproj");
        Assert.SkipWhen(PrepAssertions.IsInsideGit(game), $"{game} is inside a git repository here, so the walk outside git cannot be tested.");
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Player.cs"), "class Player;", ProjectPrepFixtures.Old);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Game.csproj"), ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(PrepScan.AssemblyPath(game, "Game"), "dll", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.RecordBuild(game);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "obj", "Generated.cs"), "class Generated;", ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "Cached.cs"), "class Cached;", ProjectPrepFixtures.Later);
        Assert.False(PrepAssertions.IsStale(game));

        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AProjectInAnIgnoredFolderCountsItsSourcesAsBuildInputs()
    {
        string game = CreateRepositoryGame(".tmp", ".tmp/\n", string.Empty);
        Assert.False(PrepAssertions.IsStale(game));

        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnIgnoredProjectSeesItsUidAndImportFiles()
    {
        string game = CreateRepositoryGame(".tmp", ".tmp/\n", string.Empty);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Player.cs.uid"), "uid://b1", ProjectPrepFixtures.Later);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "icon.png.import"), "[remap]\n", ProjectPrepFixtures.Later);

        ProjectFiles files = PrepScan.Scan(game, NullLogger.Instance);

        Assert.Contains(files.UidFiles, file => file.EndsWith("Player.cs.uid", StringComparison.Ordinal));
        Assert.Contains(files.ImportFiles, file => file.EndsWith("icon.png.import", StringComparison.Ordinal));
    }

    [Fact]
    public void AProjectInAnUnignoredDotFolderStillListsThroughGit()
    {
        string game = CreateRepositoryGame(".rig", ".godot/\nbin/\nobj/\n", "generated.cs\n");
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "generated.cs"), "class Generated;", ProjectPrepFixtures.Later);

        Assert.False(PrepAssertions.IsStale(game));

        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        Assert.True(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void AnAssemblyMsBuildsIntermediateCorroboratesIsUpToDateAndRecorded()
    {
        // Beside the project's own source the pdb names documents the comparison leaves out: MSBuild's generated file under
        // .godot, a sibling library's source, a source of a folder whose name only starts with the project's, and a generator's.
        string game = CreateUnrecordedRepository();
        WriteIntermediate(
            game,
            ProjectPrepFixtures.Built,
            Path.Combine(game, "Player.cs"),
            IntermediateFolder(game, "Game.AssemblyInfo.cs"),
            _temp.Combine("repo", "lib", "Lib.cs"),
            _temp.Combine("repo", "game2", "Player.cs"),
            Path.Combine("Godot.SourceGenerators", "Player_ScriptPath.generated.cs")
        );

        Assert.True(IsCurrent(game));
        Assert.True(File.Exists(PrepScan.StampPath(game)));
        Assert.False(PrepAssertions.IsStale(game));
    }

    [Fact]
    public void ADeletedSiblingSourceIsStale()
    {
        // lib/Lib.cs is committed, so git still lists it; the pdb compares the project folder's sources only.
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        File.Delete(_temp.Combine("repo", "lib", "Lib.cs"));

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void SourcesSpelledOutsideTheComparedFolderAreStale()
    {
        // The project folder spelled another way than the inputs (a subst drive, a junction, an 8.3 name) leaves both sides
        // of the source comparison empty; the \\?\ form is such a spelling of the same folder.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The \\\\?\\ spelling of a folder is Windows'.");
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        string aliased = @"\\?\" + Path.GetFullPath(game);

        Assert.False(PrepScan.BuildIsCurrent(aliased, "Game", PrepScan.Scan(game, NullLogger.Instance).BuildInputs, false, NullLogger.Instance));
        Assert.False(File.Exists(PrepScan.StampPath(game)));
    }

    [Fact]
    public void ASavedBuildOlderThanTheAssemblyIsStale()
    {
        // Adopting would leave validate reading that older build's diagnostics as the last build.
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        ProjectPrepFixtures.WriteFile(SavedBuild.PathIn(game), "{}", ProjectPrepFixtures.Old);

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void ARunThatReportsTheBuildNeverAdopts()
    {
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));

        Assert.False(PrepScan.BuildIsCurrent(game, "Game", PrepScan.Scan(game, NullLogger.Instance).BuildInputs, true, NullLogger.Instance));
        Assert.False(File.Exists(PrepScan.StampPath(game)));
    }

    [Fact]
    public void APdbThatIsNotPortableIsStale()
    {
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        ProjectPrepFixtures.WriteFile(IntermediateFolder(game, "Game.pdb"), "Microsoft C/C++ MSF 7.00", ProjectPrepFixtures.Built);

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void AnIntermediateOlderThanAnEditedInputIsStale()
    {
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), ProjectPrepFixtures.Later);

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void ABinAssemblyThatDiffersFromTheIntermediateIsStale()
    {
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        ProjectPrepFixtures.WriteFile(IntermediateFolder(game, "Game.dll"), "old", ProjectPrepFixtures.Built);

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void ASourceMissingFromThePdbIsStale()
    {
        string game = CreateUnrecordedRepository();
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Enemy.cs"), "class Enemy;", ProjectPrepFixtures.Old);
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void APdbNamingADeletedSourceIsStale()
    {
        // Player.cs is committed, so git still lists it after the delete.
        string game = CreateUnrecordedRepository();
        WriteIntermediate(game, ProjectPrepFixtures.Built, Path.Combine(game, "Player.cs"));
        File.Delete(Path.Combine(game, "Player.cs"));

        AssertStaleAndUnrecorded(game);
    }

    [Fact]
    public void NoIntermediateIsStale()
    {
        string game = CreateUnrecordedRepository();

        AssertStaleAndUnrecorded(game);
    }

    /// <summary>The prep's own decision (<see cref="PrepScan.BuildIsCurrent"/>) for the game's Game assembly.</summary>
    private static bool IsCurrent(string game) =>
        PrepScan.BuildIsCurrent(game, "Game", PrepScan.Scan(game, NullLogger.Instance).BuildInputs, false, NullLogger.Instance);

    private static void AssertStaleAndUnrecorded(string game)
    {
        Assert.False(IsCurrent(game));
        Assert.False(File.Exists(PrepScan.StampPath(game)));
    }

    /// <summary>A built repository whose assembly another builder left: no prep record.</summary>
    private string CreateUnrecordedRepository()
    {
        string game = ProjectPrepFixtures.CreateBuiltRepository(_temp);
        File.Delete(PrepScan.StampPath(game));
        return game;
    }

    /// <summary>A file in MSBuild's intermediate folder beside the assembly's: <c>.godot/mono/temp/obj/Debug</c>.</summary>
    private static string IntermediateFolder(string game, string file) => Path.Combine(game, ".godot", "mono", "temp", "obj", "Debug", file);

    /// <summary>
    /// MSBuild's intermediate assembly as a copy of the bin assembly last written at <paramref name="written"/>, and beside it
    /// a portable pdb whose documents are <paramref name="sources"/>.
    /// </summary>
    private static void WriteIntermediate(string game, DateTime written, params string[] sources)
    {
        string dll = IntermediateFolder(game, "Game.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.Copy(PrepScan.AssemblyPath(game, "Game"), dll, overwrite: true);
        File.SetLastWriteTimeUtc(dll, written);
        MetadataBuilder metadata = new();
        foreach (string source in sources)
        {
            metadata.AddDocument(metadata.GetOrAddDocumentName(source), default, default, default);
        }

        BlobBuilder blob = new();
        new PortablePdbBuilder(metadata, [.. new int[MetadataTokens.TableCount]], default).Serialize(blob);
        using FileStream stream = File.Create(IntermediateFolder(game, "Game.pdb"));
        blob.WriteContentTo(stream);
    }

    /// <summary>
    /// A repository whose <c>.gitignore</c> holds <paramref name="rootGitignore"/>, with a game at
    /// <paramref name="folder"/>/game (Game.csproj, project.godot, Player.cs and a .gitignore holding
    /// <paramref name="gameGitignore"/>), committed where git lists it, every file set to <see cref="ProjectPrepFixtures.Old"/>,
    /// and an assembly built after them and recorded.
    /// </summary>
    private string CreateRepositoryGame(string folder, string rootGitignore, string gameGitignore)
    {
        string repo = _temp.Combine("repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), rootGitignore);
        string game = Path.Combine(repo, folder, "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "project.godot"), "config_version=5\n\n[application]\n\nconfig/name=\"Game\"\n");
        File.WriteAllText(Path.Combine(game, "Game.csproj"), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />");
        File.WriteAllText(Path.Combine(game, "Player.cs"), "class Player;");
        File.WriteAllText(Path.Combine(game, ".gitignore"), gameGitignore);
        Git.InitAndCommitAll(repo);
        foreach (string file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, ProjectPrepFixtures.Old);
        }

        ProjectPrepFixtures.WriteFile(PrepScan.AssemblyPath(game, "Game"), "dll", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.RecordBuild(game);
        return game;
    }
}
