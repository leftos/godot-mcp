using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>
/// The killReason a stop reports, for each way the quit request went, with the grace in load-adjusted time, the bridge's
/// connection at the kill, and the game's process state and last stderr lines.
/// </summary>
public sealed class GameKillReasonTests
{
    private const string State = "Process 4242: 15 ms CPU over 1 s, 31 threads, main thread Wait/ExecutionDelay.";
    private static readonly GraceSpent Loaded = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8.4),
        TimeSpan.FromSeconds(3),
        3 / 8.4,
        Backstop: false
    );
    private static readonly GraceSpent Idle = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 1, Backstop: false);

    [Fact]
    public void AnAcknowledgedQuitThatRanOutOfTimeSaysTheGameWasStillShuttingDownWithTheConnectionOpen()
    {
        GraceKill kill = new(QuitRequest.Acknowledged, Loaded, ClosedAfter: null, State, ["ERROR: one", "ERROR: two"]);

        Assert.Equal(
            "the game acknowledged the quit but was still shutting down after 3 s of load-adjusted time (wall 8.4 s, machine free 36% on average); "
                + "the bridge's connection was still open, so the scene tree had not been freed.\n"
                + $"{State}\nLast stderr lines:\nERROR: one\nERROR: two",
            GameKillReason.AfterGrace(kill)
        );
    }

    [Fact]
    public void AConnectionThatClosedSaysHowLongAfterTheAcknowledgementAndWhereTheTimeWent()
    {
        GraceKill kill = new(QuitRequest.Acknowledged, Loaded, TimeSpan.FromMilliseconds(412.4), State, []);

        Assert.Equal(
            "the game acknowledged the quit but was still shutting down after 3 s of load-adjusted time (wall 8.4 s, machine free 36% on average); "
                + "the bridge's connection had closed 412 ms after the quit was acknowledged, "
                + "so the time went into the engine's or .NET's teardown.\n"
                + $"{State}\nLast stderr lines:\n(none)",
            GameKillReason.AfterGrace(kill)
        );
    }

    [Fact]
    public void AnUnansweredQuitSaysTheGameDidNotAnswerWithinTheGraceAndTimesTheCloseFromTheRequest()
    {
        GraceKill kill = new(QuitRequest.Unanswered, Idle, TimeSpan.FromMilliseconds(1500), State, []);

        Assert.StartsWith(
            "the game did not answer the quit request within 30 s of load-adjusted time (wall 30 s, machine free 100% on average); "
                + "the bridge's connection had closed 1500 ms after the quit was sent,",
            GameKillReason.AfterGrace(kill),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void AnAttachedGameHasNoStderrBlock()
    {
        GraceKill kill = new(QuitRequest.Acknowledged, Loaded, ClosedAfter: null, State, StderrLines: null);

        string reason = GameKillReason.AfterGrace(kill);

        Assert.EndsWith($"so the scene tree had not been freed.\n{State}", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuitNeverSentSaysTheGameHadNoConnectionAndNothingMore()
    {
        Assert.Equal(
            "the game had no bridge connection to ask it to quit, and had not exited after 3 s of load-adjusted time "
                + "(wall 8.4 s, machine free 36% on average)",
            GameKillReason.NotSent(Loaded)
        );
    }

    [Fact]
    public void AGraceTheBackstopEndedSaysItRanFiveTimesItsGraceInWallTime()
    {
        GraceSpent backstopped = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1.2), 0.08, Backstop: true);

        Assert.Equal(
            "the game had no bridge connection to ask it to quit, and had not exited after 15 s of wall time, 5 x its 3 s grace "
                + "(load-adjusted 1.2 s, machine free 8% on average)",
            GameKillReason.NotSent(backstopped)
        );
    }

    [Fact]
    public void ASilentGameSaysItWasKilledAtOnce() => Assert.Equal("the game did not answer a ping, so it was killed at once", GameKillReason.Silent);
}
