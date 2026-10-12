# gdlint: disable=max-public-methods, private-method-call
# The tests call the step's and the clock's private helpers directly.
extends "res://gd_test.gd"
## The step (bridge/godot_mcp_step.gd, the clock's Step child) and the clock's running mark it
## runs under. The nodes are never added to the tree: a step's loops run on a stand-in tree and a
## stand-in draw signal the tests emit by hand, one frame at a time.

## Stands in for RenderingServer.frame_post_draw, which a headless game never emits.
signal test_draw

## wait_for's refusal of a uiChanged condition no gesture has taken a baseline for.
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ "uiChanged wait was met; send the input first"
)
## wait_for's refusal of a signal condition on a node a bare name does not find.
const MISSING_NODE := (
	"No node named 'Missing' anywhere under /root in the running game; "
	+ "get_scene_tree lists the nodes' paths."
)
## The stall text a step of 5 frames that counted 3 makes for a draw that never came.
const STALLED_3_OF_5 := (
	"The step stopped after 3 of 5 frames: no frame was drawn for too long "
	+ "(is the window minimized?); the game is left paused."
)

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_cancel_ends_a_running_step_as_its_deadline() -> void:
	var time: Node = _time_script.new()
	var params: Dictionary = {"action": "step", "count": 5}
	var woken: Array = []
	time.step_woken.connect(func(arrived: bool) -> void: woken.append(arrived))
	time._running = "step"
	time._running_params = params
	assert_true(time.cancel(params), "the running step is cancelled")
	assert_true(time._deadline_passed, "as its deadline passing would")
	assert_eq(woken, [false], "the step's wait is woken as by its deadline")
	time.free()


func test_cancel_ignores_another_requests_params() -> void:
	var time: Node = _time_script.new()
	var running: Dictionary = {"action": "step", "count": 5}
	var woken: Array = []
	time.step_woken.connect(func(arrived: bool) -> void: woken.append(arrived))
	assert_true(not time.cancel(running), "nothing runs")
	time._running = "step"
	time._running_params = running
	assert_true(not time.cancel(running.duplicate()), "an equal Dictionary is another request's")
	assert_true(not time._deadline_passed, "the running step goes on")
	assert_eq(woken, [], "nothing is woken")
	time.free()


func test_a_step_until_is_checked_after_each_frame_and_ends_at_the_draw_that_met() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [checks[0] >= 3, checks[0]]
	var run: Dictionary = _start(rig, {"count": 10}, _until(probe))
	assert_eq(checks[0], 0, "no check before the first frame")
	_frame(rig)
	_frame(rig)
	assert_eq(checks[0], 2, "one check a counted frame")
	assert_true(not run["ended"].has("error"), "the step goes on while unmet")
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "met at the third draw, the step ends")
	assert_eq(run["result"]["processFrames"], 3, "the frames stepped")
	assert_eq(run["result"]["met"], true, "met")
	assert_eq(run["result"]["value"], 3, "the met check's value")
	assert_true(rig["tree"].paused, "paused on the met frame")
	_frame(rig)
	assert_eq(checks[0], 3, "no check after the step ended")
	_free_step_rig(rig)


func test_a_step_until_already_true_still_runs_one_frame() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [true, "ready"]
	var run: Dictionary = _start(rig, {"count": 1000}, _until(probe))
	assert_eq(checks[0], 0, "not checked at the step's start")
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends after its first frame")
	assert_eq(run["result"]["processFrames"], 1, "one frame ran")
	assert_eq(run["result"]["value"], "ready", "the value seen")
	_free_step_rig(rig)


func test_a_physics_step_until_is_checked_at_the_tick_after_a_counted_one() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [checks[0] >= 2, checks[0]]
	var run: Dictionary = _start(rig, {"count": 10, "unit": "physics"}, _until(probe))
	rig["tree"].physics_frame.emit()
	assert_eq(checks[0], 0, "no check before the first tick has run")
	rig["tree"].physics_frame.emit()
	assert_eq(checks[0], 1, "checked as the second tick starts")
	assert_true(not rig["tree"].paused, "unmet, the ticks go on")
	rig["tree"].physics_frame.emit()
	assert_eq(run["ended"], {"error": ""}, "met at the third tick's start")
	assert_eq(run["result"]["physicsFrames"], 2, "the ticks that ran")
	assert_eq(run["result"]["met"], true, "met")
	assert_true(rig["tree"].paused, "re-paused there, before that tick runs")
	_free_step_rig(rig)


