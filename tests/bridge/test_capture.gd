extends "res://gd_test.gd"
## The input capture (bridge/godot_mcp_capture.gd) on instances never added to the tree: each
## event kind as the simulate_input event that plays it, the motion filter, the 20 ms wait rule,
## the 2000-item cap, the 100-event flush, and real input told from sent by the bridge's
## _dispatching.
# gdlint: disable=private-method-call

var _capture_script: GDScript = load_bridge_script("godot_mcp_capture.gd")
var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")
## A letterboxed window's screen transform: content scaled by 2 under a 100 px bar, so the window
## point (100, 300) is the viewport point (50, 100).
var _screen := Transform2D(0.0, Vector2(2, 2), 0.0, Vector2(0, 100))


func test_a_key_carries_its_name_modifiers_and_character() -> void:
	var capture: Node = _capture_script.new()
	var press := InputEventKey.new()
	press.keycode = KEY_A
	press.pressed = true
	press.shift_pressed = true
	press.unicode = 65
	var release := InputEventKey.new()
	release.keycode = KEY_ENTER
	var expected_press: Dictionary = {
		"type": "key", "key": "A", "pressed": true, "modifiers": ["shift"], "unicode": "A"
	}
	assert_eq(capture.event_spec(press, _screen), expected_press, "a shifted A")
	assert_eq(
		capture.event_spec(release, _screen),
		{"type": "key", "key": "Enter", "pressed": false},
		"a plain release has no modifiers and no character"
	)
	capture.free()


func test_a_mouse_button_is_in_viewport_coordinates() -> void:
	var capture: Node = _capture_script.new()
	var click := InputEventMouseButton.new()
	click.button_index = MOUSE_BUTTON_LEFT
	click.pressed = true
	click.double_click = true
	click.position = Vector2(100, 300)
	var expected: Dictionary = {
		"type": "mouse_button",
		"x": 50.0,
		"y": 100.0,
		"button": "left",
		"pressed": true,
		"doubleClick": true,
	}
	assert_eq(capture.event_spec(click, _screen), expected, "a double click at (50, 100)")
	capture.free()


func test_a_wheel_notch_carries_its_button_and_factor() -> void:
	var capture: Node = _capture_script.new()
	var wheel := InputEventMouseButton.new()
	wheel.button_index = MOUSE_BUTTON_WHEEL_DOWN
	wheel.pressed = true
	wheel.factor = 0.5
	wheel.position = Vector2(100, 300)
	var expected: Dictionary = {
		"type": "mouse_button",
		"x": 50.0,
		"y": 100.0,
		"button": "wheel_down",
		"pressed": true,
		"factor": 0.5,
	}
	assert_eq(capture.event_spec(wheel, _screen), expected, "half a notch down at (50, 100)")
	var extra := InputEventMouseButton.new()
	extra.button_index = MOUSE_BUTTON_XBUTTON1
	assert_eq(capture.event_spec(extra, _screen), {}, "simulate_input plays no extra button")
	capture.free()


func test_a_pan_gesture_keeps_its_delta() -> void:
	var capture: Node = _capture_script.new()
	var pan := InputEventPanGesture.new()
	pan.position = Vector2(100, 300)
	pan.delta = Vector2(-2, 3)
	var expected: Dictionary = {
		"type": "pan_gesture",
		"x": 50.0,
		"y": 100.0,
		"delta_x": -2.0,
		"delta_y": 3.0,
	}
	assert_eq(capture.event_spec(pan, _screen), expected, "a pan at (50, 100), delta unscaled")
	var magnify := InputEventMagnifyGesture.new()
	magnify.factor = 1.5
	assert_eq(capture.event_spec(magnify, _screen), {}, "simulate_input plays no magnify")
	capture.free()


func test_a_mouse_motion_scales_its_relative() -> void:
	var capture: Node = _capture_script.new()
	var motion := InputEventMouseMotion.new()
	motion.position = Vector2(100, 300)
	motion.relative = Vector2(4, 6)
	motion.button_mask = MOUSE_BUTTON_MASK_LEFT
	var expected: Dictionary = {
		"type": "mouse_motion",
		"x": 50.0,
		"y": 100.0,
		"relative_x": 2.0,
		"relative_y": 3.0,
		"button_mask": 1,
	}
	assert_eq(capture.event_spec(motion, _screen), expected, "a motion with the left button held")
	capture.free()


