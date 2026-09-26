using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>A <see cref="GodotSession"/> on its own listener; disposing it kills any live run and removes its override.</summary>
internal sealed class SessionHarness : IDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public SessionHarness() => Session = new GodotSession(_listener, NullLogger<GodotSession>.Instance);

    public GodotSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
        _listener.Dispose();
    }
}
