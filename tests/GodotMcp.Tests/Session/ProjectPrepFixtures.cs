using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.Tests.Session;

/// <summary>The dates and the game repositories the project-prep tests build their fixtures from.</summary>
internal static class ProjectPrepFixtures
{
    public static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Built = Old.AddHours(1);
    public static readonly DateTime Later = Old.AddHours(2);
    public static readonly DateTime Latest = Old.AddHours(3);

    /// <summary>A game folder at repo/game with a project.godot, optionally naming an assembly, and the given csproj files.</summary>
    public static string CreateGame(TempDirectory temp, string? assemblyName, params string[] csprojNames)
    {
        string game = temp.Combine("repo", "game");
        Directory.CreateDirectory(game);
        string dotnet = assemblyName is null ? string.Empty : $"\n[dotnet]\n\nproject/assembly_name=\"{assemblyName}\"\n";
        File.WriteAllText(Path.Combine(game, "project.godot"), $"config_version=5\n\n[application]\n\nconfig/name=\"Game\"\n{dotnet}");
        foreach (string csproj in csprojNames)
        {
            File.WriteAllText(Path.Combine(game, csproj), "<Project Sdk=\"Godot.NET.Sdk/4.7.2\" />");
        }

        return game;
    }

    /// <summary>
    /// A repository whose game (repo/game, Game.csproj) and sibling library (repo/lib) are committed with old times, and
    /// whose assembly was built after them; .godot, bin and obj are ignored.
    /// </summary>
    public static string CreateBuiltRepository(TempDirectory temp)
    {
        string repo = temp.Combine("repo");
        string game = CreateGame(temp, null, "Game.csproj");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".godot/\nbin/\nobj/\n");
        File.WriteAllText(Path.Combine(game, "Player.cs"), "class Player;");
        File.WriteAllText(Path.Combine(game, "README.md"), "# Game");
        WriteFile(Path.Combine(repo, "lib", "Lib.cs"), "class Lib;", Old);
        Git.InitAndCommitAll(repo);
        foreach (string file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, Old);
        }

        WriteFile(PrepScan.AssemblyPath(game, "Game"), "dll", Built);
        return game;
    }

    /// <summary>A built repository with an icon and its import sidecar, both committed.</summary>
    public static string CreateRepositoryWithImportedIcon(TempDirectory temp)
    {
        string game = CreateBuiltRepository(temp);
        File.WriteAllText(Path.Combine(game, "icon.png"), "png");
        File.WriteAllText(
            Path.Combine(game, "icon.png.import"),
            "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\npath=\"res://.godot/imported/icon.png-0123.ctex\"\n\n"
                + "[deps]\n\nsource_file=\"res://icon.png\"\ndest_files=[\"res://.godot/imported/icon.png-0123.ctex\"]\n\n[params]\n\n"
        );
        Git.CommitAll(temp.Combine("repo"));
        return game;
    }

    public static void WriteFile(string path, string content, DateTime modified)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified);
    }
}
