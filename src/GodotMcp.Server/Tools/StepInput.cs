using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GodotMcp.Server.Tools;

/// <summary>
/// frame_control step's riding input (options.input): simulate_input's raw events, each placed in a stepped frame, checked
/// and expanded into the list the bridge sends, one event at a time, at each frame's process_frame.
/// </summary>
internal static class StepInput
{
    internal const int MaxEvents = 100;
    internal const int MaxExpandedEvents = 2000;

    // The events simulate_input plays as a press and a release a frame apart when pressed is left out: a key, a mouse
    // button (a wheel one aside), a pad button or an action.
    private static readonly string[] PressAndReleaseTypes = ["key", "mouse_button", "joypad_button", "action"];
    private static readonly string[] WheelButtons = ["wheel_up", "wheel_down", "wheel_left", "wheel_right"];

    /// <summary>
    /// The events the bridge sends, sorted by frame with list order kept within a frame: each a copy of its entry with frame
    /// (0 when left out) and index, the entry's place in <paramref name="input"/>; an entry with pressed left out that
    /// simulate_input would press and release a frame apart, an action among them, becomes a press at its frame and a
    /// release at the next; a repeat entry becomes its events, once for each of its iterations.
    /// </summary>
    /// <exception cref="McpException">The unit is physics, the entries name more than <see cref="MaxEvents"/> events between
    /// them or expand to more than <see cref="MaxExpandedEvents"/>, an entry is null or of an unknown type, a wait or a
    /// click_element, its frame is not a whole number 0 or more or lies past the step's last frame, the release of a press
    /// and release would, or a repeat's count, every or events is out of range, nests, or reaches past the step.</exception>
    internal static JsonArray Parameters(JsonObject?[] input, int count, string unit)
    {
        if (unit == "physics")
        {
            throw new McpException("options.input rides process frames; it cannot be used with unit physics.");
        }

        long written = WrittenCount(input);
        if (written > MaxEvents)
        {
            throw new McpException($"options.input takes at most {MaxEvents} events; got {written}.");
        }

        // Every entry is checked and its size counted before any repeat is unrolled, so the cap bounds the work as well as
        // the list.
        Plan[] plans = [.. input.Select((item, index) => PlanOf(item, index, count - 1))];
        long expanded = plans.Sum(plan => (long)plan.Count * plan.Template.Length);
        if (expanded > MaxExpandedEvents)
        {
            throw new McpException($"options.input expands to {expanded} events; at most {MaxExpandedEvents} are sent.");
        }

        return [.. plans.SelectMany(Unroll).OrderBy(entry => entry.Frame).Select(entry => (JsonNode)entry.Event)];
    }

    /// <summary>An entry checked and ready to send: its template events (frames counted from <see cref="Start"/>) sent
    /// <see cref="Count"/> times, <see cref="Every"/> frames apart; a plain entry is one iteration from frame 0.</summary>
    private readonly record struct Plan(int Start, int Every, int Count, (int Frame, JsonObject Event)[] Template);

    private static Plan PlanOf(JsonObject? item, int index, int last) =>
        TypeOf(item) == "repeat" ? PlanRepeat(item!, index, last) : new Plan(0, 1, 1, Place(item, index, Label(index), last));

    /// <summary>A repeat's plan: each inner entry checked and placed once, with the repeat's index, and the repeat refused
    /// whole when its furthest event, worked out from count, every and the inner frames, falls past the step's last
    /// frame.</summary>
    private static Plan PlanRepeat(JsonObject item, int index, int last)
    {
        string label = Label(index);
        int count = CountOf(item, label);
        int every = EveryOf(item, label);
        JsonObject?[] inner = EventsOf(item, label);
        int start = FrameOf(item, label);
        var checkedInner = new JsonObject[inner.Length];
        long furthest = 0;
        for (int innerIndex = 0; innerIndex < inner.Length; innerIndex++)
        {
            (checkedInner[innerIndex], long reach) = CheckInner(inner[innerIndex], label, innerIndex);
            furthest = Math.Max(furthest, reach);
        }

        long lastFrame = start + ((long)(count - 1) * every) + furthest;
        if (lastFrame > last)
        {
            throw new McpException(
                $"{label} repeat reaches frame {lastFrame}, past the step's last frame {last}; lower count or every, or raise the step's count."
            );
        }

        // Every inner entry now ends at or before last - start, so placing it there refuses nothing and nothing overflows.
        return new Plan(
            start,
            every,
            count,
            [.. checkedInner.SelectMany((entry, innerIndex) => Expand(entry, index, $"{label}.events[{innerIndex}]", last - start))]
        );
    }

