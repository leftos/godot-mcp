using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

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
