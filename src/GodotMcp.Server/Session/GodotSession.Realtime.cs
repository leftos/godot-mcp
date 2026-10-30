using System.Diagnostics.CodeAnalysis;
using GodotMcp.Server.Tools;

namespace GodotMcp.Server.Session;

/// <summary>
/// Recording the session's game window in real time: one recording at a time per session, started and stopped by
/// record_mark on a session not launched with options.record. The session's end (a stop, a restart, a detach) ends a running
/// recording before it lets go of the game and reports its clip, returned once; a game that quits ends it too, and its clip
/// waits on the session for the next stop. A start asked for once the session's end has begun is refused until the game has
/// restarted; one still in flight when the end began is ended as soon as it has started, and its clip is not reported.
/// </summary>
internal sealed partial class GodotSession
{
    private const string NoClip = "The real-time recording ended without a clip.";

    private readonly Lock _realtimeLock = new();

    // The session's latest real-time recording, running or finished, until a stop takes its clip; under _realtimeLock.
    private RealtimeRecording? _realtime;

    // When the session's current game was launched or attached, which a recording's startedSeconds counts from; under _realtimeLock.
    private DateTimeOffset _gameStartedAt;

    // When a start still in flight was asked for; under _realtimeLock.
    private DateTimeOffset? _realtimeStarting;

    // Completes when the start in flight, if any, has settled, started or not; under _realtimeLock.
    private Task _realtimeStartSettled = Task.CompletedTask;

    // Whether the session's end came while the start in flight ran, so its capture ends as soon as it has started; under _realtimeLock.
    private bool _realtimeEndedWhileStarting;

    // Whether the session's end has begun for its current game, so a start asked for from then on is refused; cleared when a
    // game is launched or attached; under _realtimeLock.
    private bool _realtimeSessionEnding;

    // Whether the server's exit has begun, so every recording of the session, one published by a start in flight included,
    // delivers its clip by rename only; never cleared; under _realtimeLock.
    private bool _realtimeExiting;

    /// <summary>
    /// Starts a real-time recording of the game's window at <paramref name="fps"/>, ending on its own after
    /// <paramref name="maxSeconds"/>, and returns once its encoder has opened on the first frame. With
    /// <paramref name="audio"/> the clip records the game's own sound, unless the session is muted or the game's hello sent no
    /// process id, which the start and the clip warn of. Every refusal comes before a process starts: no game, the session's
    /// end begun, one running, no window, a quiet session, then what <see cref="RealtimeRecording.Locate"/> refuses. When the
    /// session's end begins while the start runs, the capture is ended as soon as it has started. The finished clip is moved to
    /// <paramref name="destination"/>, checked already, replacing a file there only with <paramref name="overwrite"/>.
    /// </summary>
    /// <exception cref="SessionException">The start is refused or failed; nothing is left running.</exception>
    public async Task<RealtimeStarted> StartRealtimeAsync(
        int fps,
        int maxSeconds,
        bool audio,
        OutputTarget? destination,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        RealtimeEnvironment environment = registry.Realtime;
        DateTimeOffset requested = environment.Time.GetUtcNow();
        (long hwnd, TaskCompletionSource settled, int startedSeconds) = ReserveRealtimeStart(requested);
        try
        {
            RealtimeTarget target = RealtimeRecording.Locate(environment, Name, hwnd);
            (int? audioPid, string? audioWarning) = RealtimeAudio.Choose(Name, audio, Mute, GameProcessId);
            RealtimeStart start = new(Name, ProjectDir, target, fps, maxSeconds, requested)
            {
                StartedSeconds = startedSeconds,
                AudioPid = audioPid,
                AudioWarning = audioWarning,
                Destination = destination,
                Overwrite = overwrite,
            };
            RealtimeRecording recording = await RealtimeRecording.StartAsync(environment, start, cancellationToken);
            bool ended;
            lock (_realtimeLock)
            {
                _realtime = recording;
                ended = _realtimeEndedWhileStarting;
                if (_realtimeExiting)
                {
                    recording.MarkExit();
                }
            }

            if (ended)
            {
                await recording.EndCaptureAsync();
            }

            return recording.Started;
        }
        finally
        {
            lock (_realtimeLock)
            {
                _realtimeStarting = null;
            }

            settled.TrySetResult();
        }
    }

