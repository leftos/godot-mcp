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

    private string LogPath => _temp.Combine("logs", "tool.log");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AChildReadingStdinSeesItsEnd()
    {
        ToolProcessResult result = await RunPwshAsync(
            "Write-Output ('redirected:' + [Console]::IsInputRedirected); $text = [Console]::In.ReadToEnd(); Write-Output ('stdin:' + $text.Length)",
            Ceiling
        );

        Assert.False(result.KilledByCeiling);
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

        Assert.False(result.KilledByCeiling);
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

        Assert.False(result.KilledByCeiling);
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

        Assert.True(result.KilledByCeiling);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(60), $"took {result.Elapsed}");
        Match grandchild = GrandchildLine().Match(File.ReadAllText(LogPath));
        Assert.True(grandchild.Success, "the child never reported its grandchild's id");
        Assert.True(HasExited(int.Parse(grandchild.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
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

    private Task<ToolProcessResult> RunPwshAsync(string script, TimeSpan ceiling) =>
        ToolProcess.RunAsync(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", script], _temp.Path, LogPath, ceiling),
            NullLogger.Instance,
            CancellationToken.None
        );

    [GeneratedRegex(@"grandchild:(\d+)")]
    private static partial Regex GrandchildLine();
}
