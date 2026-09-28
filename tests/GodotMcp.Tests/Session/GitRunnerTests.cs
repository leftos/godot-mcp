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
    private static readonly TimeSpan RealBound = TimeSpan.FromSeconds(120);
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
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string pidFile = _temp.Combine("pid.txt");
        string script = $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Write-Output 'started'; Start-Sleep 60";
        Task<string?> run = Task.Run(() => GitRunner.Run("pwsh", _temp.Path, NullLogger.Instance, _clock, ["-NoProfile", "-Command", script]));
        await WaitForFileAsync(pidFile, cancellation);

        // The fake clock, not real time, carries the stand-in to its 30 s ceiling: one second short of it, it still runs.
        AdvanceSeconds(29);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        bool runningBeforeTheCeiling = !run.IsCompleted;
        AdvanceSeconds(2);
        string? output = await run.WaitAsync(RealBound, cancellation);

        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
        Assert.True(runningBeforeTheCeiling, "the stand-in was stopped before 30 s of its clock had passed");
        Assert.Null(output);
        Assert.True(HasExited(pid), $"the stand-in (pid {pid}) was not stopped");
    }

    /// <summary>Advances the fake clock a second at a time, so each deadline timer the run armed fires on its own tick.</summary>
    private void AdvanceSeconds(int seconds)
    {
        for (int second = 0; second < seconds; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Waits for the stand-in's pid file, bounded by <see cref="RealBound"/> against a pwsh that never starts.</summary>
    private static async Task WaitForFileAsync(string path, CancellationToken cancellation)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(RealBound);
        while (!File.Exists(path))
        {
            await Task.Delay(50, bounded.Token);
        }
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
