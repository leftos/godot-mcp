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
          "once": {"command": ["pwsh", "-NoProfile", "-File", "ticker-once.ps1"], "description": "one line then exit"},
          "failing": {"command": ["pwsh", "-NoProfile", "-File", "ticker-fails.ps1"], "description": "a stderr line then exit 1"},
          "unready": {
            "command": ["pwsh", "-NoProfile", "-File", "ticker.ps1"],
            "ready": {"output": "Now listening on", "timeoutMs": 2000},
            "description": "a line a second, never the ready line"
          },
          "ticker3": {
            "command": ["pwsh", "-NoProfile", "-File", "ticker.ps1"],
            "ready": {"output": "ticker 3", "timeoutMs": 10000},
            "description": "a line a second, ready on its third"
          },
          "gone": {"command": ["./gone.exe", "/c", "exit", "0"], "description": "a program the start-failure test removes"}
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
    public async Task AReadyLineIsWaitedForAtLaunch()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        JsonObject ready = JsonNode
            .Parse(
                await _process.RunProcessAsync(
                    _probe.Directory,
                    "ticker",
                    options: new ProcessRunOptions(Ready: new ReadyOptions("ticker 1")),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();
        string session = ready["session"]!.GetValue<string>();
        JsonObject met = ready["ready"]!.AsObject();

        Assert.Equal("ticker 1", met["line"]!.GetValue<string>());
        Assert.Equal("stdout", met["stream"]!.GetValue<string>());
        Assert.True(met["ms"]!.GetValue<long>() >= 0);

        JsonObject plain = JsonNode
            .Parse(
                await _process.RunProcessAsync(
                    _probe.Directory,
                    "ticker",
                    options: new ProcessRunOptions(Session: "plain"),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();

        Assert.Null(plain["ready"]);

        await _project.StopProjectAsync(session, cancellationToken: cancellation);
        await _project.StopProjectAsync("plain", cancellationToken: cancellation);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AReadyTimeoutStopsTheProcessAndNamesThePatternAndTime()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _process.RunProcessAsync(_probe.Directory, "unready", cancellationToken: cancellation)
        );

        Assert.StartsWith(
            "The process 'unready' printed no line matching 'Now listening on' within 2 s of load-adjusted time, so it was stopped; "
                + "its last output lines:\nstdout:\nticker ",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.EndsWith("\nstderr:\n(none)", refused.Message, StringComparison.Ordinal);
        Assert.Empty(JsonNode.Parse(_project.ListSessions(includeStopped: true))!["sessions"]!.AsArray());

        int childPid = await WaitForChildPidAsync();
        await WaitForGoneAsync(childPid);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AReadyWaitRefusesAProcessThatExitsFirst()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _process.RunProcessAsync(
                _probe.Directory,
                "failing",
                options: new ProcessRunOptions(Ready: new ReadyOptions("Now listening on")),
                cancellationToken: cancellation
            )
        );

        Assert.Equal(
            "The process 'failing' exited (code 1) before a line matched 'Now listening on'; its last stderr lines:\n[ticker] failing",
            refused.Message
        );
        Assert.Empty(JsonNode.Parse(_project.ListSessions(includeStopped: true))!["sessions"]!.AsArray());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartStartsTheStoredCommandAgainAndKeepsTheBufferNumbering()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "ticker", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();
        int firstProcessId = started["processId"]!.GetValue<int>();
        int firstChildPid = await WaitForChildPidAsync();
        await WaitForTickerLineAsync(session);
        long after = JsonNode.Parse(_project.GetDebugOutput(limit: 100, session: session))!["stdoutTotalLines"]!.GetValue<long>();

        JsonObject restarted = JsonNode.Parse(await _project.RestartProjectAsync(session: session, cancellationToken: cancellation))!.AsObject();

        Assert.NotEqual(firstProcessId, restarted["processId"]!.GetValue<int>());
        Assert.Equal(firstProcessId, restarted["previousProcessId"]!.GetValue<int>());
        Assert.Null(restarted["previousExitCode"]);
        Assert.Null(restarted["ready"]);
        Assert.Equal(
            started["command"]!.AsArray().Select(node => node!.GetValue<string>()),
            restarted["command"]!.AsArray().Select(node => node!.GetValue<string>())
        );
        Assert.Equal(started["log"]!.GetValue<string>(), restarted["log"]!.GetValue<string>());

        int secondChildPid = await WaitForNewChildPidAsync(firstChildPid);
        await WaitForGoneAsync(firstChildPid);
        using (var secondChild = Process.GetProcessById(secondChildPid))
        {
            Assert.False(secondChild.HasExited);
        }

        string waited = await _runtime.WaitForAsync(new WaitCondition(Output: TickerLine, After: after), 10_000, session, cancellation);
        Assert.True(JsonNode.Parse(waited)!["met"]!.GetValue<bool>());

        JsonObject stopped = JsonNode.Parse(await _project.StopProjectAsync(session, cancellationToken: cancellation))!.AsObject();
        Assert.Equal("process", stopped["kind"]!.GetValue<string>());
        await WaitForGoneAsync(secondChildPid);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartWaitsForTheNewProcessesReadyLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);

        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "ticker3", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();
        Assert.Equal("ticker 3", started["ready"]!["line"]!.GetValue<string>());

        JsonObject restarted = JsonNode.Parse(await _project.RestartProjectAsync(session: session, cancellationToken: cancellation))!.AsObject();

        Assert.Equal("ticker 3", restarted["ready"]!["line"]!.GetValue<string>());
        Assert.True(restarted["ready"]!["ms"]!.GetValue<long>() >= 2000);

        await _project.StopProjectAsync(session, cancellationToken: cancellation);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartThatCannotStartLeavesTheSessionStoppable()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);
        string program = Path.Combine(_probe.Directory, "gone.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), program);

        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "gone", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();
        string aside = program + ".away";
        File.Move(program, aside);
        try
        {
            McpException refused = await Assert.ThrowsAsync<McpException>(() =>
                _project.RestartProjectAsync(session: session, cancellationToken: cancellation)
            );
            Assert.StartsWith("Godot could not be started from ", refused.Message, StringComparison.Ordinal);

            JsonObject stopped = JsonNode.Parse(await _project.StopProjectAsync(session, cancellationToken: cancellation))!.AsObject();
            Assert.Equal("process", stopped["kind"]!.GetValue<string>());

            McpException again = await Assert.ThrowsAsync<McpException>(() =>
                _project.RestartProjectAsync(session: session, cancellationToken: cancellation)
            );
            Assert.StartsWith("Godot could not be started from ", again.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Move(aside, program);
        }
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartAfterShutdownIsRefused()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await WriteProfileAsync(cancellation);
        JsonObject started = JsonNode.Parse(await _process.RunProcessAsync(_probe.Directory, "ticker", cancellationToken: cancellation))!.AsObject();
        string session = started["session"]!.GetValue<string>();

        _harness.Sessions.Shutdown();

        SessionException refused = await Assert.ThrowsAsync<SessionException>(() =>
            _harness.Sessions.RestartAsync(session, prepare: false, cancellation)
        );

        Assert.Equal(SessionRegistry.ShuttingDown, refused.Message);
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

    private async Task<int> WaitForNewChildPidAsync(int oldPid)
    {
        string pidFile = Path.Combine(_probe.Directory, "ticker-child.pid");
        using CancellationTokenSource timeout = new(Wait);
        while (true)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile, timeout.Token)).Trim(), out int pid) && pid != oldPid)
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
