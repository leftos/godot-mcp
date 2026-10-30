using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Tests.Wire;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace GodotMcp.Tests.Session;

/// <summary>
/// A real-time recording ended with its session: a stop ends the capture before the game is told to quit, a detach waits for
/// the clip, a game that quits keeps its clip for one stop, a clip that could not be finished is an error the stop reports,
/// what list_sessions shows, and the result's JSON. On fake games attached to a real registry whose real-time environment is
/// fake; a run's stop and restart need a real Godot and are integration tests (RealtimeRecordingTests).
/// </summary>
public sealed class RealtimeSessionEndTests : IAsyncDisposable
{
    private const string Game = "game";

    /// <summary>The destination the exit's tests name, which their rename finds on another volume.</summary>
    private const string ExitClip = "clips/x.mp4";

    /// <summary>The helper's last line when the window its hwnd named is gone, as when the game's process has ended.</summary>
    private const string NoWindow = "godot-mcp-capture: --hwnd 4242 names no window.";

    /// <summary>The start of the refusal of a capture whose helper exited with code 2 before its first frame.</summary>
    private const string HelperEndedFirst = "The capture helper exited with code 2 before its first frame, so the recording did not start: ";

    private const string MutedWarning = "no audio: session 'game' renders no sound (options.mute or a quiet run), so the clip has no audio track";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RegistryHarness _harness = new();
    private readonly FakeRealtime _fake = new();
    private readonly List<FakeBridge> _games = [];
    private readonly RuntimeTools _tools;

    public RealtimeSessionEndTests()
    {
        _harness.Sessions.Realtime = _fake.Environment;
        _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        foreach (FakeBridge game in _games)
        {
            game.Dispose();
        }

        _fake.Dispose();
    }

    [Fact]
    public async Task AStopEndsTheCaptureBeforeTheGameIsToldToQuitAndReturnsTheClip()
    {
        FakeBridge game = await AttachAsync();
        string path = await StartAsync();
        Task<bool> endedFirst = CaptureEndedAtFirstRequestAsync(game, Path.ChangeExtension(path, ".deadline"));

        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);

