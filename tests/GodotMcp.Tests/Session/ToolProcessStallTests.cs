using System.Text;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>ToolProcess's stall guard: a silent child is killed, a chatty one is not, and an unmeasurable tree is not killed as stalled.</summary>
public sealed class ToolProcessStallTests : IDisposable
{
    private readonly PwshRunner _pwsh = new();
    private readonly FakeTimeProvider _time = new();

    private readonly LoadClock _clock;

    public ToolProcessStallTests() => _clock = new LoadClock(_time, new NoLoadSource());

    public void Dispose()
    {
        _clock.Dispose();
        _pwsh.Dispose();
    }

    [Fact]
    public async Task AStallKillsASilentChild()
    {
        // The stall must come first however slowly pwsh starts on a loaded machine: a ceiling and a sleep far past any start.
        ToolProcessResult result = await _pwsh.RunPwshAsync("Start-Sleep 600", TimeSpan.FromSeconds(120), stallLimit: TimeSpan.FromSeconds(3));

        Assert.Equal(KillReason.Stall, result.Killed);
        Assert.Equal("stalled: no output and no CPU for 3 s", result.KillDetail);
    }

    /// <summary>A hang guard a passing run never reaches: a pwsh that never starts or never answers fails here as hung.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task AChattyChildIsNotKilled()
    {
        // The guard's clock moves a second only once the guard can see the child's next line, five seconds past a 2 s stall
        // limit: the limit passes while the output never stands still for it, however long the child takes to start or answer.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        string echo = "while ($null -ne ($line = [Console]::In.ReadLine())) { Write-Output \"echo $line\" }";
        ToolProcessRequest request = new("pwsh", ["-NoProfile", "-Command", echo], _pwsh.Path, _pwsh.LogPath, ProcessProbe.Ceiling)
        {
            StallLimit = TimeSpan.FromSeconds(2),
            Clock = _clock,
            KeepStandardInput = true,
        };
        using ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance);
        Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
        for (int line = 1; line <= 5; line++)
        {
            await SendLineAsync(run, waited, line, cancellation);
            await WaitForLinesAsync(run, waited, line, cancellation);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        run.StandardInput.Close();
        ToolProcessResult result = await waited.WaitAsync(HangGuard, cancellation);
        run.CloseLog();

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Null(result.KillDetail);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("echo 5", File.ReadAllText(_pwsh.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnmeasurableTreeIsNotKilledAsStalled()
    {
        // Silent and idle on its stdin for 5 s of the guard's clock, past a 2 s stall limit: with the tree's CPU unmeasured, the
        // stall guard stays off, and only the closed stdin ends the child.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ToolProcessRequest request = new(
            "pwsh",
            ["-NoProfile", "-Command", "Write-Output 'ready'; $null = [Console]::In.ReadToEnd()"],
            _pwsh.Path,
            _pwsh.LogPath,
            ProcessProbe.Ceiling
        )
        {
            StallLimit = TimeSpan.FromSeconds(2),
            Clock = _clock,
            MeasureTree = false,
            KeepStandardInput = true,
        };
        using ToolProcess.Running run = ToolProcess.Start(request, NullLogger.Instance);
        Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
        await WaitForLinesAsync(run, waited, 1, cancellation);
        for (int second = 0; second < 5; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await SendLineAsync(run, waited, 1, cancellation);
        run.StandardInput.Close();
        ToolProcessResult result = await waited.WaitAsync(HangGuard, cancellation);

        Assert.Equal(KillReason.None, result.Killed);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>Writes the child its next line; a pipe the guard's kill closed fails with the kill's reason.</summary>
    private static async Task SendLineAsync(ToolProcess.Running run, Task<ToolProcessResult> waited, int line, CancellationToken cancellation)
    {
        try
        {
            await run.StandardInput.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"), cancellation);
            await run.StandardInput.FlushAsync(cancellation);
        }
        catch (IOException)
        {
            ToolProcessResult ended = await waited.WaitAsync(HangGuard, cancellation);
            Assert.Fail($"the child ended before its line {line}: {ended.Killed} ({ended.KillDetail})");
        }
    }

    /// <summary>
    /// Waits until the guard's line count reaches <paramref name="lines"/>, failing at once when the run ends first (a kill)
    /// and as hung at <see cref="HangGuard"/>.
    /// </summary>
    private static async Task WaitForLinesAsync(ToolProcess.Running run, Task<ToolProcessResult> waited, long lines, CancellationToken cancellation)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(HangGuard);
        while (run.OutputLines < lines)
        {
            if (waited.IsCompleted)
            {
                ToolProcessResult ended = await waited;
                Assert.Fail($"the child ended before its line {lines}: {ended.Killed} ({ended.KillDetail})");
            }

            await Task.Delay(20, bounded.Token);
        }
    }
}
