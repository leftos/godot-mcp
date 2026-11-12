using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace GodotMcp.Server.Session;

/// <summary>
/// A stop's kill of a game: the game's own process and its console wrapper, each with everything it started, then one wait for
/// both on a fresh deadline of load-adjusted time, so a killed game slow to go on a busy machine is waited for, and one still
/// running when the deadline ends is reported rather than passed over.
/// </summary>
internal static class RunKill
{
    /// <summary>The kill wait's budget of load-adjusted time; its backstop is <see cref="LoadClock.BackstopFactor"/> times it in wall time.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Kills <paramref name="game"/> and <paramref name="wrapper"/>, each that is given and still runs, with their trees, then waits
    /// for both together on one deadline of <see cref="Budget"/> started on <paramref name="clock"/> after the kills. A kill that
    /// fails is logged; a process disposed under the wait counts as gone. Never throws for a process already gone.
    /// </summary>
    /// <returns>
    /// The warning when either is still running as the deadline ends: the game's sentence when the game is (alone when both are),
    /// else the console wrapper's; null when both are gone.
    /// </returns>
    public static async Task<string?> KillAndWaitAsync(Process? wrapper, Process? game, LoadClock clock, ILogger logger, string projectDir)
    {
        Kill(game, logger, projectDir);
        Kill(wrapper, logger, projectDir);
        using LoadDeadline deadline = clock.Start(Budget);
        Task<bool> gameGone = GoneAsync(game, deadline);
        Task<bool> wrapperGone = GoneAsync(wrapper, deadline);
        await Task.WhenAll(gameGone, wrapperGone);
        if (await gameGone && await wrapperGone)
        {
            return null;
        }

        Log.StillRunningAfterKill(logger, projectDir, Budget.TotalSeconds);
        string after = GraceSpent.Of(deadline).TextFor("kill wait");
        return await gameGone
            ? $"The killed console wrapper (pid {PidOf(wrapper!)}) was still running after {after}."
            : $"The killed game (pid {PidOf(game!)}) was still running after {after}; its project folder stays in use until it is gone.";
    }

    /// <summary>
    /// Kills each of <paramref name="games"/> that still runs, with its tree, then waits for all of them, and for each of
    /// <paramref name="awaited"/>, which are never killed, together on one deadline of <see cref="Budget"/> started on
    /// <paramref name="clock"/> after the kills, as <see cref="KillAndWaitAsync"/> does. The awaited processes are waited for
    /// through registered waits on their handles, which the caller keeps open through the wait.
    /// </summary>
    /// <returns>
    /// The pids of the games still running as the deadline ends, and the warning that names them; the pids of
    /// <paramref name="awaited"/> still running (<see cref="KilledGamesWait.AwaitedStillRunning"/>), which the warning leaves to
    /// the caller; and the time the wait took when anything still runs. Empty lists and no warning when all are gone.
    /// </returns>
    public static async Task<KilledGamesWait> KillAndWaitAllAsync(
        IReadOnlyList<Process> games,
        IReadOnlyList<Process> awaited,
        LoadClock clock,
        ILogger logger,
        string projectDir
    )
    {
        foreach (Process game in games)
        {
            Kill(game, logger, projectDir);
        }

        using LoadDeadline deadline = clock.Start(Budget);
        Task<bool[]> gamesGone = Task.WhenAll(games.Select(game => GoneAsync(game, deadline)));
        IReadOnlyList<int> awaitedStillRunning = await AwaitAsync(awaited, deadline, logger);
        bool[] gone = await gamesGone;
        int[] stillRunning = [.. games.Where((_, index) => !gone[index]).Select(game => game.Id)];
        if (stillRunning.Length == 0 && awaitedStillRunning.Count == 0)
        {
            return new KilledGamesWait([], Warning: null);
        }

        Log.StillRunningAfterKill(logger, projectDir, Budget.TotalSeconds);
        string after = GraceSpent.Of(deadline).TextFor("kill wait");
        return new KilledGamesWait(stillRunning, DescribeKilledStillRunning(stillRunning, after))
        {
            After = after,
            AwaitedStillRunning = awaitedStillRunning,
        };
    }

