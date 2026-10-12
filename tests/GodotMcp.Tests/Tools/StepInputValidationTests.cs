using System.Text.Json;
using System.Text.Json.Nodes;
using GodotMcp.Server.Tools;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>frame_control step's options.input checks and the event list it sends the bridge; no Godot runs here.</summary>
public sealed class StepInputValidationTests
{
    private const string ReleaseRefusal =
        "options.input[0] presses and releases (pressed left out), so its release lands at frame 3, past the step's last frame 2; "
        + "pass pressed: true and a release event, or raise count.";

    // The sorted list of TheSentListIsSortedByFrameKeepingListOrderWithinAFrame: each event's key, frame and press (+) or
    // release (-), and its entry's index.
    private static readonly string[] SortedOrder = ["A0+", "D0+", "A1-", "B1+", "C2+"];
    private static readonly int[] SortedIndices = [1, 3, 1, 2, 0];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void InputIsRefusedWithUnitPhysics()
    {
        StepOptions options = new(Unit: "physics", Input: [Key("A", frame: 0, pressed: true)]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal("options.input rides process frames; it cannot be used with unit physics.", refused.Message);
    }

    [Fact]
    public void AFramePastTheStepsLastFrameIsRefused()
    {
        StepOptions options = new(Input: [Key("A", frame: 0, pressed: true), Key("B", frame: 3, pressed: true)]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal("options.input[1].frame is 3, past the step's last frame 2.", refused.Message);
    }

    [Fact]
    public void APressAndReleaseWhoseReleaseFallsPastTheStepIsRefused()
    {
        StepOptions options = new(Input: [Key("A", frame: 2, pressed: null)]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal(ReleaseRefusal, refused.Message);
    }

    [Fact]
    public void AWaitIsRefused()
    {
        StepOptions options = new(Input: [new JsonObject { ["type"] = "wait", ["ms"] = 50 }]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal("options.input[0] is a wait: a step's input is placed by frame; give the event a frame instead.", refused.Message);
    }

    [Fact]
    public void AClickElementIsRefused()
    {
        StepOptions options = new(Input: [new JsonObject { ["type"] = "click_element", ["element"] = "OpenButton" }]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal(
            "options.input[0] is click_element, a gesture that plays over frames of its own; in a step send mouse_button and "
                + "mouse_motion events with x, y.",
            refused.Message
        );
    }

    [Fact]
    public void AnUnknownTypeIsRefusedAsSimulateInputRefusesIt()
    {
        StepOptions options = new(Input: [new JsonObject { ["type"] = "tap" }]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.StartsWith("events[0] has type 'tap'; the types are key, mouse_button", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pause", null)]
    [InlineData("resume", null)]
    [InlineData("time_scale", 1.0)]
    public void InputIsRefusedOnAnyActionButStep(string action, double? scale)
    {
        StepOptions options = new(Input: [Key("A", frame: 0, pressed: true)]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters(action, null, scale, options));

        Assert.Equal("options.input applies to step only.", refused.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    public void AFrameThatIsNotAWholeNumberIsRefused(string frame)
    {
        JsonObject item = Key("A", frame: null, pressed: true);
        item["frame"] = JsonNode.Parse(frame);
        StepOptions options = new(Input: [item]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal($"options.input[0].frame must be a whole number, 0 or more; got {frame}.", refused.Message);
    }

    [Fact]
    public void AWholeFrameWrittenWithADecimalPointIsTaken()
    {
        JsonObject item = Key("A", frame: null, pressed: true);
        item["frame"] = JsonNode.Parse("2.0");

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 3, null, new StepOptions(Input: [item])));

        Assert.Equal(2, Assert.Single(sent)!["frame"]!.GetValue<int>());
    }

    [Fact]
    public void MoreThanAHundredEventsAreRefused()
    {
        StepOptions options = new(Input: [.. Enumerable.Range(0, 101).Select(_ => Key("A", frame: 0, pressed: true))]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal("options.input takes at most 100 events; got 101.", refused.Message);
    }

    [Fact]
    public void AHundredEventsAreTaken()
    {
        StepOptions options = new(Input: [.. Enumerable.Range(0, 100).Select(_ => Key("A", frame: 0, pressed: true))]);

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 3, null, options));

        Assert.Equal(100, sent.Count);
    }

    [Theory]
    [InlineData("key", "key", "A")]
    [InlineData("mouse_button", "button", "left")]
    [InlineData("joypad_button", "button", "A")]
    public void AnEventWithPressedLeftOutIsAPressAtItsFrameAndAReleaseAtTheNext(string type, string field, string value)
    {
        JsonObject item = new()
        {
            ["type"] = type,
            [field] = value,
            ["frame"] = 1,
        };
        if (type == "mouse_button")
        {
            item["x"] = 10;
            item["y"] = 20;
            item["doubleClick"] = true;
        }

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 3, null, new StepOptions(Input: [item])));

        Assert.Equal(2, sent.Count);
        Assert.Equal((1, true), (sent[0]!["frame"]!.GetValue<int>(), sent[0]!["pressed"]!.GetValue<bool>()));
        Assert.Equal((2, false), (sent[1]!["frame"]!.GetValue<int>(), sent[1]!["pressed"]!.GetValue<bool>()));
        Assert.All(sent, entry => Assert.Equal(value, entry![field]!.GetValue<string>()));
        Assert.All(sent, entry => Assert.Equal(0, entry!["index"]!.GetValue<int>()));
        Assert.Null(sent[1]!["doubleClick"]);
    }

    [Theory]
    [InlineData("{\"type\": \"mouse_button\", \"button\": \"wheel_down\", \"x\": 1, \"y\": 1, \"frame\": 2}")]
    [InlineData("{\"type\": \"mouse_button\", \"button\": \"Wheel_Up\", \"x\": 1, \"y\": 1, \"frame\": 2}")]
    [InlineData("{\"type\": \"mouse_button\", \"button\": 5, \"x\": 1, \"y\": 1, \"frame\": 2}")]
    [InlineData("{\"type\": \"action\", \"action\": \"jump\", \"frame\": 2}")]
    public void AWheelNotchAndAnActionWithPressedLeftOutAreSentAsGiven(string json)
    {
        JsonObject item = JsonNode.Parse(json)!.AsObject();

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 3, null, new StepOptions(Input: [item])));

        JsonNode entry = Assert.Single(sent)!;
        Assert.Equal(2, entry["frame"]!.GetValue<int>());
        Assert.Null(entry["pressed"]);
    }

    [Fact]
    public void FrameDefaultsToZero()
    {
        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 1, null, new StepOptions(Input: [Key("A", frame: null, pressed: true)])));

        Assert.Equal(0, Assert.Single(sent)!["frame"]!.GetValue<int>());
    }

    [Fact]
    public void CountStaysOneWithInputAlone()
    {
        StepOptions options = new(Input: [Key("A", frame: 1, pressed: true)]);

        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.BuildFrameParameters("step", null, null, options));

        Assert.Equal("options.input[0].frame is 1, past the step's last frame 0.", refused.Message);
    }

    [Fact]
    public void TheSentListIsSortedByFrameKeepingListOrderWithinAFrame()
    {
        StepOptions options = new(
            Input:
            [
                Key("C", frame: 2, pressed: true),
                Key("A", frame: 0, pressed: null),
                Key("B", frame: 1, pressed: true),
                Key("D", frame: 0, pressed: true),
            ]
        );

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 4, null, options));

