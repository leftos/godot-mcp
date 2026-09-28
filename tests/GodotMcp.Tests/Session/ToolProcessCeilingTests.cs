using System.Diagnostics;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>ToolProcess's ceiling: it kills the whole tree, counts a grandchild's CPU, and is reported as a ceiling kill.</summary>
public sealed class ToolProcessCeilingTests : IDisposable
{
    private readonly PwshRunner _pwsh = new();

    public void Dispose() => _pwsh.Dispose();

    [Fact]
    public async Task ACeilingKillsTheWholeProcessTree()
    {
        const string script =
            "$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru -NoNewWindow; "
            + "Write-Output ('grandchild:' + $child.Id); Start-Sleep 120";

        ToolProcessResult result = await _pwsh.RunPwshAsync(script, TimeSpan.FromSeconds(10));

        Assert.Equal(KillReason.Ceiling, result.Killed);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(60), $"took {result.Elapsed}");
        Match grandchild = ProcessProbe.GrandchildLine().Match(File.ReadAllText(_pwsh.LogPath));
        Assert.True(grandchild.Success, "the child never reported its grandchild's id");
        Assert.True(ProcessProbe.HasExited(int.Parse(grandchild.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task TheCeilingKillIsReportedAsCeiling()
    {
        ToolProcessResult result = await _pwsh.RunPwshAsync("Start-Sleep 60", TimeSpan.FromSeconds(3));

        Assert.Equal(KillReason.Ceiling, result.Killed);
        Assert.True(result.WasKilled);
        Assert.StartsWith("within 3 s of load-adjusted time (wall ", result.KillDetail, StringComparison.Ordinal);
        Assert.Contains(" s, machine free ", result.KillDetail, StringComparison.Ordinal);
        Assert.EndsWith("% on average)", result.KillDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TreeCpuCountsAGrandchild()
    {
        // The grandchild spins until it has used 1.5 s of CPU, and the child waits for it.
        const string spin = "$p = [Diagnostics.Process]::GetCurrentProcess(); while ($p.TotalProcessorTime.TotalSeconds -lt 1.5) { $p.Refresh() }";
        string script = $"Start-Process pwsh -ArgumentList '-NoProfile','-Command','{spin}' -NoNewWindow -Wait";
        using Process child = new()
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                ArgumentList = { "-NoProfile", "-Command", script },
                WorkingDirectory = _pwsh.Path,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        ChildProcesses.Start(child);
        child.StandardInput.Close();
        using var tree = OwnWork.TreeCpu.Adopt(child, NullLogger.Instance, join: true);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(ProcessProbe.Ceiling);
        await child.WaitForExitAsync(limit.Token);

        TimeSpan grandchild = TimeSpan.FromTicks(tree.Ticks) - child.TotalProcessorTime;
        Assert.True(grandchild >= TimeSpan.FromSeconds(1.5), $"the tree used {grandchild} beyond the child's own CPU");
    }

    [Fact]
    public void ACeilingKillFollowsDidNotFinishWithoutNestedParentheses()
    {
        ToolProcessResult ceiling = new(
            -1,
            TimeSpan.FromSeconds(812),
            KillReason.Ceiling,
            "within 300 s of load-adjusted time (wall 812 s, machine free 37% on average)"
        );
        ToolProcessResult stalled = new(-1, TimeSpan.FromSeconds(3), KillReason.Stall, "stalled: no output and no CPU for 3 s");

        Assert.Equal(
            "did not finish within 300 s of load-adjusted time (wall 812 s, machine free 37% on average)",
            $"did not finish {ceiling.KillPhrase}"
        );
        Assert.Equal("did not finish (stalled: no output and no CPU for 3 s)", $"did not finish {stalled.KillPhrase}");
    }
}
