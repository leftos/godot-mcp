using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>
/// Recording what the game shows: marks on a Movie Maker recording run, whose start-stop pairs become clips once the run ends,
/// or a real-time recording of any other windowed session's window between a start and a stop.
/// </summary>
internal sealed partial class RuntimeTools
{
    private const int MaxRecordFps = 60;
    private const int DefaultRecordFps = 30;
    private const int MaxRecordSeconds = 600;
    private const string ClipExtension = ".mp4";

    private static readonly TimeSpan MovieFrameTimeout = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = "record_mark", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Starts or stops a recording of the game. In a session launched with options.record (Movie Maker), it marks a clip's "
            + "start or stop at the movie frame the game has reached: when the run ends (stop_project, the game quitting, "
            + "restart_project) each start-stop pair is cut from the movie as its own file, and the full movie is deleted once "
            + "every clip is cut; a start left open clips to the end; marks alternate start, stop, start, stop. It returns {mark, "
            + "mode: movie, frame, seconds}, frame counted from the movie's first and seconds = frame / 60; a mark made while the run "
            + "is ending is not kept. In any other session with a window, start records the window in real time, as a player sees "
            + "it, until stop or options.maxSeconds, and answers once the first frame is in: {mark, mode: realtime, path, width, "
            + "height, fps, audio, encoder, warning?}, path being the .mp4 it records to under .godot/godot-mcp/recordings/ "
            + "(clip.path names where the file is once finished: the destination once delivered there) and audio whether it has the game's "
            + "own sound as its audio track (options.audio; none, with a warning, when the session is muted or the sound cannot be "
            + "captured). stop ends it and returns {mark, mode, clip: {path, seconds, frames, averageFps, width, height, audio}, "
            + "warning?}. The session's end ends a running one at that call "
            + "and reports its clip as recording {mode: realtime, clips: [path], startedSeconds, warning?, error?}: stop_project "
            + "and detach_project, and restart_project as previousRecording. One that ended on its own (maxSeconds, or the game "
            + "quitting) is returned once, by whichever comes first of record_mark stop, stop_project, detach_project and "
            + "restart_project (as previousRecording). One real-time recording "
            + "runs per session. The window must stay unminimized (a minimized window holds its "
            + "last frame); quiet and headless sessions are refused, and it needs ffmpeg. On either kind, start's options.path names "
            + "where the finished clip is moved (options.overwrite replaces a file there), and the start's result gains destination, "
            + "the full path; a clip whose move is refused when it is delivered stays under .godot/godot-mcp/recordings/ with a "
            + "warning, and every clip path reported names where the file is. A Movie Maker start mark takes only path and overwrite."
    )]
    public async Task<string> RecordMarkAsync(
        [Description("start or stop.")] string mark,
        [Description(
            "On start only: a real-time recording's frame rate, length cap and audio track, and for either kind the path the clip is "
                + "moved to and whether it may replace a file there; a Movie Maker start mark takes only path and overwrite."
        )]
            RecordMarkOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        RecordMarkOptions? started = CheckMarkOptions(mark, options);
        GodotSession target = Find(session);
        if (target.ActiveRecording is { } recording)
        {
            CheckMovieOptions(started);
            return await MarkMovieAsync(target, recording, mark, started, cancellationToken);
        }

        try
        {
            object marked = started is not null
                ? await target.StartRealtimeAsync(
                    started.Fps ?? DefaultRecordFps,
                    started.MaxSeconds ?? MaxRecordSeconds,
                    started.Audio ?? true,
                    ResolveClipTarget(target, started),
                    started.Overwrite,
                    cancellationToken
                )
                : await target.StopRealtimeAsync(cancellationToken);
            return JsonSerializer.Serialize(marked, Json);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The mark and its options checked before any session is looked at.</summary>
    /// <returns>A start's options, none set when none were given; null for a stop.</returns>
    /// <exception cref="McpException">The mark is neither start nor stop, a stop has options, or an option is out of range.</exception>
    internal static RecordMarkOptions? CheckMarkOptions(string mark, RecordMarkOptions? options)
    {
        if (mark is not (Recording.StartMark or Recording.StopMark))
        {
            throw new McpException($"mark must be \"start\" or \"stop\"; got \"{mark}\".");
        }

        if (mark == Recording.StopMark)
        {
            return options is null ? null : throw new McpException("options belong to record_mark start; stop takes none.");
        }

        RecordMarkOptions checkedOptions = options ?? new RecordMarkOptions();
        if (checkedOptions is { Overwrite: true, Path: null })
        {
            throw new McpException("options.overwrite needs options.path");
        }

        if (checkedOptions.Fps is < 1 or > MaxRecordFps)
        {
            throw new McpException($"fps must be 1 to {MaxRecordFps}; got {checkedOptions.Fps}.");
        }

        if (checkedOptions.MaxSeconds is < 1 or > MaxRecordSeconds)
        {
            throw new McpException($"maxSeconds must be 1 to {MaxRecordSeconds}; got {checkedOptions.MaxSeconds}.");
        }

        return checkedOptions;
    }

    /// <summary>A Movie Maker start mark's options checked: only a path and overwrite; a stop's are null here.</summary>
    /// <exception cref="McpException">The start sets fps, maxSeconds or audio, which belong to a real-time recording.</exception>
    internal static void CheckMovieOptions(RecordMarkOptions? started)
    {
        if (started is { Fps: not null } or { MaxSeconds: not null } or { Audio: not null })
        {
            throw new McpException(
                "a Movie Maker recording's start mark takes only options.path and options.overwrite; "
                    + "fps, maxSeconds and audio set a real-time recording"
            );
        }
    }

    /// <summary>The destination a start's options name, checked before anything starts; null without a path.</summary>
    /// <exception cref="McpException">The path breaks the output rule, or names an existing file without overwrite.</exception>
    private static OutputTarget? ResolveClipTarget(GodotSession target, RecordMarkOptions? started)
    {
        if (started?.Path is not { } path)
        {
            return null;
        }

        var places = OutputPlaces.Find(target.ProjectDir, target.Logger);
        return OutputDestination.Resolve(places, path, ClipExtension, started.Overwrite);
    }

    /// <summary>
    /// Marks the Movie Maker recording at the movie frame the game has reached; a start with a path has it checked before the
    /// game is asked for the frame, and keeps it for its clip.
    /// </summary>
    private static Task<string> MarkMovieAsync(
        GodotSession target,
        Recording recording,
        string mark,
        RecordMarkOptions? started,
        CancellationToken cancellationToken
    ) =>
        MarkMovieAsync(
            target,
            recording,
            mark,
            started,
            token => CallWithErrorsAsync(target, new BridgeCall("record_mark", "movie_frame", [], MovieFrameTimeout), token),
            target.IsRequestPending,
            cancellationToken
        );

    /// <summary>
    /// Marks with <paramref name="requestFrame"/>, which asks the game for the movie frame it has reached, after closing at that
    /// frame each request the reply lists as unanswered that <paramref name="isPending"/> says the server still awaits.
    /// </summary>
    internal static async Task<string> MarkMovieAsync(
        GodotSession target,
        Recording recording,
        string mark,
        RecordMarkOptions? started,
        Func<CancellationToken, Task<BridgeResult>> requestFrame,
        Func<long, bool> isPending,
        CancellationToken cancellationToken
    )
    {
        OutputTarget? destination = CheckedClipTarget(target, recording, started);
        BridgeResult result = await requestFrame(cancellationToken);
        long frame = ReadMovieFrame(result.Reply);
        CloseOpenCalls(recording, result.Reply?["open"], frame, isPending);
        try
        {
            MarkResult marked = recording.Mark(mark, frame, destination, started?.Overwrite ?? false);
            JsonObject reply = JsonSerializer.SerializeToNode(marked, Json)!.AsObject();
            return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The destination a Movie Maker start's options name, resolved and checked against the recording's earlier clips.</summary>
    /// <exception cref="McpException">The path breaks the output rule, names an existing file without overwrite, or is where an
    /// earlier clip of <paramref name="recording"/> goes.</exception>
    private static OutputTarget? CheckedClipTarget(GodotSession target, Recording recording, RecordMarkOptions? started)
    {
        OutputTarget? destination = ResolveClipTarget(target, started);
        try
        {
            recording.CheckDestination(destination);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }

        return destination;
    }

    /// <summary>The frame in the bridge's movie_frame reply, a whole number that GDScript's JSON may send as a float.</summary>
    /// <exception cref="McpException">The reply has no whole, non-negative frame.</exception>
    internal static long ReadMovieFrame(JsonNode? reply) =>
        WholeFrame(reply?["frame"]) ?? throw new McpException($"The bridge's movie_frame reply has no frame: {reply?.ToJsonString() ?? "null"}.");

    /// <summary>
    /// Notes each <c>[id, read]</c> request the movie_frame reply lists as still unanswered as a call from the frame it was read in
    /// to the mark's <paramref name="frame"/>. An entry of any other shape, read after the mark's frame, or whose id
    /// <paramref name="isPending"/> says the server no longer awaits (a request that timed out because its handler died), is skipped.
    /// </summary>
    private static void CloseOpenCalls(Recording recording, JsonNode? open, long frame, Func<long, bool> isPending)
    {
        if (open is not JsonArray entries)
        {
            return;
        }

        foreach (JsonNode? entry in entries)
        {
            if (OpenRequest(entry) is ({ } id, { } read) && read <= frame && isPending(id))
            {
                recording.AddCall(read, frame);
            }
        }
    }

    /// <summary>The id and read frame of one <c>[id, read]</c> entry of the movie_frame reply's open list; nulls for any other shape.</summary>
    private static (long? Id, long? Read) OpenRequest(JsonNode? entry) =>
        entry is JsonArray { Count: 2 } pair ? (WholeFrame(pair[0]), WholeFrame(pair[1])) : (null, null);

    /// <summary>A whole, non-negative frame number, which GDScript's JSON may send as a float; null for anything else.</summary>
    private static long? WholeFrame(JsonNode? node) => WholeNumber.TryRead(node, out long frame) && frame >= 0 ? frame : null;

    /// <summary>A then.record mark checked before its wait starts: the recording it marks and the start's destination.</summary>
    internal sealed record ThenMark(Recording Recording, string Mark, OutputTarget? Destination, bool Overwrite);

    /// <summary>wait_for's then.record checked against the session before the wait is sent; null when the wait has none.</summary>
    /// <exception cref="McpException">As <see cref="CheckThenRecord"/>, or no session is running.</exception>
    private ThenMark? PrepareThenMark(ThenRecord? record, string? session)
    {
        if (record is null)
        {
            return null;
        }

        GodotSession target = Find(session);
        return CheckThenRecord(target, target.ActiveRecording, record);
    }

    /// <summary>
    /// Checks a then.record mark as record_mark would check it on <paramref name="movie"/>, the session's Movie Maker recording,
    /// before the wait starts: the start's destination and the clip state the mark needs.
    /// </summary>
    /// <exception cref="McpException">The session has no Movie Maker recording (a real-time one or none), the destination is
    /// refused, a start comes while a clip is open, or a stop with none open.</exception>
    internal static ThenMark CheckThenRecord(GodotSession target, Recording? movie, ThenRecord record)
    {
        if (movie is null)
        {
            throw new McpException(
                target.RecordingState?.Mode == RealtimeRecording.Mode
                    ? "options.then.record places a mark in a Movie Maker recording's met frame; this session's recording is real-time, "
                        + "so use then: {pause: true}, then record_mark start, then frame_control resume."
                    : "options.then.record needs a recording: start one with run_project's record option."
            );
        }

        try
        {
            movie.CheckMark(record.Mark);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }

        RecordMarkOptions? started =
            record.Mark == Recording.StartMark ? new RecordMarkOptions(Path: record.Path, Overwrite: record.Overwrite) : null;
        return new ThenMark(movie, record.Mark, CheckedClipTarget(target, movie, started), record.Overwrite);
    }

    /// <summary>
    /// Places a then.record mark at the movie frame the bridge read in the met frame, replacing then.movieFrame with
    /// then.record: the mark as record_mark returns it, or {mark, error} when the recording refuses it, so a met wait stays met.
    /// Nothing for a wait that did not meet, or no mark.
    /// </summary>
    internal static void PlaceThenMark(JsonObject reply, ThenMark? mark)
    {
        if (mark is null || reply["then"] is not JsonObject then)
        {
            return;
        }

        then.Remove("movieFrame", out JsonNode? movieFrame);
        then["record"] = MarkThen(mark, movieFrame);
    }

    private static JsonObject MarkThen(ThenMark mark, JsonNode? movieFrame)
    {
        if (WholeFrame(movieFrame) is not { } frame)
        {
            return ThenMarkError(mark, $"The bridge's met then has no movieFrame: {movieFrame?.ToJsonString() ?? "null"}.");
        }

        try
        {
            MarkResult marked = mark.Recording.Mark(mark.Mark, frame, mark.Destination, mark.Overwrite);
            return JsonSerializer.SerializeToNode(marked, Json)!.AsObject();
        }
        catch (SessionException e)
        {
            return ThenMarkError(mark, e.Message);
        }
    }

    private static JsonObject ThenMarkError(ThenMark mark, string error) => new() { ["mark"] = mark.Mark, ["error"] = error };
}