    /// <summary>
    /// Opens a game whose creation time is unknown, from its pid and <paramref name="window"/>, the span it must have started in:
    /// the process with <paramref name="pid"/>, its handle opened and kept on it, when it was created inside the window and is
    /// not gone. The caller disposes the game returned.
    /// </summary>
    /// <returns>
    /// The game; <see cref="UnprovenOpen.Gone"/> when no process has the pid (or the process list cannot be read), the one that
    /// has it was created outside the window (another process took the pid), or it is gone; unopenable when a process has the
    /// pid but its creation time cannot be read, there is no window to check it against, or its handle cannot be opened.
    /// </returns>
    public static UnprovenOpen OpenIfStartedWithin(int pid, StartWindow? window, ILogger logger)
    {
        Process? opened = Listed(pid, logger);
        try
        {
            if (opened is null)
            {
                return UnprovenOpen.Gone;
            }

            UnprovenOpen found = Identify(opened, window, logger);
            if (found.Game is not null)
            {
                opened = null;
            }

            return found;
        }
        finally
        {
            opened?.Dispose();
        }
    }

    /// <summary>
    /// Opens a process a stopped session's quit game left when it still runs: the process with <paramref name="pid"/>, its handle
    /// opened and kept on it first, whose creation time read through that handle is <paramref name="started"/>, and that is not
    /// gone; so the wait is on the very process whose creation time was checked. One whose handle cannot be opened with full
    /// access, such as one being torn down, is opened as <see cref="OpenIfStillRunning"/> opens it, without a held handle. The
    /// caller disposes the process returned.
    /// </summary>
    /// <returns>The process; null when no process has the pid, its creation time differs or cannot be read, or it is gone.</returns>
    public static Process? OpenLeftoverIfStillRunning(int pid, DateTime started, ILogger logger)
    {
        Process? opened = Listed(pid, logger);
        try
        {
            if (opened is null)
            {
                return null;
            }

            if (!Hold(opened, logger))
            {
                return OpenIfStillRunning(pid, started);
            }

            if (opened.StartTime == started && !opened.WaitForExit(0))
            {
                Process leftover = opened;
                opened = null;
                return leftover;
            }

            return null;
        }
        // The held process's creation time or state cannot be read: not one the stop can tell is the leftover.
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.StoppedProcessUnreadable(logger, e, pid);
            return null;
        }
        finally
        {
            opened?.Dispose();
        }
    }

    /// <summary>
    /// What <see cref="OpenIfStartedWithin"/> finds <paramref name="process"/>, the one holding the game's pid, to be. Its handle
    /// is opened and kept first and its creation time read through it, so the process killed is the one checked: inside the
    /// window it is the game unless gone. Without a held handle it is never the game: unopenable unless created outside the
    /// window. The caller disposes it unless it is the game returned.
    /// </summary>
    private static UnprovenOpen Identify(Process process, StartWindow? window, ILogger logger)
    {
        if (!Hold(process, logger))
        {
            return new UnprovenOpen(null, Unopenable: StartedWithin(process, window, logger) != false);
        }

        return StartedWithin(process, window, logger) switch
        {
            true => process.WaitForExit(0) ? UnprovenOpen.Gone : new UnprovenOpen(process, Unopenable: false),
            false => UnprovenOpen.Gone,
            null => new UnprovenOpen(null, Unopenable: true),
        };
    }

    /// <summary>
    /// Whether <paramref name="process"/> was created inside <paramref name="window"/>; null, logged at Debug when unreadable,
    /// when there is no window or its creation time cannot be read.
    /// </summary>
    private static bool? StartedWithin(Process process, StartWindow? window, ILogger logger)
    {
        if (window is not { } span)
        {
            return null;
        }

        try
        {
            DateTime started = process.StartTime.ToUniversalTime();
            return started >= span.FromUtc && started <= span.ToUtc;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.StoppedProcessUnreadable(logger, e, process.Id);
            return null;
        }
    }

    /// <summary>
    /// Opens <paramref name="process"/>'s handle with full access and keeps it on the object, so every later read, kill and wait
    /// is of this process; false, logged at Debug, when it cannot be opened (the process has exited, is being torn down, or is
    /// not the server's to open).
    /// </summary>
    private static bool Hold(Process process, ILogger logger)
    {
        try
        {
            _ = process.Handle;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.StoppedProcessHandleFailed(logger, e, process.Id);
            return false;
        }
    }

    /// <summary>
    /// The process with <paramref name="pid"/>, as <see cref="FromProcessList"/> finds it; null when none has it, or, logged at
    /// Debug, when the list cannot be read.
    /// </summary>
    private static Process? Listed(int pid, ILogger logger)
    {
        try
        {
            return FromProcessList(pid);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.StoppedProcessUnlisted(logger, e, pid);
            return null;
        }
    }

    /// <summary>
    /// Waits for each of <paramref name="awaited"/>, never killing it, until it is gone or <paramref name="deadline"/> expires:
    /// through a registered wait on its handle, so no pool thread is held per process past the deadline, or past the handles'
    /// disposal; or, for a process opened without its handle that can no longer have one opened with full access (it has exited
    /// since, or is being torn down), through <see cref="GoneAsync"/>, as a killed game is waited for.
    /// </summary>
    /// <returns>The pids of the processes still not gone when the deadline expired; empty when all are gone.</returns>
    private static async Task<IReadOnlyList<int>> AwaitAsync(IReadOnlyList<Process> awaited, LoadDeadline deadline, ILogger logger)
    {
        List<(int Pid, SafeProcessHandle Handle)> held = [];
        List<Process> unheld = [];
        foreach (Process process in awaited)
        {
            if (HandleOf(process, logger) is { } handle)
            {
                held.Add((process.Id, handle));
            }
            else
            {
                unheld.Add(process);
            }
        }

        Task<bool[]> unheldGone = Task.WhenAll(unheld.Select(process => UnheldGoneAsync(process, deadline, logger)));
        IReadOnlyList<int> heldStillRunning = await ProcessExit.WaitUntilGoneAsync(held, deadline);
        bool[] gone = await unheldGone;
        return [.. heldStillRunning, .. unheld.Where((_, index) => !gone[index]).Select(process => process.Id)];
    }

    /// <summary>
    /// Whether an awaited process with no held handle is gone before the deadline ends, as <see cref="GoneAsync"/> waits for it;
    /// false, logged at Debug, when it cannot be opened to wait on, so it is reported still running, never counted gone.
    /// </summary>
    private static async Task<bool> UnheldGoneAsync(Process process, LoadDeadline deadline, ILogger logger)
    {
        try
        {
            return await GoneAsync(process, deadline);
        }
        catch (Win32Exception e)
        {
            Log.StoppedProcessUnwaitable(logger, e, process.Id);
            return false;
        }
    }

    /// <summary>
    /// The process's handle, opened and kept on it when it is not yet; null, logged at Debug, when it cannot be opened any more.
    /// </summary>
    private static SafeProcessHandle? HandleOf(Process process, ILogger logger)
    {
        try
        {
            return process.SafeHandle;
        }
        // The process has exited since it was opened, or is being torn down: the caller waits for it without a held handle.
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            Log.StoppedProcessHandleFailed(logger, e, process.Id);
            return null;
        }
    }

    /// <summary>The warning naming the killed games still running after a wait of <paramref name="after"/>; null when none is.</summary>
    private static string? DescribeKilledStillRunning(int[] stillRunning, string after)
    {
        string pids = string.Join(", ", stillRunning.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
        return stillRunning.Length switch
        {
            0 => null,
            1 => $"The killed game (pid {pids}) was still running after {after}; its project folder stays in use until it is gone.",
            _ => $"The killed games (pids {pids}) were still running after {after}; their project folder stays in use until they are gone.",
        };
    }

    /// <summary>
    /// Kills <paramref name="killed"/> with its tree, as <see cref="KillAndWaitAsync"/> kills a console wrapper, then waits for it
    /// and for each of <paramref name="awaited"/>, which are not killed, together on one deadline of <see cref="Budget"/> started
    /// on <paramref name="clock"/> after the kill. The awaited processes are waited for through registered waits on their handles,
    /// which the caller keeps open through the wait; a killed process disposed under the wait counts as gone.
    /// </summary>
    /// <returns>
    /// The pids of <paramref name="awaited"/> still running as the deadline ends, and the time the wait took as a warning words it
    /// (<see cref="KilledGamesWait.After"/>); no warning, since the caller words its own.
    /// </returns>
    public static async Task<KilledGamesWait> KillAndWaitWithAsync(
        Process killed,
        IReadOnlyList<Process> awaited,
        LoadClock clock,
        ILogger logger,
        string projectDir
    )
    {
        Kill(killed, logger, projectDir);
        using LoadDeadline deadline = clock.Start(Budget);
        Task<bool> killedGone = GoneAsync(killed, deadline);
        // A registered wait per handle, so no pool thread is held per process past the deadline, or past the handles' disposal.
        IReadOnlyList<int> stillRunning = await ProcessExit.WaitUntilGoneAsync(
            [.. awaited.Select(process => (process.Id, process.SafeHandle))],
            deadline
        );
        if (await killedGone && stillRunning.Count == 0)
        {
            return new KilledGamesWait([], Warning: null);
        }

        Log.StillRunningAfterKill(logger, projectDir, Budget.TotalSeconds);
        return new KilledGamesWait(stillRunning, Warning: null) { After = GraceSpent.Of(deadline).TextFor("kill wait") };
    }

    /// <summary>
    /// Opens the game, or a process its quit game left, that a stopped session recorded when it still runs: the process with
    /// <paramref name="pid"/> whose creation time is <paramref name="started"/> and that is not gone. The caller disposes the
    /// process returned.
    /// </summary>
    /// <returns>
    /// The process; null when no process has the pid, its creation time differs (another process took the pid) or cannot be read,
    /// or it is gone.
    /// </returns>
    public static Process? OpenIfStillRunning(int pid, DateTime started)
    {
        Process? opened = null;
        try
        {
            opened = FromProcessList(pid);
            if (opened is not null && opened.StartTime == started && !opened.WaitForExit(0))
            {
                Process game = opened;
                opened = null;
                return game;
            }
        }
        // Each of these says the recorded game is not there to wait for: no process has the pid, or it cannot be read.
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
        finally
        {
            opened?.Dispose();
        }

        return null;
    }

    /// <summary>
    /// The process with <paramref name="pid"/> from the list of processes Windows still holds; null when none has it. Not
    /// <see cref="Process.GetProcessById(int)"/>, which turns down a killed process whose exit code is set while Windows has not
    /// yet let it go: the very game a stop's kill wait outlived.
    /// </summary>
    private static Process? FromProcessList(int pid)
    {
        Process? found = null;
        foreach (Process process in Process.GetProcesses())
        {
            if (found is null && process.Id == pid)
            {
                found = process;
            }
            else
            {
                process.Dispose();
            }
        }

        return found;
    }

    /// <summary>Kills a process that still runs, with its tree; one not given, already exited or let go of is left alone.</summary>
    private static void Kill(Process? process, ILogger logger, string projectDir)
    {
        if (process is null || !IsRunning(process))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        // Process.Kill with its tree throws AggregateException when part of the tree cannot be killed.
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or AggregateException)
        {
            Log.KillFailed(logger, e, projectDir);
        }
    }

    private static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Never started, or disposed as the run let go of it (ObjectDisposedException is one).
            return false;
        }
    }

    /// <summary>Whether a killed process is gone before the deadline ends; one not given, or let go of meanwhile, is.</summary>
    private static async Task<bool> GoneAsync(Process? process, LoadDeadline deadline)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            return await ProcessExit.WaitUntilGoneAsync(process, deadline);
        }
        catch (InvalidOperationException)
        {
            // Never started, or disposed while the wait ran (ObjectDisposedException is one): the run has let go of it.
            return true;
        }
    }

    private static string PidOf(Process process)
    {
        try
        {
            return process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }
}

