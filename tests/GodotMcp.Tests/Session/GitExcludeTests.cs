using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

public sealed class GitExcludeTests : IDisposable
{
    private const string Pattern = "/game/override.cfg";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AppendsThePatternOnceInARepository()
    {
        string repo = CreateRepositoryWithGame("repo");
        string game = Path.Combine(repo, "game");

        GitExcludeOutcome first = GitExclude.Ensure(game, OverrideFile.FileName, NullLogger.Instance);
        GitExcludeOutcome second = GitExclude.Ensure(game, OverrideFile.FileName, NullLogger.Instance);

        Assert.Equal(GitExcludeOutcome.Added, first);
        Assert.Equal(GitExcludeOutcome.AlreadyPresent, second);
        string[] lines = File.ReadAllLines(Path.Combine(repo, ".git", "info", "exclude"));
        Assert.Single(lines, line => line == Pattern);
        AssertOverrideHidden(repo, game);
    }

    [Fact]
    public void AppendsToTheSharedExcludeFileFromAWorktree()
    {
        string repo = CreateRepositoryWithGame("repo");
        string worktree = _temp.Combine("worktree");
        Git.Run(repo, "worktree", "add", "--quiet", worktree);
        string game = Path.Combine(worktree, "game");

        GitExcludeOutcome outcome = GitExclude.Ensure(game, OverrideFile.FileName, NullLogger.Instance);

        Assert.Equal(GitExcludeOutcome.Added, outcome);
        Assert.Contains(Pattern, File.ReadAllLines(Path.Combine(repo, ".git", "info", "exclude")));
        AssertOverrideHidden(worktree, game);
    }

    [Fact]
    public void WritesTheLongPathPatternForAProjectReachedThroughAShortName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 short names exist only on Windows.");
        string repo = _temp.Combine("repo");
        string game = Path.Combine(repo, "LongGameFolderName");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "project.godot"), "config_version=5\n");
        Git.InitAndCommitAll(repo);
        string shortGame = ShortPath(game);
        Assert.SkipWhen(string.Equals(shortGame, game, StringComparison.OrdinalIgnoreCase), $"The volume of {game} does not create 8.3 short names.");

        GitExcludeOutcome outcome = GitExclude.Ensure(shortGame, OverrideFile.FileName, NullLogger.Instance);

        Assert.Equal(GitExcludeOutcome.Added, outcome);
        Assert.Contains("/LongGameFolderName/override.cfg", File.ReadAllLines(Path.Combine(repo, ".git", "info", "exclude")));
        AssertOverrideHidden(repo, game);
    }

    [Fact]
    public void SkipsAFolderOutsideAnyRepository()
    {
        GitExcludeOutcome outcome = GitExclude.Ensure(_temp.Path, OverrideFile.FileName, NullLogger.Instance);

        Assert.Equal(GitExcludeOutcome.NotARepository, outcome);
        Assert.False(Directory.Exists(_temp.Combine(".git")));
    }

    private string CreateRepositoryWithGame(string name)
    {
        string repo = _temp.Combine(name);
        Directory.CreateDirectory(Path.Combine(repo, "game"));
        File.WriteAllText(Path.Combine(repo, "game", "project.godot"), "config_version=5\n");
        Git.InitAndCommitAll(repo);
        return repo;
    }

    // cmd's %~s modifier prints the 8.3 form of every component that has one, and the long name of every other.
    private static string ShortPath(string path)
    {
        ProcessStartInfo startInfo = new("cmd.exe", $"/d /c for %I in (\"{path}\") do @echo %~sI")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static void AssertOverrideHidden(string workTree, string game)
    {
        File.WriteAllText(Path.Combine(game, OverrideFile.FileName), $"{OverrideFile.Marker}\n");
        Assert.Equal(string.Empty, Git.Status(workTree));
    }
}
