using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A process session's prep run for real, without a server: where its wrapper starts and where the prep's own command runs,
/// with a wrapper that names <c>{cwd}</c> and passes it on and one that names no <c>{cwd}</c>.
/// </summary>
public sealed class ProjectPrepWorkingDirectoryTests : IDisposable
{
    private const string WrapperWithCwd =
        "@echo off\n" + "> \"%~dp0wrapper-cwd.txt\" echo %CD%\n" + "cd /d \"%~1\"\n" + "call \"%~2\"\n" + "exit /b %ERRORLEVEL%\n";

    private const string WrapperWithoutCwd = "@echo off\n" + "> \"%~dp0wrapper-cwd.txt\" echo %CD%\n" + "call \"%~1\"\n" + "exit /b %ERRORLEVEL%\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Project => _temp.Path;

    private string ProcessFolder => Path.Combine(_temp.Path, "server");

    private string PrepCommand => Path.Combine(ProcessFolder, "prep.cmd");

    [Fact]
    public async Task AWrapperTakingTheCwdStartsInTheProjectAndItsCommandInTheProcessFolder()
    {
        WriteProject(withCwd: true);

        await RunPrepAsync();

        AssertStartedIn(Project);
        AssertRanIn(ProcessFolder);
    }

    [Fact]
    public async Task AWrapperWithoutTheCwdStartsAndRunsInTheProcessFolder()
    {
        WriteProject(withCwd: false);

        await RunPrepAsync();

        AssertStartedIn(ProcessFolder);
        AssertRanIn(ProcessFolder);
    }

    private Task<ProcessPrepResult> RunPrepAsync() =>
        ProjectPrep.RunProcessPrepAsync(
            Project,
            "server",
            [PrepCommand],
            ProcessFolder,
            new Dictionary<string, string>(),
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

    private void WriteProject(bool withCwd)
    {
        Directory.CreateDirectory(ProcessFolder);
        string wrapper = withCwd ? """{ "prepWrapper": ["./prep-wrapper-cwd.cmd", "{cwd}"] }""" : """{ "prepWrapper": ["./prep-wrapper.cmd"] }""";
        string name = withCwd ? "prep-wrapper-cwd.cmd" : "prep-wrapper.cmd";
        File.WriteAllText(Path.Combine(Project, ProjectProfile.FileName), wrapper);
        File.WriteAllText(Path.Combine(Project, name), withCwd ? WrapperWithCwd : WrapperWithoutCwd);
        File.WriteAllText(PrepCommand, PrepScript);
    }

    private string PrepScript => "@echo off\n" + $"> \"{Path.Combine(Project, "prep-cwd.txt")}\" echo %CD%\n";

    private void AssertStartedIn(string folder)
    {
        string started = Read("wrapper-cwd.txt");
        Assert.True(ProjectPaths.AreSame(folder, started), $"the wrapper started in {started}");
    }

    private void AssertRanIn(string folder)
    {
        string ran = Read("prep-cwd.txt");
        Assert.True(ProjectPaths.AreSame(folder, ran), $"the prep ran in {ran}");
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(Project, name)).Trim();
}
