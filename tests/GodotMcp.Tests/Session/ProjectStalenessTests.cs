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

        ProjectPrepFixtures.WriteFile(PrepScan.StampPath(game), string.Empty, ProjectPrepFixtures.Latest);

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

    /// <summary>
    /// A repository whose <c>.gitignore</c> holds <paramref name="rootGitignore"/>, with a game at
    /// <paramref name="folder"/>/game (Game.csproj, project.godot, Player.cs and a .gitignore holding
    /// <paramref name="gameGitignore"/>), committed where git lists it, every file set to <see cref="ProjectPrepFixtures.Old"/>,
    /// and an assembly built after them.
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
        return game;
    }
}
