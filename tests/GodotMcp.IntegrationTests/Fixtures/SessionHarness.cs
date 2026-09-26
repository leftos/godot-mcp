using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="GodotSession"/> on its own listener. Disposing it stops a live run the way stop_project does (quit, then
/// kill after the grace), which waits for Godot to exit, so the probe's folder can be deleted after it.
/// </summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public SessionHarness() => Session = new GodotSession(_listener, NullLogger<GodotSession>.Instance);

    public GodotSession Session { get; }

    public async ValueTask DisposeAsync()
    {
        // A bare kill does not wait: Godot_console.exe exits before the Godot.exe it wraps lets go of the folder.
        if (Session.IsAttached)
        {
            await Session.DetachAsync(CancellationToken.None);
        }
        else if (Session.GetDebugOutput(1).Running)
        {
            await Session.StopAsync(CancellationToken.None);
        }

        Session.Dispose();
        _listener.Dispose();
    }
}
