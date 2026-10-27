using System.Text.Json.Nodes;
using GodotMcp.IntegrationTests.Fixtures;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests;

/// <summary>
/// The set-up the run_scratches classes over the InputProbe fixture's scratch scenes share: a probe copy with the fixture's
/// scratch folder copied in (the fixtures' copies take top-level files only), a session harness, the scratch tools on it, and
/// a run that parses the tool's result.
/// </summary>
public abstract class ScratchRunBase : IAsyncDisposable
{
    private protected readonly ProbeProject _probe = new();
    private protected readonly SessionHarness _harness = new();
    private protected readonly ScratchTools _scratch;

    private protected ScratchRunBase()
    {
        _scratch = new ScratchTools(_harness.Sessions, TestCSharp.Unused());
        ScratchRunnerTests.CopyFolder(Path.Combine(RepoPaths.InputProbe, "scratch"), Path.Combine(_probe.Directory, "scratch"));
    }

    public virtual async ValueTask DisposeAsync()
    {
        // A game holds its project's folder until it exits, and the harness kills and waits for every game still running, so
        // the copies are deleted only once it is done.
        try
        {
            await _harness.DisposeAsync();
        }
        finally
        {
            _probe.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private protected async Task<JsonObject> RunAsync(string projectDir, string[]? scenes, ScratchOptions? options, CancellationToken cancellation) =>
        JsonNode.Parse(await _scratch.RunScratchesAsync(projectDir, scenes, options, cancellation))!.AsObject();
}
