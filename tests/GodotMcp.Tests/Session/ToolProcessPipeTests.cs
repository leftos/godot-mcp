using System.Text;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace GodotMcp.Tests.Session;

/// <summary>
/// ToolProcess's opt-in stdio modes on pwsh children: a stdout kept for the caller instead of the log, a stdin kept open for
/// the caller to write and close, and the public wait that still enforces the ceiling on such a process.
/// </summary>
public sealed class ToolProcessPipeTests : IDisposable
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private readonly PwshRunner _pwsh = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _pwsh.Dispose();

    [Fact]
    public async Task StdoutCanBeKeptForTheCaller()
    {
        ToolProcessRequest request = Request("Write-Output 'to-caller'; [Console]::Error.WriteLine('to-log')", _pwsh.LogPath) with
        {
            PipeStandardOutput = true,
        };
        string output;
        ToolProcessResult result;
        using (ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance))
        {
            using StreamReader reader = new(run.StandardOutput, Encoding.UTF8);
            output = await reader.ReadToEndAsync(Token);
            result = await ToolProcess.WaitAsync(run, Token);
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("to-caller", output.Trim());
        string log = File.ReadAllText(_pwsh.LogPath);
        Assert.Contains("to-log", log, StringComparison.Ordinal);
        Assert.DoesNotContain("to-caller", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdinCanBeKeptOpenForTheCaller()
    {
        ToolProcessRequest request = Request("$text = [Console]::In.ReadToEnd(); Write-Output ('stdin:' + $text.Trim())", _pwsh.LogPath) with
        {
            KeepStandardInput = true,
        };
        ToolProcessResult result;
        using (ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance))
        {
            await run.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("from-caller\n"), Token);
            run.StandardInput.Close();
            result = await ToolProcess.WaitAsync(run, Token);
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdin:from-caller", File.ReadAllText(_pwsh.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeptStdinOutlivesTheRequestOnlyUntilItIsClosed()
    {
        ToolProcessRequest request = Request("$null = [Console]::In.ReadToEnd(); Write-Output 'ended'", _pwsh.LogPath) with
        {
            KeepStandardInput = true,
        };
        using ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance);
        Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, Token);

        Task early = await Task.WhenAny(waited, Task.Delay(TimeSpan.FromSeconds(3), Token));
        bool exitedWhileOpen = early == waited;
        run.StandardInput.Close();
        Task late = await Task.WhenAny(waited, Task.Delay(ExitWait, Token));

        Assert.False(exitedWhileOpen, "the child exited while its stdin was still open");
        Assert.True(late == waited, $"the child did not exit within {ExitWait.TotalSeconds} s of its stdin closing");
        Assert.Equal(0, (await waited).ExitCode);
    }

    [Fact]
    public async Task ACeilingStillKillsAProcessThePublicWaitWatches()
    {
        ToolProcessRequest request = Request("$null = [Console]::In.ReadToEnd()", _pwsh.LogPath) with
        {
            KeepStandardInput = true,
            PipeStandardOutput = true,
            Ceiling = TimeSpan.FromSeconds(2),
        };
        using ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance);

        ToolProcessResult result = await ToolProcess.WaitAsync(run, Token);

        Assert.Equal(KillReason.Ceiling, result.Killed);
        Assert.True(run.Process.HasExited, "the killed child is still running");
    }

    [Fact]
    public async Task ALogIsNotOpenedTwice()
    {
        string otherLog = Path.Combine(_pwsh.Path, "logs", "other.log");
        ToolProcessRequest holding = Request("$null = [Console]::In.ReadToEnd()", _pwsh.LogPath) with { KeepStandardInput = true };
        ToolProcessResult beside;
        using (ToolProcess.Running first = ToolProcess.Start(holding, NullLogger.Instance))
        {
            try
            {
                Assert.Throws<IOException>(() => ToolProcess.Start(Request("Write-Output 'second'", _pwsh.LogPath), NullLogger.Instance));
                using ToolProcess.Running second = ToolProcess.Start(Request("Write-Output 'beside'", otherLog), NullLogger.Instance);
                beside = await ToolProcess.WaitAsync(second, Token);
            }
            finally
            {
                first.StandardInput.Close();
                await ToolProcess.WaitAsync(first, Token);
            }
        }

        Assert.Equal(0, beside.ExitCode);
        Assert.Contains("beside", File.ReadAllText(otherLog), StringComparison.Ordinal);
    }

    private ToolProcessRequest Request(string script, string logPath) =>
        new("pwsh", ["-NoProfile", "-Command", script], _pwsh.Path, logPath, ProcessProbe.Ceiling) { Clock = _pwsh.Clock };
}