func test_a_step_until_never_met_runs_count_frames_and_answers_last() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [false, "idle"]
	var run: Dictionary = _start(rig, {"count": 2}, _until(probe))
	_frame(rig)
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "not met is a result")
	assert_eq(run["result"]["processFrames"], 2, "count frames ran")
	assert_eq(run["result"]["met"], false, "not met")
	assert_eq(run["result"]["last"], "idle", "the last value seen")
	assert_true(not run["result"].has("value"), "no value")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_every_step_result_has_the_frame_it_stopped_on() -> void:
	var rig: Dictionary = _step_rig()
	var run: Dictionary = _start(rig, {"count": 1}, {})
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(run["result"]["frame"], Engine.get_process_frames(), "the engine's frame at the stop")
	assert_true(not run["result"].has("met"), "no until, no met")
	_free_step_rig(rig)


func test_a_step_until_that_cannot_be_met_fails_the_step_with_its_text() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [false, null, "no node 'Gone' in the running game"]
	var run: Dictionary = _start(rig, {"count": 10}, _until(probe))
	_frame(rig)
	assert_eq(run["ended"], {"error": "no node 'Gone' in the running game"}, "the check's text")
	assert_eq(run["result"]["processFrames"], 1, "the one frame it ran")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_a_physics_step_until_reports_met_when_the_game_paused_on_the_met_tick() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [true, "gone"]
	var run: Dictionary = _start(rig, {"count": 10, "unit": "physics"}, _until(probe))
	rig["tree"].physics_frame.emit()
	rig["tree"].paused = true
	rig["tree"].physics_frame.emit()
	assert_eq(run["ended"], {"error": ""}, "met, not the game's own pause change")
	assert_eq(run["result"]["met"], true, "met")
	assert_eq(run["result"]["value"], "gone", "the met check's value")
	assert_eq(run["result"]["physicsFrames"], 1, "the tick that ran")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_a_step_until_refuses_a_ui_changed_with_no_baseline_before_a_frame() -> void:
	var rig: Dictionary = _step_rig()
	var refused: Variant = rig["time"]._stepper._until_of(
		{"until": {"kind": "uiChanged", "uiChanged": true}}
	)
	assert_eq(refused, NO_UI_BASELINE, "wait_for's no-baseline text")
	assert_true(rig["time"]._running.is_empty(), "no step is marked running")
	assert_true(rig["tree"].paused, "no frame ran")
	_free_step_rig(rig)


func test_a_step_until_refuses_a_signal_on_a_missing_node_before_a_frame() -> void:
	var rig: Dictionary = _step_rig()
	var refused: Variant = rig["time"]._stepper._until_of(
		{"until": {"kind": "signal", "node": "Missing", "signal": "fired"}}
	)
	assert_eq(refused, MISSING_NODE, "wait_for's not-found text")
	assert_true(rig["time"]._running.is_empty(), "no step is marked running")
	assert_true(rig["tree"].paused, "no frame ran")
	_free_step_rig(rig)


func test_a_step_until_releases_its_signal_however_the_step_ends() -> void:
	for ending: String in ["met", "count", "cancel"]:
		var rig: Dictionary = _step_rig()
		var until: Variant = _signal_until(rig)
		assert_true(until is Dictionary, "the node and its signal are taken")
		assert_true(_armed(rig), "the until connects the signal")
		_start(rig, {"count": 2}, until)
		_end_step(rig, ending)
		rig["time"]._stepper._release_until(until)
		assert_true(not _armed(rig), "%s: the signal is released" % ending)
		_free_step_rig(rig)


