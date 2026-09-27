extends Node
## The godot-mcp bridge's input player, a child of the bridge: plays the input tools' gestures
## (click, drag, type_text, key, mouse_button, the gamepad gestures) and simulate_input's raw
## events over frames, with new event objects sent through Input. The injected pointer and the
## held buttons live on the bridge, whose _input keeps the real mouse out while they are in play.

const MIN_DRAG_STEPS := 3
const SETTLE_FRAMES := 2
## The gestures whose result says which Controls they hit, from _hits.
const HIT_GESTURES := ["click", "drag", "mouse_button"]
const MOUSE_BUTTONS := {
	"left": MOUSE_BUTTON_LEFT,
	"right": MOUSE_BUTTON_RIGHT,
	"middle": MOUSE_BUTTON_MIDDLE,
}
const MODIFIER_KEYS := {KEY_SHIFT: "shift", KEY_CTRL: "ctrl", KEY_ALT: "alt", KEY_META: "meta"}
## A US keyboard's shifted symbols, and at the same index the key that types each unshifted.
const SHIFTED_SYMBOLS := '~!@#$%^&*()_+{}|:"<>?'
const UNSHIFTED_KEYS := "`1234567890-=[]\\;',./"
const UNKNOWN_KEY_HINT := (
	"Key names are Godot's Key constants without KEY_: Enter, Escape, Space, A, 1, F1, Up, "
	+ "Shift, Ctrl, Alt, Meta. run_script can print one with OS.get_keycode_string(KEY_X)."
)

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## What the playing gesture hit: pressedOn, releasedOn, guiDragStarted, dropAccepted.
var _hits: Dictionary = {}
## The UI snapshot (godot_mcp_ui_snapshot.gd) taken as the first gesture since launch, or since
## the last uiChanged wait was met, started: the baseline wait_for {uiChanged} compares with.
## Empty while none is pending.
var _ui_baseline: Dictionary = {}
## The instance ids of the drag previews seen entering the tree, which the snapshot leaves out.
var _drag_previews: Dictionary = {}


func _ready() -> void:
	get_tree().node_added.connect(_note_drag_preview)


## Plays one gesture over frames, then waits two more frames, so the game's handlers have run
## and their errors are flushed ahead of the reply. Every point arrives in viewport coordinates.
## Answers {result: {pointer, heldButtonMask}}, to which a click, drag or mouse_button adds the
## Controls it hit, or {error}. Takes the uiChanged baseline when none is pending.
func play(params: Dictionary) -> Dictionary:
	bridge._gesture_playing = true
	_hits = {}
	if _ui_baseline.is_empty():
		_ui_baseline = _snapshot_ui()
	var error: String = await _play_gesture(params)
	for _frame in SETTLE_FRAMES:
		await get_tree().process_frame
	bridge._gesture_playing = false
	if not error.is_empty():
		return {"error": error}
	var result: Dictionary = {
		"pointer": bridge._json.to_json(_to_viewport(bridge._pointer)),
		"heldButtonMask": bridge._held_mask,
	}
	if HIT_GESTURES.has(str(params.get("gesture", ""))):
		result.merge(_hits)
	return {"result": result}


## Whether a uiChanged baseline is pending.
func has_ui_baseline() -> bool:
	return not _ui_baseline.is_empty()


## What changed in the UI since the baseline, as the snapshot's diff gives it; empty when nothing
## did or no baseline is pending.
func ui_change() -> Dictionary:
	if _ui_baseline.is_empty():
		return {}
	return bridge._ui_snapshot.diff(_ui_baseline, _snapshot_ui())


## Drops the baseline, which a met uiChanged wait uses up; the next gesture takes a new one.
func use_up_ui_baseline() -> void:
	_ui_baseline = {}


func _snapshot_ui() -> Dictionary:
	return bridge._ui_snapshot.capture(bridge, _drag_previews)


## Records a drag preview as it enters the tree, dropping the ids of freed ones. Viewport adds a
## preview as a top_level Control while the GUI drags (scene/main/viewport.cpp L2510-2525 in
## 4.7.2; dragging is already set then, L2058 and L2497) and keeps it out of script's reach.
func _note_drag_preview(node: Node) -> void:
	var control := node as Control
	if control == null or not control.top_level or not control.get_viewport().gui_is_dragging():
		return
	for id: int in _drag_previews.keys():
		if not is_instance_id_valid(id):
			_drag_previews.erase(id)
	_drag_previews[control.get_instance_id()] = true