func test_pads_and_actions_use_simulate_inputs_names() -> void:
	var capture: Node = _capture_script.new()
	var button := InputEventJoypadButton.new()
	button.button_index = JOY_BUTTON_DPAD_DOWN
	button.pressed = true
	button.device = 1
	var axis := InputEventJoypadMotion.new()
	axis.axis = JOY_AXIS_TRIGGER_LEFT
	axis.axis_value = 0.5
	var action := InputEventAction.new()
	action.action = &"jump"
	action.pressed = true
	action.strength = 0.5
	assert_eq(
		capture.event_spec(button, _screen),
		{"type": "joypad_button", "button": "DPAD_DOWN", "pressed": true, "device": 1},
		"a pad button"
	)
	assert_eq(
		capture.event_spec(axis, _screen),
		{"type": "joypad_motion", "axis": "TRIGGER_LEFT", "value": 0.5, "device": 0},
		"a pad axis"
	)
	assert_eq(
		capture.event_spec(action, _screen),
		{"type": "action", "action": "jump", "pressed": true, "strength": 0.5},
		"an action"
	)
	capture.free()


func test_kinds_simulate_input_cannot_play_are_skipped() -> void:
	var capture: Node = _capture_script.new()
	assert_eq(capture.event_spec(InputEventScreenTouch.new(), _screen), {}, "a touch")
	assert_eq(capture.event_spec(InputEventScreenDrag.new(), _screen), {}, "a touch drag")
	assert_eq(capture.event_spec(InputEventMagnifyGesture.new(), _screen), {}, "a gesture")
	assert_eq(capture.event_spec(InputEventMIDI.new(), _screen), {}, "MIDI")
	var far_pad := InputEventJoypadButton.new()
	far_pad.device = 16
	assert_eq(capture.event_spec(far_pad, _screen), {}, "a device past the pads")
	capture.free()


func test_motion_without_a_button_is_dropped_unless_asked_for() -> void:
	for motion: bool in [false, true]:
		var capture: Node = _started({"sources": ["sent"], "motion": motion})
		capture.record_sent(_motion_event(0), 1000, _screen)
		capture.record_sent(_motion_event(MOUSE_BUTTON_MASK_LEFT), 1000, _screen)
		var masks: Array = _events(capture).map(
			func(item: Dictionary) -> int: return item["button_mask"]
		)
		var expected: Array = [0, 1] if motion else [1]
		assert_eq(masks, expected, "motion %s keeps these button masks" % motion)
		capture.free()


func test_gaps_of_twenty_ms_or_more_become_waits() -> void:
	var capture: Node = _started({"sources": ["sent"]})
	for at: int in [1000, 1019, 1049, 1069]:
		capture.record_sent(_key_event(), at, _screen)
	var shape: Array = _events(capture).map(
		func(item: Dictionary) -> String:
			return "wait %d" % item["ms"] if item["type"] == "wait" else str(item["type"])
	)
	assert_eq(
		shape, ["key", "key", "wait 30", "key", "wait 20", "key"], "no wait first or at 19 ms"
	)
	capture.free()


func test_the_cap_stops_the_capture_and_marks_it_truncated() -> void:
	var sink: Array = []
	var capture: Node = _started({"sources": ["sent"]}, sink)
	for _index in _capture_script.MAX_EVENTS + 5:
		capture.record_sent(_key_event(), 1000, _screen)
	assert_eq(
		sink.size(), _capture_script.MAX_EVENTS / _capture_script.FLUSH_EVENTS, "a frame each 100"
	)
	assert_true(sink[-1].get("truncated", false), "the last frame says truncated")
	assert_eq(capture.stop(), {"count": 2000, "truncated": true}, "stop's count")
	var total: int = 0
	for frame: Dictionary in sink:
		total += frame["events"].size()
	assert_eq(total, 2000, "every recorded event was sent, none past the cap")
	capture.free()


func test_a_wait_and_its_event_that_do_not_fit_are_both_left_out() -> void:
	var capture: Node = _started({"sources": ["sent"]})
	for _index in _capture_script.MAX_EVENTS - 1:
		capture.record_sent(_key_event(), 1000, _screen)
	capture.record_sent(_key_event(), 1050, _screen)
	var events: Array = _events(capture)
	assert_eq(events.size(), _capture_script.MAX_EVENTS - 1, "the capture ends at 1999")
	assert_eq(events[-1]["type"], "key", "no wait is left dangling")
	assert_true(capture._truncated, "and is truncated")
	capture.free()


