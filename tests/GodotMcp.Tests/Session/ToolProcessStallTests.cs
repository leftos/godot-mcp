using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>ToolProcess's stall guard: a silent child is killed, a chatty one is not, and an unmeasurable tree is not killed as stalled.</summary>
public sealed class ToolProcessStallTests : IDisposable
{
    private readonly PwshRunner _pwsh = new();

    public void Dispose() => _pwsh.Dispose();

    [Fact]
    public async Task AStallKillsASilentChild()
    {
        // The stall must come first however slowly pwsh starts on a loaded machine: a ceiling and a sleep far past any start.
        ToolProcessResult result = await _pwsh.RunPwshAsync("Start-Sleep 600", TimeSpan.FromSeconds(120), stallLimit: TimeSpan.FromSeconds(3));

        Assert.Equal(KillReason.Stall, result.Killed);
        Assert.Equal("stalled: no output and no CPU for 3 s", result.KillDetail);
    }

    [Fact]
    public async Task AnUnmeasurableTreeIsNotKilledAsStalled()
    {
        // Silent and idle for 5 s, past a 2 s stall limit: with the tree's CPU unmeasured, the stall guard stays off.
        ToolProcessResult result = await ToolProcess.RunAsync(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", "Start-Sleep 5"], _pwsh.Path, _pwsh.LogPath, ProcessProbe.Ceiling)
            {
                StallLimit = TimeSpan.FromSeconds(2),
                Clock = _pwsh.Clock,
                MeasureTree = false,
            },
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AChattyChildIsNotKilled()
    {
        // A line every 200 ms for 5 s, past a 2 s stall limit.
        ToolProcessResult result = await _pwsh.RunPwshAsync(
            "for ($i = 0; $i -lt 25; $i++) { Write-Output \"line $i\"; Start-Sleep -Milliseconds 200 }",
            ProcessProbe.Ceiling,
            stallLimit: TimeSpan.FromSeconds(2)
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Null(result.KillDetail);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("line 24", File.ReadAllText(_pwsh.LogPath), StringComparison.Ordinal);
    }
}
