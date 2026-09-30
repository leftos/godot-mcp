using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// run_project's prep against the real dotnet and Godot: an unbuilt CsProbe copy built before its run, a broken source
/// refusing the launch, prepare "never", and a Godot import run when imported files are missing.
/// </summary>
public sealed partial class PrepTests : IAsyncDisposable
{
    private const int BuildTestTimeoutMs = 240_000;

    // A 1 x 1 PNG, so an import has something to import.
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    // A prep wrapper taking {log}, then "--", then the command: it notes the command's program, runs the command with its
    // output in {log}, and exits with its code.
    private const string RunningWrapper =
        "$log = $args[0]\n"
        + "$command = @($args | Select-Object -Skip 1)\n"
        + "if ($command.Count -gt 0 -and $command[0] -eq '--') { $command = @($command | Select-Object -Skip 1) }\n"
        + "Add-Content -LiteralPath (Join-Path $PSScriptRoot 'prep-wrapper-ran.txt') -Value ([IO.Path]::GetFileName($command[0]))\n"
        + "& $command[0] @($command | Select-Object -Skip 1) *> $log\n"
        + "exit $LASTEXITCODE\n";

    // A prep wrapper that runs nothing and says it stopped the command.
    private const string StoppingWrapper = "exit 124\n";
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;
    private readonly List<IDisposable> _projects = [];

