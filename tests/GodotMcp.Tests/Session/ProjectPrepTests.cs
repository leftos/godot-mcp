using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The checks run_project's prep makes before a launch: which csproj and assembly a project has, whether the assembly is
/// stale, whether a Godot import is needed, the compiler errors a failed build reports, and the prepare option.
/// </summary>
public sealed class ProjectPrepTests : IDisposable
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Built = Old.AddHours(1);
    private static readonly DateTime Later = Old.AddHours(2);
    private static readonly DateTime Latest = Old.AddHours(3);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FindsTheCsprojNamedByAssemblyName()
    {
        string game = CreateGame("Named", "Named.csproj", "Other.csproj");

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.Found, lookup.Kind);
        Assert.Equal(Path.Combine(game, "Named.csproj"), lookup.ProjectFile);
        Assert.Equal("Named", lookup.AssemblyName);
    }

    [Fact]
    public void FallsBackToTheOnlyCsprojBesideProjectGodot()
    {
        string game = CreateGame(null, "Solo.csproj");

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.Found, lookup.Kind);
        Assert.Equal(Path.Combine(game, "Solo.csproj"), lookup.ProjectFile);
        Assert.Equal("Solo", lookup.AssemblyName);
    }

    [Fact]
    public void ReportsNoCsproj()
    {
        string game = CreateGame(null);

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.None, lookup.Kind);
        Assert.Null(lookup.ProjectFile);
    }

    [Fact]
    public void SkipsSeveralCsprojWithANoteNamingThem()
    {
        string game = CreateGame(null, "A.csproj", "B.csproj");

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.Several, lookup.Kind);
        Assert.Null(lookup.ProjectFile);
        Assert.Contains("A.csproj", lookup.Note, StringComparison.Ordinal);
        Assert.Contains("B.csproj", lookup.Note, StringComparison.Ordinal);
        Assert.Contains("assembly_name", lookup.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAssemblyIsUnderGodotsMonoTempFolder()
    {
        string game = CreateGame(null, "Solo.csproj");

        Assert.Equal(Path.Combine(game, ".godot", "mono", "temp", "bin", "Debug", "Solo.dll"), PrepScan.AssemblyPath(game, "Solo"));
    }

    [Fact]
    public void StaleWhenTheAssemblyIsMissing()
    {
        string game = CreateBuiltRepository();
        File.Delete(PrepScan.AssemblyPath(game, "Game"));

        Assert.True(IsStale(game));
    }

    [Fact]
    public void UpToDateWhenEveryInputIsOlderThanTheAssembly()
    {
        string game = CreateBuiltRepository();

        Assert.False(IsStale(game));
    }

    [Fact]
    public void StaleWhenACsFileIsNewerThanTheAssembly()
    {
        string game = CreateBuiltRepository();
        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), Later);

        Assert.True(IsStale(game));
    }

    [Fact]
    public void AnEditedMarkdownFileIsNotAnInput()
    {
        string game = CreateBuiltRepository();
        File.SetLastWriteTimeUtc(Path.Combine(game, "README.md"), Later);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void ASiblingLibraryUnderTheSameTopLevelIsAnInput()
    {
        string game = CreateBuiltRepository();
        File.SetLastWriteTimeUtc(_temp.Combine("repo", "lib", "Lib.cs"), Later);

        Assert.True(IsStale(game));
    }

    [Fact]
    public void FilesUnderIgnoredBinAndObjAreNotInputs()
    {
        string game = CreateBuiltRepository();
        WriteFile(Path.Combine(game, "obj", "Generated.cs"), "class Generated;", Later);
        WriteFile(Path.Combine(game, "bin", "Copied.cs"), "class Copied;", Later);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void AnUntrackedButNotIgnoredCsFileIsAnInput()
    {
        string game = CreateBuiltRepository();
        WriteFile(Path.Combine(game, "Enemy.cs"), "class Enemy;", Later);

        Assert.True(IsStale(game));
    }

    [Fact]
    public void TheStampNewerThanAnInputThatLeftTheAssemblyAloneKeepsItUpToDate()
    {
        string game = CreateBuiltRepository();
        WriteFile(Path.Combine(game, "Directory.Build.props"), "<Project />", Later);
        Assert.True(IsStale(game));

        WriteFile(PrepScan.StampPath(game), string.Empty, Latest);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void OutsideGitTheProjectFolderIsWalkedSkippingBuildFolders()
    {
        string game = CreateGame(null, "Game.csproj");
        Assert.SkipWhen(IsInsideGit(game), $"{game} is inside a git repository here, so the walk outside git cannot be tested.");
        WriteFile(Path.Combine(game, "Player.cs"), "class Player;", Old);
        File.SetLastWriteTimeUtc(Path.Combine(game, "Game.csproj"), Old);
        WriteFile(PrepScan.AssemblyPath(game, "Game"), "dll", Built);
        WriteFile(Path.Combine(game, "obj", "Generated.cs"), "class Generated;", Later);
        WriteFile(Path.Combine(game, ".godot", "Cached.cs"), "class Cached;", Later);
        Assert.False(IsStale(game));

        File.SetLastWriteTimeUtc(Path.Combine(game, "Player.cs"), Later);

        Assert.True(IsStale(game));
    }

    [Fact]
    public void ImportNeededWhenADestFileIsMissing()
    {
        string game = CreateRepositoryWithImportedIcon();

        Assert.True(IsImportNeeded(game));
    }

    [Fact]
    public void ImportNotNeededWhenEveryDestFileIsPresent()
    {
        string game = CreateRepositoryWithImportedIcon();
        WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.ctex"), "ctex", Old);

        Assert.False(IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenUidFilesHaveNoUidCache()
    {
        string game = CreateBuiltRepository();
        WriteFile(Path.Combine(game, "player.gd.uid"), "uid://b1234567", Old);
        Assert.True(IsImportNeeded(game));

        WriteFile(Path.Combine(game, ".godot", "uid_cache.bin"), "cache", Old);

        Assert.False(IsImportNeeded(game));
    }

    [Fact]
    public void AnInputProbeCopyNeedsNoImport()
    {
        string probe = _temp.Combine("InputProbe");
        Directory.CreateDirectory(probe);
        foreach (string file in Directory.EnumerateFiles(RepoPaths.InputProbe))
        {
            File.Copy(file, Path.Combine(probe, Path.GetFileName(file)));
        }

        Git.InitAndCommitAll(probe);

        Assert.False(IsImportNeeded(probe));
    }

    [Fact]
    public void CompilerErrorsAreDeduplicatedAcrossMsBuildsSummary()
    {
        const string file = @"D:\t\CsProbe\CsProbeNode.cs";
        const string project = @" [D:\t\CsProbe\CsProbe.csproj]";
        string log = string.Join(
            '\n',
            "  Determining projects to restore...",
            "  All projects are up-to-date for restore.",
            $"{file}(9,31): error CS1002: ; expected{project}",
            $"{file}(11,5): error CS0103: The name 'oops' does not exist in the current context{project}",
            $"{file}(4,1): warning CS8019: Unnecessary using directive.{project}",
            string.Empty,
            "Build FAILED.",
            string.Empty,
            $"{file}(4,1): warning CS8019: Unnecessary using directive.{project}",
            $"{file}(9,31): error CS1002: ; expected{project}",
            $"{file}(11,5): error CS0103: The name 'oops' does not exist in the current context{project}",
            "    1 Warning(s)",
            "    2 Error(s)"
        );

        CompilerErrorList errors = CompilerErrors.Parse(log);

        Assert.Equal([$"{file}:9: CS1002 ; expected", $"{file}:11: CS0103 The name 'oops' does not exist in the current context"], errors.Errors);
        Assert.Equal(2, errors.Total);
    }

    [Fact]
    public void CompilerErrorsAreCappedAtTwenty()
    {
        string log = string.Join('\n', Enumerable.Range(1, 25).Select(line => $@"C:\p\A.cs({line},1): error CS1002: ; expected [C:\p\A.csproj]"));

        CompilerErrorList errors = CompilerErrors.Parse(log);

        Assert.Equal(20, errors.Errors.Count);
        Assert.Equal(25, errors.Total);
        Assert.Equal(@"C:\p\A.cs:20: CS1002 ; expected", errors.Errors[^1]);
    }

    [Fact]
    public void BuildDiagnosticsAreErrorsAndWarningsWithTheirPositionsDeduplicated()
    {
        const string file = @"D:\t\CsProbe\CsProbeNode.cs";
        const string project = @" [D:\t\CsProbe\CsProbe.csproj]";
        string log = string.Join(
            '\n',
            $"{file}(9,31): error CS1002: ; expected{project}",
            $"{file}(12,13): warning CS0219: The variable 'unused' is assigned but its value is never used{project}",
            $"CSC : warning CS2008: No source files specified.{project}",
            "error MSB1009: Project file does not exist.",
            "Build FAILED.",
            $"{file}(12,13): warning CS0219: The variable 'unused' is assigned but its value is never used{project}",
            $"{file}(9,31): error CS1002: ; expected{project}",
            $"{file}(9,40): error CS1002: ; expected{project}"
        );

        IReadOnlyList<BuildDiagnostic> diagnostics = CompilerErrors.ParseDiagnostics(log);

        Assert.Equal(
            [
                new BuildDiagnostic(file, 9, 31, "CS1002", "; expected", "error"),
                new BuildDiagnostic(file, 12, 13, "CS0219", "The variable 'unused' is assigned but its value is never used", "warning"),
                new BuildDiagnostic("CSC", null, null, "CS2008", "No source files specified.", "warning"),
                new BuildDiagnostic(null, null, null, "MSB1009", "Project file does not exist.", "error"),
                new BuildDiagnostic(file, 9, 40, "CS1002", "; expected", "error"),
            ],
            diagnostics
        );
        // The flat error list names no column, so the two CS1002s on line 9 read as one.
        Assert.Equal([$"{file}:9: CS1002 ; expected", "MSB1009 Project file does not exist."], CompilerErrors.Errors(diagnostics).Errors);
    }

    [Fact]
    public void EachCompileItemsEvaluationLogsToAFileOfItsOwn()
    {
        string game = CreateGame(null, "Solo.csproj");

        string first = ProjectPrep.CompileItemsLog(game);
        string second = ProjectPrep.CompileItemsLog(game);

        Assert.NotEqual(first, second);
        foreach (string log in new[] { first, second })
        {
            Assert.Equal(ProjectPrep.LogFolder(game), Path.GetDirectoryName(log));
            Assert.Matches("^compile-items-[0-9a-f]{32}\\.log$", Path.GetFileName(log));
        }
    }

    [Fact]
    public void ATrackedSidecarDeletedFromTheWorkingTreeIsSkipped()
    {
        string game = CreateRepositoryWithImportedIcon();
        File.Delete(Path.Combine(game, "icon.png.import"));

        Assert.False(IsImportNeeded(game));
    }

    [Fact]
    public void ImportNeededWhenOnlyTheSecondOfSeveralDestFilesIsMissing()
    {
        string game = CreateBuiltRepository();
        File.WriteAllText(
            Path.Combine(game, "icon.png.import"),
            "[deps]\n\nsource_file=\"res://icon.png\"\n"
                + "dest_files=[\"res://.godot/imported/icon.png-0123.s3tc.ctex\", \"res://.godot/imported/icon.png-0123.etc2.ctex\"]\n"
        );
        WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.s3tc.ctex"), "ctex", Old);
        Assert.True(IsImportNeeded(game));

        WriteFile(Path.Combine(game, ".godot", "imported", "icon.png-0123.etc2.ctex"), "ctex", Old);

        Assert.False(IsImportNeeded(game));
    }

    [Fact]
    public async Task AnAssemblyNameWithoutItsCsprojBuildsNothingAndSaysWhy()
    {
        string game = CreateGame("Missing", "Other.csproj");

        PrepResult result = await ProjectPrep.RunAsync(new PrepContext(game, NullLogger.Instance, () => []), TestContext.Current.CancellationToken);

        Assert.Equal("no-csproj", result.Build);
        Assert.Null(result.BuildMs);
        Assert.Equal("not-needed", result.Import);
        Assert.Contains("assembly_name is \"Missing\"", result.Note, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(game, "Missing.csproj"), result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsWithoutAFileAreReportedByTheirCode()
    {
        string log = "error MSB1009: Project file does not exist.\nerror NETSDK1045: The current .NET SDK does not support targeting .NET 11.0.";

        CompilerErrorList errors = CompilerErrors.Parse(log);

        Assert.Equal(
            ["MSB1009 Project file does not exist.", "NETSDK1045 The current .NET SDK does not support targeting .NET 11.0."],
            errors.Errors
        );
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("auto", true)]
    [InlineData("never", false)]
    public void PrepareTakesAutoOrNever(string? prepare, bool expected) => Assert.Equal(expected, new RunOptions(Prepare: prepare).ShouldPrepare());

    [Theory]
    [InlineData("Never")]
    [InlineData("always")]
    [InlineData("")]
    public void PrepareRefusesAnythingElse(string prepare)
    {
        McpException refused = Assert.Throws<McpException>(() => new RunOptions(Prepare: prepare).ShouldPrepare());

        Assert.Equal($"prepare takes \"auto\" or \"never\"; got \"{prepare}\".", refused.Message);
    }

    [Fact]
    public void AClassNameNewerThanTheCacheIsDueAnImport()
    {
        string game = CreateGame(null);
        string player = Path.Combine(game, "actors", "player.gd");
        WriteFile(PrepScan.ClassCachePath(game), "list=[]\n", Built);
        WriteFile(player, "extends Node\nclass_name Player\n", Old);
        Assert.False(IsImportNeeded(game));

        File.SetLastWriteTimeUtc(player, Later);

        Assert.True(IsImportNeeded(game));
    }

    [Theory]
    [InlineData("@tool\nextends Node2D\n\nclass_name Enemy\n")]
    [InlineData("class_name Enemy extends Node2D\n")]
    [InlineData("extends Node class_name Enemy\n")]
    [InlineData("@icon(\"res://enemy.svg\") class_name Enemy\nextends Node2D\n")]
    [InlineData("@tool @icon(\"res://enemy.svg\") class_name Enemy\n")]
    public void AMissingCacheWithAClassNameIsDueAnImport(string source)
    {
        string game = CreateGame(null);
        WriteFile(Path.Combine(game, "enemy.gd"), source, Old);

        Assert.True(IsImportNeeded(game));
    }

    [Fact]
    public void NoClassNameNeedsNoImportForTheCache()
    {
        string game = CreateGame(null);
        WriteFile(Path.Combine(game, "main.gd"), "extends Node\n# class_name Main would name it\nvar class_name_text := \"class_name X\"\n", Later);
        Assert.False(IsImportNeeded(game));

        WriteFile(PrepScan.ClassCachePath(game), "list=[]\n", Old);

        Assert.False(IsImportNeeded(game));
    }

    private static bool IsStale(string game) =>
        PrepScan.IsStale(PrepScan.AssemblyPath(game, "Game"), PrepScan.StampPath(game), PrepScan.Scan(game, NullLogger.Instance).BuildInputs);

    private static bool IsImportNeeded(string game) => PrepScan.ImportNeeded(game, PrepScan.Scan(game, NullLogger.Instance));

    private static bool IsInsideGit(string folder)
    {
        try
        {
            Git.Run(folder, "rev-parse", "--show-toplevel");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteFile(string path, string content, DateTime modified)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified);
    }

    /// <summary>A game folder at repo/game with a project.godot, optionally naming an assembly, and the given csproj files.</summary>
    private string CreateGame(string? assemblyName, params string[] csprojNames)
    {
        string game = _temp.Combine("repo", "game");
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
    private string CreateBuiltRepository()
    {
        string repo = _temp.Combine("repo");
        string game = CreateGame(null, "Game.csproj");
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

    private string CreateRepositoryWithImportedIcon()
    {
        string game = CreateBuiltRepository();
        File.WriteAllText(Path.Combine(game, "icon.png"), "png");
        File.WriteAllText(
            Path.Combine(game, "icon.png.import"),
            "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\npath=\"res://.godot/imported/icon.png-0123.ctex\"\n\n"
                + "[deps]\n\nsource_file=\"res://icon.png\"\ndest_files=[\"res://.godot/imported/icon.png-0123.ctex\"]\n\n[params]\n\n"
        );
        Git.CommitAll(_temp.Combine("repo"));
        return game;
    }
}
