using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// A <see cref="SessionRegistry"/> on its own listener. Disposing it detaches every attached session and stops every live
/// run the way stop_project does (quit, then kill after the grace), which waits for Godot to exit, so the probe's folder
/// can be deleted after it.
/// </summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);

    public SessionHarness() => Sessions = new SessionRegistry(_listener, NullLogger<GodotSession>.Instance);

    public SessionRegistry Sessions { get; }

    public async ValueTask DisposeAsync()
    {
        // A bare kill does not wait: Godot_console.exe exits before the Godot.exe it wraps lets go of the folder.
        foreach (SessionInfo session in Sessions.List(includeStopped: true))
        {
            if (session.Kind == "attach")
            {
                await Sessions.DetachAsync(session.Name, CancellationToken.None);
            }
            else if (session.Live)
            {
                await Sessions.StopAsync(session.Name, CancellationToken.None);
            }
        }

        Sessions.Dispose();
        _listener.Dispose();
    }
}
