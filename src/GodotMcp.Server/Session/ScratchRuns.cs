using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace GodotMcp.Server.Session;

/// <summary>
/// The server's background scratch runs: at most one live run per project folder (keyed by the plan's normalised folder with the
/// OS path comparer), the last finished runs' final snapshots, and the sessions live runs hold, which an unnamed tool call never
/// resolves to. One lock covers the live check, the insert and the closed flag the server's exit sets, so no run registers once
/// the exit has begun. The registry's lock is never held while taking this one, nor this one while taking the registry's; this
/// lock may take a run's, never the other way round. The held sessions are read and written without a lock, so the registry
/// may touch them under its own.
/// </summary>
internal sealed class ScratchRuns
{
    /// <summary>How many finished runs' final snapshots are kept.</summary>
    internal const int FinishedKept = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ScratchBackgroundRun> _live = new(PathComparer);
    private readonly List<(string ProjectDir, ScratchRunStatus Status)> _finished = [];
    private readonly ConcurrentDictionary<GodotSession, byte> _held = new();
    private bool _closed;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>What a run_scratches on a project with a live background run is refused with.</summary>
    internal static string StillRunning(string runId) =>
        $"background scratch run {runId} is still running on this project; read it with scratch_status, or end it with scratch_status {{stop: true}}";

    /// <summary>
    /// Registers a background run of <paramref name="plan"/> on its project, in place of an entry whose run has made its final
    /// write, whose final snapshot is kept among the finished.
    /// </summary>
    /// <exception cref="SessionException">A background run is live on the project, or the server's exit has begun.</exception>
    internal ScratchBackgroundRun Register(ScratchPlan plan, int serverPid, ScratchRunIo io)
    {
        lock (_lock)
        {
            if (_closed)
            {
                throw new SessionException(SessionRegistry.ShuttingDown);
            }

            RefuseLiveLocked(plan.ProjectDir);
            if (_live.GetValueOrDefault(plan.ProjectDir) is { HasEnded: true } ended)
            {
                // The status frozen at its end's start: the same object its final status takes, so Remove's keep is de-duplicated.
                KeepFinishedLocked(plan.ProjectDir, ended.Read().Status);
            }

            ScratchBackgroundRun run = new(this, plan, serverPid, io);
            _live[plan.ProjectDir] = run;
            return run;
        }
    }

    /// <summary>Refuses a run on <paramref name="projectDir"/> while a background run is live there.</summary>
    /// <exception cref="SessionException">A background run is live on the project.</exception>
    internal void RefuseLive(string projectDir)
    {
        lock (_lock)
        {
            RefuseLiveLocked(projectDir);
        }
    }

    /// <summary>The background run entered on the project, live or just ended, whatever its id; null when none.</summary>
    internal ScratchBackgroundRun? Live(string projectDir)
    {
        lock (_lock)
        {
            return _live.GetValueOrDefault(projectDir);
        }
    }

    /// <summary>The run <paramref name="runId"/> entered on the project; null when it is not entered here.</summary>
    internal ScratchBackgroundRun? Find(string projectDir, string runId) =>
        Live(projectDir) is { } run && string.Equals(run.RunId, runId, StringComparison.Ordinal) ? run : null;

    /// <summary>The final snapshot of the finished run <paramref name="runId"/> on the project, while it is among the last kept.</summary>
    internal ScratchRunStatus? FindFinished(string projectDir, string runId)
    {
        lock (_lock)
        {
            return _finished
                .LastOrDefault(entry =>
                    PathComparer.Equals(entry.ProjectDir, projectDir) && string.Equals(entry.Status.RunId, runId, StringComparison.Ordinal)
                )
                .Status;
        }
    }

