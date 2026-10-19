using System.Diagnostics;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// A process session on the InputProbe copy: run_process starts the fixture's ticker, its output fills the session's buffers,
/// wait_for meets a printed line, a runtime tool is refused, and stop_project ends it and the child it started.
/// </summary>
public sealed class ProcessSessionTests : IAsyncDisposable
{
    private const string Session = "ticker";
    private const string Profile = """
        {"processes": {
          "ticker": {"command": ["pwsh", "-NoProfile", "-File", "ticker.ps1"], "description": "a line a second"},
          "once": {"command": ["pwsh", "-NoProfile", "-File", "ticker-once.ps1"], "description": "one line then exit"}
        }}
        """;

    private const string TickerLine = "ticker \\d+";

    // The probe's SmallButton counts its presses in press_count (tests/fixtures/InputProbe/main.gd).
    private const string PressCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn scene_tree.root.get_node(\"Main/SmallButton\").press_count\n";

    // How long a step that waits on the process's own output may take; the ticker prints at once and then once a second.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly ProjectTools _project;
    private readonly ProcessTools _process;
    private readonly RuntimeTools _runtime;

    public ProcessSessionTests()
    {
        _project = new ProjectTools(_harness.Sessions);
        _process = new ProcessTools(_harness.Sessions);
        _runtime = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_harness.Sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly);
        _services = services.BuildServiceProvider();
        McpServerOptions options = _services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        _server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _services.DisposeAsync();
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AProcessSessionRunsWaitsAndStopsWithItsTree()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "ticker", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();
        int processId = started["processId"]!.GetValue<int>();
        string log = started["log"]!.GetValue<string>();
        int childPid = await WaitForChildPidAsync();

        Assert.Equal(Session, session);
        Assert.True(processId > 0);
        Assert.Equal(Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.log"), log);
        Assert.True(File.Exists(log), $"the log {log} was not written");
        Assert.Equal("pwsh", Path.GetFileNameWithoutExtension(started["command"]![0]!.GetValue<string>()));

        await WaitForTickerLineAsync(session);

        JsonObject listed = ListSession(session, includeStopped: false);
        Assert.Equal("process", listed["kind"]!.GetValue<string>());
        Assert.True(listed["live"]!.GetValue<bool>());
        Assert.Equal(processId, listed["processId"]!.GetValue<int>());
        Assert.Null(listed["gameProcessId"]);

        string waited = await _runtime.WaitForAsync(new WaitCondition(Output: TickerLine), 10_000, session, cancellation);
        Assert.True(JsonNode.Parse(waited)!["met"]!.GetValue<bool>());

        string refusal = GodotSession.ProcessRefusal(Session, "pwsh");
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _runtime.ClickAsync(new InputTarget("SmallButton"), session: session, cancellationToken: cancellation)
        );
        Assert.Equal(refusal, refused.Message);

        JsonObject batchWait = await BatchAsync(new BatchStep(Assert: "wait", Output: TickerLine, TimeoutMs: 10_000), session, cancellation);
        Assert.True(batchWait["passed"]!.GetValue<bool>());
        JsonObject batchTool = await BatchAsync(
            new BatchStep(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = "SmallButton" } }),
            session,
            cancellation
        );
        Assert.False(batchTool["passed"]!.GetValue<bool>());
        Assert.Equal(refusal, batchTool["failedAt"]!["reason"]!.GetValue<string>());

        JsonObject stopped = JsonNode.Parse(await _project.StopProjectAsync(session, cancellationToken: cancellation))!.AsObject();
        Assert.Equal("process", stopped["kind"]!.GetValue<string>());
        Assert.True(stopped["killed"]!.GetValue<bool>());
        Assert.False(stopped["alreadyExited"]!.GetValue<bool>());
        Assert.Null(stopped["exitCode"]);
        Assert.False(ListSession(session, includeStopped: true)["live"]!.GetValue<bool>());

        await WaitForGoneAsync(childPid);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AProcessThatExitsEndsItsTreeAndStaysListedAsStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "once", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();
        int childPid = await WaitForChildPidAsync();

        await WaitUntilNotLiveAsync(session);

        Assert.False(ListSession(session, includeStopped: true)["live"]!.GetValue<bool>());
        await WaitForGoneAsync(childPid);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ABatchRefusesAProcessToolStepBeforeAnyStepRuns()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);
        await _project.RunProjectAsync(_probe.Directory, options: new RunOptions(Session: "game"), cancellationToken: cancellation);
        await _process.RunProcessAsync(_probe.Directory, "ticker", options: new ProcessRunOptions("server"), cancellationToken: cancellation);

        JsonObject refused = JsonNode
            .Parse(await _runtime.BatchDriveAsync([ClickOn(null), ClickOn("server")], _server, "game", cancellation))!
            .AsObject();

        Assert.False(refused["passed"]!.GetValue<bool>());
        Assert.Equal(1, refused["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal(GodotSession.ProcessRefusal("server", "pwsh"), refused["failedAt"]!["reason"]!.GetValue<string>());
        Assert.Empty(refused["steps"]!.AsArray());
        Assert.Equal("""{"value":0}""", await PressCountAsync(cancellation));

        JsonObject ran = JsonNode.Parse(await _runtime.BatchDriveAsync([ClickOn("game")], _server, "server", cancellation))!.AsObject();
        Assert.True(ran["passed"]!.GetValue<bool>(), ran.ToJsonString());
        Assert.Equal("""{"value":1}""", await PressCountAsync(cancellation));

        await _project.StopProjectAsync("server", cancellationToken: cancellation);
        await _project.StopProjectAsync("game", cancellationToken: cancellation);
    }

    private static BatchStep ClickOn(string? session)
    {
        JsonObject args = new() { ["target"] = new JsonObject { ["element"] = "SmallButton" } };
        if (session is not null)
        {
            args["session"] = session;
        }

        return new BatchStep(Tool: "click", Args: args);
    }

    private Task<string> PressCountAsync(CancellationToken cancellation) => _runtime.RunScriptAsync(PressCountScript, 10_000, "game", cancellation);

    private Task WriteProfileAsync(CancellationToken cancellation) =>
        File.WriteAllTextAsync(Path.Combine(_probe.Directory, ProjectProfile.FileName), Profile, cancellation);

    private async Task WaitUntilNotLiveAsync(string session)
    {
        using CancellationTokenSource timeout = new(Wait);
        while (true)
        {
            JsonArray sessions = JsonNode.Parse(_project.ListSessions(includeStopped: true))!["sessions"]!.AsArray();
            if (sessions.Any(entry => entry!["name"]!.GetValue<string>() == session && !entry!["live"]!.GetValue<bool>()))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private async Task<JsonObject> BatchAsync(BatchStep step, string session, CancellationToken cancellation)
    {
        string result = await _runtime.BatchDriveAsync([step], _server, session, cancellation);
        return JsonNode.Parse(result)!.AsObject();
    }

    private JsonObject ListSession(string session, bool includeStopped)
    {
        JsonArray sessions = JsonNode.Parse(_project.ListSessions(includeStopped))!["sessions"]!.AsArray();
        return sessions.Single(entry => entry!["name"]!.GetValue<string>() == session)!.AsObject();
    }

    private async Task WaitForTickerLineAsync(string session)
    {
        using CancellationTokenSource timeout = new(Wait);
        while (true)
        {
            JsonObject output = JsonNode.Parse(_project.GetDebugOutput(limit: 100, session: session))!.AsObject();
            if (output["stdout"]!.AsArray().Any(line => line!.GetValue<string>().Contains("ticker ", StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private async Task<int> WaitForChildPidAsync()
    {
        string pidFile = Path.Combine(_probe.Directory, "ticker-child.pid");
        using CancellationTokenSource timeout = new(Wait);
        while (true)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile, timeout.Token)).Trim(), out int pid))
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForGoneAsync(int pid)
    {
        using CancellationTokenSource timeout = new(Wait);
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }
}
