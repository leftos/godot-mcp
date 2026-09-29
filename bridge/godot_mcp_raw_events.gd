extends Node
## The godot-mcp bridge's raw event player, a child of the bridge: plays simulate_input's event
## list one frame apart (the input player's "events" gesture), sending each event through the
## input player's senders, which track the injected pointer and the held buttons on the bridge.

const POINTER_EVENTS := ["mouse_button", "mouse_motion", "pan_gesture"]

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## The input player (godot_mcp_input.gd), whose senders play each event, and its script, whose
## constants name the buttons and keys.
var _gestures: Node
var _gestures_script: GDScript


func _ready() -> void:
	_gestures = bridge._gestures
	_gestures_script = _gestures.get_script() as GDScript


## Plays a raw event list, one frame apart, stopping at the first event that fails.
func play(events: Variant) -> String:
	if not events is Array:
		return "events must be an array of event objects"
	var list: Array = events
	for index in list.size():
		if index > 0:
			await get_tree().process_frame
		var error: String = await _play_event(list[index])
		if not error.is_empty():
			return "event %d: %s" % [index, error]
	return ""


func _play_event(event: Variant) -> String:
	if not event is Dictionary:
		return "not an object"
	var spec: Dictionary = event
	return await _play_event_of_kind(spec)


## Plays one raw event by its type, or says why it could not.
func _play_event_of_kind(spec: Dictionary) -> String:
	var kind: String = str(spec.get("type", ""))
	if POINTER_EVENTS.has(kind):
		return await _play_pointer_event(kind, spec)
	var error: String = (
		(
			"unknown type '%s'; the types are key, mouse_button, mouse_motion, pan_gesture, "
			+ "joypad_button, joypad_motion, action, click_element and wait"
		)
		% kind
	)
	match kind:
		"key":
			error = await _play_raw_key(spec)
		"joypad_button":
			error = await bridge._pads.play_raw_button(spec)
		"joypad_motion":
			error = bridge._pads.play_raw_motion(spec)
		"action":
			error = _play_action(spec)
		"click_element":
			error = await _play_click_element(spec)
		"wait":
			error = await _play_wait(spec)
	return error


## Plays one of POINTER_EVENTS, the raw events at a point.
func _play_pointer_event(kind: String, spec: Dictionary) -> String:
	var error: String = ""
	match kind:
		"mouse_button":
			error = await _play_raw_button(spec)
		"mouse_motion":
			error = _play_raw_motion(spec)
		"pan_gesture":
			error = _play_raw_pan(spec)
	return error


## A click on the element spec names, with its button and doubleClick, as click plays it.
func _play_click_element(spec: Dictionary) -> String:
	var click: Dictionary = {
		"target": {"element": spec.get("element", "")},
		"button": spec.get("button", "left"),
		"doubleClick": spec.get("doubleClick", false),
	}
	return await _gestures.click(click)


## Waits spec.ms milliseconds (none when negative) of game time the time scale does not stretch:
## real time in a plain run, clip time in a recording (a movie frame a 60th of a second), since a
## SceneTreeTimer subtracts the process step (scene/main/scene_tree.cpp L793-812,
## main/main_timer_sync.cpp L432-435 in 4.7.2). The timer runs while the tree is paused.
func _play_wait(spec: Dictionary) -> String:
	var seconds: float = maxf(float(spec.get("ms", 0)), 0.0) / 1000.0
	await get_tree().create_timer(seconds, true, false, true).timeout
	return ""


## A key event; with pressed omitted, a press and a release one frame apart.
func _play_raw_key(spec: Dictionary) -> String:
	var key_name: String = str(spec.get("key", ""))
	var keycode: int = _gestures.parse_key(key_name)
	if keycode == KEY_NONE:
		return "unknown key '%s'. %s" % [key_name, _gestures_script.UNKNOWN_KEY_HINT]
	var modifiers := PackedStringArray(spec.get("modifiers", []))
	var unicode: int = _gestures.key_unicode(keycode, modifiers)
	if spec.get("unicode") is String and not str(spec["unicode"]).is_empty():
		unicode = str(spec["unicode"]).unicode_at(0)
	elif spec.get("unicode") is float or spec.get("unicode") is int:
		unicode = int(spec["unicode"])
	if spec.has("pressed"):
		_gestures.send_key(keycode, bool(spec["pressed"]), unicode, modifiers)
		return ""
	_gestures.send_key(keycode, true, unicode, modifiers)
	await get_tree().process_frame
	_gestures.send_key(keycode, false, unicode, modifiers)
	return ""


