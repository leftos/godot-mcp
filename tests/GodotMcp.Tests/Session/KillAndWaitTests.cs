using System.Diagnostics;
using GodotMcp.Server.Session;
using GodotMcp.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A stop's kill of a game and its console wrapper: both are killed and waited for on one deadline of load-adjusted time, and
/// the warning names the one still running when it ends. The clocks are fake and advance only where a test says so; the waits'
/// own real-time bound is far past what a passing kill takes on a busy machine.
/// </summary>
public sealed class KillAndWaitTests
{
    private const string Project = @"X:\probe";
    private const string StillUsesTheFolder = "; its project folder stays in use until it is gone.";

    [Fact]
    public async Task AKillEndsAGameOutsideItsWrappersTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-ins are cmd.exe running ping -n, Windows' syntax.");
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        Process wrapper = RegistryHarness.StartStandInGame();
        Process game = RegistryHarness.StartStandInGame();
        try
        {
            // The two are unrelated, so only a kill of the game itself can end it.
            string? warning = await RunKill.KillAndWaitAsync(wrapper, game, clock, NullLogger.Instance, Project);

            Assert.Null(warning);
            Assert.True(wrapper.WaitForExit(0));
            Assert.True(game.WaitForExit(0));
        }
        finally
        {
            RegistryHarness.EndStandInGame(wrapper);
            RegistryHarness.EndStandInGame(game);
        }
    }

    [Fact]
    public async Task AGameHeldPastTheWaitIsReportedStillRunningWhileItsWrapperIsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        Process wrapper = RegistryHarness.StartStandInGame();
        Process game = RegistryHarness.StartStandInGame();
        try
        {
            using ExitHold hold = await ExitHold.AttachAsync(game, RegistryHarness.HangGuard);
            DateTimeOffset started = time.GetUtcNow();
            Task<string?> kill = RunKill.KillAndWaitAsync(wrapper, game, clock, NullLogger.Instance, Project);
            // A killed wrapper can take seconds to be signalled on a busy machine; the deadline is on the fake clock, so this
            // real-time wait spends none of it.
            Assert.True(await Task.Run(() => wrapper.WaitForExit(RegistryHarness.HangGuard), cancellation));
            await RegistryHarness.AdvanceUntilDoneAsync(time, kill, cancellation);
            string? warning = await kill;
            TimeSpan spent = time.GetUtcNow() - started;
            bool gameGone = game.WaitForExit(0);
            hold.Release();

            Assert.False(gameGone);
            Assert.NotNull(warning);
            Assert.StartsWith(
                $"The killed game (pid {game.Id}) was still running after 5 s of load-adjusted time (wall ",
                warning,
                StringComparison.Ordinal
            );
            Assert.EndsWith(StillUsesTheFolder, warning, StringComparison.Ordinal);
            Assert.DoesNotContain("console wrapper", warning, StringComparison.Ordinal);
            // The wait ended at its budget of fake time, not later.
            Assert.True(spent < TimeSpan.FromSeconds(10), $"the kill waited {spent} of fake time");
        }
        finally
        {
            RegistryHarness.EndStandInGame(wrapper);
            RegistryHarness.EndStandInGame(game);
        }
    }

    // Both held past the wait: the warning names only the game, the one that keeps the folder, and both waits ran on one
    // deadline, so the fake clock never ran a second 5 s wait.
    [Fact]
    public async Task AGameAndWrapperBothHeldPastTheWaitAreReportedAsTheGameAloneAfterOneWait()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exits are held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        Process wrapper = RegistryHarness.StartStandInGame();
        Process game = RegistryHarness.StartStandInGame();
        try
        {
            using ExitHold wrapperHold = await ExitHold.AttachAsync(wrapper, RegistryHarness.HangGuard);
            using ExitHold gameHold = await ExitHold.AttachAsync(game, RegistryHarness.HangGuard);
            DateTimeOffset started = time.GetUtcNow();
            Task<string?> kill = RunKill.KillAndWaitAsync(wrapper, game, clock, NullLogger.Instance, Project);
            await RegistryHarness.AdvanceUntilDoneAsync(time, kill, cancellation);
            string? warning = await kill;
            TimeSpan spent = time.GetUtcNow() - started;
            bool wrapperGone = wrapper.WaitForExit(0);
            bool gameGone = game.WaitForExit(0);
            wrapperHold.Release();
            gameHold.Release();

            Assert.False(wrapperGone);
            Assert.False(gameGone);
            Assert.NotNull(warning);
            Assert.StartsWith(
                $"The killed game (pid {game.Id}) was still running after 5 s of load-adjusted time (wall ",
                warning,
                StringComparison.Ordinal
            );
            Assert.EndsWith(StillUsesTheFolder, warning, StringComparison.Ordinal);
            Assert.DoesNotContain("console wrapper", warning, StringComparison.Ordinal);
            Assert.True(spent < TimeSpan.FromSeconds(10), $"the kill waited {spent} of fake time");
        }
        finally
        {
            RegistryHarness.EndStandInGame(wrapper);
            RegistryHarness.EndStandInGame(game);
        }
    }

    [Fact]
    public async Task AWrapperHeldPastTheWaitWhileItsGameIsGoneIsReportedAsTheWrapper()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        Process wrapper = RegistryHarness.StartStandInGame();
        Process game = RegistryHarness.StartStandInGame();
        try
        {
            using ExitHold hold = await ExitHold.AttachAsync(wrapper, RegistryHarness.HangGuard);
            Task<string?> kill = RunKill.KillAndWaitAsync(wrapper, game, clock, NullLogger.Instance, Project);
            // The game's signal can take seconds on a busy machine; the deadline is on the fake clock, so this spends none of it.
            Assert.True(await Task.Run(() => game.WaitForExit(RegistryHarness.HangGuard), cancellation));
            await RegistryHarness.AdvanceUntilDoneAsync(time, kill, cancellation);
            string? warning = await kill;
            bool wrapperGone = wrapper.WaitForExit(0);
            hold.Release();

            Assert.False(wrapperGone);
            Assert.NotNull(warning);
            Assert.StartsWith(
                $"The killed console wrapper (pid {wrapper.Id}) was still running after 5 s of load-adjusted time (wall ",
                warning,
                StringComparison.Ordinal
            );
            Assert.EndsWith("% on average).", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("killed game", warning, StringComparison.Ordinal);
        }
        finally
        {
            RegistryHarness.EndStandInGame(wrapper);
            RegistryHarness.EndStandInGame(game);
        }
    }

    [Fact]
    public async Task AKilledGameSlowerThanItsBudgetInWallTimeButNotInLoadAdjustedTimeIsWaitedFor()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        FakeTimeProvider time = new();
        FakeLoadSource machine = new(time, processors: 4);
        using LoadClock clock = new(time, machine);
        // Three of four processors busy with other work leave a quarter of the machine: 8 s of wall time is under 5 s adjusted.
        machine.SetLoad(busyShare: 0.75, ownCores: 0);
        Process game = RegistryHarness.StartStandInGame();
        try
        {
            using ExitHold hold = await ExitHold.AttachAsync(game, RegistryHarness.HangGuard);
            Task<string?> kill = RunKill.KillAndWaitAsync(wrapper: null, game, clock, NullLogger.Instance, Project);
            await hold.Killed.WaitAsync(RegistryHarness.HangGuard, TestContext.Current.CancellationToken);

            for (TimeSpan passed = TimeSpan.Zero; passed < TimeSpan.FromSeconds(8); passed += TimeSpan.FromMilliseconds(100))
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
            }

            Assert.False(kill.IsCompleted, "the kill wait ended inside its load-adjusted budget");
            hold.Release();

            Assert.Null(await kill.WaitAsync(RegistryHarness.HangGuard, TestContext.Current.CancellationToken));
            Assert.True(game.WaitForExit(0));
        }
        finally
        {
            RegistryHarness.EndStandInGame(game);
        }
    }

    [Fact]
    public async Task AKillOfAProcessAlreadyGoneOrLetGoOfDoesNotThrow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-ins are cmd.exe running ping -n, Windows' syntax.");
        using LoadClock clock = new(new FakeTimeProvider(), new NoLoadSource());
        Process exited = RegistryHarness.StartStandInGame();
        Process letGoOf = RegistryHarness.StartStandInGame();
        RegistryHarness.EndStandInGame(letGoOf);
        exited.Kill(entireProcessTree: true);
        exited.WaitForExit();
        try
        {
            Assert.Null(await RunKill.KillAndWaitAsync(exited, letGoOf, clock, NullLogger.Instance, Project));
        }
        finally
        {
            exited.Dispose();
        }
    }
}