func _play_gesture(params: Dictionary) -> String:
	var gesture: String = str(params.get("gesture", ""))
	var error: String = "unknown gesture '%s'" % gesture
	match gesture:
		"click":
			error = await _play_click(params)
		"drag":
			error = await _play_drag(params)
		"type_text":
			error = await _play_text(str(params.get("text", "")))
		"key":
			error = await _play_key(params)
		"mouse_button":
			error = await _play_mouse_button(params)
		"gamepad_button":
			error = await bridge._pads.play_button(params)
		"gamepad_axes":
			error = await bridge._pads.play_axes(params)
		"events":
			error = await _play_events(params.get("events"))
	return error


func _play_click(params: Dictionary) -> String:
	var point: Variant = _resolve_target(params.get("target"))
	if point is String:
		return point
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	await _click_at(_to_window(point), button, bool(params.get("doubleClick", false)))
	return ""


## Moves to the point, then presses and releases one frame apart; a double click follows
## with a second press marked double_click. The hits are the last press's and release's.
func _click_at(window_point: Vector2, button: int, double_click: bool) -> void:
	await _dismiss_tooltips()
	_move_to(window_point)
	_send_and_record(window_point, button, true, false)
	await get_tree().process_frame
	_send_and_record(window_point, button, false, false)
	if double_click:
		await get_tree().process_frame
		_send_and_record(window_point, button, true, true)
		await get_tree().process_frame
		_send_and_record(window_point, button, false, false)


## Sends a button press or release and records the Control it landed on as pressedOn or
## releasedOn. A release that drops a GUI drag reads the Control before it: the drop ends by
## moving the hover to the real mouse (Window.update_mouse_cursor_state, 4.7.2 window.cpp
## L935-949), and the pointer is already at the release point.
func _send_and_record(
	window_point: Vector2, button: int, pressed: bool, double_click: bool
) -> void:
	var point: Vector2 = _to_viewport(window_point)
	var drops: bool = (
		not pressed and button == MOUSE_BUTTON_LEFT and get_tree().root.gui_is_dragging()
	)
	var before_drop: Variant = _control_under(point) if drops else null
	_send_button(window_point, button, pressed, double_click)
	_hits["pressedOn" if pressed else "releasedOn"] = (
		before_drop if drops else _control_under(point)
	)


## Frees every tooltip the root shows before a press whose pressedOn is read, and waits a frame
## for the free when there was one. A tooltip is mouse-passthrough, so the press reaches the
## Control beneath (input forwarding skips it, scene/main/viewport.cpp L3184 in 4.7.2), but the
## root still routes hover into it (_update_mouse_over, L3281-3320), leaving the root's hovered
## Control null. It is freed with queue_free, as the engine's own _gui_cancel_tooltip does
## (L1561-1563): the popup's NOTIFICATION_PREDELETE clears the viewport's pointer to it
## (L771-774), and its removal clears the root's subwindow_over (L502-504), so the next motion
## (the gesture's move) hovers the Control beneath. A press does not cancel a tooltip itself;
## the release does (L2011).
func _dismiss_tooltips() -> void:
	var dismissed: bool = false
	for window: Window in get_tree().root.get_embedded_subwindows():
		if window.visible and bridge._ui_snapshot.is_tooltip(window):
			window.queue_free()
			dismissed = true
	if dismissed:
		await get_tree().process_frame


## The Control under a viewport point as {path, class}, or null over none, read right after a
## mouse event there: the topmost visible embedded window holding the point (a popup) answers
## for it, else the root, each with the Control its GUI picked for its last mouse event.
func _control_under(point: Vector2) -> Variant:
	var viewport: Viewport = get_tree().root
	var windows: Array[Window] = get_tree().root.get_embedded_subwindows()
	for index in range(windows.size() - 1, -1, -1):
		var window: Window = windows[index]
		if (
			window.visible
			and Rect2(Vector2(window.position), Vector2(window.size)).has_point(point)
		):
			viewport = window
			break
	var control: Control = viewport.gui_get_hovered_control()
	if control == null:
		return null
	return {"path": str(control.get_path()), "class": control.get_class()}


func _play_drag(params: Dictionary) -> String:
	var from: Variant = _resolve_target(params.get("from"))
	if from is String:
		return "from: %s" % from
	var to: Variant = _resolve_target(params.get("to"))
	if to is String:
		return "to: %s" % to
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	var duration_ms: int = maxi(0, int(params.get("durationMs", 300)))
	await _drag(_to_window(from), _to_window(to), duration_ms, button)
	return ""