        string[] order = [.. sent.Select(entry => $"{entry!["key"]}{entry["frame"]}{(entry["pressed"]!.GetValue<bool>() ? "+" : "-")}")];
        Assert.Equal(SortedOrder, order);
        Assert.Equal(SortedIndices, sent.Select(entry => entry!["index"]!.GetValue<int>()));
    }

    [Fact]
    public void AStepWithoutInputSendsNone()
    {
        JsonObject parameters = RuntimeTools.BuildFrameParameters("step", 3, null, new StepOptions());

        Assert.False(parameters.ContainsKey("input"), parameters.ToJsonString());
    }

    [Fact]
    public void InputSurvivesTheJsonRoundTripABatchStepTakes()
    {
        StepOptions options = JsonSerializer.Deserialize<StepOptions>(
            "{\"input\": [{\"type\": \"key\", \"key\": \"A\", \"pressed\": true, \"frame\": 1}]}",
            WebJson
        )!;

        JsonArray sent = Sent(RuntimeTools.BuildFrameParameters("step", 2, null, options));

        Assert.Equal(1, Assert.Single(sent)!["frame"]!.GetValue<int>());
    }

    private static JsonArray Sent(JsonObject parameters) => parameters["input"]!.AsArray();

    private static JsonObject Key(string key, int? frame, bool? pressed)
    {
        JsonObject item = new() { ["type"] = "key", ["key"] = key };
        if (frame is not null)
        {
            item["frame"] = frame;
        }

        if (pressed is not null)
        {
            item["pressed"] = pressed;
        }

        return item;
    }
}
