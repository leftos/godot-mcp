using GodotMcp.Server;
using GodotMcp.Server.Agents;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// --list-tools, --sweep-agents and --run-scratches run and exit; with no command the exe is the MCP server.
CommandEnvironment commandEnvironment = new(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    Environment.GetEnvironmentVariable(AgentSweep.RootsVariable)
);
if (ServerCommands.Run(args, Console.Out, Console.Error, commandEnvironment) is { } commandStatus)
{
    Environment.ExitCode = commandStatus;
    return;
}

DriveClock.Start();
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol: every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// The listener, the registry over it and the C# tools' half of the bridge, built where --run-scratches builds them too, so the
// command and the host cannot drift. GameServices is built first and disposed last, so the registry goes before the listener
// it listens on.
builder.Services.AddSingleton(services => GameServices.Create(services.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(services => services.GetRequiredService<GameServices>().Sessions);
builder.Services.AddSingleton(services => services.GetRequiredService<GameServices>().CSharp);
builder.Services.AddSingleton(services => new FrontBusy(
    services.GetRequiredService<SessionRegistry>(),
    Environment.GetEnvironmentVariable("MCP_FRONT"),
    services.GetRequiredService<ILogger<FrontBusy>>()
));
builder
    .Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "godot-mcp", Version = ServerVersion.Value })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: ToolJson.Options)
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(ArgumentErrors.Filter);
        filters.AddCallToolFilter(FrontBusy.Filter);
    });

using IHost host = builder.Build();
Log.Ambient = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GodotMcp.Server.Load");
SessionRegistry sessions = host.Services.GetRequiredService<SessionRegistry>();

// A server killed before its shutdown left its override.cfg files behind; remove the ones no live server owns.
sessions.OverrideFolders.Sweep();

// The host's stop asks the games to quit; process exit is the backup for an exit without one, and finds them gone otherwise.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(sessions.Shutdown);
AppDomain.CurrentDomain.ProcessExit += (_, _) => sessions.Shutdown();

await host.RunAsync();