## Presses at start, then sends one motion a frame along the straight line to end for
## duration_ms (and at least MIN_DRAG_STEPS frames), each carrying the held button in its
## button_mask and its step as relative: Godot's viewport starts a drag only from motions
## with LEFT in the mask whose relatives add up past gui/common/drag_threshold. Records
## whether the GUI was dragging after any motion, and whether the release dropped it.
func _drag(start: Vector2, end: Vector2, duration_ms: int, button: int) -> void:
	var root: Window = get_tree().root
	var gui_drag_started: bool = false
	await _dismiss_tooltips()
	_move_to(start)
	_send_and_record(start, button, true, false)
	var began: int = Time.get_ticks_msec()
	var step: int = 0
	var progress: float = 0.0
	while progress < 1.0:
		await get_tree().process_frame
		step += 1
		var elapsed: float = float(Time.get_ticks_msec() - began)
		progress = clampf(elapsed / maxf(float(duration_ms), 1.0), 0.0, 1.0)
		if step < MIN_DRAG_STEPS:
			progress = minf(progress, float(step) / MIN_DRAG_STEPS)
		_move_to(start.lerp(end, progress))
		gui_drag_started = gui_drag_started or root.gui_is_dragging()
	await get_tree().process_frame
	_send_and_record(end, button, false, false)
	_hits["guiDragStarted"] = gui_drag_started
	_hits["dropAccepted"] = gui_drag_started and root.gui_is_drag_successful()


func _play_text(text: String) -> String:
	for index in text.length():
		if index > 0:
			await get_tree().process_frame
		_type_character(text.unicode_at(index))
	return ""


## Presses and releases the key that types code on a US layout, with shift where the
## character needs it and the character itself as the event's unicode.
func _type_character(code: int) -> void:
	var character: String = String.chr(code)
	var keycode: int = KEY_NONE
	var unicode: int = code
	var modifiers := PackedStringArray()
	var symbol_index: int = SHIFTED_SYMBOLS.find(character)
	if code == 10:
		keycode = KEY_ENTER
		unicode = 0
	elif code == 9:
		keycode = KEY_TAB
		unicode = 0
	elif symbol_index >= 0:
		keycode = UNSHIFTED_KEYS.unicode_at(symbol_index)
		modifiers.append("shift")
	elif code >= 32 and code < 127:
		keycode = character.to_upper().unicode_at(0)
		if character != character.to_lower():
			modifiers.append("shift")
	_send_key(keycode, true, unicode, modifiers)
	_send_key(keycode, false, unicode, modifiers)


func _play_key(params: Dictionary) -> String:
	var key_name: String = str(params.get("key", ""))
	var keycode: int = _parse_key(key_name)
	if keycode == KEY_NONE:
		return "unknown key '%s'. %s" % [key_name, UNKNOWN_KEY_HINT]
	var action: String = str(params.get("action", "tap"))
	if not action in ["tap", "press", "release"]:
		return "unknown key action '%s'; use tap, press or release" % action
	var modifiers := PackedStringArray(params.get("modifiers", []))
	var unicode: int = _key_unicode(keycode, modifiers)
	if action != "release":
		_send_key(keycode, true, unicode, modifiers)
	if action == "tap":
		await get_tree().process_frame
	if action != "press":
		_send_key(keycode, false, unicode, modifiers)
	return ""


func _play_mouse_button(params: Dictionary) -> String:
	var point: Variant = _resolve_target(params.get("target"))
	if point is String:
		return point
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	var action: String = str(params.get("action", "press"))
	if not action in ["press", "release"]:
		return "unknown mouse_button action '%s'; use press or release" % action
	var window_point: Vector2 = _to_window(point)
	if action == "press":
		await _dismiss_tooltips()
	_move_to(window_point)
	_send_and_record(window_point, button, action == "press", false)
	await get_tree().process_frame
	return ""


## Plays a raw event list, one frame apart, stopping at the first event that fails.
func _play_events(events: Variant) -> String:
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
	var error: String = (
		(
			"unknown type '%s'; the types are key, mouse_button, mouse_motion, joypad_button, "
			+ "joypad_motion, action, click_element and wait"
		)
		% kind
	)
	match kind:
		"key":
			error = await _play_raw_key(spec)
		"mouse_button":
			error = await _play_raw_button(spec)
		"mouse_motion":
			error = _play_raw_motion(spec)
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


