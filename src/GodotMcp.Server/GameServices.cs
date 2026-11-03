using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging;

namespace GodotMcp.Server;

/// <summary>
/// The three singletons a run of the tools needs, built in one place for the MCP host and the exe's commands alike: the bridge
/// listener, the session registry over it, and the C# tools' bridge. The listener is this type's own: nothing else holds it, so
/// disposing this closes it. The registry is the host's to dispose through its own registration; a command disposes both.
/// </summary>
internal sealed record GameServices(BridgeListener Listener, SessionRegistry Sessions, CSharpBridge CSharp) : IDisposable
{
    /// <summary>Closes the listener, which no other registration owns, so the host's container lets go of it as it did before.</summary>
    public void Dispose() => Listener.Dispose();

    /// <summary>The three, built as the MCP server builds them, with every log line going to <paramref name="logging"/>.</summary>
    /// <param name="logging">The host's logger factory, or a command's own, writing to stderr.</param>
    public static GameServices Create(ILoggerFactory logging)
    {
        BridgeListener listener = new(logging.CreateLogger<BridgeListener>());
        return new(
            listener,
            new SessionRegistry(listener, logging.CreateLogger<GodotSession>()),
            new CSharpBridge(new HelperCache(HelperCache.DefaultRoot), Installation.FindDotnetExtension)
        );
    }
}
