using System.Text.Json.Nodes;

namespace GodotMcp.Server.Session;

/// <summary>
/// What capture_input stop returns: the events in simulate_input's format, whether the capture hit its cap, and how its game
/// ended while it ran (stop, restart or exit), null while the game still runs.
/// </summary>
internal sealed record CapturedInput(JsonArray Events, bool Truncated, string? Ended);

/// <summary>
/// The input captures the bridges stream in <c>captured</c> frames, keyed by session name (in any case) rather than held on a
/// <see cref="GodotSession"/>, so a capture outlives its session: one whose game stops, restarts or exits is kept, marked ended,
/// until one capture_input stop takes it or a new start replaces it. A capture holds at most <see cref="MaxEvents"/> events.
/// </summary>
internal sealed class CaptureStore
{
    /// <summary>The most events a capture holds, waits included; the bridge stops at the same count.</summary>
    public const int MaxEvents = 2000;

    public const string EndedByStop = "stop";
    public const string EndedByRestart = "restart";
    public const string EndedByExit = "exit";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Capture> _captures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts an empty capture for the session, replacing an ended one.</summary>
    /// <returns>False, changing nothing, when the session's capture is still running.</returns>
    public bool Begin(string session)
    {
        lock (_lock)
        {
            if (_captures.TryGetValue(session, out Capture? held) && held.Ended is null)
            {
                return false;
            }

            _captures[session] = new Capture();
            return true;
        }
    }

    /// <summary>Whether the session has a capture that has not ended.</summary>
    public bool IsRunning(string session)
    {
        lock (_lock)
        {
            return _captures.TryGetValue(session, out Capture? held) && held.Ended is null;
        }
    }

    /// <summary>Whether the session has a capture, running or ended.</summary>
    public bool Holds(string session)
    {
        lock (_lock)
        {
            return _captures.ContainsKey(session);
        }
    }

    /// <summary>
    /// Appends a <c>{type: "captured", events, truncated?}</c> frame's events to the session's running capture, in order, up to
    /// <see cref="MaxEvents"/>; reaching the cap, or a frame marked truncated, marks the capture truncated. A frame for a session
    /// with no running capture is dropped.
    /// </summary>
    public void Receive(string session, JsonObject frame)
    {
        lock (_lock)
        {
            if (!_captures.TryGetValue(session, out Capture? held) || held.Ended is not null)
            {
                return;
            }

            if (frame["events"] is JsonArray events)
            {
                Append(held, events);
            }

            held.Truncated = held.Truncated || IsMarkedTruncated(frame) || held.Events.Count >= MaxEvents;
        }
    }

    /// <summary>Marks the session's running capture ended by <paramref name="reason"/>; an ended capture keeps its first reason.</summary>
    public void End(string session, string reason)
    {
        lock (_lock)
        {
            if (_captures.TryGetValue(session, out Capture? held) && held.Ended is null)
            {
                held.Ended = reason;
            }
        }
    }

    /// <summary>Drops the session's capture, as a start whose bridge command failed does.</summary>
    public void Discard(string session)
    {
        lock (_lock)
        {
            _captures.Remove(session);
        }
    }

    /// <summary>Removes the session's capture and returns it; null when it holds none.</summary>
    public CapturedInput? Take(string session)
    {
        lock (_lock)
        {
            if (!_captures.Remove(session, out Capture? held))
            {
                return null;
            }

            return new CapturedInput(held.Events, held.Truncated, held.Ended);
        }
    }

    private static void Append(Capture held, JsonArray events)
    {
        foreach (JsonNode? item in events.Take(MaxEvents - held.Events.Count))
        {
            held.Events.Add(item?.DeepClone());
        }
    }

    private static bool IsMarkedTruncated(JsonObject frame) =>
        frame["truncated"] is JsonValue flag && flag.TryGetValue(out bool truncated) && truncated;

    private sealed class Capture
    {
        public JsonArray Events { get; } = [];

        public bool Truncated { get; set; }

        public string? Ended { get; set; }
    }
}
