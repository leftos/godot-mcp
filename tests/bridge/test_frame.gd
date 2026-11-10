# gdlint: disable=private-method-call
# The tests call the clock's private _with_capture and the frame's private _saved and
# _with_sizes_of directly.
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
const SCALE_MODE_REFUSAL := (
	"scale needs the project's stretch mode canvas_items (it is %s): any other mode re-lays out "
	+ "the game or adds no detail; relaunch with --resolution instead"
)
const SCALE_WINDOW_REFUSAL := (
	"scale needs a windowed, not maximized or fullscreen, window: Windows does not resize it; "
	+ "relaunch with --resolution instead"
)
const SCALE_RECORDING_REFUSAL := (
	"scale is refused while the run records: " + "the resize would be in the clip"
)
const SAVE_FAILURE := "saving the screenshot failed: the disk is full"

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


func test_a_saved_capture_says_the_window_and_viewport_it_was_taken_in() -> void:
	var root: Window = (Engine.get_main_loop() as SceneTree).root
	var image := Image.create(4, 3, false, Image.FORMAT_RGBA8)
	var saved: Dictionary = _frame_script._saved({"image": image, "path": "shot.png"})
	var window: Vector2i = root.size
	var viewport: Vector2 = root.get_visible_rect().size
	assert_eq(saved.get("window"), {"width": window.x, "height": window.y}, "the window")
	var viewport_size: Dictionary = {"width": roundi(viewport.x), "height": roundi(viewport.y)}
	assert_eq(saved.get("viewport"), viewport_size, "the viewport")
	for size: String in ["window", "viewport"]:
		for side: String in ["width", "height"]:
			var value: Variant = saved.get(size, {}).get(side)
			assert_true(value is int, "%s.%s is an int: %s" % [size, side, value])
	assert_eq([saved["width"], saved["height"]], [4, 3], "the capture's own size")


func test_a_saved_capture_says_the_sizes_stamped_at_its_grab_not_the_current_ones() -> void:
	var image := Image.create(4, 3, false, Image.FORMAT_RGBA8)
	_frame_script.stamp_sizes(image, Vector2i(1366, 768), Vector2i(1920, 1080))
	var saved: Dictionary = _frame_script._saved({"image": image, "path": "shot.png"})
	assert_eq(saved.get("window"), {"width": 1366, "height": 768}, "the stamped window")
	assert_eq(saved.get("viewport"), {"width": 1920, "height": 1080}, "the stamped viewport")


func test_a_crop_keeps_the_stamped_sizes() -> void:
	var image := Image.create(8, 8, false, Image.FORMAT_RGBA8)
	_frame_script.stamp_sizes(image, Vector2i(1366, 768), Vector2i(1920, 1080))
	var region: Image = image.get_region(Rect2i(2, 2, 4, 4))
	var kept: Image = _frame_script._with_sizes_of(image, region)
	var meta: StringName = _frame_script.SIZES_META
	assert_eq(kept.get_meta(meta, {}), image.get_meta(meta), "the crop's stamped sizes")
	assert_eq(kept.get_size(), Vector2i(4, 4), "the crop's own size")


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


func test_a_scratch_untils_failed_save_is_a_warning_on_the_met_result() -> void:
	var rig: Dictionary = _clock_rig()
	rig["bridge"]._frame.free()
	rig["bridge"]._frame = _failing_save_frame()
	var image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	var params: Dictionary = {"limitGameMs": 500}
	var reply: Dictionary = await rig["time"]._with_capture(image, params, {"met": true})
	assert_true(not reply.has("error"), "a met until stays met: %s" % reply)
	assert_eq(reply, {"result": {"met": true, "warning": SAVE_FAILURE}}, "the until's result")
	_free_clock_rig(rig)


func test_a_wait_fors_failed_save_stays_its_error() -> void:
	var rig: Dictionary = _clock_rig()
	rig["bridge"]._frame.free()
	rig["bridge"]._frame = _failing_save_frame()
	var image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	var reply: Dictionary = await rig["time"]._with_capture(image, {}, {"met": true})
	assert_eq(reply, {"error": SAVE_FAILURE}, "wait_for's reply")
	_free_clock_rig(rig)


func test_take_screenshot_refuses_a_headless_game_before_it_waits_for_a_frame() -> void:
	var bridge: Node = _recording_bridge()
	var frame: Node = _waiting_frame()
	bridge._frame = frame
	bridge._handle_screenshot(7, {})
	var now: int = Engine.get_process_frames()
	var refused: Dictionary = {
		"id": 7, "ok": false, "error": HEADLESS_REFUSAL, "frames": [now, now]
	}
	assert_eq(bridge.sent, [refused], "the refusal, sent as the call returns")
	assert_eq(frame.waited, false, "no frame was waited for")
	frame.free()
	bridge.free()