    public PrepTests() => _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task AnUnbuiltCopyIsBuiltThenUpToDate()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());

        LaunchResult first = await LaunchAsync(csProbe.Directory, prepare: true, cancellation);
        JsonNode called = await CallAsync("CsProbe", "PlayStep", "4");
        await _harness.Sessions.StopAsync(null, cancellation);
        LaunchResult second = await LaunchAsync(csProbe.Directory, prepare: true, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        Assert.Equal("built", first.Prep.Build);
        Assert.True(first.Prep.BuildMs > 0);
        Assert.Equal("not-needed", first.Prep.Import);
        Assert.StartsWith(Path.Combine(csProbe.Directory, ".godot", "godot-mcp"), first.Prep.BuildLog, StringComparison.Ordinal);
        Assert.EndsWith("build.log", first.Prep.BuildLog, StringComparison.Ordinal);
        Assert.True(File.Exists(first.Prep.BuildLog), $"{first.Prep.BuildLog} does not exist");
        Assert.Equal(5, called["value"]!.GetValue<int>());
        Assert.Equal("up-to-date", second.Prep.Build);
        Assert.Null(second.Prep.BuildMs);
        Assert.Null(second.Prep.BuildLog);
        Assert.Equal(string.Empty, Git.Status(csProbe.Directory));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task NeverLaunchesAnUnbuiltCopyAsItIs()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());

        LaunchResult launched = await LaunchAsync(csProbe.Directory, prepare: false, cancellation);

        Assert.Equal("skipped", launched.Prep.Build);
        Assert.Equal("skipped", launched.Prep.Import);
        Assert.False(File.Exists(PrepScan.AssemblyPath(csProbe.Directory, "CsProbe")));
        await Assert.ThrowsAsync<McpException>(() => CallAsync("CsProbe", "PlayStep", "4"));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task ABrokenSourceRefusesTheLaunchWithTheCompilerError()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string source = File.ReadAllText(csProbe.SourcePath("CsProbeNode.cs"));
        csProbe.WriteSource("CsProbeNode.cs", source.Replace("\"hidden\";", "\"hidden\"", StringComparison.Ordinal));

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => LaunchAsync(csProbe.Directory, prepare: true, cancellation));

        Assert.StartsWith("The Debug C# build of ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CsProbeNode.cs:10: CS1002", refused.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(csProbe.Directory, ".godot", "godot-mcp", "build.log"), refused.Message, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
        Assert.False(File.Exists(Path.Combine(csProbe.Directory, "override.cfg")));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnInputProbeRunNeedsNoImport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());

        LaunchResult launched = await LaunchAsync(probe.Directory, prepare: true, cancellation);

        Assert.Equal("no-csproj", launched.Prep.Build);
        Assert.Equal("not-needed", launched.Prep.Import);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task MissingImportedFilesAreImported()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string target = await ImportIconThenDeleteGodotFolderAsync(probe.Directory, cancellation);

        LaunchResult launched = await LaunchAsync(probe.Directory, prepare: true, cancellation);

        Assert.Equal("done", launched.Prep.Import);
        Assert.True(launched.Prep.ImportMs > 0);
        Assert.EndsWith("import.log", launched.Prep.ImportLog, StringComparison.Ordinal);
        Assert.True(File.Exists(launched.Prep.ImportLog), $"{launched.Prep.ImportLog} does not exist");
        Assert.True(File.Exists(target), $"{target} was not imported");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AnImportWhileAnotherSessionIsLiveIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        await ImportIconThenDeleteGodotFolderAsync(probe.Directory, cancellation);
        await _harness.Sessions.LaunchAsync(Request(probe.Directory, prepare: false), "first", cancellation);

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.LaunchAsync(Request(probe.Directory, prepare: true), "second", cancellation)
        );

        Assert.Equal(
            $"the project at {ProjectPaths.Normalise(probe.Directory)} needs a Godot import, but session(s) first are running on it; "
                + "stop them first, or pass options.prepare: \"never\" to launch without importing.",
            refused.Message
        );
        Assert.Equal(["first"], _harness.Sessions.List(includeStopped: true).Select(session => session.Name));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task TwoLaunchesAtOnceOnAFolderNeedingAnImportImportOnce()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string target = await ImportIconThenDeleteGodotFolderAsync(probe.Directory, cancellation);

        // Both are reserved, and so starting, before either prep runs: the second waits on the folder's lock, not refused.
        Task<LaunchResult> first = _harness.Sessions.LaunchAsync(Request(probe.Directory, prepare: true), "first", cancellation);
        Task<LaunchResult> second = _harness.Sessions.LaunchAsync(Request(probe.Directory, prepare: true), "second", cancellation);
        LaunchResult[] launched = await Task.WhenAll(first, second);

        Assert.Equal(["done", "not-needed"], launched.Select(result => result.Prep.Import).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(target));
    }

    [Fact(Timeout = BuildTestTimeoutMs)]
    public async Task APrepWrapperRunsTheBuildAndTheImport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        await ImportIconThenDeleteGodotFolderAsync(csProbe.Directory, cancellation);
        string marker = WritePrepWrapper(csProbe.Directory, RunningWrapper);
        string logFolder = Path.Combine(csProbe.Directory, ".godot", "godot-mcp");

        LaunchResult launched = await LaunchAsync(csProbe.Directory, prepare: true, cancellation);
        await _harness.Sessions.StopAsync(null, cancellation);

        string[] ran = File.ReadAllLines(marker);
        Assert.Equal("built", launched.Prep.Build);
        Assert.Equal("done", launched.Prep.Import);
        Assert.Contains(ran, line => line.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ran, line => line.Contains("godot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(logFolder, "build.log"), launched.Prep.BuildLog);
        Assert.Contains("CsProbe.dll", File.ReadAllText(launched.Prep.BuildLog!), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(logFolder, "build.wrapper.log")), "build.wrapper.log does not exist");
        Assert.True(File.Exists(Path.Combine(logFolder, "import.wrapper.log")), "import.wrapper.log does not exist");
        Assert.Contains("ran through prepWrapper (pwsh)", launched.Prep.Note, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APrepWrapperExiting124StopsTheBuild()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        string marker = WritePrepWrapper(csProbe.Directory, StoppingWrapper);
        string logFolder = Path.Combine(csProbe.Directory, ".godot", "godot-mcp");

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() => LaunchAsync(csProbe.Directory, prepare: true, cancellation));

        Assert.Equal(
            $"the prep wrapper stopped the build (exit 124); its log: {Path.Combine(logFolder, "build.log")}; "
                + $"the wrapper's output: {Path.Combine(logFolder, "build.wrapper.log")}",
            refused.Message
        );
        Assert.Equal("stopped", SavedBuild.Load(csProbe.Directory)?.State);
        Assert.False(File.Exists(marker), "the stopping wrapper ran its command");
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    /// <summary>
    /// Writes <paramref name="script"/> as the copy's prep wrapper, run by pwsh from the project folder, and commits it with its
    /// godot-mcp.json; returns the file the running wrapper notes each command in.
    /// </summary>
    private static string WritePrepWrapper(string project, string script)
    {
        File.WriteAllText(Path.Combine(project, "prep-wrapper.ps1"), script);
        File.WriteAllText(
            Path.Combine(project, ProjectProfile.FileName),
            """{ "prepWrapper": ["pwsh", "-NoProfile", "-File", "prep-wrapper.ps1", "{log}", "--"] }""" + "\n"
        );
        Git.CommitAll(project);
        return Path.Combine(project, "prep-wrapper-ran.txt");
    }

    /// <summary>Adds a PNG, imports it with Godot the way the editor would, then deletes .godot/; returns the import's target.</summary>
    internal static async Task<string> ImportIconThenDeleteGodotFolderAsync(string project, CancellationToken cancellation)
    {
        File.WriteAllBytes(Path.Combine(project, "icon.png"), Convert.FromBase64String(OnePixelPng));
        string log = Path.Combine(Path.GetTempPath(), "godot-mcp-tests", $"import-{Guid.NewGuid():N}.log");
        ToolProcessRequest import = new(
            Installation.FindGodot(),
            ["--headless", "--path", project, "--import"],
            project,
            log,
            TimeSpan.FromSeconds(60)
        );
        ToolProcessResult imported = await ToolProcess.RunAsync(import, NullLogger.Instance, cancellation);
        File.Delete(log);
        Assert.Equal(0, imported.ExitCode);
        string sidecar = File.ReadAllText(Path.Combine(project, "icon.png.import"));
        Match dest = DestFile().Match(sidecar);
        Assert.True(dest.Success, $"no dest_files in icon.png.import:\n{sidecar}");
        string target = Path.Combine(project, dest.Groups[1].Value);
        Assert.True(File.Exists(target));
        Directory.Delete(Path.Combine(project, ".godot"), recursive: true);
        return target;
    }

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }

    private static LaunchRequest Request(string directory, bool prepare) => new(directory, null, [], [], true, false, prepare);

    private Task<LaunchResult> LaunchAsync(string directory, bool prepare, CancellationToken cancellation) =>
        _harness.Sessions.LaunchAsync(Request(directory, prepare), null, cancellation);

    private async Task<JsonNode> CallAsync(string node, string method, params string[] argsJson)
    {
        JsonElement[] args = [.. argsJson.Select(arg => JsonSerializer.Deserialize<JsonElement>(arg))];
        string json = await _tools.CallMethodAsync(node, method, args, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(json)!;
    }

    [GeneratedRegex("dest_files=\\[\"res://([^\"]+)\"")]
    private static partial Regex DestFile();
}
