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
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

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
}