    /// <summary>
    /// Ends the session's real-time recording, or takes the clip of one that ended on its own, and returns the clip, once:
    /// a stop_project, restart_project, detach_project or another stop that took it first leaves this one a refusal.
    /// </summary>
    /// <exception cref="SessionException">
    /// None was started, another call took the clip, or the recording failed: the message names the files kept, or is the
    /// error stop_project and list_sessions report for a finish that threw.
    /// </exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled; the recording finishes on its own and stays on the session.</exception>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A finish that threw, whatever the failure, is the stop's refusal with the error the session's end reports for it."
    )]
    public async Task<RealtimeStopped> StopRealtimeAsync(CancellationToken cancellationToken)
    {
        RealtimeRecording recording;
        lock (_realtimeLock)
        {
            recording =
                _realtime
                ?? throw new SessionException(
                    HasGame ? $"No real-time recording is running in session '{Name}'; record_mark start first." : NoWindowToRecord()
                );
        }

        RealtimeStopped stopped;
        try
        {
            stopped = await recording.StopAsync(cancellationToken);
        }
        catch (SessionException)
        {
            RefuseUnlessClaimed(recording);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RefuseUnlessClaimed(recording);
            throw new SessionException(DescribeFault(e), e);
        }

        RefuseUnlessClaimed(recording);
        return stopped;
    }

    /// <summary>
    /// Ends the session's real-time recording, a start still in flight included, and waits until its clip is finished or has
    /// failed, as the server's exit does so that no .mkv or deadline file outlives it; the exit marks the session first
    /// (<see cref="MarkRealtimeExit"/>), so the clip is delivered by rename only.
    /// </summary>
    /// <returns>The recording's outcome, its warnings included; null when the session has none.</returns>
    internal async Task<RealtimeOutcome?> FinishRealtimeAsync()
    {
        (RealtimeRecording? recording, Task starting) = MarkRealtimeEnded();
        if (recording is not null)
        {
            await recording.EndCaptureAsync();
        }

        await starting;
        lock (_realtimeLock)
        {
            recording = _realtime;
        }

        return recording is null ? null : await recording.Finished;
    }

    /// <summary>
    /// Marks the server's exit as begun for the session, at once and before anything is awaited: its recording, and one a start
    /// still in flight publishes later, deliver their clips by rename only (<see cref="RealtimeRecording.MarkExit"/>), so no
    /// copy across volumes begins that the exit could cut short.
    /// </summary>
    internal void MarkRealtimeExit()
    {
        lock (_realtimeLock)
        {
            _realtimeExiting = true;
            _realtime?.MarkExit();
        }
    }

    /// <summary>
    /// The session's real-time recording as list_sessions shows it: its path and startedSeconds while it runs, its clip or
    /// error once it has finished, until a stop takes it; null when there is none, and while a start is still in flight.
    /// </summary>
    private RecordingResult? RealtimeState
    {
        get
        {
            RealtimeRecording recording;
            lock (_realtimeLock)
            {
                if (_realtimeStarting is not null || _realtime is null)
                {
                    return null;
                }

                recording = _realtime;
            }

            Task<RealtimeOutcome> finished = recording.Finished;
            if (!finished.IsCompleted)
            {
                return new RecordingResult
                {
                    Mode = RealtimeRecording.Mode,
                    Path = recording.Started.Path,
                    Destination = recording.Started.Destination,
                    StartedSeconds = recording.StartedSeconds,
                };
            }

            return finished.IsCompletedSuccessfully
                ? DescribeRealtime(recording, finished.Result)
                : DescribeFailedFinish(recording, finished.Exception?.GetBaseException().Message ?? NoClip);
        }
    }

    /// <summary>
    /// Notes the session's game as launched or attached now, which a real-time recording's startedSeconds counts from, and
    /// lets real-time recordings start normally again after the end of the game before it.
    /// </summary>
    private void MarkGameStarted()
    {
        DateTimeOffset now = registry.Realtime.Time.GetUtcNow();
        lock (_realtimeLock)
        {
            _gameStartedAt = now;
            _realtimeSessionEnding = false;
        }
    }

    /// <summary>
    /// Ends a running real-time capture now, before the session quits or lets go of its game, so the clip ends at that call
    /// rather than at the window's teardown; a start still in flight is marked to end its capture as soon as it has started.
    /// </summary>
    private async Task EndRealtimeCaptureAsync()
    {
        (RealtimeRecording? recording, _) = MarkRealtimeEnded();
        if (recording is not null)
        {
            await recording.EndCaptureAsync();
        }
    }

    /// <summary>
    /// Ends the session's real-time recording, waits for its clip and takes the outcome off the session, as a stop, a restart
    /// and a detach do, so it is returned once: by this call, or by a record_mark stop that came first. A start still in flight
    /// is marked to end its capture as soon as it has started and is not waited for, and one asked for from now on is refused.
    /// A clip that could not be finished is the outcome's error; nothing is thrown.
    /// </summary>
    /// <returns>The outcome; null when there was none, or another call took it.</returns>
    private async Task<RecordingResult?> TakeRealtimeResultAsync()
    {
        (RealtimeRecording? recording, _) = MarkRealtimeEnded();
        if (recording is null)
        {
            return null;
        }

        RecordingResult result = await FinishRealtimeResultAsync(recording);
        return ClaimRealtime(recording) ? result : null;
    }

    /// <summary>Takes <paramref name="recording"/> off the session, unless another call has taken it already.</summary>
    /// <returns>Whether this call took it.</returns>
    private bool ClaimRealtime(RealtimeRecording recording)
    {
        lock (_realtimeLock)
        {
            if (!ReferenceEquals(_realtime, recording))
            {
                return false;
            }

            _realtime = null;
            return true;
        }
    }

    /// <summary>Takes a finished <paramref name="recording"/> off the session for record_mark stop.</summary>
    /// <exception cref="SessionException">Another call took its clip first.</exception>
    private void RefuseUnlessClaimed(RealtimeRecording recording)
    {
        if (!ClaimRealtime(recording))
        {
            throw new SessionException(
                $"The real-time recording's clip in session '{Name}' was already returned by another call (stop_project, restart_project, "
                    + "detach_project or another record_mark stop)."
            );
        }
    }

    /// <summary>
    /// Ends the session's real-time recording once its game has gone and waits for its clip, which stays on the session for
    /// list_sessions and the next stop.
    /// </summary>
    private async Task KeepRealtimeResultAsync()
    {
        (RealtimeRecording? recording, _) = MarkRealtimeEnded();
        if (recording is not null)
        {
            await FinishRealtimeResultAsync(recording);
        }
    }

    /// <summary>
    /// Ends <paramref name="recording"/>'s capture, if it still runs, gives its helper 10 s to exit before it is killed, and
    /// waits for its clip. A second call returns the same outcome and does no work. A finish that throws is logged, by the
    /// first call to see it, and reported as the outcome's error, so the session's end goes on.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A stop, restart or detach must report a recording whose finish failed, whatever the failure, not fail with it; "
            + "a cancellation too, since no token reaches this wait, so one can only come from a fault."
    )]
    private async Task<RecordingResult> FinishRealtimeResultAsync(RealtimeRecording recording)
    {
        try
        {
            if (recording.IsRunning)
            {
                await recording.EndCaptureWithinCeilingAsync();
            }

            return DescribeRealtime(recording, await recording.Finished);
        }
        catch (Exception e)
        {
            if (recording.TakeFaultLog())
            {
                Log.RealtimeFinishFailed(_logger, e, ProjectDir);
            }

            return DescribeFailedFinish(recording, DescribeFault(e));
        }
    }

    /// <summary>
    /// The error a stop, list_sessions and record_mark stop report for a finish that threw: its message, or no clip for a
    /// cancellation, whose message list_sessions cannot read back from the finish.
    /// </summary>
    private static string DescribeFault(Exception fault) => fault is OperationCanceledException ? NoClip : fault.Message;

    /// <summary>A finished real-time recording as the session's end reports it: its clip and warning, or why it has none.</summary>
    private static RecordingResult DescribeRealtime(RealtimeRecording recording, RealtimeOutcome outcome) =>
        outcome.Stopped is { } stopped
            ? new RecordingResult
            {
                Mode = RealtimeRecording.Mode,
                Clips = [stopped.Clip.Path],
                StartedSeconds = recording.StartedSeconds,
                Warning = stopped.Warning,
            }
            : DescribeFailedFinish(recording, outcome.Error ?? NoClip);

    /// <summary>A real-time recording with no clip, as the session's end reports it: when it started and why it has none.</summary>
    private static RecordingResult DescribeFailedFinish(RealtimeRecording recording, string error) =>
        new()
        {
            Mode = RealtimeRecording.Mode,
            StartedSeconds = recording.StartedSeconds,
            Error = error,
        };

    /// <summary>
    /// Marks the session's end as begun for its game, so a start asked for from now on is refused and one still in flight ends
    /// its capture as soon as it has started.
    /// </summary>
    /// <returns>The session's latest recording, and the start in flight's settling.</returns>
    private (RealtimeRecording? Recording, Task Starting) MarkRealtimeEnded()
    {
        lock (_realtimeLock)
        {
            _realtimeSessionEnding = true;
            if (_realtimeStarting is not null)
            {
                _realtimeEndedWhileStarting = true;
            }

            return (_realtime, _realtimeStartSettled);
        }
    }

    /// <summary>
    /// Refuses a start the session's state rules out, cheapest first, and marks one in flight.
    /// </summary>
    /// <returns>
    /// The game's window handle, what the start completes once it has settled, and the whole seconds from the game's launch or
    /// attach to <paramref name="now"/>.
    /// </returns>
    /// <exception cref="SessionException">
    /// No game, the session's end begun, a recording running or starting, no window, or a quiet session.
    /// </exception>
    private (long Hwnd, TaskCompletionSource Settled, int StartedSeconds) ReserveRealtimeStart(DateTimeOffset now)
    {
        lock (_realtimeLock)
        {
            if (!HasGame)
            {
                throw new SessionException(NoWindowToRecord());
            }

            if (_realtimeSessionEnding)
            {
                throw new SessionException(
                    $"Session '{Name}' is ending, so a real-time recording cannot start now; start it once the game has restarted."
                );
            }

            if ((_realtime is { IsRunning: true } running ? running.StartedAt : _realtimeStarting) is { } since)
            {
                int age = (int)Math.Max(0, (now - since).TotalSeconds);
                throw new SessionException(
                    $"A real-time recording is already running in session '{Name}' (started {age} s ago); record_mark stop first."
                );
            }

            if (WindowHandle is not (> 0 and long hwnd))
            {
                throw new SessionException($"Session '{Name}' is headless and has no window to record.");
            }

            if (Quiet)
            {
                throw new SessionException(
                    $"Session '{Name}' is quiet, so its window is on the server's hidden desktop, which a real-time recording cannot see. "
                        + "Launch it with options.quiet: false (options.mute: true keeps it silent), or with options.record for a Movie Maker "
                        + "recording."
                );
            }

            TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _realtimeStarting = now;
            _realtimeStartSettled = settled.Task;
            _realtimeEndedWhileStarting = false;
            return (hwnd, settled, (int)Math.Max(0, (now - _gameStartedAt).TotalSeconds));
        }
    }

    private string NoWindowToRecord() => $"Session '{Name}' is not running, so there is no window to record.";
}
