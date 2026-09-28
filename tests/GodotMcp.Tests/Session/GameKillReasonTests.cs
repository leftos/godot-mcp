using GodotMcp.Server.Session;

namespace GodotMcp.Tests.Session;

/// <summary>The killReason a stop reports, for each way the quit request went and for the recording run's longer grace.</summary>
public sealed class GameKillReasonTests
{
    [Fact]
    public void AnAcknowledgedQuitThatRanOutOfTimeSaysTheGameWasStillShuttingDown()
    {
        Assert.Equal(
            "the game acknowledged the quit but was still shutting down after 3 s",
            GameKillReason.AfterGrace(QuitRequest.Acknowledged, TimeSpan.FromSeconds(3))
        );
    }

    [Fact]
    public void AnUnansweredQuitSaysTheGameDidNotAnswerWithinTheGrace()
    {
        Assert.Equal(
            "the game did not answer the quit request within 30 s",
            GameKillReason.AfterGrace(QuitRequest.Unanswered, TimeSpan.FromSeconds(30))
        );
    }

    [Fact]
    public void AQuitNeverSentSaysTheGameHadNoConnection()
    {
        Assert.Equal(
            "the game had no bridge connection to ask it to quit, and had not exited after 3 s",
            GameKillReason.AfterGrace(QuitRequest.NotSent, TimeSpan.FromSeconds(3))
        );
    }

    [Fact]
    public void ASilentGameSaysItWasKilledAtOnce() => Assert.Equal("the game did not answer a ping, so it was killed at once", GameKillReason.Silent);
}
