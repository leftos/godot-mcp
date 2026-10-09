# gdlint: disable=private-method-call
# The tests call the clock's private _with_capture directly.
extends "res://gd_test.gd"
## The frame readers on a headless game (bridge/godot_mcp_frame.gd and the clock's captures in
## godot_mcp_time.gd): gdtest runs Godot with --headless, so every test here meets the real
## headless display server, which draws no frames. The nodes are never added to the tree, so a
## reader that reaches for the viewport or the tree logs an error, which fails its test.

const HEADLESS_REFUSAL := (
	"a headless game draws no frames, so there is nothing to capture; read state with "
	+ "get_game_state, get_ui_elements or run_script, or run the scene windowed."
)
const HEADLESS_WARNING := (
	"a headless game draws no frames, so no screenshot was taken; read state with "
	+ "get_game_state, get_ui_elements or run_script, or run the scene windowed."
)

var _frame_script: GDScript = load_bridge_script("godot_mcp_frame.gd")
var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_the_display_server_is_headless() -> void:
	assert_eq(DisplayServer.get_name(), "headless", "gdtest's display server")


func test_grab_frame_is_null_on_a_headless_game_without_an_engine_error() -> void:
	var frame: Node = _frame_script.new()
	assert_eq(frame.grab_frame(), null, "no frame to grab")
	frame.free()


func test_the_refusal_names_headless() -> void:
	var frame: Node = _frame_script.new()
	assert_eq(frame.headless_refusal(), HEADLESS_REFUSAL, "the refusal")
	assert_eq(frame.headless_warning(), HEADLESS_WARNING, "the warning")
	frame.free()


func test_saving_no_image_on_a_headless_game_gives_the_refusal() -> void:
	var frame: Node = _frame_script.new()
	assert_eq(frame.save_screenshot(null, {}), HEADLESS_REFUSAL, "save_screenshot")
	frame.free()


func test_capture_frames_refuses_a_headless_game() -> void:
	var rig: Dictionary = _clock_rig()
	var reply: Dictionary = rig["time"].capture_frames({"points": [0.1]})
	assert_eq(reply, {"error": HEADLESS_REFUSAL}, "capture_frames")
	_free_clock_rig(rig)


func test_a_step_with_a_screenshot_refuses_a_headless_game() -> void:
	var rig: Dictionary = _clock_rig()
	var time: Node = rig["time"]
	var shot: Dictionary = time.frame_control({"action": "step", "screenshot": true})
	assert_eq(shot, {"error": HEADLESS_REFUSAL}, "a step with a screenshot")
	var plain: Dictionary = time.frame_control({"action": "step"})
	assert_eq(plain, {"error": time.NO_DRAW_REFUSAL}, "a step without one keeps the no-draw text")
	_free_clock_rig(rig)


func test_a_waits_screenshot_on_a_headless_game_is_a_warning() -> void:
	var rig: Dictionary = _clock_rig()
	var reply: Dictionary = rig["time"]._with_capture(null, {}, {"met": true})
	assert_eq(reply, {"result": {"met": true, "warning": HEADLESS_WARNING}}, "the wait's result")
	_free_clock_rig(rig)


func test_take_screenshot_refuses_a_headless_game_before_it_waits_for_a_frame() -> void:
	var bridge: Node = _recording_bridge()
	var frame: Node = _waiting_frame()
	bridge._frame = frame
	bridge._handle_screenshot(7, {})
	var refused: Dictionary = {"id": 7, "ok": false, "error": HEADLESS_REFUSAL}
	assert_eq(bridge.sent, [refused], "the refusal, sent as the call returns")
	assert_eq(frame.waited, false, "no frame was waited for")
	frame.free()
	bridge.free()


## A bridge whose frames are kept in its sent Array rather than written to a socket: a subclass
## with no _ready, so it neither frees itself nor dials.
func _recording_bridge() -> Node:
	var source: String = (
		"\n"
		. join(
			[
				'extends "%s"' % load_bridge_script("godot_mcp_bridge.gd").resource_path,
				"",
				"var sent: Array = []",
				"",
				"",
				"func _ready() -> void:",
				"\tpass",
				"",
				"",
				"func _send(message: Dictionary) -> void:",
				"\tsent.append(message)",
			]
		)
	)
	return _compile(source)


## A frame module whose wait for a drawn frame notes that it began and never ends, so a request
## that waits for a frame cannot reply before its call returns.
func _waiting_frame() -> Node:
	var source: String = (
		"\n"
		. join(
			[
				'extends "%s"' % _frame_script.resource_path,
				"",
				"signal never",
				"",
				"var waited: bool = false",
				"",
				"",
				"func wait_for_drawn_frame() -> void:",
				"\twaited = true",
				"\tawait never",
			]
		)
	)
	return _compile(source)


## An instance of a script compiled from source.
func _compile(source: String) -> Node:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()


## A clock outside the tree whose bridge holds a frame module, as the bridge's own children do.
func _clock_rig() -> Dictionary:
	var bridge: Node = _compile("extends Node\n\nvar _frame: Node\n")
	bridge._frame = _frame_script.new()
	var time: Node = _time_script.new()
	time.bridge = bridge
	return {"time": time, "bridge": bridge}


func _free_clock_rig(rig: Dictionary) -> void:
	rig["time"].free()
	rig["bridge"]._frame.free()
	rig["bridge"].free()