    /// <summary>
    /// Removes the run's entry while it is still this run, keeps its final snapshot among the finished once, whether or not the
    /// entry was still this run, and lets go of the sessions it held.
    /// </summary>
    internal void Remove(ScratchBackgroundRun run)
    {
        ScratchRunStatus? final = run.FinalStatus;
        IReadOnlyList<GodotSession> held = run.HeldSessions;
        lock (_lock)
        {
            if (_live.TryGetValue(run.ProjectDir, out ScratchBackgroundRun? current) && ReferenceEquals(current, run))
            {
                _live.Remove(run.ProjectDir);
            }

            if (final is not null)
            {
                KeepFinishedLocked(run.ProjectDir, final);
            }
        }

        foreach (GodotSession session in held)
        {
            Release(session);
        }
    }

    /// <summary>Marks a session as held by a live background run; lock-free.</summary>
    internal void Hold(GodotSession session) => _held.TryAdd(session, 0);

    /// <summary>Lets go of a session's hold; lock-free.</summary>
    internal void Release(GodotSession session) => _held.TryRemove(session, out _);

    /// <summary>Whether a live background run holds the session; lock-free.</summary>
    internal bool Holds(GodotSession session) => _held.ContainsKey(session);

    /// <summary>
    /// The server's exit begins: no run registers from now on, and every entered run records interrupted and is cancelled, a
    /// failure reported to stderr so the others go on. Returns the tasks of the runs that have one.
    /// </summary>
    internal Task[] Interrupt()
    {
        ScratchBackgroundRun[] runs;
        lock (_lock)
        {
            _closed = true;
            runs = [.. _live.Values];
        }

        foreach (ScratchBackgroundRun run in runs)
        {
            AtShutdown(run, "interrupting", run.Interrupt);
        }

        return [.. runs.Where(run => run.IsLive).Select(run => run.Task).OfType<Task>()];
    }

    /// <summary>
    /// Waits for <paramref name="runs"/> for at most <paramref name="left"/>, then finishes every run still entered as interrupted,
    /// all at once and within what is left of <paramref name="left"/> (each makes one write attempt at least); an entry with no
    /// task is removed here, since no task's finally will.
    /// </summary>
    internal void AwaitInterrupted(Task[] runs, TimeSpan left)
    {
        long started = Stopwatch.GetTimestamp();
        if (runs.Length > 0 && left > TimeSpan.Zero)
        {
            Task.WhenAny(Task.WhenAll(runs), Task.Delay(left)).Wait();
        }

        ScratchBackgroundRun[] entered;
        lock (_lock)
        {
            entered = [.. _live.Values.Where(run => run.IsLive)];
        }

        TimeSpan budget = left - Stopwatch.GetElapsedTime(started);
        Task.WaitAll([.. entered.Select(run => Task.Run(() => AtShutdown(run, "finishing", () => FinishInterrupted(run, budget))))]);
    }

    private void FinishInterrupted(ScratchBackgroundRun run, TimeSpan budget)
    {
        run.Finish(ScratchEnd.Interrupted, budget);
        if (run.Task is null)
        {
            Remove(run);
        }
    }

    /// <summary>One run's step of the server's exit, a failure reported to stderr so the other runs and the exit go on.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "At the server's exit one background run's failure, whatever it is, must not stop the others or the games' stops."
    )]
    private static void AtShutdown(ScratchBackgroundRun run, string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"godot-mcp: {what} background scratch run {run.RunId} at shutdown failed: {e.Message}");
        }
    }

    /// <summary>Keeps a final snapshot among the last finished, once.</summary>
    private void KeepFinishedLocked(string projectDir, ScratchRunStatus final)
    {
        if (_finished.Any(entry => ReferenceEquals(entry.Status, final)))
        {
            return;
        }

        _finished.Add((projectDir, final));
        _finished.RemoveRange(0, Math.Max(0, _finished.Count - FinishedKept));
    }

    /// <summary>Refuses while the project's entry holds a run whose end has not begun; the run's lock is taken under this one.</summary>
    private void RefuseLiveLocked(string projectDir)
    {
        if (_live.TryGetValue(projectDir, out ScratchBackgroundRun? entered) && !entered.HasEnded)
        {
            throw new SessionException(StillRunning(entered.RunId));
        }
    }
}
