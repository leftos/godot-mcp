using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Warm headless hosts against the real Godot: consecutive headless calls on an InputProbe copy answered by one host, a
/// file written outside, an import or a host killed between calls answered by a new one, a host killed during a request
/// failing it with the log from its marker, the folder free once the registry is disposed or stop_project names it, the
/// host listed by list_sessions, and a CsProbe copy's call run cold.
/// </summary>
public sealed class WarmHeadlessTests : IAsyncDisposable
{
    private const int TestTimeoutMs = 120_000;
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MoveRetryBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan HangWait = TimeSpan.FromSeconds(60);

    private readonly SessionHarness _harness = new();
    private readonly HeadlessTools _tools;
    private readonly List<IDisposable> _projects = [];
    private bool _harnessDisposed;

    public WarmHeadlessTests() => _tools = new HeadlessTools(_harness.Sessions);

    private HeadlessHosts Hosts => _harness.Sessions.HeadlessHosts;

    public async ValueTask DisposeAsync()
    {
        if (!_harnessDisposed)
        {
            await _harness.DisposeAsync();
        }

        foreach (IDisposable project in _projects)
        {
            project.Dispose();
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TwoCallsOnOneFolderAreAnsweredByOneHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");

        JsonNode first = await TreeAsync(probe.Directory, cancellation);
        int? firstHost = Hosts.ProcessIdOf(probe.Directory);
        JsonNode second = await TreeAsync(probe.Directory, cancellation);
        int? secondHost = Hosts.ProcessIdOf(probe.Directory);

        Assert.NotNull(firstHost);
        Assert.Equal(firstHost, secondHost);
        Assert.Equal(["Level", "First"], NodeNames(first));
        Assert.Equal(["Level", "First"], NodeNames(second));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AFileWrittenOutsideBetweenCallsStartsANewHostThatReadsIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await TreeAsync(probe.Directory, cancellation);
        int? firstHost = Hosts.ProcessIdOf(probe.Directory);

        WriteScene(probe.Directory, "Second");
        JsonNode edited = await TreeAsync(probe.Directory, cancellation);
        int? secondHost = Hosts.ProcessIdOf(probe.Directory);

        Assert.NotNull(firstHost);
        Assert.NotNull(secondHost);
        Assert.NotEqual(firstHost, secondHost);
        Assert.Equal(["Level", "Second"], NodeNames(edited));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHostKilledBetweenCallsIsReplacedByTheNextCall()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await TreeAsync(probe.Directory, cancellation);
        int killed = Hosts.ProcessIdOf(probe.Directory) ?? throw new InvalidOperationException("no host after the first call");

        using (var host = Process.GetProcessById(killed))
        {
            host.Kill(entireProcessTree: true);
            Assert.True(host.WaitForExit(KillWait), "the host was still running after its kill");
        }

        JsonNode next = await TreeAsync(probe.Directory, cancellation);
        int? replacement = Hosts.ProcessIdOf(probe.Directory);

        Assert.NotNull(replacement);
        Assert.NotEqual(killed, replacement);
        Assert.Equal(["Level", "First"], NodeNames(next));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task TheFolderCanBeMovedOnceTheRegistryIsDisposed()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await TreeAsync(probe.Directory, cancellation);
        Assert.NotNull(Hosts.ProcessIdOf(probe.Directory));

        await _harness.DisposeAsync();
        _harnessDisposed = true;
        string moved = probe.Directory + "-moved";
        await MoveAsync(probe.Directory, moved, cancellation);

        Assert.True(Directory.Exists(moved));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ListSessionsListsAWarmHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await TreeAsync(probe.Directory, cancellation);

        JsonNode listed = JsonNode.Parse(new ProjectTools(_harness.Sessions).ListSessions())!;

        JsonNode host = Assert.Single(listed["headlessHosts"]!.AsArray())!;
        Assert.Equal(ProjectPaths.Normalise(probe.Directory), host["projectPath"]!.GetValue<string>());
        Assert.Equal(Hosts.ProcessIdOf(probe.Directory), host["pid"]!.GetValue<int>());
        Assert.True(host["requests"]!.GetValue<int>() >= 1, $"requests: {host["requests"]}");
        Assert.True(host["idleSeconds"]!.GetValue<int>() >= 0, $"idleSeconds: {host["idleSeconds"]}");
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(host["startedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture).Offset);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task StopProjectOnAFolderReleasesIt()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await TreeAsync(probe.Directory, cancellation);
        Assert.NotNull(Hosts.ProcessIdOf(probe.Directory));

        string stopped = await new ProjectTools(_harness.Sessions).StopProjectAsync(projectPath: probe.Directory, cancellationToken: cancellation);
        string moved = probe.Directory + "-moved";
        await MoveAsync(probe.Directory, moved, cancellation);

        JsonNode result = JsonNode.Parse(stopped)!;
        Assert.Equal(ProjectPaths.Normalise(probe.Directory), result["projectPath"]!.GetValue<string>());
        Assert.True(result["headlessHostStopped"]!.GetValue<bool>(), stopped);
        Assert.Null(Hosts.ProcessIdOf(probe.Directory));
        Assert.True(Directory.Exists(moved));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AnImportStartsANewHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        await RunAsync(probe.Directory, "level.tscn", prepare: true, cancellation);
        int? firstHost = Hosts.ProcessIdOf(probe.Directory);

        // A script declaring a class_name newer than the class cache makes the prep import.
        File.WriteAllText(Path.Combine(probe.Directory, "enemy.gd"), "class_name Enemy\nextends Node\n");
        HeadlessResult result = await RunAsync(probe.Directory, "level.tscn", prepare: true, cancellation);
        int? secondHost = Hosts.ProcessIdOf(probe.Directory);

        Assert.NotNull(firstHost);
        Assert.NotNull(secondHost);
        Assert.NotEqual(firstHost, secondHost);
        Assert.Contains(
            "Enemy",
            File.ReadAllText(Path.Combine(probe.Directory, ".godot", "global_script_class_cache.cfg")),
            StringComparison.Ordinal
        );
        Assert.Equal("Level", result.Result?["nodes"]?[0]?["name"]?.GetValue<string>());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task AHostKilledDuringARequestFailsItWithTheLogFromItsMarkerAndTheNextCallGetsANewHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ProbeProject probe = Track(new ProbeProject());
        WriteScene(probe.Directory, "First");
        WriteHangingScene(probe.Directory);
        await TreeAsync(probe.Directory, cancellation);
        int killed = Hosts.ProcessIdOf(probe.Directory) ?? throw new InvalidOperationException("no host after the first call");

        Task<HeadlessResult> hanging = RunAsync(probe.Directory, "hang.tscn", prepare: false, cancellation);
        await WaitForFileAsync(Path.Combine(probe.Directory, "hang-started.txt"), hanging, cancellation);
        using (var host = Process.GetProcessById(killed))
        {
            host.Kill(entireProcessTree: true);
            Assert.True(host.WaitForExit(KillWait), "the host was still running after its kill");
        }

        SessionException failed = await Assert.ThrowsAsync<SessionException>(() => hanging);
        JsonNode next = await TreeAsync(probe.Directory, cancellation);
        int? replacement = Hosts.ProcessIdOf(probe.Directory);

        string log = HeadlessHost.LogPathOf(ProjectPaths.Normalise(probe.Directory), _harness.Sessions.ServerProcessId);
        Assert.Matches(
            $@"^The headless get_scene_file_tree run on .+ ended \(Godot exited -?\d+\) before it answered\. Its log from the request on, "
                + $@"{Regex.Escape(log)}:\n\[godot-mcp\] request \d+ get_scene_file_tree\n",
            failed.Message
        );
        Assert.Contains("hanging from its static init", failed.Message, StringComparison.Ordinal);
        Assert.NotNull(replacement);
        Assert.NotEqual(killed, replacement);
        Assert.Equal(["Level", "First"], NodeNames(next));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task ACSharpProjectRunsColdWithoutAHost()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        CsProbeProject csProbe = Track(CsProbeProject.Unbuilt());
        WriteScene(csProbe.Directory, "First");
        HeadlessRequest request = new(
            ProjectPaths.Normalise(csProbe.Directory),
            "get_scene_file_tree",
            new JsonObject { ["scene"] = "res://level.tscn" },
            Prepare: false,
            Ceiling
        );

        HeadlessResult result = await HeadlessRunner.RunAsync(_harness.Sessions, request, cancellation);

        Assert.Equal("Level", result.Result?["nodes"]?[0]?["name"]?.GetValue<string>());
        Assert.Null(Hosts.ProcessIdOf(csProbe.Directory));
    }

    private static void WriteScene(string projectDir, string child) =>
        File.WriteAllText(
            Path.Combine(projectDir, "level.tscn"),
            $"[gd_scene format=3]\n\n[node name=\"Level\" type=\"Node2D\"]\n\n[node name=\"{child}\" type=\"Node2D\" parent=\".\"]\n"
        );

    /// <summary>
    /// <c>hang.tscn</c>, whose script, once loaded, prints a line, writes <c>hang-started.txt</c> and never returns: a request
    /// that loads the scene holds its host until the host is killed, and the file says the request is under way.
    /// </summary>
    private static void WriteHangingScene(string projectDir)
    {
        File.WriteAllText(
            Path.Combine(projectDir, "hang.gd"),
            "extends Node2D\n\nstatic func _static_init() -> void:\n\tprint(\"hanging from its static init\")\n"
                + "\tvar started := FileAccess.open(\"res://hang-started.txt\", FileAccess.WRITE)\n\tstarted.store_line(\"started\")\n"
                + "\tstarted.close()\n\twhile true:\n\t\tOS.delay_msec(10)\n"
        );
        File.WriteAllText(
            Path.Combine(projectDir, "hang.tscn"),
            "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://hang.gd\" id=\"1\"]\n\n"
                + "[node name=\"Hang\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n"
        );
    }

    /// <summary>Waits for <paramref name="path"/> to appear, failing when <paramref name="call"/> ends first or it takes too long.</summary>
    private static async Task WaitForFileAsync(string path, Task call, CancellationToken cancellation)
    {
        var waited = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.False(call.IsCompleted, $"the call ended before {path} appeared: {call.Exception?.InnerException?.Message}");
            Assert.True(waited.Elapsed < HangWait, $"{path} did not appear within {HangWait.TotalSeconds} s");
            await Task.Delay(PollPeriod, cancellation);
        }
    }

    /// <summary>
    /// Moves the folder, retrying for up to 2 s while it is refused: a killed Godot holds its folder for tens of ms after its
    /// exit code, and a scanner holds a fresh file.
    /// </summary>
    private static async Task MoveAsync(string from, string to, CancellationToken cancellation)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && waited.Elapsed < MoveRetryBudget)
            {
                await Task.Delay(PollPeriod, cancellation);
            }
        }
    }

    private static string[] NodeNames(JsonNode tree) => [.. tree["nodes"]!.AsArray().Select(node => node!["name"]!.GetValue<string>())];

    private async Task<JsonNode> TreeAsync(string projectDir, CancellationToken cancellation) =>
        JsonNode.Parse(await _tools.GetSceneFileTreeAsync(projectDir, "level.tscn", null, null, cancellation))!;

    /// <summary>get_scene_file_tree on <paramref name="scene"/> through the runner, with the prep or without it.</summary>
    private Task<HeadlessResult> RunAsync(string projectDir, string scene, bool prepare, CancellationToken cancellation)
    {
        HeadlessRequest request = new(
            ProjectPaths.Normalise(projectDir),
            "get_scene_file_tree",
            new JsonObject { ["scene"] = "res://" + scene },
            prepare,
            Ceiling
        );
        return HeadlessRunner.RunAsync(_harness.Sessions, request, cancellation);
    }

    private T Track<T>(T project)
        where T : IDisposable
    {
        _projects.Add(project);
        return project;
    }
}
