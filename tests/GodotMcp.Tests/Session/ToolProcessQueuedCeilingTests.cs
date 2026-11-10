using System.Text;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A wrapped step's ceiling on a hand-advanced clock: it does not run while the wrapper waits for a slot, a fresh one starts
/// at the took line, the wall backstop bounds the wait, and a wrapper that says nothing keeps the ceiling from its start. The
/// child echoes each line the test writes it to stderr, as a gate prints its wait lines, and ends when its stdin closes. A
/// kill is reached by advancing the clock in small steps until the run ends, whichever of the deadline's timer and the check
/// loop's wait fires first on a tick.
/// </summary>
public sealed class ToolProcessQueuedCeilingTests : IDisposable
{
    private const string Waiting = "gate: waiting for a heavy slot (1 busy)";
    private const string StillWaiting = "gate: still waiting for a heavy slot (480 s; 1 busy)";
    private const string Took = "gate: took a heavy slot after 200.0 s";
    private const string Echo = "while ($null -ne ($line = [Console]::In.ReadLine())) { [Console]::Error.WriteLine($line) }";

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(300);

    private readonly PwshRunner _pwsh = new();
    private readonly FakeTimeProvider _time = new();
    private readonly LoadClock _clock;
    private readonly Lock _seenLock = new();
    private readonly List<string> _seen = [];

    public ToolProcessQueuedCeilingTests() => _clock = new LoadClock(_time, new NoLoadSource());

    public void Dispose()
    {
        _clock.Dispose();
        _pwsh.Dispose();
    }

