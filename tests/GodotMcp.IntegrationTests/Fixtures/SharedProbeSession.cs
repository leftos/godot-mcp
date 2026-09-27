using System.Text.Json.Nodes;
using GodotMcp.Server.Session;
using GodotMcp.Server.Tools;
using GodotMcp.TestSupport;

namespace GodotMcp.IntegrationTests.Fixtures;

/// <summary>
/// One quiet InputProbe run shared by a test class, with the machine's real pads shut out (shutOutRealGamepads): launched
/// once, put back to a fresh launch's state by <see cref="ResetAsync"/> before each test, stopped after the last with the
/// probe's tree checked clean.
/// </summary>
public sealed class SharedProbeSession : IAsyncLifetime
{
    private const int ScriptTimeoutMs = 10_000;

    // Frees every root child but the autoloads, loads the main scene again and waits for it, then undoes what a test may
    // have set on the engine. Keys and actions carry no bridge-side state, so they are released here; the mouse buttons
    // and pad buttons and axes the bridge holds are returned, for ResetAsync to release through simulate_input. The
    // bridge's pointer goes back to the origin, since a raw mouse_motion with no relative takes its step from it.
    private const string ResetScript = """
        extends RefCounted


        func execute(scene_tree: SceneTree) -> Variant:
        	scene_tree.paused = false
        	Engine.time_scale = 1.0
        	RenderingServer.render_loop_enabled = true
        	Input.emulate_touch_from_mouse = {EMULATE_TOUCH}
        	_release_keys_and_actions()
        	var root: Window = scene_tree.root
        	root.get_node("GodotMcpBridge").set("_pointer", Vector2.ZERO)
        	root.get_node("GodotMcpBridge/Gestures").set("_ui_baseline", {})
        	for child: Node in root.get_children():
        		if not ProjectSettings.has_setting("autoload/" + child.name):
        			root.remove_child(child)
        			child.queue_free()
        	scene_tree.change_scene_to_file(ProjectSettings.get_setting("application/run/main_scene"))
        	for _frame in 120:
        		await scene_tree.process_frame
        		if scene_tree.current_scene != null:
        			break
        	return {"reloaded": scene_tree.current_scene != null, "held": _held(root)}


        func _release_keys_and_actions() -> void:
        	var codes: Array = range(32, 0x300) + range(KEY_SPECIAL, KEY_SPECIAL + 0x200)
        	for code: int in codes:
        		var key := code as Key
        		if Input.is_key_pressed(key) or Input.is_physical_key_pressed(key) or Input.is_key_label_pressed(key):
        			var up := InputEventKey.new()
        			up.keycode = key
        			up.physical_keycode = key
        			up.key_label = key
        			up.pressed = false
        			Input.parse_input_event(up)
        	Input.flush_buffered_events()
        	for action: StringName in InputMap.get_actions():
        		if Input.is_action_pressed(action):
        			Input.action_release(action)


        func _held(root: Window) -> Array:
        	var events: Array = []
        	var mask: int = root.get_node("GodotMcpBridge").get("_held_mask")
        	for button in [1, 2, 3]:
        		if mask & (1 << (button - 1)):
        			events.append({"type": "mouse_button", "x": 0, "y": 0, "button": button, "pressed": false})
        	var pads: Node = root.get_node("GodotMcpBridge/Gamepad")
        	var names: Dictionary = (pads.get_script() as Script).get_script_constant_map()
        	var held_buttons: Dictionary = pads.get("_held_buttons")
        	for key: Vector2i in held_buttons:
        		var button_name: String = names["JOY_BUTTON_NAMES"][key.y]
        		events.append({"type": "joypad_button", "button": button_name, "pressed": false, "device": key.x})
        	var axes: Dictionary = pads.get("_axes")
        	for key: Vector2i in axes:
        		if axes[key] != 0.0:
        			var axis_name: String = names["JOY_AXIS_NAMES"][key.y]
        			events.append({"type": "joypad_motion", "axis": axis_name, "value": 0, "device": key.x})
        	return events

        """;

    private const string ClearUiBaselineScript =
        "extends RefCounted\n\n\nfunc execute(scene_tree: SceneTree) -> Variant:\n\t"
        + "scene_tree.root.get_node(\"GodotMcpBridge/Gestures\").set(\"_ui_baseline\", {})\n\treturn true\n";

    private readonly ProbeProject _probe = new();
    private readonly SessionHarness _harness = new();
    private readonly RuntimeTools _tools;
    private bool _emulateTouch;

    public SharedProbeSession() => _tools = new RuntimeTools(_harness.Sessions, TestCSharp.Unused());

    /// <summary>The probe project the shared run plays.</summary>
    public string ProbeDirectory => _probe.Directory;

    /// <summary>The error feed's sequence number at the end of the last reset: <c>get_errors</c> from it sees only the test's own.</summary>
    public long ErrorCursor { get; private set; }

    internal SessionRegistry Sessions => _harness.Sessions;

    public async ValueTask InitializeAsync() => await LaunchAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _harness.DisposeAsync();
            Assert.Equal(string.Empty, Git.Status(_probe.Directory));
        }
        finally
        {
            _probe.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Puts the shared game back to the state a fresh launch has; relaunches it, and says so in the test's output, when a
    /// test left it dead.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellation)
    {
        if (!Sessions.List().Any(session => session.Live))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("The shared InputProbe session was not live, so it was launched again.");
            await LaunchAsync(cancellation);
        }

        string script = ResetScript.Replace("{EMULATE_TOUCH}", _emulateTouch ? "true" : "false", StringComparison.Ordinal);
        JsonNode reset = JsonNode.Parse(await _tools.RunScriptAsync(script, ScriptTimeoutMs, cancellationToken: cancellation))!["value"]!;
        Assert.True(reset["reloaded"]!.GetValue<bool>(), "The shared InputProbe's main scene did not load again within 120 frames.");
        JsonObject[] releases = [.. reset["held"]!.AsArray().Select(release => release!.AsObject())];
        if (releases.Length > 0)
        {
            await _tools.SimulateInputAsync(releases, cancellationToken: cancellation);
            // The releases are a gesture, which takes a uiChanged baseline; a fresh launch has none.
            await _tools.RunScriptAsync(ClearUiBaselineScript, ScriptTimeoutMs, cancellationToken: cancellation);
        }

        ErrorCursor = Sessions.Resolve(null).Errors.Mark();
    }

    private async Task LaunchAsync(CancellationToken cancellation)
    {
        await Sessions.LaunchAsync(new LaunchRequest(_probe.Directory, null, [], [], true, true, Prepare: true), null, cancellation);
        string emulate = await _tools.RunScriptAsync(
            "extends RefCounted\n\n\nfunc execute(_scene_tree: SceneTree) -> Variant:\n\treturn Input.emulate_touch_from_mouse\n",
            ScriptTimeoutMs,
            cancellationToken: cancellation
        );
        _emulateTouch = JsonNode.Parse(emulate)!["value"]!.GetValue<bool>();
        ErrorCursor = Sessions.Resolve(null).Errors.Mark();
    }
}
