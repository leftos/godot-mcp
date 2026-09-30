using GodotMcp.Server.CSharp;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// One quiet, prepared CsProbe run shared by a test class: the CsProbe copied and built once, launched once, and a
/// <see cref="CSharpBridge"/> whose helper copies go to a cache folder of the fixture's own, deleted with it, so the
/// user's real cache is never pruned. A derived fixture adds project settings to the copy before the launch.
/// </summary>
public class SharedCsProbeSession : IAsyncLifetime
{
    private readonly TempDirectory _cache = new();
    private readonly SessionHarness _harness = new();
    private CsProbeProject? _probe;

    public SharedCsProbeSession() => Bridge = new CSharpBridge(new HelperCache(_cache.Combine("dotnet")), Installation.FindDotnetExtension);

    internal SessionRegistry Sessions => _harness.Sessions;

    internal CSharpBridge Bridge { get; }

    /// <summary>The copy of the CsProbe project the shared game runs from.</summary>
    internal string ProbeDirectory => _probe?.Directory ?? throw new InvalidOperationException("The shared CsProbe session is not started.");

    /// <summary>Sections appended to the copy's project.godot before the launch; none for the plain CsProbe.</summary>
    protected virtual string AddedSettings => string.Empty;

    /// <summary>The copy the game runs from: the fixture, built; a derived fixture may change its sources instead.</summary>
    protected virtual CsProbeProject CreateProbe() => new();

    public async ValueTask InitializeAsync()
    {
        _probe = CreateProbe();
        if (AddedSettings.Length > 0)
        {
            await File.AppendAllTextAsync(Path.Combine(_probe.Directory, "project.godot"), AddedSettings);
        }
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
        GC.SuppressFinalize(this);
    }
}

/// <summary>The shared CsProbe run with CsTools.cs as the Tools autoload, whose marked methods list_game_tools lists on it.</summary>
public sealed class SharedCsToolsSession : SharedCsProbeSession
{
    protected override string AddedSettings => "\n[autoload]\n\nTools=\"*res://CsTools.cs\"\n";
}

/// <summary>
/// A CsProbe run that marks no game tool: every source undefines DEBUG, which the fixture's GodotMcpToolAttribute is
/// conditional on, so the build the launch's prep makes carries no mark.
/// </summary>
public sealed class SharedCsUnmarkedSession : SharedCsProbeSession
{
    protected override CsProbeProject CreateProbe()
    {
        var probe = CsProbeProject.Unbuilt();
        foreach (string source in Directory.EnumerateFiles(probe.Directory, "*.cs"))
        {
            File.WriteAllText(source, "#undef DEBUG\n" + File.ReadAllText(source));
        }

        return probe;
    }
}
