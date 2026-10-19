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

        await _harness.Sessions.LaunchAsync(HostRequest(port), "host", cancellation);
        LaunchResult guest = await _harness.Sessions.LaunchAsync(GuestRequest(port), "guest", cancellation);
        bool joined = await JoinedAsync(cancellation);
        JsonObject connected = await HostConnectedAsync(cancellation);
        JsonObject batch = await BatchAsync(cancellation);
        JsonArray listed = SessionsListed();
        StopResult hostStopped = await _harness.Sessions.StopAsync("host", cancellation);
        StopResult guestStopped = await _harness.Sessions.StopAsync("guest", cancellation);

        Assert.True(joined, "the guest printed no [net] joined line");
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

    /// <summary>A port of the loopback interface no process holds, closed before the host binds it.</summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private LaunchRequest HostRequest(int port) =>
        new(_probe.Directory, NetProbe, [], [$"--net-host={port}"], Quiet: true, ShutOutRealGamepads: false, Prepare: true);

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

    private Task<bool> JoinedAsync(CancellationToken cancellation) =>
        Poll.UntilAsync(
            () => _harness.Sessions.GetDebugOutput("guest", GodotRun.OutputCapacity, null).Stdout.Contains("[net] joined"),
            TimeSpan.FromMilliseconds(JoinedWaitMs),
            cancellation
        );

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
        return JsonNode.Parse(await _tools.BatchDriveAsync([click, wait], _server, cancellationToken: cancellation))!.AsObject();
    }

    /// <summary>The sessions <c>list_sessions</c> reports, live ones only.</summary>
    private JsonArray SessionsListed() => JsonNode.Parse(new ProjectTools(_harness.Sessions).ListSessions())!["sessions"]!.AsArray();
}