func test_a_stalled_capture_names_the_requested_frames_not_the_frames_run() -> void:
	var rig: Dictionary = _step_rig()
	rig["time"]._deadline_passed = true
	var error: String = await rig["time"]._stepper._save_capture(null, true, 3, 5, {}, {})
	assert_eq(error, STALLED_3_OF_5, "the requested count, not the frames counted")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_riding_input_is_sent_at_its_frames_process_frame_in_list_order() -> void:
	var rig: Dictionary = _step_rig()
	var input: Array = [_key(0, "A", 0), _key(1, "B", 2), _key(2, "C", 2)]
	var run: Dictionary = _start(rig, {"count": 4, "input": input}, {})
	assert_eq(_sent_keys(rig), [], "nothing is sent before the first stepped frame")
	rig["tree"].process_frame.emit()
	assert_eq(_sent_keys(rig), ["A"], "frame 0's event at its process_frame")
	test_draw.emit()
	_frame(rig)
	assert_eq(_sent_keys(rig), ["A"], "frame 1 sends nothing")
	rig["tree"].process_frame.emit()
	assert_eq(_sent_keys(rig), ["A", "B", "C"], "frame 2's events at the third, before its draw")
	test_draw.emit()
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_true(not run["result"].has("inputNotSent"), "every event was sent")
	_free_step_rig(rig)


func test_an_until_met_early_leaves_later_frames_events_unsent_and_counts_them() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [checks[0] >= 2, checks[0]]
	var input: Array = [_key(0, "A", 0), _key(1, "B", 1), _key(2, "C", 3), _key(3, "D", 4)]
	var run: Dictionary = _start(rig, {"count": 6, "input": input}, _until(probe))
	_frame(rig)
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "met at the second frame's draw")
	assert_eq(_sent_keys(rig), ["A", "B"], "the later frames' events are not sent")
	assert_eq(run["result"]["inputNotSent"], 2, "their count")
	assert_eq(run["result"]["held"], _held_keys(["A", "B"]), "what the sent events hold")
	_free_step_rig(rig)


func test_held_is_left_out_when_the_riding_input_leaves_nothing_pressed() -> void:
	var rig: Dictionary = _step_rig()
	var release: Dictionary = _key(1, "A", 1)
	release["pressed"] = false
	var run: Dictionary = _start(rig, {"count": 2, "input": [_key(0, "A", 0), release]}, {})
	_frame(rig)
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_true(not run["result"].has("held"), "nothing is held")
	assert_true(not run["result"].has("inputNotSent"), "nothing is left unsent")
	_free_step_rig(rig)


func test_held_lists_keys_mouse_buttons_pad_buttons_and_actions_by_name() -> void:
	InputMap.add_action(&"gdtest_ride")
	var rig: Dictionary = _step_rig()
	var input: Array = [
		_key(0, "K", 0),
		{"type": "mouse_button", "button": "left", "x": 4, "y": 6, "pressed": true, "index": 1},
		{"type": "joypad_button", "button": "a", "device": 0, "pressed": true, "index": 2},
		{"type": "action", "action": "gdtest_ride", "pressed": true, "index": 3},
	]
	var run: Dictionary = _start(rig, {"count": 1, "input": input}, {})
	_frame(rig)
	var held: Dictionary = {
		"keys": ["K"],
		"mouseButtons": ["left"],
		"padButtons": [{"device": 0, "button": "A"}],
		"actions": ["gdtest_ride"],
	}
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(run["result"]["held"], held, "each held kind by name")
	_free_step_rig(rig)
	InputMap.erase_action(&"gdtest_ride")


func test_gesture_playing_is_set_while_riding_input_plays_and_cleared_however_it_ends() -> void:
	for ending: String in ["met", "count", "deadline", "error"]:
		var rig: Dictionary = _step_rig()
		var input: Array = [_key(0, "A", 0)]
		if ending == "error":
			input.append({"type": "key", "key": "NoSuchKey", "pressed": true, "index": 1})
		var probe := func() -> Array: return [ending == "met", null]
		var run: Dictionary = _start(rig, {"count": 2, "input": input}, _until(probe))
		assert_true(rig["bridge"]._gesture_playing, "%s: set while the step runs" % ending)
		if ending == "deadline":
			rig["time"]._deadline_passed = true
		_frame(rig)
		if ending == "count":
			_frame(rig)
		assert_true(run["ended"].has("error"), "%s: the step ended" % ending)
		assert_true(not rig["bridge"]._gesture_playing, "%s: cleared after" % ending)
		_free_step_rig(rig)


func test_a_step_without_riding_input_leaves_gesture_playing_alone() -> void:
	var rig: Dictionary = _step_rig()
	rig["bridge"]._gesture_playing = true
	var run: Dictionary = _start(rig, {"count": 1}, {})
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_true(rig["bridge"]._gesture_playing, "a gesture's mark is not cleared")
	_free_step_rig(rig)


