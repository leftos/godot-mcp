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

    [Fact]
    public void OpenIfStartedWithinTakesAProcessCreatedInsideTheWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in is cmd.exe running ping -n, Windows' syntax.");
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        Process standIn = RegistryHarness.StartStandInGame();
        DateTime after = DateTime.UtcNow.AddSeconds(1);
        try
        {
            UnprovenOpen opened = RunKill.OpenIfStartedWithin(standIn.Id, new StartWindow(before, after), NullLogger.Instance);
            using Process? game = opened.Game;

            Assert.NotNull(game);
            Assert.Equal(standIn.Id, game.Id);
            Assert.False(opened.Unopenable);
        }
        finally
        {
            RegistryHarness.EndStandInGame(standIn);
        }
    }

    [Fact]
    public void OpenIfStartedWithinCountsAProcessCreatedOutsideTheWindowAsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in is cmd.exe running ping -n, Windows' syntax.");
        DateTime ended = DateTime.UtcNow.AddSeconds(-1);
        Process standIn = RegistryHarness.StartStandInGame();
        try
        {
            UnprovenOpen opened = RunKill.OpenIfStartedWithin(standIn.Id, new StartWindow(ended.AddMinutes(-1), ended), NullLogger.Instance);

            Assert.Equal(UnprovenOpen.Gone, opened);
            Assert.False(standIn.WaitForExit(0), "the process outside the window was ended");
        }
        finally
        {
            RegistryHarness.EndStandInGame(standIn);
        }
    }

    [Fact]
    public void OpenIfStartedWithinCountsAPidNoProcessHoldsAsGone()
    {
        // Windows hands out multiples of four far below it.
        const int noProcessId = int.MaxValue & ~3;
        DateTime now = DateTime.UtcNow;

        UnprovenOpen opened = RunKill.OpenIfStartedWithin(noProcessId, new StartWindow(now.AddMinutes(-1), now), NullLogger.Instance);

        Assert.Equal(UnprovenOpen.Gone, opened);
    }

    [Fact]
    public void OpenIfStillRunningReadsTheCreationTimeThroughTheHandleItHoldsWhileItKills()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in is cmd.exe running ping -n, Windows' syntax.");
        Process standIn = RegistryHarness.StartStandInGame();
        try
        {
            using HeldProcess? held = RunKill.OpenIfStillRunning(standIn.Id, standIn.StartTime);
            // Read again through a process object of its own, apart from the argument the open compared.
            using var byPid = Process.GetProcessById(standIn.Id);
            DateTime readApart = byPid.StartTime;

            Assert.NotNull(held);
            Assert.Equal(standIn.Id, held.Id);
            Assert.Equal(readApart, held.StartTime);
            held.Process.Kill(entireProcessTree: true);
            Assert.True(standIn.WaitForExit(RegistryHarness.HangGuard), "the kill through the held process did not end the stand-in");
        }
        finally
        {
            RegistryHarness.EndStandInGame(standIn);
        }
    }

    // The pid-reuse analogue: the process holding the pid was created at another time than the one recorded.
    [Fact]
    public void OpenIfStillRunningCountsAProcessWithAnotherCreationTimeAsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in is cmd.exe running ping -n, Windows' syntax.");
        Process standIn = RegistryHarness.StartStandInGame();
        try
        {
            using HeldProcess? held = RunKill.OpenIfStillRunning(standIn.Id, standIn.StartTime.AddSeconds(1));

            Assert.Null(held);
            Assert.False(standIn.WaitForExit(0), "the process with another creation time was ended");
        }
        finally
        {
            RegistryHarness.EndStandInGame(standIn);
        }
    }

    [Theory]
    [InlineData("Godot_v4.7.2-stable_win64", "svchost", true)]
    [InlineData("Godot_v4.7.2-stable_win64", "godot_v4.7.2-stable_WIN64", false)]
    [InlineData(null, "svchost", false)]
    [InlineData("Godot_v4.7.2-stable_win64", null, false)]
    [InlineData(null, null, false)]
    public void APidIsHeldByAnotherImageOnlyWhenBothNamesAreKnownAndDiffer(string? recorded, string? holding, bool another) =>
        Assert.Equal(another, RunKill.HeldByAnotherImage(recorded, holding));

    // A killed leftover lingering with its exit code set is refused by .NET's own full-access open, so the fallback opens it,
    // with the rights a wait needs and no right to end it.
    [Fact]
    public async Task OpenLeftoverIfStillRunningOpensALingeringLeftoverWithWaitRightsOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Process lingering = RegistryHarness.StartStandInGame();
        try
        {
            DateTime started = lingering.StartTime;
            using ExitHold hold = await ExitHold.AttachAsync(lingering, RegistryHarness.HangGuard);
            lingering.Kill();
            await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
            List<uint> asked = [];

            using Process? leftover = RunKill.OpenLeftoverIfStillRunning(
                lingering.Id,
                started,
                NullLogger.Instance,
                (pid, start, rights) =>
                {
                    asked.Add(rights);
                    return HeldProcess.Open(pid, start, rights);
                }
            );
            hold.Release();

            Assert.NotNull(leftover);
            Assert.Equal([HeldProcess.WaitRights], asked);
        }
        finally
        {
            RegistryHarness.EndStandInGame(lingering);
        }
    }

    // Killed and waited for, it is gone, though the test's own handle keeps its pid; killed while the test's debugger holds its
    // exit, it lingers with its exit code set, which .NET's own full-access open refuses (it checks the exit code first): the
    // open with the games' rights still holds it, and a wait on the held handle reports it still running.
    [Fact]
    public async Task OpenIfStillRunningCountsAKilledProcessAsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The exit is held through the Win32 debug API.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Process ended = RegistryHarness.StartStandInGame();
        Process lingering = RegistryHarness.StartStandInGame();
        try
        {
            DateTime endedStarted = ended.StartTime;
            ended.Kill(entireProcessTree: true);
            Assert.True(ended.WaitForExit(RegistryHarness.HangGuard), "the killed stand-in was not gone");
            using (HeldProcess? gone = RunKill.OpenIfStillRunning(ended.Id, endedStarted))
            {
                Assert.Null(gone);
            }

            DateTime lingeringStarted = lingering.StartTime;
            using ExitHold hold = await ExitHold.AttachAsync(lingering, RegistryHarness.HangGuard);
            lingering.Kill();
            await hold.Killed.WaitAsync(RegistryHarness.HangGuard, cancellation);
            using HeldProcess? held = RunKill.OpenIfStillRunning(lingering.Id, lingeringStarted);
            Assert.NotNull(held);
            FakeTimeProvider time = new();
            using LoadClock clock = new(time, new NoLoadSource());
            using LoadDeadline deadline = clock.Start(RunKill.Budget, cancellation);
            Task<IReadOnlyList<int>> wait = ProcessExit.WaitUntilGoneAsync([(held.Id, held.Handle)], deadline);
            await RegistryHarness.AdvanceUntilDoneAsync(time, wait, cancellation);
            IReadOnlyList<int> stillRunning = await wait;
            hold.Release();

            Assert.Equal([lingering.Id], stillRunning);
        }
        finally
        {
            RegistryHarness.EndStandInGame(ended);
            RegistryHarness.EndStandInGame(lingering);
        }
    }

    // An awaited process is only waited for: held past the wait's deadline on the fake clock, it is reported still running, the
    // warning left to the caller, and it is still running afterwards.
    [Fact]
    public async Task KillAndWaitAllWaitsForAnAwaitedProcessWithoutKillingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in is cmd.exe running ping -n, Windows' syntax.");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        FakeTimeProvider time = new();
        using LoadClock clock = new(time, new NoLoadSource());
        Process standIn = RegistryHarness.StartStandInGame();
        try
        {
            Task<KilledGamesWait> wait = RunKill.KillAndWaitAllAsync([], [standIn], clock, NullLogger.Instance, Project);
            await RegistryHarness.AdvanceUntilDoneAsync(time, wait, cancellation);
            KilledGamesWait waited = await wait;

            Assert.Equal([standIn.Id], waited.AwaitedStillRunning);
            Assert.Empty(waited.StillRunning);
            Assert.NotNull(waited.After);
            Assert.Null(waited.Warning);
            Assert.False(standIn.WaitForExit(0), "the awaited process was killed");
        }
        finally
        {
            RegistryHarness.EndStandInGame(standIn);
        }
    }
}
