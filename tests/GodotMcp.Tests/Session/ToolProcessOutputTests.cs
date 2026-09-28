using System.Diagnostics;
using System.Text.RegularExpressions;
using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>ToolProcess on pwsh children: stdin closed, the exit code and both streams captured, and a grandchild holding the output open.</summary>
public sealed class ToolProcessOutputTests : IDisposable
{
    private readonly PwshRunner _pwsh = new();

    public void Dispose() => _pwsh.Dispose();

    [Fact]
    public async Task AChildReadingStdinSeesItsEnd()
    {
        ToolProcessResult result = await _pwsh.RunPwshAsync(
            "Write-Output ('redirected:' + [Console]::IsInputRedirected); $text = [Console]::In.ReadToEnd(); Write-Output ('stdin:' + $text.Length)",
            ProcessProbe.Ceiling
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
        string log = File.ReadAllText(_pwsh.LogPath);
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
        ToolProcessResult result = await _pwsh.RunPwshAsync(script, ProcessProbe.Ceiling);
        TimeSpan took = clock.Elapsed;
        string[] lines = File.ReadAllLines(_pwsh.LogPath);
        Match grandchild = ProcessProbe.GrandchildLine().Match(string.Join('\n', lines));
        if (grandchild.Success)
        {
            ProcessProbe.KillIfRunning(int.Parse(grandchild.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
        Assert.True(took >= TimeSpan.FromSeconds(4.9) && took < TimeSpan.FromSeconds(15), $"took {took}");
        Assert.StartsWith("[godot-mcp] the process exited but its output stayed open", lines.Last(line => line.Length > 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturesTheExitCodeAndBothStreams()
    {
        ToolProcessResult result = await _pwsh.RunPwshAsync(
            "Write-Output 'to-stdout'; [Console]::Error.WriteLine('to-stderr'); exit 3",
            ProcessProbe.Ceiling
        );

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(3, result.ExitCode);
        string log = File.ReadAllText(_pwsh.LogPath);
        Assert.Contains("to-stdout", log, StringComparison.Ordinal);
        Assert.Contains("to-stderr", log, StringComparison.Ordinal);
    }
}