## A click on the element spec names, with its button and doubleClick, as click plays it.
func _play_click_element(spec: Dictionary) -> String:
	var click: Dictionary = {
		"target": {"element": spec.get("element", "")},
		"button": spec.get("button", "left"),
		"doubleClick": spec.get("doubleClick", false),
	}
	return await _play_click(click)


## Waits spec.ms milliseconds (none when negative) of real time: the timer runs while the tree
## is paused and ignores the time scale.
func _play_wait(spec: Dictionary) -> String:
	var seconds: float = maxf(float(spec.get("ms", 0)), 0.0) / 1000.0
	await get_tree().create_timer(seconds, true, false, true).timeout
	return ""


## A key event; with pressed omitted, a press and a release one frame apart.
func _play_raw_key(spec: Dictionary) -> String:
	var key_name: String = str(spec.get("key", ""))
	var keycode: int = _parse_key(key_name)
	if keycode == KEY_NONE:
		return "unknown key '%s'. %s" % [key_name, UNKNOWN_KEY_HINT]
	var modifiers := PackedStringArray(spec.get("modifiers", []))
	var unicode: int = _key_unicode(keycode, modifiers)
	if spec.get("unicode") is String and not str(spec["unicode"]).is_empty():
		unicode = str(spec["unicode"]).unicode_at(0)
	elif spec.get("unicode") is float or spec.get("unicode") is int:
		unicode = int(spec["unicode"])
	if spec.has("pressed"):
		_send_key(keycode, bool(spec["pressed"]), unicode, modifiers)
		return ""
	_send_key(keycode, true, unicode, modifiers)
	await get_tree().process_frame
	_send_key(keycode, false, unicode, modifiers)
	return ""


## A mouse button event at a point; with pressed omitted, a press and a release one frame
## apart.
func _play_raw_button(spec: Dictionary) -> String:
	if not (spec.has("x") and spec.has("y")):
		return "mouse_button needs x and y"
	var button: int = _parse_button(spec.get("button", "left"))
	if button == 0:
		return _unknown_button(spec.get("button"))
	var window_point: Vector2 = _to_window(Vector2(float(spec["x"]), float(spec["y"])))
	var double_click: bool = bool(spec.get("doubleClick", false))
	if spec.has("pressed"):
		_send_button(window_point, button, bool(spec["pressed"]), double_click)
		return ""
	_send_button(window_point, button, true, double_click)
	await get_tree().process_frame
	_send_button(window_point, button, false, false)
	return ""


## A motion to a point. relative defaults to the step from the last pointer position and
## button_mask to the buttons held now; an explicit relative is in viewport units.
func _play_raw_motion(spec: Dictionary) -> String:
	if not (spec.has("x") and spec.has("y")):
		return "mouse_motion needs x and y"
	var window_point: Vector2 = _to_window(Vector2(float(spec["x"]), float(spec["y"])))
	var relative: Vector2 = window_point - bridge._pointer
	if spec.has("relative_x") or spec.has("relative_y"):
		var given := Vector2(float(spec.get("relative_x", 0)), float(spec.get("relative_y", 0)))
		relative = get_viewport().get_screen_transform().basis_xform(given)
	_send_motion(window_point, relative, int(spec.get("button_mask", bridge._held_mask)))
	return ""


func _play_action(spec: Dictionary) -> String:
	var action := StringName(str(spec.get("action", "")))
	if not InputMap.has_action(action):
		return "no input action '%s' in the project's InputMap" % action
	var event := InputEventAction.new()
	event.action = action
	event.pressed = bool(spec.get("pressed", true))
	event.strength = float(spec.get("strength", 1.0))
	_dispatch(event)
	return ""


## The viewport point a target {element} or {x, y} names; an element's point is the centre of
## its global rect. A String instead says why the target cannot be resolved.
func _resolve_target(target: Variant) -> Variant:
	if not target is Dictionary:
		return "a target must be an object {element} or {x, y}"
	var spec: Dictionary = target
	if spec.has("element"):
		return _resolve_element(str(spec["element"]))
	if spec.has("x") and spec.has("y"):
		return Vector2(float(spec["x"]), float(spec["y"]))
	return "a target needs element, or both x and y; got %s" % JSON.stringify(spec)