func test_a_ui_changed_until_with_riding_input_takes_the_baseline_first() -> void:
	var rig: Dictionary = _step_rig()
	var params: Dictionary = {
		"input": [_key(0, "A", 0)], "until": {"kind": "uiChanged", "uiChanged": true}
	}
	var until: Variant = rig["time"]._stepper._until_with_baseline(params)
	assert_true(until is Dictionary, "no refusal: the baseline was taken before the until")
	assert_true(rig["bridge"]._gestures.has_ui_baseline(), "the baseline is pending")
	rig["bridge"]._gestures._ui_baseline = {"kept": true}
	rig["time"]._stepper._until_with_baseline(params)
	assert_eq(rig["bridge"]._gestures._ui_baseline, {"kept": true}, "a pending one is kept")
	var plain: Dictionary = {"until": {"kind": "uiChanged", "uiChanged": true}}
	rig["bridge"]._gestures._ui_baseline = {}
	assert_eq(rig["time"]._stepper._until_with_baseline(plain), NO_UI_BASELINE, "no input, none")
	_free_step_rig(rig)


func test_an_event_the_bridge_cannot_play_fails_the_step_at_its_frame() -> void:
	var rig: Dictionary = _step_rig()
	var input: Array = [
		_key(0, "A", 0),
		{"type": "key", "key": "NoSuchKey", "pressed": true, "frame": 1, "index": 1},
		_key(2, "B", 1),
		_key(3, "C", 2),
	]
	var run: Dictionary = _start(rig, {"count": 4, "input": input}, {})
	_frame(rig)
	assert_true(not run["ended"].has("error"), "frame 0 plays")
	_frame(rig)
	var reason: String = (
		"unknown key 'NoSuchKey'. %s" % load_bridge_script("godot_mcp_input.gd").UNKNOWN_KEY_HINT
	)
	var held: String = JSON.stringify(_held_keys(["A"]), "", false)
	assert_eq(run["ended"], {"error": "event 1: %s; held: %s" % [reason, held]}, "its text")
	assert_true(rig["tree"].paused, "left paused at that frame")
	_frame(rig)
	assert_eq(_sent_keys(rig), ["A"], "nothing after the failed event is sent")
	_free_step_rig(rig)


func test_a_riding_mouse_button_leaves_the_fired_listener_not_begun() -> void:
	var rig: Dictionary = _step_rig()
	var press: Dictionary = {
		"type": "mouse_button", "button": "left", "x": 4, "y": 6, "pressed": true, "index": 0
	}
	var release: Dictionary = press.duplicate()
	release["pressed"] = false
	release["frame"] = 1
	var run: Dictionary = _start(rig, {"count": 2, "input": [press, release]}, {})
	_frame(rig)
	_frame(rig)
	var fired: Node = rig["bridge"]._gestures._fired
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(_sent_count(rig, "InputEventMouseButton"), 2, "both button events are sent")
	assert_true(not fired._begun, "no fired listing is begun")
	assert_eq(fired.finish(), {}, "nothing is listed")
	_free_step_rig(rig)


func test_a_step_the_game_pauses_mid_input_names_what_is_held_in_its_error() -> void:
	var rig: Dictionary = _step_rig()
	var run: Dictionary = _start(rig, {"count": 4, "input": [_key(0, "A", 0), _key(1, "B", 2)]}, {})
	_frame(rig)
	rig["tree"].paused = true
	_frame(rig)
	var paused: String = "The game changed its own pause state during the step, after 1 of 4 frames."
	var held: String = JSON.stringify(_held_keys(["A"]), "", false)
	assert_eq(run["ended"], {"error": "%s; held: %s" % [paused, held]}, "the pause text, then held")
	assert_eq(_sent_keys(rig), ["A"], "the later frame's event is not sent")
	_free_step_rig(rig)


func test_riding_input_waits_for_counted_frames_while_frames_are_not_drawn() -> void:
	var rig: Dictionary = _step_rig()
	var input: Array = [_key(0, "A", 0), _key(1, "B", 1), _key(2, "C", 2), _key(3, "D", 3)]
	var run: Dictionary = _start(rig, {"count": 4, "input": input}, {})
	_frame(rig)
	for _undrawn in 3:
		rig["tree"].process_frame.emit()
	assert_eq(_sent_keys(rig), ["A", "B"], "frame 1's events once, nothing in the undrawn rest")
	test_draw.emit()
	rig["tree"].process_frame.emit()
	assert_eq(_sent_keys(rig), ["A", "B", "C"], "frame 2's event once frame 1 is counted")
	test_draw.emit()
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(_sent_keys(rig), ["A", "B", "C", "D"], "every event sent, each once")
	_free_step_rig(rig)


