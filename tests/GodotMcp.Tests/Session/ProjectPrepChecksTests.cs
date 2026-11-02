using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The prep checks that read no repository: which csproj and assembly a project has, the compile-items log, the compiler
/// errors a failed build reports, and the prepare option.
/// </summary>
public sealed class ProjectPrepChecksTests : IDisposable
{
    private const string GlobalLoaderSource = "[GlobalClass, Tool]\npublic partial class Loader : ResourceFormatLoader { }\n";
    private const string GlobalLoaderCache = "list=[{\n\"class\": &\"Loader\"\n}]\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A GDScript game with a .uid file and no UID cache, so an import is due.</summary>
    private string ImportDueGame()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);
        File.WriteAllText(Path.Combine(game, "main.gd"), "extends Node\n");
        File.WriteAllText(Path.Combine(game, "main.gd.uid"), "uid://bq3lfv6wdnbx8\n");
        return game;
    }

    /// <summary>A game building Solo, with its assembly written <paramref name="built"/> and one source declaring Loader.</summary>
    private string GlobalClassGame(DateTime built, string source = GlobalLoaderSource)
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Solo.csproj");
        string assembly = PrepScan.AssemblyPath(game, "Solo");
        ProjectPrepFixtures.WriteFile(assembly, "dll", built);
        File.WriteAllText(Path.Combine(game, "Loader.cs"), source);
        return game;
    }

    private static void WriteClassCache(string game, string text, DateTime written) =>
        ProjectPrepFixtures.WriteFile(PrepScan.ClassCachePath(game), text, written);

    private static bool IsStale(string game, params string[] extraInputs) =>
        PrepScan.IsFormatClassStale(game, new ProjectFiles([Path.Combine(game, "Loader.cs"), .. extraInputs], [], [], [], []));

    [Fact]
    public void AGlobalClassWithNoClassCacheIsStale() => Assert.True(IsStale(GlobalClassGame(ProjectPrepFixtures.Built)));

    [Fact]
    public void AGlobalClassMissingFromAnOlderCacheIsStale()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Later);
        WriteClassCache(game, "list=[]\n", ProjectPrepFixtures.Old);

        Assert.True(IsStale(game));
    }

    [Fact]
    public void AGlobalClassInAnOlderCacheIsNotStale()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Later);
        WriteClassCache(game, GlobalLoaderCache, ProjectPrepFixtures.Old);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void ACacheNewerThanTheAssemblyIsNotAskedAgain()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Old);
        WriteClassCache(game, "list=[]\n", ProjectPrepFixtures.Later);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void AMissingAssemblyMakesNoImportDue()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Built);
        File.Delete(PrepScan.AssemblyPath(game, "Solo"));

        Assert.False(IsStale(game));
    }

    [Fact]
    public void NoCsprojMakesNoImportDue()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Built);
        File.Delete(Path.Combine(game, "Solo.csproj"));

        Assert.False(IsStale(game));
    }

    [Fact]
    public void SeveralCsprojsMakeNoImportDue()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Built);
        File.WriteAllText(Path.Combine(game, "Other.csproj"), string.Empty);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void NoGlobalClassInAnySourceIsNotStale()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Later, "public partial class Plain : Node { }\n");
        WriteClassCache(game, "list=[]\n", ProjectPrepFixtures.Old);

        Assert.False(IsStale(game));
    }

    [Fact]
    public void NoGlobalClassAndNoCacheIsNotStale() =>
        Assert.False(IsStale(GlobalClassGame(ProjectPrepFixtures.Built, "public partial class Plain : Node { }\n")));

    [Fact]
    public void AGlobalClassOnlyInASiblingFolderIsNotStale()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Later, "public partial class Plain : Node { }\n");
        WriteClassCache(game, "list=[]\n", ProjectPrepFixtures.Old);
        string sibling = _temp.Combine("sibling", "Outside.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, GlobalLoaderSource);

        Assert.False(IsStale(game, sibling));
    }

    [Fact]
    public void AnImportThatRegistersAGlobalClassRunsTwice()
    {
        string game = GlobalClassGame(ProjectPrepFixtures.Later);
        ProjectFiles files = new([Path.Combine(game, "Loader.cs")], [], [], [], []);
        WriteClassCache(game, "list=[]\n", ProjectPrepFixtures.Old);
        IReadOnlySet<string> before = PrepScan.CachedFormatClasses(game, files);
        WriteClassCache(game, GlobalLoaderCache, ProjectPrepFixtures.Latest);
        IReadOnlySet<string> after = PrepScan.CachedFormatClasses(game, files);

        Assert.Empty(before);
        Assert.Equal(["Loader"], after);
        Assert.True(ProjectPrep.RegisteredNewClasses(before, after));
    }

    [Fact]
    public void AnImportThatRegistersNoNewGlobalClassRunsOnce()
    {
        HashSet<string> loader = new(["Loader"], StringComparer.Ordinal);

        Assert.False(ProjectPrep.RegisteredNewClasses(loader, loader));
        Assert.False(ProjectPrep.RegisteredNewClasses(new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void AGlobalClassThatIsNoLoaderOrSaverMakesNoImportDue() =>
        Assert.False(IsStale(GlobalClassGame(ProjectPrepFixtures.Built, "[GlobalClass]\npublic partial class Loader : Resource { }\n")));

    [Theory]
    [InlineData("[GlobalClass]\npublic partial class XscnLoader : ResourceFormatLoader { }", "XscnLoader")]
    [InlineData("[Tool, GlobalClass]\n[Icon(\"x\")]\npublic sealed partial class Saver : ResourceFormatSaver { }", "Saver")]
    public void TheMatcherFindsALoaderOrSaverNamedAfterItsFile(string source, string stem) =>
        Assert.Equal([stem], PrepScan.FormatClassesIn(source, stem));

    [Theory]
    [InlineData("[GlobalClass]\npublic partial class Found : Resource { }", "Found")]
    [InlineData("[GlobalClassName]\npublic partial class Found : ResourceFormatLoader { }", "Found")]
    [InlineData("// [GlobalClass]\npublic partial class Found : ResourceFormatLoader { }", "Found")]
    [InlineData("[GlobalClass]\npublic partial class Found : MyBase { }", "Found")]
    [InlineData("[GlobalClass]\npublic partial class Found : ResourceFormatLoader { }", "Other")]
    [InlineData("[GlobalClass]\npublic partial class Found : Resource { }\npublic partial class Later : ResourceFormatLoader { }", "Found")]
    [InlineData("[GlobalClass]\npublic partial class Found : Resource { }\npublic partial class Later : ResourceFormatLoader { }", "Later")]
    public void TheMatcherSkipsAnythingElse(string source, string stem) => Assert.Empty(PrepScan.FormatClassesIn(source, stem));

    [Fact]
    public void FindsTheCsprojNamedByAssemblyName()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, "Named", "Named.csproj", "Other.csproj");

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.Found, lookup.Kind);
        Assert.Equal(Path.Combine(game, "Named.csproj"), lookup.ProjectFile);
        Assert.Equal("Named", lookup.AssemblyName);
    }

    [Fact]
    public void FallsBackToTheOnlyCsprojBesideProjectGodot()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Solo.csproj");

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.Found, lookup.Kind);
        Assert.Equal(Path.Combine(game, "Solo.csproj"), lookup.ProjectFile);
        Assert.Equal("Solo", lookup.AssemblyName);
    }

    [Fact]
    public void ReportsNoCsproj()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null);

        CsprojLookup lookup = PrepScan.FindCsproj(game);

        Assert.Equal(CsprojKind.None, lookup.Kind);
        Assert.Null(lookup.ProjectFile);
    }

    [Fact]
    public void SkipsSeveralCsprojWithANoteNamingThem()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "A.csproj", "B.csproj");

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
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Solo.csproj");

        Assert.Equal(Path.Combine(game, ".godot", "mono", "temp", "bin", ProjectPrep.Configuration, "Solo.dll"), PrepScan.AssemblyPath(game, "Solo"));
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
    public void AQuoteListsTheErrorsThenHowManyWereLeftOut()
    {
        string log = string.Join('\n', Enumerable.Range(1, 25).Select(line => $@"C:\p\A.cs({line},1): error CS1002: ; expected [C:\p\A.csproj]"));

        string[] quoted = CompilerErrors.Parse(log).Quote().Split('\n');

        Assert.Equal(21, quoted.Length);
        Assert.Equal(@"C:\p\A.cs:1: CS1002 ; expected", quoted[0]);
        Assert.Equal(@"C:\p\A.cs:20: CS1002 ; expected", quoted[19]);
        Assert.Equal("(and 5 more)", quoted[20]);
    }

    [Fact]
    public void AQuoteOfAllTheErrorsOrOfNoneHasNoOmittedLine()
    {
        const string log = @"C:\p\A.cs(3,1): error CS1002: ; expected [C:\p\A.csproj]";

        Assert.Equal(@"C:\p\A.cs:3: CS1002 ; expected", CompilerErrors.Parse(log).Quote());
        Assert.Equal("No compiler errors were found in its output.", CompilerErrors.Parse("Build FAILED.").Quote());
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
    public void BuildDiagnosticsReadTheFileLoggerWhenItWroteTheBuildsDiagnostics()
    {
        const string file = @"D:\t\CsProbe\ProbeWarn.cs";
        const string project = @" [D:\t\CsProbe\CsProbe.csproj]";
        string fileLogger = $"{file}(1,62): warning CS0168: The variable 'unused' is declared but never used{project}";
        const string buildLog = "2026-01-01T00:00:00 out Build succeeded.";

        IReadOnlyList<BuildDiagnostic> diagnostics = ProjectPrep.BuildDiagnostics(fileLogger, buildLog, failed: false);

        Assert.Equal([new BuildDiagnostic(file, 1, 62, "CS0168", "The variable 'unused' is declared but never used", "warning")], diagnostics);
    }

    [Fact]
    public void BuildDiagnosticsFallBackToTheBuildLogWhenTheFileLoggerWroteNothing()
    {
        const string buildLog = "MSBUILD : error MSB1009: Project file does not exist.";

        IReadOnlyList<BuildDiagnostic> diagnostics = ProjectPrep.BuildDiagnostics(null, buildLog, failed: true);

        BuildDiagnostic error = Assert.Single(diagnostics);
        Assert.Equal("MSB1009", error.Code);
        Assert.Equal(BuildDiagnostic.Error, error.Severity);
    }

    [Fact]
    public void AFailedBuildWhoseFileLoggerHoldsNoErrorReadsTheBuildLog()
    {
        const string file = @"D:\t\CsProbe\CsProbeNode.cs";
        const string project = @" [D:\t\CsProbe\CsProbe.csproj]";
        string fileLogger = $"{file}(12,13): warning CS0219: The variable 'unused' is assigned but its value is never used{project}";
        const string buildLog = "MSBUILD : error MSB1009: Project file does not exist.";

        IReadOnlyList<BuildDiagnostic> diagnostics = ProjectPrep.BuildDiagnostics(fileLogger, buildLog, failed: true);

        BuildDiagnostic error = Assert.Single(diagnostics);
        Assert.Equal("MSB1009", error.Code);
        Assert.Equal(BuildDiagnostic.Error, error.Severity);
    }

    [Fact]
    public void EachCompileItemsEvaluationLogsToAFileOfItsOwn()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, null, "Solo.csproj");

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
    public async Task AnAssemblyNameWithoutItsCsprojBuildsNothingAndSaysWhy()
    {
        string game = ProjectPrepFixtures.CreateGame(_temp, "Missing", "Other.csproj");

        PrepResult result = await ProjectPrep.RunAsync(
            new PrepContext(game, NullLogger.Instance, () => [], () => { }),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("no-csproj", result.Build);
        Assert.Null(result.BuildMs);
        Assert.Equal("not-needed", result.Import);
        Assert.Contains("assembly_name is \"Missing\"", result.Note, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(game, "Missing.csproj"), result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheImportHookRunsOnceNoSessionRefusesTheImportAndBeforeItStarts()
    {
        string game = ImportDueGame();
        List<string> calls = [];
        PrepContext context = new(
            game,
            NullLogger.Instance,
            () =>
            {
                calls.Add("sessions");
                return [];
            },
            () =>
            {
                calls.Add("hook");
                // Thrown so the import after it never runs: the prep ends with this, not with a Godot run.
                throw new InvalidOperationException("the hook ran");
            }
        );

        InvalidOperationException stopped = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectPrep.RunAsync(context, TestContext.Current.CancellationToken)
        );

        Assert.Equal("the hook ran", stopped.Message);
        Assert.Equal(["sessions", "hook"], calls);
    }

    [Fact]
    public async Task TheImportHookDoesNotRunWhenRunningSessionsRefuseTheImport()
    {
        string game = ImportDueGame();
        bool hooked = false;
        PrepContext context = new(game, NullLogger.Instance, () => ["game"], () => hooked = true);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            ProjectPrep.RunAsync(context, TestContext.Current.CancellationToken)
        );

        Assert.Contains("session(s) game are running on it", refused.Message, StringComparison.Ordinal);
        Assert.False(hooked);
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
}
