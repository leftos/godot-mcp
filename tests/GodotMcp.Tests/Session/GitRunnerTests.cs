using System.Diagnostics;
using System.Globalization;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>GitRunner's ceiling, on a pwsh stand-in for a git that hangs and a fake clock that runs it out.</summary>
public sealed class GitRunnerTests : IDisposable
{
    private static readonly TimeSpan RealBound = TimeSpan.FromSeconds(30);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;

    public GitRunnerTests() => _clock = new LoadClock(_time, new FakeLoadSource(_time, processors: 4));

    public void Dispose()
    {
        _clock.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AHungGitIsKilledAtItsCeiling()
    {
        string pidFile = _temp.Combine("pid.txt");
        string script = $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Write-Output 'started'; Start-Sleep 60";
        Task<string?> run = Task.Run(() => GitRunner.Run("pwsh", _temp.Path, NullLogger.Instance, _clock, ["-NoProfile", "-Command", script]));

        // Once the stand-in runs, the fake clock moves a second at a time until the run gives up: 30 of them, its ceiling.
        var real = Stopwatch.StartNew();
        while (!run.IsCompleted && real.Elapsed < RealBound)
        {
            if (File.Exists(pidFile))
            {
                _time.Advance(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(run.IsCompleted, $"the stand-in still ran after {RealBound.TotalSeconds} s of real time");
        Assert.Null(await run);
        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
        Assert.True(HasExited(pid), $"the stand-in (pid {pid}) was not stopped");
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