        Assert.True(await endedFirst, "the game was told to quit while the capture still ran");
        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal("realtime", recording.Mode);
        Assert.Equal(path, Assert.Single(recording.Clips!));
        Assert.Null(recording.Path);
        Assert.Null(recording.Error);
        Assert.True(File.Exists(path), $"the clip {path} was not finished when the stop returned");
    }

    [Fact]
    public async Task ADetachWaitsForTheClipAndReportsIt()
    {
        await AttachAsync();
        string path = await StartAsync();

        DetachResult detached = await _harness.Sessions.DetachAsync(Game, Token);

        RecordingResult recording = detached.Recording ?? throw new InvalidOperationException("detach_project returned no recording.");
        Assert.Equal("realtime", recording.Mode);
        Assert.Equal(path, Assert.Single(recording.Clips!));
        Assert.True(File.Exists(path), $"the clip {path} was not finished when the detach returned");
        Assert.False(File.Exists(Path.ChangeExtension(path, ".mkv")), "the .mkv was left behind");
    }

    [Fact]
    public async Task ADetachWithNoRecordingReportsNone()
    {
        await AttachAsync();

        DetachResult detached = await _harness.Sessions.DetachAsync(Game, Token);

        Assert.Null(detached.Recording);
        Assert.DoesNotContain("recording", JsonSerializer.Serialize(detached, Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGameThatQuitsKeepsItsClipForOneStop()
    {
        FakeBridge game = await AttachAsync();
        string path = await StartAsync();

        game.Dispose();
        await RegistryHarness.WaitUntilAsync(() => ListedRecording()?.Clips is not null);
        RecordingResult listed = ListedRecording()!;
        JsonNode taken = JsonNode.Parse(await _tools.RecordMarkAsync("stop", session: Game, cancellationToken: Token))!;
        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);

        Assert.Equal(path, Assert.Single(listed.Clips!));
        Assert.Equal(path, taken["clip"]!["path"]!.GetValue<string>());
        Assert.Null(stopped.Recording);
    }

    [Fact]
    public async Task AGameThatQuitsGivesItsClipToTheNextStop()
    {
        FakeBridge game = await AttachAsync();
        string path = await StartAsync();

        game.Dispose();
        await RegistryHarness.WaitUntilAsync(() => ListedRecording()?.Clips is not null);
        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);

        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal(path, Assert.Single(recording.Clips!));
    }

    [Fact]
    public async Task AClipThatCouldNotBeFinishedIsAnErrorAndTheDetachGoesOn()
    {
        _fake.FfmpegExit = 1;
        await AttachAsync();
        string path = await StartAsync();

        DetachResult detached = await _harness.Sessions.DetachAsync(Game, Token);

        RecordingResult recording = detached.Recording ?? throw new InvalidOperationException("detach_project returned no recording.");
        Assert.Equal("realtime", recording.Mode);
        Assert.Null(recording.Clips);
        Assert.StartsWith("ffmpeg exited with code 1 while it encoded the recording: ", recording.Error, StringComparison.Ordinal);
        Assert.Contains(Path.ChangeExtension(path, ".mkv"), recording.Error, StringComparison.Ordinal);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task ListSessionsShowsARunningRecordingWithItsStartFromTheAttach()
    {
        await AttachAsync();
        // Only a lower bound: a busy machine lengthens the gap between the attach and the start, never shortens it.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Token);
        string path = await StartAsync();

        RecordingResult running = ListedRecording() ?? throw new InvalidOperationException("list_sessions showed no recording.");
        DetachResult detached = await _harness.Sessions.DetachAsync(Game, Token);

        Assert.Equal(
            new RecordingResult
            {
                Mode = "realtime",
                Path = path,
                StartedSeconds = running.StartedSeconds,
            },
            running
        );
        Assert.True(running.StartedSeconds >= 1, $"startedSeconds is {running.StartedSeconds}; the start came over 1 s after the attach");
        Assert.Equal(running.StartedSeconds, detached.Recording?.StartedSeconds);
    }

    [Fact]
    public async Task ListSessionsShowsNoRecordingWhileItsStartIsInFlight()
    {
        await AttachAsync();
        TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource listed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeProbe = async _ =>
        {
            probing.TrySetResult();
            await listed.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        };
        Task<string> start = _tools.RecordMarkAsync("start", cancellationToken: Token);
        await probing.Task.WaitAsync(RegistryHarness.HangGuard, Token);

        RecordingResult? whileStarting = ListedRecording();
        listed.TrySetResult();
        await start;

        Assert.Null(whileStarting);
        Assert.Equal("realtime", ListedRecording()?.Mode);
    }

    [Fact]
    public async Task AStartWhoseGameExitsBeforeTheCaptureSaysTheGameExited()
    {
        Process game = StartGameThatCrashesWhenToldTo();
        try
        {
            await AttachToGameAsync(new AttachRequest(_harness.Project(Game), Game, RegistryHarness.LongWait, false, false, null), game);
            HelperEndsWithNoWindow();
            _fake.BeforeProbe = async _ =>
            {
                game.StandardInput.Close();
                await game.WaitForExitAsync(Token).WaitAsync(RegistryHarness.HangGuard, Token);
            };

            SessionException refused = await Assert.ThrowsAsync<SessionException>(StartDirectlyAsync);

            Assert.StartsWith(HelperEndedFirst, refused.Message, StringComparison.Ordinal);
            Assert.Contains(NoWindow, refused.Message, StringComparison.Ordinal);
            Assert.EndsWith("-capture.log. The game exited during the start (code -1073741819).", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            RegistryHarness.EndStandInGame(game);
        }
    }

    [Fact]
    public async Task AStartWhoseGameStillRunsIsRefusedWithTheHelpersMessageUnchanged()
    {
        await AttachWithPidAsync(new AttachRequest(_harness.Project(Game), Game, RegistryHarness.LongWait, false, false, null));
        HelperEndsWithNoWindow();

        SessionException refused = await Assert.ThrowsAsync<SessionException>(StartDirectlyAsync);

        Assert.StartsWith(HelperEndedFirst, refused.Message, StringComparison.Ordinal);
        Assert.Contains(NoWindow, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("-capture.log.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartAskedForWhileADetachWaitsForTheClipIsRefusedOrEndedAndNotReported()
    {
        await AttachAsync();
        string first = await StartAsync();
        GodotSession session = _harness.Sessions.Resolve(Game);
        (TaskCompletionSource remuxing, TaskCompletionSource release) = HoldRemux();
        // A record_mark stop ends the capture and waits on the clip before the detach does, and the second start is asked for
        // as soon as it returns: after the first recording has finished, most often while the detach is still taking the clip,
        // which refuses it as the session is ending, else once the detach has let go of the game. One that came before the
        // detach began is ended as it starts.
        Task<RealtimeStopped> stopMark = await RunUntilItWaitsAsync(() => session.StopRealtimeAsync(Token));
        Task<RealtimeStarted> second = stopMark
            .ContinueWith(
                _ => session.StartRealtimeAsync(30, 600, audio: true, destination: null, overwrite: false, Token),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            .Unwrap();
        await remuxing.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        Task<DetachResult> detach = await RunUntilItWaitsAsync(() => _harness.Sessions.DetachAsync(Game, Token));

        release.TrySetResult();
        await detach;
        // The first clip goes to whichever of the two calls takes it first; ARecordMarkStopAndAStopProjectAtOnceReturnTheClipOnce
        // checks that it goes to one only.
        Exception? stopped = await Record.ExceptionAsync(() => stopMark);
        Exception? refused = await Record.ExceptionAsync(() => second);

        Assert.True(stopped is null or SessionException, $"record_mark stop failed: {stopped}");
        if (refused is null)
        {
            string secondPath = (await second).Path;
            Assert.NotEqual(first, secondPath);
            // Its capture was ended as it started, so its clip is finished within moments rather than at maxSeconds.
            await RegistryHarness.WaitUntilAsync(() => File.Exists(secondPath));
        }
        else
        {
            string[] refusals =
            [
                "Session 'game' is ending, so a real-time recording cannot start now; start it once the game has restarted.",
                "Session 'game' is not running, so there is no window to record.",
            ];
            Assert.Contains(refused.Message, refusals);
        }
    }

    [Fact]
    public async Task ARecordMarkStopAndAStopProjectAtOnceReturnTheClipOnce()
    {
        FakeBridge game = await AttachAsync();
        string path = await StartAsync();
        (TaskCompletionSource remuxing, TaskCompletionSource release) = HoldRemux();
        game.Dispose();
        await remuxing.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        // Both wait on the clip when it is finished; which takes it first is the thread pool's choice.
        Task<StopResult> stop = await RunUntilItWaitsAsync(() => _harness.Sessions.StopAsync(Game, Token));
        Task<string> mark = await RunUntilItWaitsAsync(() => _tools.RecordMarkAsync("stop", session: Game, cancellationToken: Token));

        release.TrySetResult();
        StopResult stopped = await stop;
        Exception? refused = await Record.ExceptionAsync(() => mark);

        if (stopped.Recording is { } recording)
        {
            Assert.Equal(path, Assert.Single(recording.Clips!));
            Assert.Equal(
                "The real-time recording's clip in session 'game' was already returned by another call (stop_project, "
                    + "restart_project, detach_project or another record_mark stop).",
                Assert.IsType<McpException>(refused).Message
            );
        }
        else
        {
            Assert.Null(refused);
            Assert.Equal(path, JsonNode.Parse(await mark)!["clip"]!["path"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task AStopWhoseClipCouldNotBeFinishedReportsTheErrorAndStillStops()
    {
        const string Broke = "the remux broke in a way the recording does not expect";
        _fake.RemuxFailure = new InvalidOperationException(Broke);
        FakeBridge game = await AttachAsync();
        await StartAsync();
        Task quit = QuitOnFirstRequestAsync(game);

        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);
        await quit;

        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal("realtime", recording.Mode);
        Assert.Null(recording.Clips);
        Assert.NotNull(recording.StartedSeconds);
        Assert.Equal(Broke, recording.Error);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task AFinishThatThrowsShowsAsAnErrorInListSessionsUntilTheNextStop()
    {
        const string Broke = "the remux broke in a way the recording does not expect";
        _fake.RemuxFailure = new InvalidOperationException(Broke);
        FakeBridge game = await AttachAsync();
        await StartAsync();

        game.Dispose();
        await RegistryHarness.WaitUntilAsync(() => ListedRecording()?.Error is not null);
        RecordingResult listed = ListedRecording()!;
        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);

        Assert.Equal(Broke, listed.Error);
        Assert.Equal(listed, stopped.Recording);
    }

    [Fact]
    public async Task TheExitDeliversARunningRecordingsClipByRenameOnly()
    {
        await AttachAsync();
        _fake.Rename = (_, _, _) => false;
        await _tools.RecordMarkAsync("start", new RecordMarkOptions(Path: ExitClip), session: Game, cancellationToken: Token);
        GodotSession session = _harness.Sessions.Resolve(Game);

        session.MarkRealtimeExit();
        RealtimeOutcome? outcome = await session.FinishRealtimeAsync().WaitAsync(RegistryHarness.HangGuard, Token);

        AssertKeptForTheExit(session, outcome);
    }

    [Fact]
    public async Task TheExitDeliversTheClipOfAStartStillInFlightByRenameOnly()
    {
        await AttachAsync();
        _fake.Rename = (_, _, _) => false;
        TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource marked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeProbe = async _ =>
        {
            probing.TrySetResult();
            await marked.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        };
        Task<string> start = _tools.RecordMarkAsync("start", new RecordMarkOptions(Path: ExitClip), session: Game, cancellationToken: Token);
        await probing.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        GodotSession session = _harness.Sessions.Resolve(Game);

        session.MarkRealtimeExit();
        Task<RealtimeOutcome?> finish = session.FinishRealtimeAsync();
        marked.TrySetResult();
        await start;
        RealtimeOutcome? outcome = await finish.WaitAsync(RegistryHarness.HangGuard, Token);

        AssertKeptForTheExit(session, outcome);
    }

    [Fact]
    public async Task AStopDuringAStartReportsNoRecordingAndEndsTheCapture()
    {
        FakeBridge game = await AttachAsync();
        TaskCompletionSource probing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeProbe = async _ =>
        {
            probing.TrySetResult();
            await stopped.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        };
        Task<string> start = _tools.RecordMarkAsync("start", session: Game, cancellationToken: Token);
        await probing.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        Task quit = QuitOnFirstRequestAsync(game);

        StopResult result = await _harness.Sessions.StopAsync(Game, Token);
        stopped.TrySetResult();
        string path = JsonNode.Parse(await start)!["path"]!.GetValue<string>();
        await quit;

        Assert.Null(result.Recording);
        await RegistryHarness.WaitUntilAsync(() => File.Exists(path));
    }

    [Fact]
    public async Task AStopDuringItsEndRefusesAStartAndStillReportsTheClipThatEndedOnItsOwn()
    {
        FakeBridge game = await AttachAsync();
        string path = await StartAsync();
        // Set once the start has returned, so the helper ends on its own at its next frame, as one whose window closed does.
        _fake.HelperFrameLimit = 0;
        await RegistryHarness.WaitUntilAsync(() => ListedRecording()?.Clips is not null);
        Task<string?> startDuringEnd = StartOnFirstRequestAsync(game);

        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);

        Assert.Equal(
            "Session 'game' is ending, so a real-time recording cannot start now; start it once the game has restarted.",
            await startDuringEnd
        );
        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Equal(path, Assert.Single(recording.Clips!));
    }

    [Fact]
    public async Task AStopWhoseFinishIsCancelledByAFaultReportsAnErrorAndStillStops()
    {
        // Nothing on the finish's path takes a token, so a cancellation there is a fault like any other.
        _fake.RemuxFailure = new OperationCanceledException("a fault cancelled the remux");
        FakeBridge game = await AttachAsync();
        await StartAsync();
        Task quit = QuitOnFirstRequestAsync(game);

        StopResult stopped = await _harness.Sessions.StopAsync(Game, Token);
        await quit;

        RecordingResult recording = stopped.Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
        Assert.Null(recording.Clips);
        Assert.Equal("The real-time recording ended without a clip.", recording.Error);
        Assert.Empty(_harness.Sessions.List(includeStopped: true));
    }

    [Fact]
    public async Task ARecordMarkStopWhoseFinishThrowsIsRefusedWithItsErrorOnce()
    {
        const string Broke = "the remux broke in a way the recording does not expect";
        _fake.RemuxFailure = new InvalidOperationException(Broke);
        await AttachAsync();
        await StartAsync();
        GodotSession session = _harness.Sessions.Resolve(Game);

        Exception? first = await Record.ExceptionAsync(() => session.StopRealtimeAsync(Token));
        Exception? second = await Record.ExceptionAsync(() => session.StopRealtimeAsync(Token));

        Assert.Equal(Broke, Assert.IsType<SessionException>(first).Message);
        // The first stop took the recording off the session, so the second finds none, as one after a clip does.
        Assert.Equal(
            "No real-time recording is running in session 'game'; record_mark start first.",
            Assert.IsType<SessionException>(second).Message
        );
    }

    [Fact]
    public async Task AStopKillsAHelperThatHasNotExitedTenSecondsAfterItsCaptureEnded()
    {
        FakeTimeProvider clockTime = new();
        TimerWatch watched = new(clockTime);
        using FakeRealtime fake = new(watched);
        _harness.Sessions.Realtime = fake.Environment;
        FakeBridge game = await AttachAsync();
        // At 1 fps the fake's frames never fall below the rate a low-rate warning needs, and no audio is asked for, so the kill's
        // is the only warning.
        string answer = await _tools.RecordMarkAsync("start", new RecordMarkOptions(Fps: 1, Audio: false), Game, Token);
        string path = JsonNode.Parse(answer)!["path"]!.GetValue<string>();
        fake.HelperIgnoresDeadline = true;
        // The start's timers are all made by now, so the next timer made on the fake clock is the ceiling's.
        Task armed = watched.NextTimerAsync();
        Task quit = QuitOnFirstRequestAsync(game);
        try
        {
            Task<StopResult> stop = _harness.Sessions.StopAsync(Game, Token);
            // The fake clock stands still until the ceiling is armed, so every second it advances counts against the ceiling. A
            // stop that kills without arming one returns first.
            await Task.WhenAny(armed, stop);
            DateTimeOffset armedAt = clockTime.GetUtcNow();
            while (!stop.IsCompleted)
            {
                clockTime.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Delay(10, Token);
            }

            RecordingResult recording = (await stop).Recording ?? throw new InvalidOperationException("stop_project returned no recording.");
            TimeSpan killedAfter = (fake.HelperKilledAt ?? throw new InvalidOperationException("the helper was never killed.")) - armedAt;
            // Only the ceiling kills at or after it ends; a kill before it is another path's, such as a deadline move the
            // helper's read of the file refused, which the warning names.
            Assert.True(
                killedAfter >= TimeSpan.FromSeconds(10),
                $"the helper was killed {killedAfter.TotalSeconds} s after its ceiling was armed: {recording.Warning}"
            );
            Assert.Equal(path, Assert.Single(recording.Clips!));
            Assert.Equal(
                "the capture helper did not exit within 10 s of the recording's end and was killed; the clip may end earlier or later "
                    + "than the recording did",
                recording.Warning
            );
        }
        finally
        {
            // A helper the stop never killed is let go here, so the session's end finishes and the test's cleanup does not hang.
            fake.HelperIgnoresDeadline = false;
            await quit;
        }
    }

    [Fact]
    public async Task AnAttachAskedToMuteRecordsNoTrackAndSaysWhy()
    {
        AttachRequest request = new(_harness.Project(Game), Game, RegistryHarness.LongWait, false, false, null) { Mute = true };
        (bool toldToMute, _) = await AttachWithPidAsync(request);

        JsonNode started = await StartAndStopAsync();

        Assert.True(toldToMute, "the attach file did not tell the game to mute");
        AssertNoTrack(started, MutedWarning);
    }

    [Fact]
    public async Task AnAttachOnAFolderArmedMutedRecordsNoTrackAndSaysWhy()
    {
        string project = _harness.Project(Game);
        await _harness.Sessions.ArmAsync(project, new ArmSettings(Quiet: false, ShutOutRealGamepads: false, Mute: true), Token);
        (bool toldToMute, _) = await AttachWithPidAsync(new AttachRequest(project, Game, RegistryHarness.LongWait, null, null, null));

        JsonNode started = await StartAndStopAsync();

        Assert.True(toldToMute, "the attach file did not tell the game to mute");
        AssertNoTrack(started, MutedWarning);
    }

    [Fact]
    public async Task AGameWhoseHelloHasNoPidRecordsNoTrackAndSaysWhy()
    {
        await AttachAsync();

        JsonNode started = await StartAndStopAsync();

        AssertNoTrack(started, "no audio: the game's bridge sent no process id, so the clip has no audio track");
    }

    [Fact]
    public async Task AGameWhoseHelloHasAPidRecordsThatProcessesSound()
    {
        (bool toldToMute, int pid) = await AttachWithPidAsync(
            new AttachRequest(_harness.Project(Game), Game, RegistryHarness.LongWait, false, false, null)
        );

        JsonNode started = await StartAndStopAsync();

        Assert.False(toldToMute, "the attach file told a game that was not asked to mute to mute");
        Assert.True(started["audio"]!.GetValue<bool>(), $"the start has no audio track: {started.ToJsonString()}");
        Assert.Null(started["warning"]);
        List<string> capture = [.. Assert.Single(_fake.Starts, request => request.Arguments.Contains("--until-file")).Arguments];
        Assert.Equal(pid.ToString(CultureInfo.InvariantCulture), capture[capture.IndexOf("--audio-pid") + 1]);
    }

    [Fact]
    public void AMovieResultCarriesItsModeAndNoRealtimeField()
    {
        Assert.Equal(
            """{"mode":"movie","path":"m.avi"}""",
            JsonSerializer.Serialize(new RecordingResult { Mode = Recording.MovieMode, Path = "m.avi" }, Json)
        );
        Assert.Equal(
            """{"mode":"movie","clips":["a.mp4"],"error":"e"}""",
            JsonSerializer.Serialize(
                new RecordingResult
                {
                    Mode = Recording.MovieMode,
                    Clips = ["a.mp4"],
                    Error = "e",
                },
                Json
            )
        );
    }

    [Fact]
    public void ARealtimeResultLeavesOutWhatItDoesNotHave()
    {
        Assert.Equal(
            """{"mode":"realtime","clips":["c.mp4"],"startedSeconds":3}""",
            JsonSerializer.Serialize(
                new RecordingResult
                {
                    Mode = "realtime",
                    Clips = ["c.mp4"],
                    StartedSeconds = 3,
                },
                Json
            )
        );
        Assert.Equal(
            """{"mode":"realtime","clips":["c.mp4"],"startedSeconds":3,"warning":"w"}""",
            JsonSerializer.Serialize(
                new RecordingResult
                {
                    Mode = "realtime",
                    Clips = ["c.mp4"],
                    StartedSeconds = 3,
                    Warning = "w",
                },
                Json
            )
        );
        Assert.Equal(
            """{"mode":"realtime","startedSeconds":3,"error":"e"}""",
            JsonSerializer.Serialize(
                new RecordingResult
                {
                    Mode = "realtime",
                    StartedSeconds = 3,
                    Error = "e",
                },
                Json
            )
        );
        Assert.Equal(
            """{"mode":"realtime","path":"r.mp4","startedSeconds":0}""",
            JsonSerializer.Serialize(
                new RecordingResult
                {
                    Mode = "realtime",
                    Path = "r.mp4",
                    StartedSeconds = 0,
                },
                Json
            )
        );
    }

    /// <summary>
    /// Reads the first request the server sends the game after the stop began, then closes the game as one that quit does:
    /// true when the capture had already been ended by then, its deadline moved to the past or its recording finished.
    /// </summary>
    private static async Task<bool> CaptureEndedAtFirstRequestAsync(FakeBridge game, string deadline)
    {
        using (game)
        {
            await game.ReadRequestAsync(Token);
            return !FakeRealtime.TryReadDeadline(deadline, out DateTimeOffset at) || at <= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Reads the first request the server sends the game, then closes the game as one that quit does.</summary>
    private static async Task QuitOnFirstRequestAsync(FakeBridge game)
    {
        using (game)
        {
            await game.ReadRequestAsync(Token);
        }
    }

    /// <summary>
    /// Reads the first request the server sends the game, asks for a record_mark start while the game is still there, then
    /// closes the game as one that quit does.
    /// </summary>
    /// <returns>The start's refusal; null when it started.</returns>
    private async Task<string?> StartOnFirstRequestAsync(FakeBridge game)
    {
        using (game)
        {
            await game.ReadRequestAsync(Token);
            Exception? refused = await Record.ExceptionAsync(() => _tools.RecordMarkAsync("start", session: Game, cancellationToken: Token));
            return refused?.Message;
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> on the thread pool, where no synchronization context is, up to its first wait, and returns
    /// its task: its wait is registered by then, and resumes on the thread that completes what it waits on, in the order the
    /// waits were registered.
    /// </summary>
    private static Task<T> RunUntilItWaitsAsync<T>(Func<T> call) =>
        Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    /// <summary>Holds the remux, the recording's last step, until the test releases it.</summary>
    /// <returns>What completes once the remux is held, and what releases it.</returns>
    private (TaskCompletionSource Remuxing, TaskCompletionSource Release) HoldRemux()
    {
        TaskCompletionSource remuxing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.BeforeRemux = async () =>
        {
            remuxing.TrySetResult();
            await release.Task.WaitAsync(RegistryHarness.HangGuard, Token);
        };
        return (remuxing, release);
    }

    /// <summary>The exit's outcome: the clip kept in recordings/ with the refusal to copy it to another volume, nothing at its destination.</summary>
    private static void AssertKeptForTheExit(GodotSession session, RealtimeOutcome? outcome)
    {
        RealtimeStopped stopped = outcome?.Stopped ?? throw new InvalidOperationException($"the exit finished no clip: {outcome?.Error}");
        string destination = Path.Combine(session.ProjectDir, "clips", "x.mp4");
        Assert.Contains(
            $"the clip could not be moved to {destination}: {stopped.Clip.Path} is on another volume than {destination}; "
                + "the server's exit leaves no time to copy it",
            stopped.Warning ?? "",
            StringComparison.Ordinal
        );
        Assert.True(File.Exists(stopped.Clip.Path), $"the kept clip {stopped.Clip.Path} is gone");
        Assert.False(File.Exists(destination), $"the clip was copied to {destination}");
    }

    private async Task<string> StartAsync() =>
        JsonNode.Parse(await _tools.RecordMarkAsync("start", cancellationToken: Token))!["path"]!.GetValue<string>();

    private RecordingResult? ListedRecording() => Assert.Single(_harness.Sessions.List(includeStopped: true)).Recording;

    private async Task<FakeBridge> AttachAsync()
    {
        FakeBridge game = await _harness.AttachFakeGameAsync(_harness.Project(Game), Game, new FakeHello { WindowHandle = FakeRealtime.Hwnd });
        _games.Add(game);
        return game;
    }

    /// <summary>
    /// Attaches as <paramref name="request"/> asks to a fake game whose hello carries the fake window and the pid of a stand-in
    /// game the harness ends.
    /// </summary>
    /// <returns>Whether the attach file the game read told it to mute, and the pid its hello carried.</returns>
    private Task<(bool ToldToMute, int Pid)> AttachWithPidAsync(AttachRequest request) => AttachToGameAsync(request, _harness.StartOwnedGame());

    /// <summary>
    /// Attaches as <paramref name="request"/> asks to a fake game whose hello carries the fake window and
    /// <paramref name="standIn"/>'s pid.
    /// </summary>
    /// <returns>Whether the attach file the game read told it to mute, and the pid its hello carried.</returns>
    private async Task<(bool ToldToMute, int Pid)> AttachToGameAsync(AttachRequest request, Process standIn)
    {
        string attachFile = AttachFile.PathIn(request.ProjectPath);
        Task<AttachResult> attach = _harness.Sessions.AttachAsync(request, Token);
        await RegistryHarness.WaitUntilAsync(() => File.Exists(attachFile), attach);
        JsonNode written = JsonNode.Parse(File.ReadAllText(attachFile))!;
        int pid = standIn.Id;
        FakeHello hello = new() { ProcessId = pid, WindowHandle = FakeRealtime.Hwnd };
        FakeBridge game = await FakeBridge.DialAsync(_harness.Listener.Port, written["token"]!.GetValue<string>(), request.ProjectPath, hello, Token);
        _games.Add(game);
        await attach;
        return (written["mute"]!.GetValue<bool>(), pid);
    }

    /// <summary>
    /// A stand-in game that runs until its standard input is closed, then exits with an access violation's code
    /// (0xC0000005), as a game that crashed does.
    /// </summary>
    private static Process StartGameThatCrashesWhenToldTo() =>
        Process.Start(
            new ProcessStartInfo("cmd.exe", "/c set /p _= & exit -1073741819")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            }
        )!;

    /// <summary>The capture helper exits with code 2 before its first frame, saying the window it was given is gone.</summary>
    private void HelperEndsWithNoWindow()
    {
        _fake.HelperFrameLimit = 0;
        _fake.HelperExit = 2;
        _fake.HelperLines = [NoWindow];
    }

    /// <summary>A real-time start on the session itself, whose refusal is the <see cref="SessionException"/> it throws.</summary>
    private Task<RealtimeStarted> StartDirectlyAsync() =>
        _harness.Sessions.Resolve(Game).StartRealtimeAsync(30, 600, audio: false, destination: null, overwrite: false, Token);

    /// <summary>A record_mark start with the default options, then its stop.</summary>
    /// <returns>The start's result.</returns>
    private async Task<JsonNode> StartAndStopAsync()
    {
        JsonNode started = JsonNode.Parse(await _tools.RecordMarkAsync("start", session: Game, cancellationToken: Token))!;
        await _tools.RecordMarkAsync("stop", session: Game, cancellationToken: Token);
        return started;
    }

    /// <summary>Asserts the start has no audio track, says <paramref name="warning"/>, and gave the helper no game to record.</summary>
    private void AssertNoTrack(JsonNode started, string warning)
    {
        Assert.False(started["audio"]!.GetValue<bool>(), $"the start has an audio track: {started.ToJsonString()}");
        Assert.Equal(warning, started["warning"]?.GetValue<string>());
        Assert.DoesNotContain(_fake.Starts, request => request.Arguments.Contains("--audio-pid"));
    }

    /// <summary>A fake clock that says when the next timer is made on it, once that timer is made.</summary>
    private sealed class TimerWatch(FakeTimeProvider time) : TimeProvider
    {
        private TaskCompletionSource? _next;

        public override long TimestampFrequency => time.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

        /// <summary>Completes once the next timer is made on this clock.</summary>
        public Task NextTimerAsync()
        {
            TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _next, next);
            return next.Task;
        }

        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override long GetTimestamp() => time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = time.CreateTimer(callback, state, dueTime, period);
            Interlocked.Exchange(ref _next, null)?.TrySetResult();
            return timer;
        }
    }
}
