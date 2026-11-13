using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using Microsoft.Extensions.Time.Testing;
using Outcome = (string? Error, bool Closed, System.Collections.Generic.IReadOnlyList<GodotMcp.Server.Session.ErrorEntry>? Marked);

namespace GodotMcp.Tests.Session;

/// <summary>
/// The exit hold's wait for its end markers (<see cref="ScratchHoldWait"/>): a stall guard on the hold's own game clock, read
/// through the bridge's hold command once each window of load-adjusted time passes with no markers, which waits on while that
/// clock advances and fails a window after it stands still. The clock is fake and advances in half-second steps; the reads and
/// probes are scripted against it.
/// </summary>
public sealed class ScratchHoldWaitTests
{
    private const string Marker = "[godot-mcp] scratch pace end n";
    private const int GameMs = 10_000;
    private const string ClosedError = "wait_for 10000 game ms failed: The bridge closed the connection.";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan Readers = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyList<ErrorEntry> Entries = [];
    private static readonly HoldRead NotYet = new(null, false, false);
    private static readonly HoldRead MarkedRead = new(Entries, true, true);

    [Fact]
    public async Task AHoldWhoseMarkersComeInItsFirstWindowSendsNoProbe()
    {
        Rig rig = new();
        int probes = 0;

        Outcome outcome = await rig.RunAsync(
            () => rig.Elapsed >= TimeSpan.FromSeconds(5) ? MarkedRead : NotYet,
            _ =>
            {
                probes++;
                return Task.FromResult<JsonNode?>(Running(0));
            }
        );

        Assert.Same(Entries, outcome.Marked);
        Assert.Null(outcome.Error);
        Assert.False(outcome.Closed);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task AHoldPausedFromItsCutFailsAtTheFirstWindow()
    {
        Rig rig = new();

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => Task.FromResult<JsonNode?>(Running(0)));

        Assert.Equal(
            "the exit hold ran 0 of 10 game s, then no more for 25 s of load-adjusted time: the game's clock stood still (is it paused?)",
            outcome.Error
        );
        Assert.Equal(ScratchHoldWait.HoldStoodStill(0, GameMs, Window), outcome.Error);
        Assert.Null(outcome.Marked);
        Assert.False(outcome.Closed);
    }

    [Fact]
    public async Task ASlowedHoldThatAdvancesEachWindowWaitsForItsMarkers()
    {
        Rig rig = new();

        Outcome outcome = await rig.RunAsync(
            () => rig.Elapsed >= TimeSpan.FromSeconds(49) ? MarkedRead : NotYet,
            _ => Task.FromResult<JsonNode?>(Running(5.1))
        );

        Assert.Same(Entries, outcome.Marked);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task AHoldThatStopsAdvancingFailsOneWindowLater()
    {
        Rig rig = new();
        int probes = 0;

        Outcome outcome = await rig.RunAsync(
            () => NotYet,
            _ =>
            {
                probes++;
                return Task.FromResult<JsonNode?>(Running(3.2));
            }
        );

        Assert.Equal(
            "the exit hold ran 3.2 of 10 game s, then no more for 25 s of load-adjusted time: the game's clock stood still (is it paused?)",
            outcome.Error
        );
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task AHoldThatEndsAsItIsProbedWaitsForTheReadersNotAWindow()
    {
        Rig rig = new();
        TimeSpan probedAt = TimeSpan.Zero;

        Outcome outcome = await rig.RunAsync(
            () => NotYet,
            _ =>
            {
                probedAt = rig.Elapsed;
                return Task.FromResult<JsonNode?>(Ended(10));
            }
        );
        TimeSpan after = rig.Elapsed - probedAt;

        Assert.Equal("the pace end marker reached stdout and stderr not both within 35 s", outcome.Error);
        // The readers' 10 s after the probe, not another 25 s window; the fake clock moves in half-second steps.
        Assert.InRange(after, Readers, Window - TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnEndedHoldWhoseStdoutMarkerNeverArrivesKeepsTheBufferingHint()
    {
        Rig rig = new();
        bool probed = false;

        Outcome outcome = await rig.RunAsync(
            () => probed ? new HoldRead(null, false, true) : NotYet,
            _ =>
            {
                probed = true;
                return Task.FromResult<JsonNode?>(Ended(10));
            }
        );

        Assert.Equal(
            "the pace end marker reached stdout and stderr not both within 35 s; stdout may be buffered: is "
                + "application/run/flush_stdout_on_print.debug off in project.godot?",
            outcome.Error
        );
        Assert.Null(outcome.Marked);
    }

    [Fact]
    public async Task AMarkerReadAfterAStallVerdictStillWins()
    {
        Rig rig = new();
        bool probed = false;

        Outcome outcome = await rig.RunAsync(
            () => probed ? MarkedRead : NotYet,
            _ =>
            {
                probed = true;
                return Task.FromResult<JsonNode?>(Running(0));
            }
        );

        Assert.Same(Entries, outcome.Marked);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task ANoHoldReportFailsAtTheFirstWindow()
    {
        Rig rig = new();

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => Task.FromResult<JsonNode?>(new JsonObject { ["state"] = "none" }));

        Assert.Equal("the bridge ran no exit hold, so its pace end marker never came", outcome.Error);
        Assert.Null(outcome.Marked);
    }

    [Fact]
    public async Task ARefusedProbeFailsTheExitWithTheBridgesWords()
    {
        Rig rig = new();

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => throw new InvalidOperationException("boom"));

        Assert.Equal("the exit hold's progress could not be read: boom", outcome.Error);
        Assert.False(outcome.Closed);
    }

    [Fact]
    public async Task AConnectionClosedDuringTheProbeIsTheClosedFallback()
    {
        Rig rig = new();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Outcome outcome = await rig.RunAsync(
            () => NotYet,
            _ =>
            {
                closed.SetResult();
                throw new IOException("The bridge closed the connection.");
            },
            closed.Task
        );

        Assert.Equal(ClosedError, outcome.Error);
        Assert.True(outcome.Closed);
        Assert.Null(outcome.Marked);
    }

    [Fact]
    public async Task ACancellationDuringTheProbePropagatesEvenOnceTheConnectionClosed()
    {
        Rig rig = new();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.RunAsync(
                () => NotYet,
                _ =>
                {
                    closed.SetResult();
                    throw new OperationCanceledException("cancelled");
                },
                closed.Task
            )
        );
    }

    [Fact]
    public async Task AProbeTheSessionRefusesFailsTheExitWithItsWords()
    {
        Rig rig = new();

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => throw new SessionException("paused under a debugger"));

        Assert.Equal("the exit hold's progress could not be read: paused under a debugger", outcome.Error);
        Assert.False(outcome.Closed);
    }

    [Fact]
    public async Task AHoldRunningUnderAnotherMarkerIsNoHoldOfOurs()
    {
        Rig rig = new();
        JsonObject other = Running(5);
        other["marker"] = "[godot-mcp] scratch pace end other";

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => Task.FromResult<JsonNode?>(other));

        Assert.Equal("the bridge ran no exit hold, so its pace end marker never came", outcome.Error);
    }

