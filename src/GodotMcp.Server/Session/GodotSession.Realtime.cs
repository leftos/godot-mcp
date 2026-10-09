namespace GodotMcp.Server.Session;

/// <summary>
/// Recording the session's game window in real time: one recording at a time per session, started and stopped by
/// record_mark on a session not launched with options.record. A recording the session's end leaves running still finishes on
/// its own and keeps its file; one whose start was still in flight when the session ended is ended as soon as it has started.
/// </summary>
internal sealed partial class GodotSession
{
    private readonly Lock _realtimeLock = new();

    // The session's latest real-time recording, running or finished, until a stop takes its clip; under _realtimeLock.
    private RealtimeRecording? _realtime;

    // When a start still in flight was asked for; under _realtimeLock.
    private DateTimeOffset? _realtimeStarting;

    // Completes when the start in flight, if any, has settled, started or not; under _realtimeLock.
    private Task _realtimeStartSettled = Task.CompletedTask;

    // Whether the session's end came while the start in flight ran, so its capture ends as soon as it has started; under _realtimeLock.
    private bool _realtimeEndedWhileStarting;

    /// <summary>
    /// Starts a real-time recording of the game's window at <paramref name="fps"/>, ending on its own after
    /// <paramref name="maxSeconds"/>, and returns once its encoder has opened on the first frame. Every refusal comes before a
    /// process starts: no game, one running, no window, a quiet session, then what <see cref="RealtimeRecording.Locate"/>
    /// refuses. When the session ends while the start runs, the capture is ended as soon as it has started.
    /// </summary>
    /// <exception cref="SessionException">The start is refused or failed; nothing is left running.</exception>
    public async Task<RealtimeStarted> StartRealtimeAsync(int fps, int maxSeconds, CancellationToken cancellationToken)
    {
        RealtimeEnvironment environment = registry.Realtime;
        DateTimeOffset requested = environment.Time.GetUtcNow();
        (long hwnd, TaskCompletionSource settled) = ReserveRealtimeStart(requested);
        try
        {
            RealtimeTarget target = RealtimeRecording.Locate(environment, Name, hwnd);
            RealtimeRecording recording = await RealtimeRecording.StartAsync(
                environment,
                new RealtimeStart(Name, ProjectDir, target, fps, maxSeconds, requested),
                cancellationToken
            );
            bool ended;
            lock (_realtimeLock)
            {
                _realtime = recording;
                ended = _realtimeEndedWhileStarting;
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
    /// Ends the session's real-time recording, or takes the clip of one that ended on its own, and returns the clip.
    /// </summary>
    /// <exception cref="SessionException">None was started, or the recording failed; the message names the files kept.</exception>
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

        try
        {
            return await recording.StopAsync(cancellationToken);
        }
        finally
        {
            lock (_realtimeLock)
            {
                if (ReferenceEquals(_realtime, recording) && !recording.IsRunning)
                {
                    _realtime = null;
                }
            }
        }
    }

    /// <summary>
    /// Ends the session's real-time recording, a start still in flight included, and waits until its clip is finished or has
    /// failed, as the server's exit does so that no .mkv or deadline file outlives it.
    /// </summary>
    internal async Task FinishRealtimeAsync()
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

        if (recording is not null)
        {
            await recording.Finished;
        }
    }

    /// <summary>
    /// Ends a running real-time capture now, as the session lets go of a game that keeps running, and marks a start still in
    /// flight to end its capture as soon as it has started; the recording finishes on its own.
    /// </summary>
    private async Task EndRealtimeCaptureAsync()
    {
        (RealtimeRecording? recording, _) = MarkRealtimeEnded();
        if (recording is not null)
        {
            await recording.EndCaptureAsync();
        }
    }

    /// <summary>Marks a start in flight as ended with the session.</summary>
    /// <returns>The session's latest recording, and the start in flight's settling.</returns>
    private (RealtimeRecording? Recording, Task Starting) MarkRealtimeEnded()
    {
        lock (_realtimeLock)
        {
            if (_realtimeStarting is not null)
            {
                _realtimeEndedWhileStarting = true;
            }

            return (_realtime, _realtimeStartSettled);
        }
    }

    /// <summary>Refuses a start the session's state rules out, cheapest first, and marks one in flight.</summary>
    /// <returns>The game's window handle, and what the start completes once it has settled.</returns>
    /// <exception cref="SessionException">No game, a recording running or starting, no window, or a quiet session.</exception>
    private (long Hwnd, TaskCompletionSource Settled) ReserveRealtimeStart(DateTimeOffset now)
    {
        lock (_realtimeLock)
        {
            if (!HasGame)
            {
                throw new SessionException(NoWindowToRecord());
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
            return (hwnd, settled);
        }
    }

    private string NoWindowToRecord() => $"Session '{Name}' is not running, so there is no window to record.";
}
