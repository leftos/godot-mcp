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
    private const int TestTimeoutMs = 90_000;

    // A 1 x 1 PNG, so an import has something to import.
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;
    private readonly List<IDisposable> _projects = [];

    public PrepTests() => _tools = new RuntimeTools(_harness.Sessions);

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
        Assert.Equal(5, called["value"]!.GetValue<int>());
        Assert.Equal("up-to-date", second.Prep.Build);
        Assert.Null(second.Prep.BuildMs);
        Assert.Equal(string.Empty, Git.Status(csProbe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
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

        Assert.Contains("CsProbeNode.cs:10: CS1002", refused.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(csProbe.Directory, ".godot", "godot-mcp", "build.log"), refused.Message, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List());
        Assert.False(File.Exists(Path.Combine(csProbe.Directory, "override.cfg")));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnInputProbeRunNeedsNoImport()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());

        LaunchResult launched = await LaunchAsync(probe.Directory, prepare: true, cancellation);

        Assert.Equal("no-csproj", launched.Prep.Build);
        Assert.Equal("not-needed", launched.Prep.Import);
        Assert.Equal(string.Empty, Git.Status(probe.Directory));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task MissingImportedFilesAreImported()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        string target = await ImportIconThenDeleteGodotFolderAsync(probe.Directory, cancellation);

        LaunchResult launched = await LaunchAsync(probe.Directory, prepare: true, cancellation);

        Assert.Equal("done", launched.Prep.Import);
        Assert.True(launched.Prep.ImportMs > 0);
        Assert.True(File.Exists(target), $"{target} was not imported");
    }

    [Fact(Timeout = TestTimeoutMs)]
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
        Assert.Equal(["first"], _harness.Sessions.List().Select(session => session.Name));
    }

    [Fact(Timeout = TestTimeoutMs)]
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

    /// <summary>Adds a PNG, imports it with Godot the way the editor would, then deletes .godot/; returns the import's target.</summary>
    private static async Task<string> ImportIconThenDeleteGodotFolderAsync(string project, CancellationToken cancellation)
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