## A mouse button event at a point; with pressed omitted, a press and a release one frame
## apart, or for a wheel button a notch (_play_raw_wheel).
func _play_raw_button(spec: Dictionary) -> String:
	if not (spec.has("x") and spec.has("y")):
		return "mouse_button needs x and y"
	var button: int = _parse_raw_button(spec.get("button", "left"))
	if button == 0:
		return _unknown_raw_button(spec.get("button"))
	var window_point: Vector2 = _gestures.to_window(Vector2(float(spec["x"]), float(spec["y"])))
	if _gestures_script.WHEEL_BUTTONS.values().has(button):
		_play_raw_wheel(window_point, button, spec)
		return ""
	var double_click: bool = bool(spec.get("doubleClick", false))
	if spec.has("pressed"):
		_gestures.send_button(window_point, button, bool(spec["pressed"]), double_click)
		return ""
	_gestures.send_button(window_point, button, true, double_click)
	await get_tree().process_frame
	_gestures.send_button(window_point, button, false, false)
	return ""


## A wheel button event with spec.factor (1 when left out): the one spec.pressed names, or with
## pressed omitted a whole notch in this frame (the input player's send_notch).
func _play_raw_wheel(window_point: Vector2, button: int, spec: Dictionary) -> void:
	var factor: float = float(spec.get("factor", 1.0))
	if spec.has("pressed"):
		_gestures.send_wheel(window_point, button, bool(spec["pressed"]), factor)
		return
	_gestures.send_notch(window_point, button, factor)


## A pan gesture at a point with spec's delta_x and delta_y (0 when left out), as given.
func _play_raw_pan(spec: Dictionary) -> String:
	if not (spec.has("x") and spec.has("y")):
		return "pan_gesture needs x and y"
	var window_point: Vector2 = _gestures.to_window(Vector2(float(spec["x"]), float(spec["y"])))
	_gestures.send_pan(
		window_point, Vector2(float(spec.get("delta_x", 0)), float(spec.get("delta_y", 0)))
	)
	return ""


## A motion to a point. relative defaults to the step from the last pointer position and
## button_mask to the buttons held now; an explicit relative is in viewport units.
func _play_raw_motion(spec: Dictionary) -> String:
	if not (spec.has("x") and spec.has("y")):
		return "mouse_motion needs x and y"
	var window_point: Vector2 = _gestures.to_window(Vector2(float(spec["x"]), float(spec["y"])))
	var relative: Vector2 = window_point - bridge._pointer
	if spec.has("relative_x") or spec.has("relative_y"):
		var given := Vector2(float(spec.get("relative_x", 0)), float(spec.get("relative_y", 0)))
		relative = get_viewport().get_screen_transform().basis_xform(given)
	_gestures.send_motion(window_point, relative, int(spec.get("button_mask", bridge._held_mask)))
	return ""


func _play_action(spec: Dictionary) -> String:
	var action := StringName(str(spec.get("action", "")))
	if not InputMap.has_action(action):
		return "no input action '%s' in the project's InputMap" % action
	var event := InputEventAction.new()
	event.action = action
	event.pressed = bool(spec.get("pressed", true))
	event.strength = float(spec.get("strength", 1.0))
	_gestures.dispatch(event)
	return ""


## A button simulate_input's mouse_button takes: the input player's parse_button's, or a wheel
## button by name or index (4 to 7); 0 for none.
func _parse_raw_button(value: Variant) -> int:
	if value is String and _gestures_script.WHEEL_BUTTONS.has((value as String).to_lower()):
		return _gestures_script.WHEEL_BUTTONS[(value as String).to_lower()]
	if (value is int or value is float) and int(value) >= 4 and int(value) <= 7:
		return int(value)
	return _gestures.parse_button(value)


func _unknown_raw_button(value: Variant) -> String:
	return (
		(
			"unknown mouse button '%s'; use left, right, middle, wheel_up, wheel_down, "
			+ "wheel_left or wheel_right"
		)
		% str(value)
	)
