using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Wire;

namespace GodotMcp.Server.Tools;

/// <summary>
/// A watch timeline's bridge ticks converted to drive time before it is shaped: each point
/// ([frame, gameMs, value, ticks]), signal event ([frame, gameMs, node, signal, args, track, ticks[, stack]]) and voice
/// ([frame, gameMs, event, path, info, ticks]) carries at, its drive-clock ms (0.1 ms) through the session's clock, where its tick
/// was, or null when the clock cannot convert it; an item one shorter, from a bridge that sends no tick, gets the null appended,
/// so every item keeps one length and no raw tick reaches the client. The clock's bounds are read once before the walk, so every
/// item converts with one offset, and the timeline carries that read's error as clockErrorMs once, none when nothing converted.
/// </summary>
internal static class WatchStamps
{
    /// <summary>A point's length with its tick.</summary>
    private const int PointLength = 4;

    /// <summary>A signal event's length with its tick, the track index still before it.</summary>
    private const int EventLength = 7;

    /// <summary>A signal event's length with its tick and, on a stack track, the emission's stack after it.</summary>
    private const int StackedEventLength = EventLength + 1;

    /// <summary>A voice's length with its tick.</summary>
    private const int VoiceLength = 6;

    /// <summary>Converts every point's, event's and voice's tick in <paramref name="reply"/> through one read of
    /// <paramref name="clock"/>'s bounds, so every item shares its offset, and adds that read's error as clockErrorMs when
    /// one converted.</summary>
    internal static void Stamp(JsonObject reply, SessionClock clock)
    {
        double? offsetMs = clock.TryGetOffset(out double offset, out double errorMs) ? offset : null;
        bool converted = false;
        foreach (JsonObject track in (reply["tracks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            converted |= StampAll(track["points"] as JsonArray, PointLength, offsetMs);
        }

        converted |= StampEvents(reply["events"] as JsonArray, offsetMs);
        converted |= StampAll((reply["audio"] as JsonObject)?["voices"] as JsonArray, VoiceLength, offsetMs);
        if (converted)
        {
            reply["clockErrorMs"] = errorMs;
        }
    }

    /// <summary>Stamps each item of <paramref name="items"/> that is <paramref name="length"/> long with its tick, or one
    /// shorter; returns whether any converted.</summary>
    private static bool StampAll(JsonArray? items, int length, double? offsetMs)
    {
        bool converted = false;
        foreach (JsonArray item in (items ?? []).OfType<JsonArray>())
        {
            converted |= StampItem(item, length, offsetMs, trailing: false);
        }

        return converted;
    }

    /// <summary>Stamps each signal event's tick, which sits at <see cref="EventLength"/> - 1 whether or not the event ends with
    /// a stack; returns whether any converted.</summary>
    private static bool StampEvents(JsonArray? events, double? offsetMs)
    {
        bool converted = false;
        foreach (JsonArray item in (events ?? []).OfType<JsonArray>())
        {
            converted |= StampItem(item, EventLength, offsetMs, trailing: item.Count == StackedEventLength);
        }

        return converted;
    }

    /// <summary>Replaces the item's tick, at <paramref name="length"/> - 1, by at through <paramref name="offsetMs"/>, or null
    /// when there is no offset or no readable tick; appends null to an item one shorter; leaves an item of any other length alone,
    /// but for one item more when <paramref name="trailing"/>, as an event's stack is. Returns whether it converted.</summary>
    private static bool StampItem(JsonArray item, int length, double? offsetMs, bool trailing)
    {
        if (item.Count == length - 1)
        {
            item.Add((JsonNode?)null);
            return false;
        }

        if (item.Count != length && !(trailing && item.Count == length + 1))
        {
            return false;
        }

        int tick = length - 1;
        if (offsetMs is double offset && HandshakeExpectation.ReadTicksUsec(item[tick]) is { } ticksUsec)
        {
            item[tick] = SessionClock.ToDriveMs(offset, ticksUsec);
            return true;
        }

        item[tick] = null;
        return false;
    }
}