func _resolve_element(element: String) -> Variant:
	var node: Node = bridge._find_node(element)
	if node == null:
		return (
			"no node '%s' in the running game; get_ui_elements lists the Controls' paths and names"
			% element
		)
	if not node is Control:
		return (
			"'%s' is a %s, not a Control, so it has no rect to aim at" % [element, node.get_class()]
		)
	return (node as Control).get_global_rect().get_center()


## The one place a viewport (canvas) point becomes the window point the display server's own
## events carry: the root window's screen transform holds the stretch scale and the letterbox
## offset.
func _to_window(point: Vector2) -> Vector2:
	return get_viewport().get_screen_transform() * point


func _to_viewport(point: Vector2) -> Vector2:
	return get_viewport().get_screen_transform().affine_inverse() * point


func _move_to(window_point: Vector2) -> void:
	_send_motion(window_point, window_point - bridge._pointer, bridge._held_mask)


func _send_motion(window_point: Vector2, relative: Vector2, button_mask: int) -> void:
	var motion := InputEventMouseMotion.new()
	motion.device = bridge.INJECTED_DEVICE
	motion.position = window_point
	motion.global_position = window_point
	motion.relative = relative
	motion.screen_relative = relative
	motion.button_mask = button_mask
	bridge._pointer = window_point
	_dispatch(motion)


func _send_button(window_point: Vector2, button: int, pressed: bool, double_click: bool) -> void:
	var bit: int = 1 << (button - 1)
	bridge._held_mask = (bridge._held_mask | bit) if pressed else (bridge._held_mask & ~bit)
	var event := InputEventMouseButton.new()
	event.device = bridge.INJECTED_DEVICE
	event.button_index = button as MouseButton
	event.pressed = pressed
	event.double_click = double_click
	event.button_mask = bridge._held_mask
	event.position = window_point
	event.global_position = window_point
	bridge._pointer = window_point
	_dispatch(event)


func _send_key(keycode: int, pressed: bool, unicode: int, modifiers: PackedStringArray) -> void:
	var event := InputEventKey.new()
	event.keycode = keycode as Key
	event.physical_keycode = keycode as Key
	event.key_label = keycode as Key
	event.unicode = unicode
	event.pressed = pressed
	var held: PackedStringArray = modifiers.duplicate()
	if pressed and MODIFIER_KEYS.has(keycode):
		held.append(MODIFIER_KEYS[keycode])
	event.shift_pressed = held.has("shift")
	event.ctrl_pressed = held.has("ctrl")
	event.alt_pressed = held.has("alt")
	event.meta_pressed = held.has("meta")
	_dispatch(event)


## Sends a new event object through Input, as the display server's own events go, and
## flushes it at once so accumulated input neither merges nor delays it.
func _dispatch(event: InputEvent) -> void:
	bridge._dispatching = true
	Input.parse_input_event(event)
	Input.flush_buffered_events()
	bridge._dispatching = false


func _parse_button(value: Variant) -> int:
	if value is String and MOUSE_BUTTONS.has((value as String).to_lower()):
		return MOUSE_BUTTONS[(value as String).to_lower()]
	if (value is int or value is float) and int(value) >= 1 and int(value) <= 3:
		return int(value)
	return 0


func _unknown_button(value: Variant) -> String:
	return "unknown mouse button '%s'; use left, right or middle" % str(value)


## The Key a name like Enter, A or F1 stands for; KEY_NONE for an unknown name or a combination
## such as Ctrl+A, whose modifiers go in modifiers instead.
func _parse_key(key_name: String) -> int:
	if key_name.is_empty() or key_name.contains("+"):
		return KEY_NONE
	return OS.find_keycode_from_string(key_name)


## The character a printable key types with these modifiers; 0 for a key that types none, or
## when ctrl, alt or meta is held.
func _key_unicode(keycode: int, modifiers: PackedStringArray) -> int:
	if keycode < 32 or keycode >= 127:
		return 0
	if modifiers.has("ctrl") or modifiers.has("alt") or modifiers.has("meta"):
		return 0
	var character: String = String.chr(keycode)
	if not modifiers.has("shift"):
		return character.to_lower().unicode_at(0)
	var symbol_index: int = UNSHIFTED_KEYS.find(character)
	if symbol_index >= 0:
		return SHIFTED_SYMBOLS.unicode_at(symbol_index)
	return character.to_upper().unicode_at(0)
