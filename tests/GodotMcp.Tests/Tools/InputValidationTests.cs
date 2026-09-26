using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.Server.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace GodotMcp.Tests.Tools;

/// <summary>The input tools' argument checks, which refuse before anything reaches a game; no Godot runs here.</summary>
public sealed class InputValidationTests : IDisposable
{
    private static readonly InputTarget Point = new(null, 10, 10);
    private static readonly string[] Buttons = ["left", "right", "middle"];
    private static readonly string[] EventTypes = ["key", "mouse_button", "mouse_motion", "action", "click_element", "wait"];
    private readonly BridgeListener _listener = new(NullLogger<BridgeListener>.Instance);
    private readonly GodotSession _session;
    private readonly RuntimeTools _tools;

    public InputValidationTests()
    {
        _session = new GodotSession(_listener, NullLogger<GodotSession>.Instance);
        _tools = new RuntimeTools(_session);
    }

    public void Dispose()
    {
        _session.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void KnownButtonsPass() => Assert.All(Buttons, button => Assert.Equal(button, RuntimeTools.CheckButton(button)));

    [Fact]
    public void AnUnknownButtonIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckButton("Left"));

        Assert.Equal("button 'Left' is not one of left, right, middle.", refused.Message);
    }

    [Fact]
    public void ModifiersAreLowerCased() =>
        Assert.Equal("[\"shift\",\"ctrl\",\"alt\",\"meta\"]", RuntimeTools.CheckModifiers(["Shift", "CTRL", "alt", "Meta"]).ToJsonString());

    [Fact]
    public void AnUnknownModifierIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckModifiers(["shift", "super"]));

        Assert.Equal("modifier 'super' is not one of shift, ctrl, alt, meta.", refused.Message);
    }

    [Fact]
    public void EveryEventTypePasses() =>
        Assert.All(
            EventTypes,
            type =>
            {
                JsonObject item = Event(type);
                Assert.Same(item, RuntimeTools.CheckEvent(item, 0));
            }
        );

    [Fact]
    public void ANullEventIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(null, 3));

        Assert.Equal("events[3] is null.", refused.Message);
    }

    [Fact]
    public void AnEventWithoutATypeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(new JsonObject { ["x"] = 1 }, 0));

        Assert.StartsWith("events[0] has type 'null'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventOfAnUnknownTypeIsRefused()
    {
        McpException refused = Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(Event("scroll"), 1));

        Assert.Equal("events[1] has type 'scroll'; the types are key, mouse_button, mouse_motion, action, click_element, wait.", refused.Message);
    }

    [Fact]
    public void AnEventWhoseTypeIsNotAStringIsRefused() =>
        Assert.Throws<McpException>(() => RuntimeTools.CheckEvent(new JsonObject { ["type"] = 5 }, 0));

    [Fact]
    public async Task SimulateInputRefusesAnEmptyList()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() => _tools.SimulateInputAsync([], TestContext.Current.CancellationToken));

        Assert.StartsWith("events is empty", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyRefusesAnUnknownAction()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.KeyAsync("A", "hold", null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("action 'hold' is not one of tap, press, release.", refused.Message);
    }

    [Fact]
    public async Task MouseButtonRefusesTap()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.MouseButtonAsync(Point, "left", "tap", TestContext.Current.CancellationToken)
        );

        Assert.Equal("action 'tap' is not one of press, release.", refused.Message);
    }

    [Fact]
    public async Task DragRefusesANegativeDuration()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.DragAsync(Point, Point, -1, "left", TestContext.Current.CancellationToken)
        );

        Assert.Equal("durationMs must be 0 or more; got -1.", refused.Message);
    }

    [Fact]
    public async Task TypeTextRefusesEmptyText() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.TypeTextAsync(string.Empty, TestContext.Current.CancellationToken));

    [Fact]
    public async Task AValidCallWithoutASessionSaysNoneIsRunning()
    {
        McpException refused = await Assert.ThrowsAsync<McpException>(() =>
            _tools.ClickAsync(Point, "left", false, TestContext.Current.CancellationToken)
        );

        Assert.StartsWith("No Godot session is running", refused.Message, StringComparison.Ordinal);
    }

    private static JsonObject Event(string type) => new() { ["type"] = type };
}