    [Fact]
    public async Task ARunningReportWithoutSecondsIsNoHold()
    {
        Rig rig = new();
        JsonObject bare = Running(5);
        bare.Remove("seconds");

        Outcome outcome = await rig.RunAsync(() => NotYet, _ => Task.FromResult<JsonNode?>(bare));

        Assert.Equal("the bridge ran no exit hold, so its pace end marker never came", outcome.Error);
    }

    [Fact]
    public async Task AProbeUnansweredWithinTheCallBoundThrowsItsTimeout()
    {
        Rig rig = new();

        await Assert.ThrowsAsync<TimeoutException>(() => rig.RunAsync(() => NotYet, _ => throw new TimeoutException("unanswered")));
    }

    [Fact]
    public async Task TheHoldWaitLeavesTheLimitsCancellationUncaught()
    {
        Rig rig = new();
        using CancellationTokenSource limit = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.RunAsync(
                () =>
                {
                    if (rig.Elapsed >= TimeSpan.FromSeconds(10))
                    {
                        limit.Cancel();
                    }

                    return NotYet;
                },
                _ => Task.FromResult<JsonNode?>(Running(1)),
                limit: limit
            )
        );
    }

    private static JsonObject Running(double seconds) =>
        new()
        {
            ["state"] = "running",
            ["marker"] = Marker,
            ["seconds"] = seconds,
            ["gameMs"] = GameMs,
        };

    private static JsonObject Ended(double seconds) =>
        new()
        {
            ["state"] = "ended",
            ["marker"] = Marker,
            ["seconds"] = seconds,
        };

    /// <summary>A fake clock and the hold wait run on it, advanced in half-second steps until the wait ends.</summary>
    private sealed class Rig
    {
        private readonly FakeTimeProvider _time = new();
        private readonly DateTimeOffset _start;

        public Rig() => _start = _time.GetUtcNow();

        /// <summary>The fake time since the rig was made.</summary>
        public TimeSpan Elapsed => _time.GetUtcNow() - _start;

        /// <summary>Runs the wait with <paramref name="limit"/>'s token as its limit, else the test's own.</summary>
        public async Task<Outcome> RunAsync(
            Func<HoldRead> read,
            Func<CancellationToken, Task<JsonNode?>> probe,
            Task? closed = null,
            CancellationTokenSource? limit = null
        )
        {
            using LoadClock clock = new(_time, new NoLoadSource());
            ScratchHoldWait wait = new(clock, new HoldWaitTerms(Window, Readers, GameMs, Marker, ClosedError), read, probe);
            Task<Outcome> run = wait.RunAsync(closed ?? new TaskCompletionSource().Task, limit?.Token ?? TestContext.Current.CancellationToken);
            DateTime guard = DateTime.UtcNow + RegistryHarness.HangGuard;
            while (!run.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < guard, $"the hold wait did not end within its {RegistryHarness.HangGuard} hang guard");
                _time.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Delay(TimeSpan.FromMilliseconds(2), TestContext.Current.CancellationToken);
            }

            return await run;
        }
    }
}