/// <summary>What a wait for killed games ended with: the pids still running at its deadline, and the warning naming them.</summary>
internal sealed record KilledGamesWait(IReadOnlyList<int> StillRunning, string? Warning)
{
    /// <summary>
    /// The time the wait took as its warning words it (<see cref="GraceSpent.TextFor"/> of "kill wait"), for a caller that words
    /// its own; null when all were gone.
    /// </summary>
    public string? After { get; init; }

    /// <summary>
    /// The pids of the processes the wait only awaited, never killed, still running at its deadline; empty when all are gone.
    /// </summary>
    public IReadOnlyList<int> AwaitedStillRunning { get; init; } = [];
}

/// <summary>A span of creation times, in UTC, both ends included, that a process must have started in to be the one meant.</summary>
internal readonly record struct StartWindow(DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// What an open of a game whose creation time is unknown found: the game, opened and still running; or nothing, with
/// <paramref name="Unopenable"/> true when a process holds the pid but cannot be told apart or opened, false when the game is
/// gone (no process holds the pid, or one created outside the game's start window does).
/// </summary>
internal readonly record struct UnprovenOpen(Process? Game, bool Unopenable)
{
    /// <summary>The game is gone: nothing to kill, wait for or report.</summary>
    public static UnprovenOpen Gone => new(null, false);
}
