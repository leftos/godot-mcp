using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>The note a launch with prepare "never" carries about the Debug assembly it will load.</summary>
public sealed class PrepStaleAssemblyNoteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A game building Solo, whose csproj was last written at <see cref="ProjectPrepFixtures.Old"/>.</summary>
    private string SoloGame()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Solo.csproj");
        File.SetLastWriteTimeUtc(Path.Combine(game, "Solo.csproj"), ProjectPrepFixtures.Old);
        return game;
    }

    private static string Assembly(string game) => PrepScan.AssemblyPath(game, "Solo");

    [Fact]
    public void AnAssemblyOlderThanASourceNamesBothTimes()
    {
        string game = SoloGame();
        ProjectPrepFixtures.WriteFile(Assembly(game), "dll", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Player.cs"), "class Player;", ProjectPrepFixtures.Old.AddSeconds(10));

        Assert.Equal(
            "The C# build was skipped (prepare: never), and the assembly this run loads, .godot/mono/temp/bin/Debug/Solo.dll "
                + "(2020-01-01T00:00:00Z), is older than Player.cs (2020-01-01T00:00:10Z); "
                + "a game run loads only this Debug build, never a Release one.",
            PrepScan.StaleAssemblyNote(game, NullLogger.Instance)
        );
    }

    [Fact]
    public void ASourceOneSecondNewerIsWithinTheFileSystemSlack()
    {
        string game = SoloGame();
        ProjectPrepFixtures.WriteFile(Assembly(game), "dll", ProjectPrepFixtures.Old);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Player.cs"), "class Player;", ProjectPrepFixtures.Old.AddSeconds(1));

        Assert.Null(PrepScan.StaleAssemblyNote(game, NullLogger.Instance));
    }

    [Fact]
    public void AnAssemblyNewerThanEveryInputIsFresh()
    {
        string game = SoloGame();
        ProjectPrepFixtures.WriteFile(Assembly(game), "dll", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "Player.cs"), "class Player;", ProjectPrepFixtures.Old);

        Assert.Null(PrepScan.StaleAssemblyNote(game, NullLogger.Instance));
    }

    [Fact]
    public void NoAssemblySaysTheScriptsWillNotLoad()
    {
        string game = SoloGame();

        Assert.Equal(
            "The C# build was skipped (prepare: never), and there is no assembly at .godot/mono/temp/bin/Debug/Solo.dll, "
                + "so the project's C# scripts will not load.",
            PrepScan.StaleAssemblyNote(game, NullLogger.Instance)
        );
    }

    [Fact]
    public void NoCsprojSaysNothing()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);

        Assert.Null(PrepScan.StaleAssemblyNote(game, NullLogger.Instance));
    }

    [Fact]
    public void TwoCsprojsSayNothing()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "One.csproj", "Two.csproj");

        Assert.Null(PrepScan.StaleAssemblyNote(game, NullLogger.Instance));
    }

    [Fact]
    public void SourcesUnderIgnoredFoldersAreNotCounted()
    {
        string game = SoloGame();
        ProjectPrepFixtures.WriteFile(Assembly(game), "dll", ProjectPrepFixtures.Built);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, ".godot", "Cached.cs"), "class Cached;", ProjectPrepFixtures.Latest);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "bin", "Bin.cs"), "class Bin;", ProjectPrepFixtures.Latest);
        ProjectPrepFixtures.WriteFile(Path.Combine(game, "obj", "Obj.cs"), "class Obj;", ProjectPrepFixtures.Latest);

        Assert.Null(PrepScan.StaleAssemblyNote(game, NullLogger.Instance));
    }
}
