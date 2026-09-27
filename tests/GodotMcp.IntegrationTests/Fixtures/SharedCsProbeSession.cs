using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// One quiet, prepared CsProbe run shared by a test class: the CsProbe copied and built once, launched once, and a
/// <see cref="CSharpBridge"/> whose helper copies go to a cache folder of the fixture's own, deleted with it, so the
/// user's real cache is never pruned.
/// </summary>
public sealed class SharedCsProbeSession : IAsyncLifetime
{
    private readonly TempDirectory _cache = new();
    private readonly SessionHarness _harness = new();
    private CsProbeProject? _probe;

    public SharedCsProbeSession() => Bridge = new CSharpBridge(new HelperCache(_cache.Combine("dotnet")), Installation.FindDotnetExtension);

    internal SessionRegistry Sessions => _harness.Sessions;

    internal CSharpBridge Bridge { get; }

    /// <summary>The copy of the CsProbe project the shared game runs from.</summary>
    internal string ProbeDirectory => _probe?.Directory ?? throw new InvalidOperationException("The shared CsProbe session is not started.");

    public async ValueTask InitializeAsync()
    {
        _probe = new CsProbeProject();
        LaunchRequest request = new(_probe.Directory, null, [], [], Quiet: true, false, Prepare: true);
        await Sessions.LaunchAsync(request, null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        // The game holds the helper's copy and the probe's folder until it exits, so it is stopped before either is deleted.
        try
        {
            await _harness.DisposeAsync();
        }
        finally
        {
            _probe?.Dispose();
            _cache.Dispose();
        }
    }
}
