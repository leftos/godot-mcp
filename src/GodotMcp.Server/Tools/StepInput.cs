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

    // The events simulate_input plays as a press and a release a frame apart when pressed is left out (a wheel button aside).
    private static readonly string[] PressAndReleaseTypes = ["key", "mouse_button", "joypad_button"];
    private static readonly string[] WheelButtons = ["wheel_up", "wheel_down", "wheel_left", "wheel_right"];

    /// <summary>
    /// The events the bridge sends, sorted by frame with list order kept within a frame: each a copy of its entry with frame
    /// (0 when left out) and index, the entry's place in <paramref name="input"/>; an entry with pressed left out that
    /// simulate_input would press and release a frame apart becomes a press at its frame and a release at the next.
    /// </summary>
    /// <exception cref="McpException">The unit is physics, there are more than <see cref="MaxEvents"/> events, an entry is
    /// null or of an unknown type, a wait or a click_element, its frame is not a whole number 0 or more or lies past the step's
    /// last frame, or the release of a press and release would.</exception>
    internal static JsonArray Parameters(JsonObject?[] input, int count, string unit)
    {
        if (unit == "physics")
        {
            throw new McpException("options.input rides process frames; it cannot be used with unit physics.");
        }

        if (input.Length > MaxEvents)
        {
            throw new McpException($"options.input takes at most {MaxEvents} events; got {input.Length}.");
        }

        List<(int Frame, JsonObject Event)> sent = [];
        for (int index = 0; index < input.Length; index++)
        {
            sent.AddRange(Place(input[index], index, count - 1));
        }

        return [.. sent.OrderBy(entry => entry.Frame).Select(entry => (JsonNode)entry.Event)];
    }

    /// <summary>The events one entry sends, each with the frame it is sent in.</summary>
    private static (int Frame, JsonObject Event)[] Place(JsonObject? item, int index, int last)
    {
        JsonObject checkedItem = RuntimeTools.CheckEvent(item, index);
        string type = checkedItem["type"]!.GetValue<string>();
        RefuseGesture(type, index);
        int frame = FrameOf(checkedItem, index);
        if (frame > last)
        {
            throw new McpException($"options.input[{index}].frame is {frame}, past the step's last frame {last}.");
        }

        if (!PressesAndReleases(checkedItem, type))
        {
            return [(frame, Copy(checkedItem, index, frame, null))];
        }

        return frame < last
            ? [(frame, Copy(checkedItem, index, frame, true)), (frame + 1, Copy(checkedItem, index, frame + 1, false))]
            : throw new McpException(
                $"options.input[{index}] presses and releases (pressed left out), so its release lands at frame {frame + 1}, past "
                    + $"the step's last frame {last}; pass pressed: true and a release event, or raise count."
            );
    }

    private static void RefuseGesture(string type, int index)
    {
        if (type == "wait")
        {
            throw new McpException($"options.input[{index}] is a wait: a step's input is placed by frame; give the event a frame instead.");
        }

        if (type == "click_element")
        {
            throw new McpException(
                $"options.input[{index}] is click_element, a gesture that plays over frames of its own; in a step send mouse_button "
                    + "and mouse_motion events with x, y."
            );
        }
    }

    /// <summary>The entry's frame, 0 when left out or null.</summary>
    /// <exception cref="McpException">It is not a whole number 0 or more.</exception>
    private static int FrameOf(JsonObject item, int index)
    {
        JsonNode? given = item["frame"];
        if (given is null)
        {
            return 0;
        }

        return WholeNumber(given)
            ?? throw new McpException($"options.input[{index}].frame must be a whole number, 0 or more; got {given.ToJsonString()}.");
    }

    // A JSON number arrives as a JsonElement, which gives a double for 2.0 as for 2; one built in code holds an int.
    private static int? WholeNumber(JsonNode node)
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
    /// pad button, or a mouse button other than a wheel one, which is a notch in one frame.</summary>
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
    /// made from a press-and-release entry carries no doubleClick, as simulate_input's own release does not.</summary>
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
        }

        return copy;
    }
}
