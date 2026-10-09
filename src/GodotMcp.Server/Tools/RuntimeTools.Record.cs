using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
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
    private const int MaxRecordSeconds = 600;

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
            + "height, fps, encoder, warning?}, path being the .mp4 the clip becomes. stop ends it and returns {mark, mode, clip: "
            + "{path, seconds, frames, averageFps, width, height}, warning?}; a recording that ended on its own is returned by the "
            + "next stop. One real-time recording runs per session. The window must stay unminimized (a minimized window holds its "
            + "last frame); quiet and headless sessions are refused, and it needs ffmpeg."
    )]
    public async Task<string> RecordMarkAsync(
        [Description("start or stop.")] string mark,
        [Description("A real-time recording's frame rate and length cap, on start only; a Movie Maker recording's marks take none.")]
            RecordMarkOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        RecordMarkOptions? realtime = CheckMarkOptions(mark, options);
        GodotSession target = Find(session);
        if (target.ActiveRecording is { } recording)
        {
            return options is null
                ? await MarkMovieAsync(target, recording, mark, cancellationToken)
                : throw new McpException(
                    $"options belong to a real-time recording; session '{target.Name}' records with Movie Maker, whose marks take none."
                );
        }

        try
        {
            object marked = realtime is not null
                ? await target.StartRealtimeAsync(realtime.Fps, realtime.MaxSeconds, cancellationToken)
                : await target.StopRealtimeAsync(cancellationToken);
            return JsonSerializer.Serialize(marked, Json);
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The mark and its options checked before any session is looked at.</summary>
    /// <returns>A start's options, the defaults when none were given; null for a stop.</returns>
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

    /// <summary>Marks the Movie Maker recording at the movie frame the game has reached.</summary>
    private static async Task<string> MarkMovieAsync(GodotSession target, Recording recording, string mark, CancellationToken cancellationToken)
    {
        BridgeResult result = await CallWithErrorsAsync(
            target,
            new BridgeCall("record_mark", "movie_frame", [], MovieFrameTimeout),
            cancellationToken
        );
        long frame = ReadMovieFrame(result.Reply);
        try
        {
            MarkResult marked = recording.Mark(mark, frame);
            JsonObject reply = JsonSerializer.SerializeToNode(marked, Json)!.AsObject();
            return ErrorReport.AddTo(reply, result.Errors).ToJsonString();
        }
        catch (SessionException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The frame in the bridge's movie_frame reply, a whole number that GDScript's JSON may send as a float.</summary>
    /// <exception cref="McpException">The reply has no whole, non-negative frame.</exception>
    internal static long ReadMovieFrame(JsonNode? reply)
    {
        if (reply?["frame"] is JsonValue value && value.TryGetValue(out double frame) && frame >= 0 && frame == Math.Floor(frame))
        {
            return (long)frame;
        }

        throw new McpException($"The bridge's movie_frame reply has no frame: {reply?.ToJsonString() ?? "null"}.");
    }
}
