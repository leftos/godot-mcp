using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>Marking the parts of a recording run to keep: each start-stop pair becomes a clip once the run ends.</summary>
internal sealed partial class RuntimeTools
{
    private static readonly TimeSpan MovieFrameTimeout = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = "record_mark", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Marks the start or the stop of a clip in a session launched with options.record, at the movie frame the game has "
            + "reached. When the run ends (stop_project, the game quitting, restart_project) each start-stop pair is cut from the "
            + "movie as its own file, and the full movie is deleted once every clip is cut; a start left open clips to the end. "
            + "Marks alternate: start, stop, start, stop. Returns {mark, frame, seconds}, frame counted from the movie's first "
            + "and seconds = frame / 60. A mark made while the run is ending is not kept."
    )]
    public async Task<string> RecordMarkAsync(
        [Description("start or stop.")] string mark,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        if (mark is not (Recording.StartMark or Recording.StopMark))
        {
            throw new McpException($"mark must be \"start\" or \"stop\"; got \"{mark}\".");
        }

        GodotSession target = Find(session);
        Recording recording =
            target.ActiveRecording ?? throw new McpException($"session '{target.Name}' is not recording; launch it with options.record.");
        BridgeResult result = await CallWithErrorsAsync(
            target,
            new BridgeCall("record_mark", "movie_frame", new JsonObject(), MovieFrameTimeout),
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