func test_real_input_is_told_from_sent_by_dispatching() -> void:
	var bridge: Node = _bridge_script.new()
	var capture: Node = _started({}, [], bridge)
	bridge._dispatching = true
	capture.record_real(_key_event(), 1000, _screen)
	capture.record_sent(_key_event(KEY_S), 1000, _screen)
	bridge._dispatching = false
	capture.record_real(_key_event(KEY_R), 1000, _screen)
	var keys: Array = _events(capture).map(func(item: Dictionary) -> String: return item["key"])
	assert_eq(keys, ["S", "R"], "the dispatched copy of a sent event is not recorded as real")
	capture.free()
	bridge.free()


func test_sources_limit_what_is_recorded() -> void:
	var bridge: Node = _bridge_script.new()
	for sources: Array in [["real"], ["sent"]]:
		var capture: Node = _started({"sources": sources}, [], bridge)
		capture.record_real(_key_event(KEY_R), 1000, _screen)
		capture.record_sent(_key_event(KEY_S), 1000, _screen)
		var keys: Array = _events(capture).map(func(item: Dictionary) -> String: return item["key"])
		assert_eq(keys, ["R"] if sources == ["real"] else ["S"], "sources %s" % [sources])
		capture.free()
	bridge.free()


func test_injected_pad_events_are_recorded_as_sent() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = load_bridge_script("godot_mcp_input.gd").new()
	var pads: Node = load_bridge_script("godot_mcp_gamepad.gd").new()
	gestures.bridge = bridge
	pads.bridge = bridge
	bridge._gestures = gestures
	var capture: Node = _started({"sources": ["sent"]}, [], bridge)
	bridge._capture = capture
	pads.play_raw_button({"button": "A", "pressed": true})
	pads.play_raw_motion({"axis": "LEFT_X", "value": 0.5})
	pads.play_raw_button({"button": "A", "pressed": false})
	pads.play_raw_motion({"axis": "LEFT_X", "value": 0.0})
	var shape: Array = _events(capture).map(
		func(item: Dictionary) -> String:
			if item["type"] == "joypad_button":
				return "button %s %s" % [item["button"], item["pressed"]]
			return "%s %s %s" % [item["type"], item.get("axis"), item.get("value")]
	)
	var expected: Array = [
		"button A true", "joypad_motion LEFT_X 0.5", "button A false", "joypad_motion LEFT_X 0.0"
	]
	assert_eq(shape, expected, "the pad's injected events, as sent")
	assert_true(not bridge._dispatching, "dispatching is over once the events are sent")
	capture.free()
	pads.free()
	gestures.free()
	bridge.free()


func test_nothing_is_recorded_after_stop_and_actions_are_checked() -> void:
	var capture: Node = _started({"sources": ["sent"]})
	capture.stop()
	capture.record_sent(_key_event(), 1000, _screen)
	assert_eq(capture.stop(), {"count": 0, "truncated": false}, "a stopped capture records nothing")
	assert_eq(
		capture.handle({"action": "pause"}),
		{"error": "unknown capture action 'pause'; use start or stop"},
		"an unknown action"
	)
	capture.free()


## A capture started with params, its frames going to sink, answering to bridge.
func _started(params: Dictionary, sink: Array = [], bridge: Node = null) -> Node:
	var capture: Node = _capture_script.new()
	capture.bridge = bridge
	capture.send_frame = func(frame: Dictionary) -> void: sink.append(frame)
	capture.set_meta("sink", sink)
	capture.start(params)
	return capture


## Stops capture and returns every event its frames carried, in order.
func _events(capture: Node) -> Array:
	capture.stop()
	var events: Array = []
	for frame: Dictionary in capture.get_meta("sink"):
		events.append_array(frame["events"])
	return events


func _key_event(keycode: Key = KEY_A) -> InputEventKey:
	var event := InputEventKey.new()
	event.keycode = keycode
	event.pressed = true
	return event


func _motion_event(button_mask: int) -> InputEventMouseMotion:
	var event := InputEventMouseMotion.new()
	event.position = Vector2(100, 300)
	event.button_mask = button_mask
	return event
