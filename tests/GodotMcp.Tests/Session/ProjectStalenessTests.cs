using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

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
}