    /// <summary>One of a repeat's entries, checked, with the furthest frame it sends in counted from its iteration's start
    /// (its release's for a press and release); <paramref name="label"/> names the repeat and <paramref name="innerIndex"/>
    /// the entry's place in its events.</summary>
    /// <exception cref="McpException">The entry is a repeat, null or of an unknown type, a wait or a click_element, or its
    /// frame is not a whole number 0 or more, each named options.input[i].events[j].</exception>
    private static (JsonObject Item, long Reach) CheckInner(JsonObject? item, string label, int innerIndex)
    {
        string innerLabel = $"{label}.events[{innerIndex}]";
        if (TypeOf(item) == "repeat")
        {
            throw new McpException($"{innerLabel} is a repeat; repeats do not nest: write the inner pattern as events.");
        }

        JsonObject checkedItem;
        try
        {
            checkedItem = RuntimeTools.CheckEvent(item, innerIndex);
        }
        catch (McpException refused)
        {
            // CheckEvent names the entry events[j]; the repeat's own label puts it in the caller's list.
            throw new McpException($"{label}.{refused.Message}", refused);
        }

        string type = checkedItem["type"]!.GetValue<string>();
        RefuseGesture(type, innerLabel);
        long frame = FrameOf(checkedItem, innerLabel);
        return (checkedItem, PressesAndReleases(checkedItem, type) ? frame + 1 : frame);
    }

    /// <summary>A plan's events, a copy of each template event for each iteration, at the iteration's start plus its
    /// frame.</summary>
    private static IEnumerable<(int Frame, JsonObject Event)> Unroll(Plan plan)
    {
        for (int iteration = 0; iteration < plan.Count; iteration++)
        {
            int shift = plan.Start + (iteration * plan.Every);
            foreach ((int frame, JsonObject template) in plan.Template)
            {
                JsonObject copy = template.DeepClone().AsObject();
                copy["frame"] = shift + frame;
                yield return (shift + frame, copy);
            }
        }
    }

    /// <summary>The events one entry sends, each with the frame it is sent in; <paramref name="label"/> names the entry in a
    /// refusal and <paramref name="index"/> is its place in the caller's list.</summary>
    private static (int Frame, JsonObject Event)[] Place(JsonObject? item, int index, string label, int last) =>
        Expand(RuntimeTools.CheckEvent(item, index), index, label, last);

    /// <summary>The events a checked entry sends, each with the frame it is sent in.</summary>
    private static (int Frame, JsonObject Event)[] Expand(JsonObject checkedItem, int index, string label, int last)
    {
        string type = checkedItem["type"]!.GetValue<string>();
        RefuseGesture(type, label);
        int frame = FrameOf(checkedItem, label);
        if (frame > last)
        {
            throw new McpException($"{label}.frame is {frame}, past the step's last frame {last}.");
        }

        if (!PressesAndReleases(checkedItem, type))
        {
            return [(frame, Copy(checkedItem, index, frame, null))];
        }

        return frame < last
            ? [(frame, Copy(checkedItem, index, frame, true)), (frame + 1, Copy(checkedItem, index, frame + 1, false))]
            : throw new McpException(
                $"{label} presses and releases (pressed left out), so its release lands at frame {frame + 1}, past the step's "
                    + $"last frame {last}; pass pressed: true and a release event, or raise count."
            );
    }

    private static void RefuseGesture(string type, string label)
    {
        if (type == "wait")
        {
            throw new McpException($"{label} is a wait: a step's input is placed by frame; give the event a frame instead.");
        }

        if (type == "click_element")
        {
            throw new McpException(
                $"{label} is click_element, a gesture that plays over frames of its own; in a step send mouse_button "
                    + "and mouse_motion events with x, y."
            );
        }
    }