func test_a_scaled_take_screenshot_still_refuses_a_headless_game_first() -> void:
	var bridge: Node = _recording_bridge()
	var frame: Node = _waiting_frame()
	bridge._frame = frame
	bridge._handle_screenshot(8, {"scale": 2.0})
	var now: int = Engine.get_process_frames()
	var refused: Dictionary = {
		"id": 8, "ok": false, "error": HEADLESS_REFUSAL, "frames": [now, now]
	}
	assert_eq(bridge.sent, [refused], "the headless refusal, not a scale one")
	assert_eq(frame.waited, false, "no frame was waited for")
	frame.free()
	bridge.free()


func test_scale_is_refused_under_any_stretch_mode_but_canvas_items() -> void:
	var frame: Node = _frame_script.new()
	var windowed: int = Window.MODE_WINDOWED
	var disabled: String = frame.scale_refusal(Window.CONTENT_SCALE_MODE_DISABLED, windowed, "")
	var viewport: String = frame.scale_refusal(Window.CONTENT_SCALE_MODE_VIEWPORT, windowed, "")
	assert_eq(disabled, SCALE_MODE_REFUSAL % "disabled", "disabled")
	assert_eq(viewport, SCALE_MODE_REFUSAL % "viewport", "viewport")
	var canvas_items: int = Window.CONTENT_SCALE_MODE_CANVAS_ITEMS
	assert_eq(frame.scale_refusal(canvas_items, windowed, ""), "", "canvas_items, windowed")
	frame.free()


func test_scale_is_refused_on_a_maximized_or_fullscreen_window() -> void:
	var frame: Node = _frame_script.new()
	var canvas_items: int = Window.CONTENT_SCALE_MODE_CANVAS_ITEMS
	var modes: Array[int] = [
		Window.MODE_MAXIMIZED, Window.MODE_FULLSCREEN, Window.MODE_EXCLUSIVE_FULLSCREEN
	]
	for mode: int in modes:
		var refusal: String = frame.scale_refusal(canvas_items, mode, "")
		assert_eq(refusal, SCALE_WINDOW_REFUSAL, "window mode %d" % mode)
	frame.free()


func test_scale_is_refused_while_movie_maker_records() -> void:
	var frame: Node = _frame_script.new()
	var canvas_items: int = Window.CONTENT_SCALE_MODE_CANVAS_ITEMS
	var refusal: String = frame.scale_refusal(canvas_items, Window.MODE_WINDOWED, "clip.avi")
	assert_eq(refusal, SCALE_RECORDING_REFUSAL, "a Movie Maker run")
	frame.free()


func test_a_crop_maps_to_the_scaled_frames_pixels() -> void:
	var twice := Transform2D.IDENTITY.scaled(Vector2(2, 2))
	var frame_size := Vector2i(1280, 720)
	var square: Rect2i = _frame_script.crop_rect(twice, Rect2i(400, 40, 120, 80), frame_size)
	assert_eq(square, Rect2i(800, 80, 240, 160), "the red square at 2x")
	var edge: Rect2i = _frame_script.crop_rect(twice, Rect2i(600, 300, 100, 100), frame_size)
	assert_eq(edge, Rect2i(1200, 600, 80, 120), "a crop past the frame's edge, clipped")


func test_a_scaled_shot_reports_the_scale_reached() -> void:
	var original := Vector2i(640, 360)
	var image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	var full: Dictionary = _frame_script.scaled_shot(
		image, original, Vector2i(1280, 720), Vector2i(1280, 720), 2.0
	)
	assert_eq(full, {"image": image, "scale": 2.0}, "the window grew as asked")
	var clamped: Dictionary = _frame_script.scaled_shot(
		image, original, Vector2i(2560, 1440), Vector2i(1500, 900), 4.0
	)
	var warning: String = (
		"the window could only grow to 1500x900, not 2560x1440, "
		+ "so the capture is 2.34x, not 4x"
	)
	assert_eq(clamped, {"image": image, "scale": 2.34, "warning": warning}, "the screen clamped it")


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


## A frame module on a game that draws, whose every save fails with SAVE_FAILURE.
func _failing_save_frame() -> Node:
	var source: String = (
		"\n"
		. join(
			[
				"extends Node",
				"",
				"",
				"func headless_warning() -> String:",
				'\treturn ""',
				"",
				"",
				"func save_screenshot(_image: Image, _params: Dictionary) -> Variant:",
				'\treturn "%s"' % SAVE_FAILURE,
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
