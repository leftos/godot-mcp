using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// Two games of one project as two sessions: a quiet host listening on a loopback port and a headless guest joining it, driven
/// together in one batch. The host's click on Send reaches the guest over the socket and the guest's Mirror label shows the
/// count, so the batch's guest wait proves the two sessions and the batch across them.
/// </summary>
public sealed class MultiSessionTests : IAsyncDisposable
{
    private const string NetProbe = "res://NetProbe.tscn";
    private const int JoinedWaitMs = 20_000;
    private const string NoUiBaseline =
        "uiChanged has no baseline: no input gesture has started since launch or since the last uiChanged wait was met; "
        + "send the input first, or wait for {uiChanged: \"now\"} in a parallel group beside an input in another session";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly RuntimeTools _tools;

    public MultiSessionTests()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_harness.Sessions);
        services.AddSingleton(TestCSharp.Unused());
        services.AddMcpServer().WithToolsFromAssembly(typeof(RuntimeTools).Assembly);
        _services = services.BuildServiceProvider();
        McpServerOptions options = _services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        _server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), options, null, _services);
        _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _services.DisposeAsync();
        await _harness.DisposeAsync();
        _probe.Dispose();
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AHostAndAHeadlessGuestDriveInOneBatch()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int port = FreePort();

        await _harness.Sessions.LaunchAsync(HostRequest(port, faults: false), "host", cancellation);
        LaunchResult guest = await _harness.Sessions.LaunchAsync(GuestRequest(port), "guest", cancellation);
        JsonObject joined = await JoinedAsync(cancellation);
        JsonObject connected = await HostConnectedAsync(cancellation);
        JsonObject batch = await BatchAsync(cancellation);
        JsonArray listed = SessionsListed();
        StopResult hostStopped = await _harness.Sessions.StopAsync("host", cancellation);
        StopResult guestStopped = await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.Equal("stdout", joined["value"]!["stream"]!.GetValue<string>());
        Assert.True(joined["value"]!["lineNumber"]!.GetValue<long>() > 0, joined.ToJsonString());
        Assert.True(joined["value"]!["at"]!.GetValue<double>() >= 0, joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        Assert.True(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(batch.ContainsKey("failedAt"), batch.ToJsonString());
        Assert.Equal("guest", guest.Session);
        Assert.Empty(_harness.Sessions.List(includeStopped: false));
        Assert.False(hostStopped.OverrideRemoved);
        Assert.True(guestStopped.OverrideRemoved);
        Assert.False(File.Exists(_probe.OverrideFile));
        Assert.Equal(["guest", "host"], listed.Select(session => session!["name"]!.GetValue<string>()));
        Assert.All(listed, session => Assert.True(session!["live"]!.GetValue<bool>(), session.ToJsonString()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AParallelGroupCancelsTheGuestsWaitWhenTheHostsClickFailsAndPassesWhenItLands()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int port = FreePort();
        await _harness.Sessions.LaunchAsync(HostRequest(port, faults: false), "host", cancellation);
        await _harness.Sessions.LaunchAsync(GuestRequest(port), "guest", cancellation);
        JsonObject joined = await JoinedAsync(cancellation);
        JsonObject connected = await HostConnectedAsync(cancellation);
        BatchStep never = new(Assert: "wait", Expression: "false", TimeoutMs: 10_000, Session: "guest");
        BatchStep missing = HostClick("NoSuchElement");
        BatchStep mirrored = new(
            Assert: "wait",
            Node: "Mirror",
            Property: "text",
            EqualsValue: JsonSerializer.SerializeToElement("presses: 1"),
            Session: "guest"
        );

        JsonObject failed = await GroupAsync(never, missing, cancellation);
        JsonObject passed = await GroupAsync(mirrored, HostClick("Send"), cancellation);
        await _harness.Sessions.StopAsync("host", cancellation);
        await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        Assert.False(failed["passed"]!.GetValue<bool>(), failed.ToJsonString());
        Assert.Equal(0, failed["failedAt"]!["index"]!.GetValue<int>());
        Assert.Equal(1, failed["failedAt"]!["member"]!.GetValue<int>());
        JsonObject wait = failed["steps"]![0]!["members"]![0]!.AsObject();
        Assert.True(wait["cancelled"]!.GetValue<bool>(), failed.ToJsonString());
        Assert.True(wait["sent"]!.GetValue<bool>(), failed.ToJsonString());
        // A 10 s load-adjusted wait cannot time out in under 10 s of wall time, so a shorter span means the cancel ended it.
        double waitedMs = wait["endMs"]!.GetValue<double>() - wait["startMs"]!.GetValue<double>();
        Assert.True(waitedMs < 10_000, $"the wait member ran {waitedMs} ms: {failed.ToJsonString()}");
        Assert.True(passed["passed"]!.GetValue<bool>(), passed.ToJsonString());
        Assert.All(passed["steps"]![0]!["members"]!.AsArray(), member => Assert.True(member!["ok"]!.GetValue<bool>(), passed.ToJsonString()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGuestsWaitArmedBeforeTheHostsClickCatchesALabelShownForOneFrame()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (JsonObject joined, JsonObject connected) = await LaunchPairAsync(cancellation);
        BatchStep flashed = new(
            Assert: "wait",
            Node: "Flash",
            Property: "text",
            EqualsValue: JsonSerializer.SerializeToElement("press 1"),
            Session: "guest"
        );

        JsonObject passed = await GroupAsync(flashed, HostClick("Send"), cancellation);
        await _harness.Sessions.StopAsync("host", cancellation);
        await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        Assert.True(passed["passed"]!.GetValue<bool>(), passed.ToJsonString());
        Assert.All(passed["steps"]![0]!["members"]!.AsArray(), member => Assert.True(member!["ok"]!.GetValue<bool>(), passed.ToJsonString()));
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGuestsMetStampFallsAfterTheHostsClickStart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (JsonObject joined, JsonObject connected) = await LaunchPairAsync(cancellation);
        BatchStep mirrored = new(
            Assert: "wait",
            Node: "Mirror",
            Property: "text",
            EqualsValue: JsonSerializer.SerializeToElement("presses: 1"),
            Session: "guest"
        );

        JsonObject passed = await GroupAsync(mirrored, HostClick("Send"), cancellation);
        await _harness.Sessions.StopAsync("host", cancellation);
        await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        Assert.True(passed["passed"]!.GetValue<bool>(), passed.ToJsonString());
        JsonArray members = passed["steps"]![0]!["members"]!.AsArray();
        JsonObject result = members[0]!["result"]!.AsObject();
        double clickStart = members[1]!["startMs"]!.GetValue<double>();
        double metAt = result["metAt"]!.GetValue<double>();
        Assert.True(result["clockErrorMs"]!.GetValue<double>() >= 0, passed.ToJsonString());
        Assert.True(clickStart < metAt, $"the guest met at {metAt} ms, before the host's click started at {clickStart}: {passed.ToJsonString()}");
        Assert.True(metAt < clickStart + 500, $"the guest met {metAt - clickStart} ms after the host's click started: {passed.ToJsonString()}");
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGuestsUiChangedNowWaitSeesTheHostsClickAndAPlainOneHasNoBaseline()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (JsonObject joined, JsonObject connected) = await LaunchPairAsync(cancellation);
        BatchStep changed = new(Assert: "wait", UiChanged: JsonSerializer.SerializeToElement("now"), Session: "guest");
        BatchStep plain = new(Assert: "wait", UiChanged: JsonSerializer.SerializeToElement(true), Session: "guest");

        JsonObject passed = await GroupAsync(changed, HostClick("Send"), cancellation);
        JsonObject refused = JsonNode.Parse(await _tools.BatchDriveAsync([plain], _server, cancellationToken: cancellation))!.AsObject();
        await _harness.Sessions.StopAsync("host", cancellation);
        await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        Assert.True(passed["passed"]!.GetValue<bool>(), passed.ToJsonString());
        Assert.All(passed["steps"]![0]!["members"]!.AsArray(), member => Assert.True(member!["ok"]!.GetValue<bool>(), passed.ToJsonString()));
        Assert.False(refused["passed"]!.GetValue<bool>(), refused.ToJsonString());
        Assert.Contains(NoUiBaseline, refused["steps"]![0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.OwnLaunchMs)]
    public async Task AGuestsErrorShowsElsewhereOnAHostStepAndFailsNoErrorsOverAllSessions()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        int port = FreePort();
        await _harness.Sessions.LaunchAsync(HostRequest(port, faults: true), "host", cancellation);
        await _harness.Sessions.LaunchAsync(GuestRequest(port), "guest", cancellation);
        JsonObject joined = await JoinedAsync(cancellation);
        JsonObject connected = await HostConnectedAsync(cancellation);
        BatchStep acked = new(
            Assert: "wait",
            Node: "NetProbe",
            Property: "acked_count",
            EqualsValue: JsonSerializer.SerializeToElement(1),
            Session: "host"
        );
        // The guest's check, before the click, names the guest in the batch, so its feed is marked and "all" covers it, while
        // every step from the click to the final check is a host step.
        BatchStep guestJoined = new(
            Assert: "property",
            Node: "NetProbe",
            Property: "connected",
            EqualsValue: JsonSerializer.SerializeToElement(true),
            Session: "guest"
        );
        BatchStep settled = new(Assert: "wait", Frames: 2, Session: "host");
        BatchStep all = new(Assert: "no_errors", Sessions: JsonSerializer.SerializeToElement("all"));

        JsonObject batch = JsonNode
            .Parse(await _tools.BatchDriveAsync([guestJoined, HostClick("Send"), acked, settled, all], _server, cancellationToken: cancellation))!
            .AsObject();
        await _harness.Sessions.StopAsync("host", cancellation);
        await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined["met"]!.GetValue<bool>(), joined.ToJsonString());
        Assert.True(connected["met"]!.GetValue<bool>(), connected.ToJsonString());
        JsonArray steps = batch["steps"]!.AsArray();
        // The guest writes the error to its socket before the ack to the host's, but the server reads each socket on its own
        // loop, so on a busy machine the error can reach it after the last host step: it then shows on no host entry, and the
        // final no_errors, which reads the marks rather than the cursors, is the one place left that must name it.
        JsonObject[] carrying =
        [
            .. steps.Skip(1).Take(3).Select(entry => entry!.AsObject()).Where(entry => entry["errorsElsewhere"]?["guest"] is not null),
        ];
        Assert.True(carrying.Length <= 1, batch.ToJsonString());
        foreach (JsonObject shown in carrying)
        {
            Assert.Contains("net_probe: fault 1", shown["errorsElsewhere"]!["guest"]!.ToJsonString(), StringComparison.Ordinal);
        }

        Assert.True(steps[2]!["ok"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.False(batch["passed"]!.GetValue<bool>(), batch.ToJsonString());
        Assert.Equal(4, batch["failedAt"]!["index"]!.GetValue<int>());
        string reason = batch["failedAt"]!["reason"]!.GetValue<string>();
        Assert.StartsWith("session 'guest' raised ", reason, StringComparison.Ordinal);
        Assert.Contains("net_probe: fault 1 at ", reason, StringComparison.Ordinal);
    }

    private async Task<(JsonObject Joined, JsonObject Connected)> LaunchPairAsync(CancellationToken cancellation)
    {
        int port = FreePort();
        await _harness.Sessions.LaunchAsync(HostRequest(port, faults: false), "host", cancellation);
        await _harness.Sessions.LaunchAsync(GuestRequest(port), "guest", cancellation);
        return (await JoinedAsync(cancellation), await HostConnectedAsync(cancellation));
    }

    private static BatchStep HostClick(string element) =>
        new(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = element } }, Session: "host");

    private async Task<JsonObject> GroupAsync(BatchStep first, BatchStep second, CancellationToken cancellation) =>
        JsonNode
            .Parse(await _tools.BatchDriveAsync([new BatchStep(Parallel: [first, second])], _server, cancellationToken: cancellation))!
            .AsObject();

    /// <summary>A port of the loopback interface no process holds, closed before the host binds it.</summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The host's launch; with <paramref name="faults"/>, each Send makes the guest raise an error and acknowledge it.</summary>
    private LaunchRequest HostRequest(int port, bool faults) =>
        new(
            _probe.Directory,
            NetProbe,
            [],
            faults ? [$"--net-host={port}", "--net-fault"] : [$"--net-host={port}"],
            Quiet: true,
            ShutOutRealGamepads: false,
            Prepare: true
        );

    private LaunchRequest GuestRequest(int port) =>
        new(
            _probe.Directory,
            NetProbe,
            ["--headless", "--resolution", "640x360"],
            [$"--net-join={port}"],
            Quiet: true,
            ShutOutRealGamepads: false,
            Prepare: true
        );

    private async Task<JsonObject> JoinedAsync(CancellationToken cancellation) =>
        JsonNode
            .Parse(
                await _tools.WaitForAsync(
                    new WaitCondition(Output: "\\[net\\] joined", Stream: "stdout", After: 0),
                    JoinedWaitMs,
                    "guest",
                    cancellation
                )
            )!
            .AsObject();

    private async Task<JsonObject> HostConnectedAsync(CancellationToken cancellation)
    {
        WaitCondition condition = new(Node: "NetProbe", Property: "connected", EqualsValue: JsonSerializer.SerializeToElement(true));
        return JsonNode.Parse(await _tools.WaitForAsync(condition, JoinedWaitMs, "host", cancellation))!.AsObject();
    }

    private async Task<JsonObject> BatchAsync(CancellationToken cancellation)
    {
        BatchStep click = new(Tool: "click", Args: new JsonObject { ["target"] = new JsonObject { ["element"] = "Send" } }, Session: "host");
        BatchStep wait = new(
            Assert: "wait",
            Node: "Mirror",
            Property: "text",
            EqualsValue: JsonSerializer.SerializeToElement("presses: 1"),
            Session: "guest"
        );
        BatchStep printed = new(Assert: "wait", Output: "presses: 1", Stream: "stdout", After: 0, Session: "guest");
        return JsonNode.Parse(await _tools.BatchDriveAsync([click, wait, printed], _server, cancellationToken: cancellation))!.AsObject();
    }

    /// <summary>The sessions <c>list_sessions</c> reports, live ones only.</summary>
    private JsonArray SessionsListed() => JsonNode.Parse(new ProjectTools(_harness.Sessions).ListSessions())!["sessions"]!.AsArray();
}