    /// <summary>The entry's frame, 0 when left out or null.</summary>
    /// <exception cref="McpException">It is not a whole number 0 or more.</exception>
    private static int FrameOf(JsonObject item, string label)
    {
        JsonNode? given = item["frame"];
        if (given is null)
        {
            return 0;
        }

        return WholeNumber(given) ?? throw new McpException($"{label}.frame must be a whole number, 0 or more; got {given.ToJsonString()}.");
    }

    /// <summary>The repeat's count, a whole number 1 to 1000.</summary>
    /// <exception cref="McpException">It is not one.</exception>
    private static int CountOf(JsonObject item, string label)
    {
        int? count = WholeNumber(item["count"]);
        return count is >= 1 and <= 1000
            ? count.Value
            : throw new McpException($"{label}.count must be a whole number, 1 to 1000; got {Given(item["count"])}.");
    }

    /// <summary>The repeat's every, a whole number 1 or more.</summary>
    /// <exception cref="McpException">It is not one.</exception>
    private static int EveryOf(JsonObject item, string label)
    {
        int? every = WholeNumber(item["every"]);
        return every is >= 1 ? every.Value : throw new McpException($"{label}.every must be a whole number, 1 or more; got {Given(item["every"])}.");
    }

    /// <summary>The repeat's inner entries, 1 to 100.</summary>
    /// <exception cref="McpException">They are not a list of 1 to 100.</exception>
    private static JsonObject?[] EventsOf(JsonObject item, string label)
    {
        if (item["events"] is not JsonArray events || events.Count is < 1 or > 100)
        {
            throw new McpException($"{label}.events must be a list of 1 to 100 events.");
        }

        return [.. events.Select(entry => entry as JsonObject)];
    }

    /// <summary>How many events the caller wrote: each entry is 1, a repeat is 1 plus its inner entries' count.</summary>
    private static long WrittenCount(JsonObject?[] input)
    {
        long written = 0;
        foreach (JsonObject? item in input)
        {
            written += item is not null && TypeOf(item) == "repeat" && item["events"] is JsonArray events ? 1 + events.Count : 1;
        }

        return written;
    }

    private static string Label(int index) => $"options.input[{index}]";

    // The entry's type when it is an object naming one as a string, else null, as CheckEvent reads it.
    private static string? TypeOf(JsonObject? item) => item?["type"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // The value as the caller wrote it, "null" when the field is missing or null.
    private static string Given(JsonNode? node) => node?.ToJsonString() ?? "null";

    // A JSON number arrives as a JsonElement, which gives a double for 2.0 as for 2; one built in code holds an int.
    private static int? WholeNumber(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        double number =
            value.TryGetValue(out int whole) ? whole
            : value.TryGetValue(out double real) ? real
            : -1;
        return number is >= 0 and <= int.MaxValue && Math.Floor(number) == number ? (int)number : null;
    }

    /// <summary>Whether simulate_input plays the entry as a press and a release a frame apart: pressed is left out of a key, a
    /// pad button, an action, or a mouse button other than a wheel one, which is a notch in one frame.</summary>
    private static bool PressesAndReleases(JsonObject item, string type) =>
        !item.ContainsKey("pressed") && PressAndReleaseTypes.Contains(type) && !(type == "mouse_button" && IsWheel(item["button"]));

    // As the bridge reads a mouse_button's button: a wheel name in any case, or a number 4 to 7.
    private static bool IsWheel(JsonNode? button) =>
        button is JsonValue value
        && (
            value.TryGetValue(out string? name)
                ? WheelButtons.Contains(name.ToLowerInvariant())
                : value.TryGetValue(out double number) && (int)number is >= 4 and <= 7
        );

    /// <summary>A copy of the entry sent at <paramref name="frame"/>, with its index; pressed set when given, and a release
    /// made from a press-and-release entry carries no strength and no doubleClick, as simulate_input's own releases do
    /// not.</summary>
    private static JsonObject Copy(JsonObject item, int index, int frame, bool? pressed)
    {
        JsonObject copy = item.DeepClone().AsObject();
        copy["frame"] = frame;
        copy["index"] = index;
        if (pressed is bool isPressed)
        {
            copy["pressed"] = isPressed;
        }

        if (pressed is false)
        {
            copy.Remove("doubleClick");
            copy.Remove("strength");
        }

        return copy;
    }
}
