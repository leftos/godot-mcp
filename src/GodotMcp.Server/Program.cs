using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol: every log line goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<BridgeListener>();
builder.Services.AddSingleton<GodotSession>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();

using IHost host = builder.Build();
GodotSession session = host.Services.GetRequiredService<GodotSession>();
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(session.Shutdown);
AppDomain.CurrentDomain.ProcessExit += (_, _) => session.Shutdown();

await host.RunAsync();
