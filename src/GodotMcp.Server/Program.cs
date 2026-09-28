using GodotMcp.Server;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol: every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<BridgeListener>();
builder.Services.AddSingleton<SessionRegistry>();

// The C# tools' half of the bridge: the copies of the helper the game loads, and where a published server finds its build.
builder.Services.AddSingleton(new CSharpBridge(new HelperCache(HelperCache.DefaultRoot), Installation.FindDotnetExtension));
builder
    .Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "godot-mcp", Version = ServerVersion.Value })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: ToolJson.Options)
    .WithRequestFilters(filters => filters.AddCallToolFilter(ArgumentErrors.Filter));

using IHost host = builder.Build();
Log.Ambient = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GodotMcp.Server.Load");
SessionRegistry sessions = host.Services.GetRequiredService<SessionRegistry>();
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(sessions.Shutdown);
AppDomain.CurrentDomain.ProcessExit += (_, _) => sessions.Shutdown();

await host.RunAsync();
