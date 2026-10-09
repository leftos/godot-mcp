using GodotMcp.Server;
using GodotMcp.Server.Agents;
using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// --list-tools and --sweep-agents print to stdout and exit; with no command the exe is the MCP server.
CommandEnvironment commandEnvironment = new(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    Environment.GetEnvironmentVariable(AgentSweep.RootsVariable)
);
if (ServerCommands.Run(args, Console.Out, Console.Error, commandEnvironment) is { } commandStatus)
{
    Environment.ExitCode = commandStatus;
    return;
}

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

// A server killed before its shutdown left its override.cfg files behind; remove the ones no live server owns.
sessions.OverrideFolders.Sweep();

// The host's stop asks the games to quit; process exit is the backup for an exit without one, and finds them gone otherwise.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(sessions.Shutdown);
AppDomain.CurrentDomain.ProcessExit += (_, _) => sessions.Shutdown();

await host.RunAsync();
