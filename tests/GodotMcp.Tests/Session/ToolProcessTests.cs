using System.Diagnostics;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>ToolProcess on pwsh children: stdin closed, the exit code and both streams captured, a ceiling killing the tree.</summary>
public sealed partial class ToolProcessTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);
    private readonly TempDirectory _temp = new();

    // Wall time: another build loading the machine would stretch a ceiling into its backstop; LoadClockTests cover the load.
    private readonly LoadClock _wallClock = new(TimeProvider.System, new NoLoadSource());

    private string LogPath => _temp.Combine("logs", "tool.log");

    public void Dispose()
    {
        _wallClock.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AChildReadingStdinSeesItsEnd()
    {
        ToolProcessResult result = await RunPwshAsync(
            "Write-Output ('redirected:' + [Console]::IsInputRedirected); $text = [Console]::In.ReadToEnd(); Write-Output ('stdin:' + $text.Length)",
            Ceiling
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
        string log = File.ReadAllText(LogPath);
        Assert.Contains("redirected:True", log, StringComparison.Ordinal);
        Assert.Contains("stdin:0", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGrandchildHoldingTheOutputOpenIsLeftAfterTheGrace()
    {
        const string script =
            "$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 30' -PassThru -NoNewWindow; "
            + "Write-Output ('grandchild:' + $child.Id); exit 0";
        var clock = Stopwatch.StartNew();
        ToolProcessResult result = await RunPwshAsync(script, Ceiling);
        TimeSpan took = clock.Elapsed;
        string[] lines = File.ReadAllLines(LogPath);
        Match grandchild = GrandchildLine().Match(string.Join('\n', lines));
        if (grandchild.Success)
        {
            KillIfRunning(int.Parse(grandchild.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
        Assert.True(took >= TimeSpan.FromSeconds(4.9) && took < TimeSpan.FromSeconds(15), $"took {took}");
        Assert.StartsWith("[godot-mcp] the process exited but its output stayed open", lines.Last(line => line.Length > 0), StringComparison.Ordinal);
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            // It is the temp folder's working directory until it has exited, which Kill does not wait for.
            process.WaitForExit(5000);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // It has already exited.
        }
    }

    [Fact]
    public async Task CapturesTheExitCodeAndBothStreams()
    {
        ToolProcessResult result = await RunPwshAsync("Write-Output 'to-stdout'; [Console]::Error.WriteLine('to-stderr'); exit 3", Ceiling);

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(3, result.ExitCode);
        string log = File.ReadAllText(LogPath);
        Assert.Contains("to-stdout", log, StringComparison.Ordinal);
        Assert.Contains("to-stderr", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACeilingKillsTheWholeProcessTree()
    {
        const string script =
            "$child = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru -NoNewWindow; "
            + "Write-Output ('grandchild:' + $child.Id); Start-Sleep 120";

        ToolProcessResult result = await RunPwshAsync(script, TimeSpan.FromSeconds(10));

        Assert.Equal(KillReason.Ceiling, result.Killed);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(60), $"took {result.Elapsed}");
        Match grandchild = GrandchildLine().Match(File.ReadAllText(LogPath));
        Assert.True(grandchild.Success, "the child never reported its grandchild's id");
        Assert.True(HasExited(int.Parse(grandchild.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
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
                WorkingDirectory = _temp.Path,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        ChildProcesses.Start(child);
        child.StandardInput.Close();
        using var tree = OwnWork.TreeCpu.Adopt(child, NullLogger.Instance, join: true);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(Ceiling);
        await child.WaitForExitAsync(limit.Token);

        TimeSpan grandchild = TimeSpan.FromTicks(tree.Ticks) - child.TotalProcessorTime;
        Assert.True(grandchild >= TimeSpan.FromSeconds(1.5), $"the tree used {grandchild} beyond the child's own CPU");
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task AStallKillsASilentChild()
    {
        ToolProcessResult result = await RunPwshAsync("Start-Sleep 60", Ceiling, stallLimit: TimeSpan.FromSeconds(3));

        Assert.Equal(KillReason.Stall, result.Killed);
        Assert.Equal("stalled: no output and no CPU for 3 s", result.KillDetail);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(30), $"took {result.Elapsed}");
    }

    [Fact]
    public async Task AnUnmeasurableTreeIsNotKilledAsStalled()
    {
        // Silent and idle for 5 s, past a 2 s stall limit: with the tree's CPU unmeasured, the stall guard stays off.
        ToolProcessResult result = await ToolProcess.RunAsync(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", "Start-Sleep 5"], _temp.Path, LogPath, Ceiling)
            {
                StallLimit = TimeSpan.FromSeconds(2),
                Clock = _wallClock,
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
        ToolProcessResult result = await RunPwshAsync(
            "for ($i = 0; $i -lt 25; $i++) { Write-Output \"line $i\"; Start-Sleep -Milliseconds 200 }",
            Ceiling,
            stallLimit: TimeSpan.FromSeconds(2)
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Null(result.KillDetail);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("line 24", File.ReadAllText(LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCeilingKillIsReportedAsCeiling()
    {
        ToolProcessResult result = await RunPwshAsync("Start-Sleep 60", TimeSpan.FromSeconds(3));

        Assert.Equal(KillReason.Ceiling, result.Killed);
        Assert.True(result.WasKilled);
        Assert.StartsWith("within 3 s of load-adjusted time (wall ", result.KillDetail, StringComparison.Ordinal);
        Assert.Contains(" s, machine free ", result.KillDetail, StringComparison.Ordinal);
        Assert.EndsWith("% on average)", result.KillDetail, StringComparison.Ordinal);
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

    private Task<ToolProcessResult> RunPwshAsync(string script, TimeSpan ceiling) => RunPwshAsync(script, ceiling, ToolProcess.DefaultStallLimit);

    private Task<ToolProcessResult> RunPwshAsync(string script, TimeSpan ceiling, TimeSpan stallLimit) =>
        ToolProcess.RunAsync(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", script], _temp.Path, LogPath, ceiling)
            {
                StallLimit = stallLimit,
                Clock = _wallClock,
            },
            NullLogger.Instance,
            CancellationToken.None
        );

    [GeneratedRegex(@"grandchild:(\d+)")]
    private static partial Regex GrandchildLine();
}
