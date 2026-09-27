using System.ComponentModel;
using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GodotMcp.Server.Tools;

/// <summary>What capture_input start captures: which sources, and whether mouse motions with no button held are kept.</summary>
internal sealed record CaptureOptions(
    [property: Description(
        "Which input to capture: real (a person's input to the game's window), sent (the gestures the server plays), or both; both "
            + "when left out."
    )]
        string[]? Sources = null,
    [property: Description("Keep mouse motions with no button held; false by default, which still keeps a drag's motions.")] bool Motion = false
);

/// <summary>
/// Input capture: the bridge's capture module (bridge/godot_mcp_capture.gd) records the real input the game's window receives and
/// the events the bridge sends, each as a simulate_input event with gaps as wait steps, and streams them to the server in captured
/// frames, which the registry's <see cref="CaptureStore"/> holds by session name until capture_input stop takes them.
/// </summary>
internal sealed partial class RuntimeTools
{
    internal const string QuietCaptureWarning =
        "this run is quiet: its window gets no real input, so only the server's gestures are captured. Run with quiet:false or "
        + "attach_project to capture a person's input.";
    private const string RealSource = "real";
    private const string SentSource = "sent";
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] CaptureSources = [RealSource, SentSource];

    [McpServerTool(Name = "capture_input", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Captures input in simulate_input's event format: mode start begins, mode stop returns the events. Captures a person's real "
            + "input and the gestures the server sends, on the game's own clock, with gaps as wait steps. Replay the result with "
            + "simulate_input."
    )]
    public Task<string> CaptureInputAsync(
        [Description(
            "start begins a capture, returning {capturing, sources, motion} and a warning when a quiet run cannot capture real "
                + "input; stop ends it, returning {events, count, truncated} and ended (stop, restart or exit) when the game went "
                + "away first."
        )]
            string mode,
        [Description("{sources, motion}, for start only: sources any of real and sent (both by default); motion false by default.")]
            CaptureOptions? options = null,
        [Description(ProjectTools.SessionDescription)] string? session = null,
        CancellationToken cancellationToken = default
    )
    {
        CaptureSettings? settings = CheckCaptureArguments(mode, options);
        return settings is null ? StopCaptureAsync(session, cancellationToken) : StartCaptureAsync(settings, session, cancellationToken);
    }

    /// <summary>A start's settings, with the sources in the order real, sent and each once; null for a stop.</summary>
    /// <exception cref="McpException">An unknown mode, options given with stop, or sources empty or naming something else.</exception>
    internal static CaptureSettings? CheckCaptureArguments(string mode, CaptureOptions? options)
    {
        if (mode is not ("start" or "stop"))
        {
            throw new McpException($"mode must be start or stop; got '{mode}'");
        }

        if (mode == "stop")
        {
            return options is null ? null : throw new McpException("options apply to mode start only");
        }

        return new CaptureSettings(CheckSources(options?.Sources), options?.Motion ?? false);
    }

    private static string[] CheckSources(string[]? sources)
    {
        if (sources is null)
        {
            return CaptureSources;
        }

        if (sources.Length == 0)
        {
            throw new McpException("options.sources is empty");
        }

        if (sources.Any(source => !CaptureSources.Contains(source)))
        {
            throw new McpException("options.sources must name real, sent or both");
        }

        return [.. CaptureSources.Where(sources.Contains)];
    }

    private async Task<string> StartCaptureAsync(CaptureSettings settings, string? session, CancellationToken cancellationToken)
    {
        if (session is not null && sessions.Captures.IsRunning(session))
        {
            throw RunningCapture(session);
        }

        GodotSession target = Find(session);
        bool quietDropsReal = target.Quiet && settings.Sources.Contains(RealSource);
        IReadOnlyList<string> sources = quietDropsReal ? [SentSource] : settings.Sources;
        if (!sessions.Captures.Begin(target.Name))
        {
            throw RunningCapture(target.Name);
        }

        JsonObject parameters = new()
        {
            ["action"] = "start",
            ["sources"] = SourceList(sources),
            ["motion"] = settings.Motion,
        };
        BridgeResult result;
        try
        {
            result = await CallWithErrorsAsync(target, new BridgeCall("capture_input", "capture", parameters, CaptureTimeout), cancellationToken);
        }
        catch
        {
            sessions.Captures.Discard(target.Name);
            throw;
        }

        JsonObject started = new()
        {
            ["capturing"] = true,
            ["sources"] = SourceList(sources),
            ["motion"] = settings.Motion,
        };
        if (quietDropsReal)
        {
            started["warning"] = QuietCaptureWarning;
        }

        return ErrorReport.AddTo(started, result.Errors).ToJsonString();
    }

    /// <summary>
    /// Returns the session's capture and forgets it: a running one after the bridge's stop reply, which follows its last captured
    /// frame; an ended one as it is, even when its session is gone.
    /// </summary>
    private async Task<string> StopCaptureAsync(string? session, CancellationToken cancellationToken)
    {
        string name = session ?? Find(null).Name;
        if (!sessions.Captures.Holds(name))
        {
            throw NoCapture(name);
        }

        IReadOnlyList<ErrorEntry> errors = sessions.Captures.IsRunning(name) ? await StopRunningCaptureAsync(name, cancellationToken) : [];
        CapturedInput captured = sessions.Captures.Take(name) ?? throw NoCapture(name);
        JsonObject stopped = new()
        {
            ["events"] = captured.Events,
            ["count"] = captured.Events.Count,
            ["truncated"] = captured.Truncated,
        };
        if (captured.Ended is not null)
        {
            stopped["ended"] = captured.Ended;
        }

        return ErrorReport.AddTo(stopped, errors).ToJsonString();
    }

    /// <summary>Asks the bridge to stop capturing; a game that has gone meanwhile ends the capture as an exit.</summary>
    private async Task<IReadOnlyList<ErrorEntry>> StopRunningCaptureAsync(string name, CancellationToken cancellationToken)
    {
        GodotSession target = Find(name);
        try
        {
            BridgeCall call = new("capture_input", "capture", new JsonObject { ["action"] = "stop" }, CaptureTimeout);
            return (await CallWithErrorsAsync(target, call, cancellationToken)).Errors;
        }
        catch (McpException) when (!target.HasGame)
        {
            sessions.Captures.End(name, CaptureStore.EndedByExit);
            return [];
        }
    }

    private static JsonArray SourceList(IReadOnlyList<string> sources) => new([.. sources.Select(source => (JsonNode)source)]);

    private static McpException RunningCapture(string session) => new($"a capture is already running in session '{session}'; stop it first");

    private static McpException NoCapture(string session) => new($"no capture in session '{session}': start one with capture_input mode start");

    /// <summary>What a capture starts with: its sources (real, sent, or both in that order) and whether plain mouse motions are kept.</summary>
    internal sealed record CaptureSettings(IReadOnlyList<string> Sources, bool Motion);
}