func test_a_riding_pad_event_on_a_connected_real_pad_adds_simulate_inputs_warning() -> void:
	var rig: Dictionary = _step_rig()
	var pads: Node = rig["bridge"]._pads
	pads.read_connected_pads = func() -> Array: return [0]
	pads.report = {"warning": "a gesture's before the step"}
	var input: Array = [
		{"type": "joypad_button", "button": "a", "device": 0, "pressed": true, "index": 0}
	]
	var run: Dictionary = _start(rig, {"count": 1, "input": input}, {})
	_frame(rig)
	var warning: String = (
		"Device 0 is a connected real pad (%s); its own input mixes with what is injected there."
		% Input.get_joy_name(0)
	)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(run["result"].get("warning"), warning, "simulate_input's warning, alone")
	_free_step_rig(rig)


func test_a_refused_until_with_riding_input_leaves_no_ui_baseline_behind() -> void:
	var rig: Dictionary = _step_rig()
	var params: Dictionary = {
		"input": [_key(0, "A", 0)],
		"until": {"kind": "signal", "node": "Missing", "signal": "fired"},
	}
	var stepper: Node = rig["time"]._stepper
	assert_eq(stepper._until_with_baseline(params), MISSING_NODE, "the until is refused")
	assert_true(not rig["bridge"]._gestures.has_ui_baseline(), "no baseline is left pending")
	rig["bridge"]._gestures._ui_baseline = {"kept": true}
	stepper._until_with_baseline(params)
	assert_eq(rig["bridge"]._gestures._ui_baseline, {"kept": true}, "a pending one is kept")
	_free_step_rig(rig)


## A clock not in any tree whose Step counts frames on a stand-in tree (paused, with process_frame
## and physics_frame) and at test_draw, and whose bridge holds the conditions, input, inspector,
## JSON, raw event and gamepad modules and a stand-in capture listing what is sent, and a stand-in
## Target node outside any tree whose signal a signal until takes. The input player is a subclass
## whose window points are viewport points and whose UI snapshot is a constant, since it is in no
## tree.
func _step_rig() -> Dictionary:
	var tree: RefCounted = _compile(
		"extends RefCounted\n\nsignal process_frame\nsignal physics_frame\n\nvar paused: bool = true\n"
	)
	var bridge: Node = _compile(
		(
			"extends Node\n\nconst INJECTED_DEVICE := 0x6D6370\n\nvar _conditions: Node\n"
			+ "var _gestures: Node\nvar _inspect: Node\nvar _json: GDScript\nvar _raw_events: Node\n"
			+ "var _pads: Node\nvar _capture: Node\nvar _gesture_playing: bool = false\n"
			+ "var _dispatching: bool = false\nvar _held_mask: int = 0\n"
			+ "var _pointer: Vector2 = Vector2.ZERO\nvar _owns_pointer: bool = false\n"
			+ "var target: Node\n\n\nfunc _find_node(element: String) -> Node:\n"
			+ "\treturn target if element == str(target.name) else null\n"
		)
	)
	var target: Node = _compile("extends Node\n\nsignal fired(value: Variant)\n")
	target.name = "Target"
	bridge.target = target
	bridge._conditions = load_bridge_script("godot_mcp_conditions.gd").new()
	bridge._conditions.bridge = bridge
	_add_input_modules(bridge)
	bridge._inspect = load_bridge_script("godot_mcp_inspect.gd").new()
	bridge._inspect._bridge = bridge
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var time: Node = _time_script.new()
	time.bridge = bridge
	time._stepper.frames_tree = tree
	time._stepper.draws = test_draw
	return {"time": time, "tree": tree, "bridge": bridge, "target": target}


