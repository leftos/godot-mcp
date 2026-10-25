using System.Diagnostics;
using System.Globalization;
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

    // A prep wrapper taking {log}, then "--", then the command: it records the command it received, runs it with its output in
    // {log}, and exits with its code.
    private const string PrepWrapperScript =
        "$log = $args[0]\n"
        + "$command = @($args | Select-Object -Skip 1)\n"
        + "if ($command.Count -gt 0 -and $command[0] -eq '--') { $command = @($command | Select-Object -Skip 1) }\n"
        + "Add-Content -LiteralPath (Join-Path $PSScriptRoot 'prep-wrapper-ran.txt') -Value ($command -join ' ')\n"
        + "& $command[0] @($command | Select-Object -Skip 1) *> $log\n"
        + "exit $LASTEXITCODE\n";

    // A prep that leaves its process's marker where its own folder points, holding the variable the profile sets for the
    // process; once the test writes the file it names, its next run fails instead, so a restart's prep can be made to fail.
    private const string PrepStepScript =
        "if (Test-Path -LiteralPath 'prep-fail-once.txt') {\n"
        + "  [Console]::Error.WriteLine('the prep failed on its second run')\n"
        + "  exit 5\n"
        + "}\n"
        + "Set-Content -LiteralPath 'prep-marker.txt' -Value $env:PREP_ENV\n";

    // A process that prints whether the marker is in its own folder, ready either way, and then stays up as the ticker does.
    private const string PrepCheckScript =
        "if (Test-Path -LiteralPath 'prep-marker.txt') { 'prep-marker: yes' } else { 'prep-marker: no' }\n"
        + "while ($true) { Start-Sleep -Milliseconds 200 }\n";

    // A prep that fails, as a broken build would.
    private const string PrepFailsScript = "[Console]::Error.WriteLine('prep failed')\nexit 3\n";

    private const string TickerLine = "ticker \\d+";

    // The probe's SmallButton counts its presses in press_count (tests/fixtures/InputProbe/main.gd).
    private const string PressCountScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n"
        + "\treturn scene_tree.root.get_node(\"Main/SmallButton\").press_count\n";

    // A hang guard on a wait for the process's own output, its child's pid or a process's end: the ticker prints at once and then
    // once a second, but it and its child each start through pwsh, which a busy machine can hold for tens of seconds.
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(2);

    // The HResult of an IOException for ERROR_SHARING_VIOLATION.
    private const int SharingViolation = unchecked((int)0x80070020);

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
        int childPid = await WaitForChildPidAsync(session, null);

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
        int childPid = await WaitForChildPidAsync(session, null);

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

        // The refused session is no longer listed, so there is no session whose stop could end the wait early.
        int childPid = await WaitForChildPidAsync(null, null);
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
        int firstChildPid = await WaitForChildPidAsync(session, null);
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

        int secondChildPid = await WaitForChildPidAsync(session, firstChildPid);
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

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APrepRunsThroughThePrepWrapperBeforeTheStartAndAgainOnRestart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WritePreppingProfile();
        string folder = Path.Combine(_probe.Directory, "server");
        string marker = Path.Combine(folder, "prep-marker.txt");
        string log = Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.prep.log");
        string wrapperLog = Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.prep.wrapper.log");
        string ran = Path.Combine(_probe.Directory, "prep-wrapper-ran.txt");

        JsonObject started = JsonNode
            .Parse(
                await _process.RunProcessAsync(
                    _probe.Directory,
                    "prepped",
                    options: new ProcessRunOptions(Session: Session),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();

        // The process's own first line reports the marker, so it was there before the process started.
        Assert.Equal("prep-marker: yes", started["ready"]!["line"]!.GetValue<string>());
        JsonObject prep = started["prep"]!.AsObject();
        Assert.Equal(0, prep["exit"]!.GetValue<int>());
        Assert.Equal(log, prep["log"]!.GetValue<string>());
        Assert.Equal(["pwsh", "-NoProfile", "-File", "prep-step.ps1"], prep["command"]!.AsArray().Select(node => node!.GetValue<string>()));

        // Wrapped, the prep still ran in the process's own folder, with the variables the profile sets for the process.
        Assert.True(File.Exists(marker), $"the prep wrote no marker in {folder}");
        Assert.False(File.Exists(Path.Combine(_probe.Directory, "prep-marker.txt")), "the prep ran in the project folder, not in the process's cwd");
        Assert.Equal("from-profile", File.ReadAllText(marker).Trim());
        Assert.True(File.Exists(log), $"the prep log {log} was not written");
        Assert.True(File.Exists(wrapperLog), $"the wrapper's own log {wrapperLog} was not written");
        string[] firstRan = File.ReadAllLines(ran);
        Assert.Single(firstRan);
        Assert.Contains("prep-step.ps1", firstRan[0], StringComparison.Ordinal);

        File.Delete(marker);

        JsonObject restarted = JsonNode.Parse(await _project.RestartProjectAsync(session: Session, cancellationToken: cancellation))!.AsObject();

        // The marker was gone, so the restarted process's own line says its prep ran again before the relaunch.
        Assert.Equal("prep-marker: yes", restarted["ready"]!["line"]!.GetValue<string>());
        Assert.Equal(0, restarted["prep"]!["exit"]!.GetValue<int>());
        Assert.Equal(log, restarted["prep"]!["log"]!.GetValue<string>());
        Assert.Equal("from-profile", File.ReadAllText(marker).Trim());
        string[] twiceRan = File.ReadAllLines(ran);
        Assert.Equal(2, twiceRan.Length);
        Assert.Contains("prep-step.ps1", twiceRan[1], StringComparison.Ordinal);

        await _project.StopProjectAsync(Session, cancellationToken: cancellation);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task APrepWithoutAPrepWrapperRunsUnwrapped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WritePreppingProfile(withWrapper: false);
        string marker = Path.Combine(_probe.Directory, "server", "prep-marker.txt");
        string log = Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.prep.log");

        JsonObject started = JsonNode
            .Parse(
                await _process.RunProcessAsync(
                    _probe.Directory,
                    "prepped",
                    options: new ProcessRunOptions(Session: Session),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();

        Assert.Equal("prep-marker: yes", started["ready"]!["line"]!.GetValue<string>());
        Assert.Equal(0, started["prep"]!["exit"]!.GetValue<int>());
        Assert.Equal(log, started["prep"]!["log"]!.GetValue<string>());
        Assert.Equal("from-profile", File.ReadAllText(marker).Trim());
        Assert.True(File.Exists(log), $"the prep log {log} was not written");
        Assert.False(File.Exists(Path.Combine(_probe.Directory, "prep-wrapper-ran.txt")), "a prep wrapper ran although the profile names none");

        await _project.StopProjectAsync(Session, cancellationToken: cancellation);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task ARestartWhosePrepFailsLeavesTheSessionStopped()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WritePreppingProfile();
        string log = Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.prep.log");

        JsonObject started = JsonNode
            .Parse(
                await _process.RunProcessAsync(
                    _probe.Directory,
                    "prepped",
                    options: new ProcessRunOptions(Session: Session),
                    cancellationToken: cancellation
                )
            )!
            .AsObject();
        Assert.Equal("prep-marker: yes", started["ready"]!["line"]!.GetValue<string>());

        File.WriteAllText(Path.Combine(_probe.Directory, "server", "prep-fail-once.txt"), string.Empty);

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _project.RestartProjectAsync(session: Session, cancellationToken: cancellation)
        );

        Assert.Equal($"The prep of process {Session} exited 5, so it was not started. Its log: {log}", refused.Message);
        Assert.False(ListSession(Session, includeStopped: true)["live"]!.GetValue<bool>());

        JsonObject stopped = JsonNode.Parse(await _project.StopProjectAsync(Session, cancellationToken: cancellation))!.AsObject();
        Assert.Equal("process", stopped["kind"]!.GetValue<string>());
        Assert.True(stopped["alreadyExited"]!.GetValue<bool>());
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AFailingPrepRefusesTheStartAndLeavesNoSession()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        WritePreppingProfile();
        string log = Path.Combine(ProjectPrep.LogFolder(_probe.Directory), $"process-{Session}.prep.log");

        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _process.RunProcessAsync(
                _probe.Directory,
                "failingprep",
                options: new ProcessRunOptions(Session: Session),
                cancellationToken: cancellation
            )
        );

        Assert.Equal($"The prep of process {Session} exited 3, so it was not started. Its log: {log}", refused.Message);
        Assert.True(File.Exists(log), $"the prep log {log} was not written");
        Assert.Empty(JsonNode.Parse(_project.ListSessions(includeStopped: true))!["sessions"]!.AsArray());
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

    // A profile whose processes run in a "server" subfolder and prep there: the prep's marker and the variable it holds show
    // the folder and the environment the prep ran with. The first process prep is wrapped when withWrapper is set.
    private void WritePreppingProfile(bool withWrapper = true)
    {
        string folder = Path.Combine(_probe.Directory, "server");
        Directory.CreateDirectory(folder);
        // Absolute, since a wrapped prep runs its wrapper in the process's cwd, not in the project folder.
        string wrapperPath = Path.Combine(_probe.Directory, "prep-wrapper.ps1").Replace('\\', '/');
        string wrapper = withWrapper
            ? $"\"prepWrapper\": [\"pwsh\", \"-NoProfile\", \"-File\", \"{wrapperPath}\", \"{{log}}\", \"--\"],"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(_probe.Directory, ProjectProfile.FileName),
            $$"""
            {
              {{wrapper}}
              "processes": {
                "prepped": {
                  "command": ["pwsh", "-NoProfile", "-File", "prep-check.ps1"],
                  "cwd": "server",
                  "env": {"PREP_ENV": "from-profile"},
                  "prep": ["pwsh", "-NoProfile", "-File", "prep-step.ps1"],
                  "ready": {"output": "prep-marker: (yes|no)"},
                  "description": "prints whether its prep ran before it started"
                },
                "failingprep": {
                  "command": ["pwsh", "-NoProfile", "-File", "prep-check.ps1"],
                  "cwd": "server",
                  "prep": ["pwsh", "-NoProfile", "-File", "prep-fails.ps1"],
                  "description": "a prep that exits 3"
                }
              }
            }
            """
        );
        File.WriteAllText(Path.Combine(_probe.Directory, "prep-wrapper.ps1"), PrepWrapperScript);
        File.WriteAllText(Path.Combine(folder, "prep-step.ps1"), PrepStepScript);
        File.WriteAllText(Path.Combine(folder, "prep-check.ps1"), PrepCheckScript);
        File.WriteAllText(Path.Combine(folder, "prep-fails.ps1"), PrepFailsScript);
    }

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

    /// <summary>
    /// Waits for the ticker's child to write its pid, one other than <paramref name="oldPid"/> when that is given. Fails at once
    /// when <paramref name="session"/> is given and stops first, else at the <see cref="Wait"/> guard, naming the last pid read.
    /// </summary>
    private async Task<int> WaitForChildPidAsync(string? session, int? oldPid)
    {
        string pidFile = Path.Combine(_probe.Directory, "ticker-child.pid");
        var guard = Stopwatch.StartNew();
        int? last = null;
        while (true)
        {
            // The session's state is read before the file, so a pid written before the session stopped is still found.
            bool stopped = HasStopped(session);
            last = ReadPid(pidFile) ?? last;
            if (last is int pid && pid != oldPid)
            {
                return pid;
            }

            if (stopped || guard.Elapsed >= Wait)
            {
                Assert.Fail(ChildPidMissing(pidFile, oldPid, last, stopped ? $"the session '{session}' stopped first" : null));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Why <see cref="WaitForChildPidAsync"/> failed: the pid it waited for, the last one it read, and why it gave up.</summary>
    private static string ChildPidMissing(string pidFile, int? oldPid, int? last, string? stopped)
    {
        string wanted = oldPid is null ? "a pid" : $"a pid other than {oldPid}";
        string why = stopped ?? $"none came within {Wait.TotalMinutes:0} min";
        string read = last?.ToString(CultureInfo.InvariantCulture) ?? "none";
        return $"The ticker's child did not write {wanted} to {pidFile}: {why}; the last pid read was {read}.";
    }

    /// <summary>
    /// The pid in the ticker child's pid file; null while the file is missing or not a pid yet, or a sharing violation says
    /// the child is still writing or renaming it.
    /// </summary>
    private static int? ReadPid(string pidFile)
    {
        try
        {
            using FileStream stream = new(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return int.TryParse(reader.ReadToEnd().Trim(), out int pid) ? pid : null;
        }
        catch (IOException e) when (e is FileNotFoundException || e.HResult == SharingViolation)
        {
            // Not written yet, or held by its writer for a moment: the next poll reads it again.
            return null;
        }
    }

    /// <summary>Whether a session is given and is no longer listed live.</summary>
    private bool HasStopped(string? session)
    {
        if (session is null)
        {
            return false;
        }

        JsonArray sessions = JsonNode.Parse(_project.ListSessions(includeStopped: true))!["sessions"]!.AsArray();
        return !sessions.Any(entry => entry!["name"]!.GetValue<string>() == session && entry!["live"]!.GetValue<bool>());
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