    [Fact]
    public async Task AQueuedWrapperIsNotKilledAtItsCeiling()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (ToolProcess.Running run, WaitWatch _) = Start();
        using (run)
        {
            Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
            await SayAsync(run, waited, Waiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(400));

            // The child answers only while it runs, and ends only when its stdin closes: a kill at the ceiling fails here.
            await SayAsync(run, waited, StillWaiting, cancellation);
            run.StandardInput.Close();
            ToolProcessResult result = await waited.WaitAsync(RegistryHarness.HangGuard, cancellation);

            Assert.Equal(KillReason.None, result.Killed);
            Assert.Equal(0, result.ExitCode);
            Assert.Null(result.LastWait);
        }
    }

    [Fact]
    public async Task TheCeilingRunsFromTheTookLine()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (ToolProcess.Running run, WaitWatch _) = Start();
        using (run)
        {
            Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
            await SayAsync(run, waited, Waiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(400));
            await SayAsync(run, waited, Took, cancellation);
            _time.Advance(TimeSpan.FromSeconds(299));

            // One more line through the child: it answers only while it runs, so a kill before 300 s fails here.
            await SayAsync(run, waited, "Build started", cancellation);
            await RegistryHarness.AdvanceUntilDoneAsync(_time, waited, cancellation);
            ToolProcessResult result = await waited;

            Assert.Equal(KillReason.Ceiling, result.Killed);
            Assert.StartsWith("within 300 s of load-adjusted time (wall 300", result.KillDetail, StringComparison.Ordinal);
            Assert.Null(result.LastWait);
        }
    }

    [Fact]
    public async Task AQueuedWrapperIsKilledAtTheBackstop()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (ToolProcess.Running run, WaitWatch _) = Start();
        using (run)
        {
            Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
            await SayAsync(run, waited, Waiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(1000));
            await SayAsync(run, waited, "2026-01-01T10:16:40+00:00 gat " + StillWaiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(499));
            await SayAsync(run, waited, "still alive", cancellation);
            await RegistryHarness.AdvanceUntilDoneAsync(_time, waited, cancellation);
            ToolProcessResult result = await waited;

            Assert.Equal(KillReason.Backstop, result.Killed);
            Assert.StartsWith("ran 1500", result.KillDetail, StringComparison.Ordinal);
            Assert.Contains(" s of wall time, 5 x its 300 s ceiling (load-adjusted ", result.KillDetail, StringComparison.Ordinal);
            Assert.Equal(StillWaiting, result.LastWait);
        }
    }

    [Fact]
    public async Task AWrapperThatPrintsNoWaitLineKeepsTheCeilingFromItsStart()
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (ToolProcess.Running run, WaitWatch watch) = Start();
        using (run)
        {
            Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
            await SayAsync(run, waited, "Build started", cancellation);
            _time.Advance(TimeSpan.FromSeconds(299));
            await SayAsync(run, waited, "Build going", cancellation);
            await RegistryHarness.AdvanceUntilDoneAsync(_time, waited, cancellation);
            ToolProcessResult result = await waited;

            Assert.Equal(KillReason.Ceiling, result.Killed);
            Assert.StartsWith("within 300 s of load-adjusted time (wall 300", result.KillDetail, StringComparison.Ordinal);
            Assert.Equal(WaitState.None, watch.State);
            Assert.Null(result.LastWait);
        }
    }

    [Fact]
    public async Task AWaitingLineAfterTheTookLineDoesNotPauseTheCeiling()
    {
        // Took at 200 s: a ceiling from the start would kill at 300 s, before the line at 499 s; one paused again by the
        // waiting line after the took line would run on to the backstop instead of the ceiling.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        (ToolProcess.Running run, WaitWatch watch) = Start();
        using (run)
        {
            Task<ToolProcessResult> waited = ToolProcess.WaitAsync(run, cancellation);
            await SayAsync(run, waited, Waiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(200));
            await SayAsync(run, waited, Took, cancellation);
            await SayAsync(run, waited, StillWaiting, cancellation);
            _time.Advance(TimeSpan.FromSeconds(299));
            await SayAsync(run, waited, "Build going", cancellation);
            await RegistryHarness.AdvanceUntilDoneAsync(_time, waited, cancellation);
            ToolProcessResult result = await waited;

            Assert.Equal(KillReason.Ceiling, result.Killed);
            Assert.StartsWith("within 300 s of load-adjusted time (wall 300", result.KillDetail, StringComparison.Ordinal);
            Assert.Equal(WaitState.Took, watch.State);
            Assert.Null(result.LastWait);
        }
    }

    /// <summary>
    /// Starts the echoing child as a prep wrapper's request is run: watched for its wait lines, with no stall kill, and each
    /// stderr line recorded once the watch has read it.
    /// </summary>
    private (ToolProcess.Running Run, WaitWatch Watch) Start()
    {
        ToolProcessRequest watched = PrepWrapper.Watch(
            new ToolProcessRequest("pwsh", ["-NoProfile", "-Command", Echo], _pwsh.Path, _pwsh.LogPath, Ceiling)
            {
                StallLimit = TimeSpan.MaxValue,
                Clock = _clock,
                KeepStandardInput = true,
            }
        );
        Action<string> watch = watched.StandardErrorLine!;
        ToolProcessRequest request = watched with
        {
            StandardErrorLine = line =>
            {
                watch(line);
                lock (_seenLock)
                {
                    _seen.Add(line);
                }
            },
        };
        return (ToolProcess.Start(request, NullLogger.Instance), request.WaitWatch!);
    }

    /// <summary>
    /// Has the child print <paramref name="line"/> and waits until the watch has read it, failing at once when the run ends
    /// first (a kill) and as hung at <see cref="RegistryHarness.HangGuard"/>.
    /// </summary>
    private async Task SayAsync(ToolProcess.Running run, Task<ToolProcessResult> waited, string line, CancellationToken cancellation)
    {
        int before = SeenCount();
        try
        {
            await run.StandardInput.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellation);
            await run.StandardInput.FlushAsync(cancellation);
        }
        catch (IOException)
        {
            ToolProcessResult ended = await waited.WaitAsync(RegistryHarness.HangGuard, cancellation);
            Assert.Fail($"the child ended before the line \"{line}\": {ended.Killed} ({ended.KillDetail})");
        }

        DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
        while (!Seen(line, before))
        {
            if (waited.IsCompleted)
            {
                ToolProcessResult ended = await waited;
                Assert.Fail($"the child ended before the line \"{line}\": {ended.Killed} ({ended.KillDetail})");
            }

            Assert.True(DateTime.UtcNow < guard, $"hung: the line \"{line}\" did not come back within the {RegistryHarness.HangGuard} hang guard");
            await Task.Delay(20, cancellation);
        }
    }

    private int SeenCount()
    {
        lock (_seenLock)
        {
            return _seen.Count;
        }
    }

    private bool Seen(string line, int from)
    {
        lock (_seenLock)
        {
            return _seen.Skip(from).Contains(line);
        }
    }
}