## Gives the rig's bridge its input player (with its fired listener), raw event player, gamepad
## and a capture that keeps every event sent in sent_events.
func _add_input_modules(bridge: Node) -> void:
	var input_script: GDScript = load_bridge_script("godot_mcp_input.gd")
	bridge._gestures = _compile(
		(
			(
				'extends "%s"\n\n\nfunc to_window(point: Vector2) -> Vector2:\n\treturn point\n\n\n'
				% input_script.resource_path
			)
			+ 'func _snapshot_ui() -> Dictionary:\n\treturn {"stand-in": true}\n'
		)
	)
	bridge._gestures.bridge = bridge
	bridge._gestures._fired = load_bridge_script("godot_mcp_fired.gd").new()
	bridge._gestures._fired.bridge = bridge
	bridge._raw_events = load_bridge_script("godot_mcp_raw_events.gd").new()
	bridge._raw_events.bridge = bridge
	bridge._raw_events._gestures = bridge._gestures
	bridge._raw_events._gestures_script = input_script
	bridge._pads = load_bridge_script("godot_mcp_gamepad.gd").new()
	bridge._pads.bridge = bridge
	bridge._capture = _compile(
		(
			"extends Node\n\nvar sent_events: Array = []\n\n\n"
			+ "func sent(event: InputEvent) -> void:\n\tsent_events.append(event)\n"
		)
	)


## Releases what the rig's riding input left held, so Input carries nothing into the next test,
## and frees the rig.
func _free_step_rig(rig: Dictionary) -> void:
	var bridge: Node = rig["bridge"]
	bridge._raw_events.release_all()
	rig["time"].free()
	bridge._conditions.free()
	bridge._gestures._fired.free()
	bridge._gestures.free()
	bridge._raw_events.free()
	bridge._pads.free()
	bridge._capture.free()
	bridge._inspect.free()
	bridge.free()
	rig["target"].free()


## A riding key press of key at frame, the index-th entry of the agent's list.
static func _key(index: int, key: String, frame: int) -> Dictionary:
	return {"type": "key", "key": key, "pressed": true, "frame": frame, "index": index}


## The held report of keys alone.
static func _held_keys(keys: Array) -> Dictionary:
	return {"keys": keys, "mouseButtons": [], "padButtons": [], "actions": []}


## The names of the keys the rig's capture saw sent, pressed or released, in order.
func _sent_keys(rig: Dictionary) -> Array:
	var keys: Array = []
	for event: InputEvent in rig["bridge"]._capture.sent_events:
		if event is InputEventKey:
			keys.append(OS.get_keycode_string((event as InputEventKey).keycode))
	return keys


## How many events of class_name the rig's capture saw sent.
func _sent_count(rig: Dictionary, event_class: String) -> int:
	var sent: Array = rig["bridge"]._capture.sent_events
	return sent.filter(func(event: InputEvent) -> bool: return event.is_class(event_class)).size()


## The until state of a signal condition on the rig's stand-in node, as _guarded_step builds one,
## or the String wait_for's refusal gives when the node or its signal is not there.
func _signal_until(rig: Dictionary) -> Variant:
	var condition: Dictionary = {"kind": "signal", "node": "Target", "signal": "fired"}
	return rig["time"]._stepper._until_of({"until": condition})


## Whether the rig's stand-in node has a listener on its signal.
func _armed(rig: Dictionary) -> bool:
	return not rig["target"].fired.get_connections().is_empty()


## Ends the step in flight as one of its three endings: its until met, its count run out, or the
## deadline a cancel reaches.
func _end_step(rig: Dictionary, ending: String) -> void:
	if ending == "met":
		rig["target"].fired.emit(1)
		_frame(rig)
	elif ending == "count":
		_frame(rig)
		_frame(rig)
	else:
		rig["time"]._deadline_passed = true
		_frame(rig)


## An until state around probe, as the step builds it from an expression condition.
static func _until(probe: Callable) -> Dictionary:
	return {"kind": "expression", "failures": {}, "probe": probe}


## Starts a step that answers into run.ended and run.result once it ends, and lets its lead-in
## draw pass, so the tree is unpaused and its first frame is to come.
func _start(rig: Dictionary, params: Dictionary, until: Dictionary) -> Dictionary:
	var run: Dictionary = {"ended": {}, "result": {}}
	_step_into(rig["time"]._stepper, params, until, run)
	test_draw.emit()
	return run


func _step_into(step: Node, params: Dictionary, until: Dictionary, run: Dictionary) -> void:
	run["ended"]["error"] = await step._step(params, run["result"], until)


## One stepped frame: its process_frame, then its draw.
func _frame(rig: Dictionary) -> void:
	rig["tree"].process_frame.emit()
	test_draw.emit()


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
