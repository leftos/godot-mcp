extends Node
## The godot-mcp bridge's input capture, a child of the bridge: while capture_input runs, records
## input as simulate_input's event dicts and streams them to the server in id-less frames
## {type: "captured", events, truncated?}.
##
## Real input comes from the root Window's window_input signal, which Window::_window_input emits
## for every event the window receives (any but DEVICE_ID_INTERNAL), before push_input hands it
## to the viewport and so before any node's _input (scene/main/window.cpp L2004-2030 in 4.7.2).
## Events injected through Input reach it too, so one arriving while the input player's dispatch
## runs is skipped there: the bridge's input player records it as sent instead, from dispatch.
## Positions are viewport coordinates, the inverse of the mapping the input tools play through.
## Before an event 20 ms or more after the previous recorded one, a {type: "wait", ms} step goes
## in; the capture stops at MAX_EVENTS items, waits included, and is then marked truncated.

## The most items a capture records, waits included; the server holds the same number.
const MAX_EVENTS := 2000
## How often pending events go out, and how many make a frame go out at once.
const FLUSH_MS := 250
const FLUSH_EVENTS := 100
## The shortest gap between two recorded events that becomes a wait step.
const MIN_WAIT_MS := 20
const GAMEPAD_SCRIPT := "godot_mcp_gamepad.gd"
## The mouse buttons simulate_input plays, by button index.
const MOUSE_BUTTON_NAMES := {
	MOUSE_BUTTON_LEFT: "left",
	MOUSE_BUTTON_RIGHT: "right",
	MOUSE_BUTTON_MIDDLE: "middle",
	MOUSE_BUTTON_WHEEL_UP: "wheel_up",
	MOUSE_BUTTON_WHEEL_DOWN: "wheel_down",
	MOUSE_BUTTON_WHEEL_LEFT: "wheel_left",
	MOUSE_BUTTON_WHEEL_RIGHT: "wheel_right",
}

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## Where a captured frame goes, func(frame: Dictionary): the bridge's _send_captured.
var send_frame: Callable
## The gamepad module's script, for the joypad button and axis names simulate_input takes.
var _gamepad: GDScript
var _capturing: bool = false
var _real: bool = false
var _sent: bool = false
var _motion: bool = false
## Recorded items not yet sent.
var _pending: Array = []
## Items recorded since start, waits included.
var _count: int = 0
var _truncated: bool = false
var _truncation_sent: bool = false
## When the last recorded event happened (Time.get_ticks_msec()); -1 before the first.
var _last_ms: int = -1
var _last_flush_ms: int = 0


func _init() -> void:
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_gamepad = load(script_dir.path_join(GAMEPAD_SCRIPT)) as GDScript


func _process(_delta: float) -> void:
	if not _pending.is_empty() and Time.get_ticks_msec() - _last_flush_ms >= FLUSH_MS:
		flush()


## Runs a capture request, {action: "start", sources, motion} or {action: "stop"}; answers
## {result} or {error}.
func handle(params: Dictionary) -> Dictionary:
	var action: String = str(params.get("action", ""))
	match action:
		"start":
			return {"result": start(params)}
		"stop":
			return {"result": stop()}
	return {"error": "unknown capture action '%s'; use start or stop" % action}


## Starts a capture afresh, of the sources named (real, sent; both when left out), keeping mouse
## motions with no button held only when motion is true. Replies {capturing: true}.
func start(params: Dictionary) -> Dictionary:
	_stop_listening()
	var sources: Array = params["sources"] if params.get("sources") is Array else ["real", "sent"]
	_real = sources.has("real")
	_sent = sources.has("sent")
	_motion = bool(params.get("motion", false))
	_pending = []
	_count = 0
	_truncated = false
	_truncation_sent = false
	_last_ms = -1
	_last_flush_ms = Time.get_ticks_msec()
	_capturing = true
	if _real and is_inside_tree():
		get_tree().root.window_input.connect(_on_window_input)
	return {"capturing": true}


## Stops capturing and sends what is pending, so the last frame is on the wire before the reply.
## Replies {count, truncated}.
func stop() -> Dictionary:
	_stop_listening()
	flush()
	return {"count": _count, "truncated": _truncated}


## Sends the pending items as one captured frame, carrying truncated once the cap has been hit.
func flush() -> void:
	_last_flush_ms = Time.get_ticks_msec()
	var report_truncation: bool = _truncated and not _truncation_sent
	if _pending.is_empty() and not report_truncation:
		return
	var frame: Dictionary = {"type": "captured", "events": _pending}
	if report_truncation:
		frame["truncated"] = true
		_truncation_sent = true
	_pending = []
	if send_frame.is_valid():
		send_frame.call(frame)


## Records an event the bridge sends, called by the input player's dispatch.
func sent(event: InputEvent) -> void:
	if _capturing and _sent:
		record_sent(event, Time.get_ticks_msec(), _screen())


## Records an event the bridge sent at now_ms, when sent input is captured; screen is the root
## viewport's screen transform, which maps viewport points to the window.
func record_sent(event: InputEvent, now_ms: int, screen: Transform2D) -> void:
	if _capturing and _sent:
		_record(event, now_ms, screen)


## Records an event the window received at now_ms, when real input is captured and the bridge is
## not dispatching it (then it is the bridge's own, recorded as sent).
func record_real(event: InputEvent, now_ms: int, screen: Transform2D) -> void:
	if _capturing and _real and not bridge._dispatching:
		_record(event, now_ms, screen)


## event as the simulate_input event that plays it, positions in viewport coordinates through
## screen's inverse; empty for a kind simulate_input cannot play (touch, gestures other than a
## pan, MIDI, the extra mouse buttons, pads outside 0-15).
func event_spec(event: InputEvent, screen: Transform2D) -> Dictionary:
	var spec: Dictionary = {}
	if event is InputEventKey:
		spec = _key_spec(event)
	elif event is InputEventMouseButton:
		spec = _button_spec(event, screen.affine_inverse())
	elif event is InputEventMouseMotion:
		spec = _motion_spec(event, screen.affine_inverse())
	elif event is InputEventPanGesture:
		spec = _pan_spec(event, screen.affine_inverse())
	elif event is InputEventJoypadButton:
		spec = _pad_button_spec(event)
	elif event is InputEventJoypadMotion:
		spec = _pad_motion_spec(event)
	elif event is InputEventAction:
		var action := event as InputEventAction
		spec = {
			"type": "action",
			"action": str(action.action),
			"pressed": action.pressed,
			"strength": action.strength,
		}
	return spec


func _on_window_input(event: InputEvent) -> void:
	record_real(event, Time.get_ticks_msec(), _screen())


## The root viewport's screen transform, which maps viewport points to the window; the identity
## outside the tree (the GDScript unit tests), where there is no window.
func _screen() -> Transform2D:
	return get_viewport().get_screen_transform() if is_inside_tree() else Transform2D.IDENTITY


## Appends event's spec, after a wait when it came MIN_WAIT_MS or more after the last one;
## stops the capture at MAX_EVENTS, and sends a frame once FLUSH_EVENTS are pending.
func _record(event: InputEvent, now_ms: int, screen: Transform2D) -> void:
	var spec: Dictionary = event_spec(event, screen)
	if spec.is_empty() or not _keeps(spec):
		return
	var items: Array = _with_wait(spec, now_ms)
	if _count + items.size() > MAX_EVENTS:
		_truncate()
		return
	_pending.append_array(items)
	_count += items.size()
	_last_ms = now_ms
	if _count >= MAX_EVENTS:
		_truncate()
	elif _pending.size() >= FLUSH_EVENTS:
		flush()


## spec, after a wait step when it came MIN_WAIT_MS or more after the last recorded event; no
## wait before the first.
func _with_wait(spec: Dictionary, now_ms: int) -> Array:
	var gap: int = now_ms - _last_ms
	if _last_ms < 0 or gap < MIN_WAIT_MS:
		return [spec]
	return [{"type": "wait", "ms": gap}, spec]


## Whether spec is kept: a mouse motion with no button held only when motion is captured.
func _keeps(spec: Dictionary) -> bool:
	return _motion or spec["type"] != "mouse_motion" or int(spec["button_mask"]) != 0


func _truncate() -> void:
	_truncated = true
	_stop_listening()
	flush()


func _stop_listening() -> void:
	_capturing = false
	if is_inside_tree() and get_tree().root.window_input.is_connected(_on_window_input):
		get_tree().root.window_input.disconnect(_on_window_input)


func _key_spec(event: InputEventKey) -> Dictionary:
	var keycode: Key = event.keycode if event.keycode != KEY_NONE else event.physical_keycode
	var key_name: String = OS.get_keycode_string(keycode)
	if key_name.is_empty():
		return {}
	var spec: Dictionary = {"type": "key", "key": key_name, "pressed": event.pressed}
	var modifiers: Array = []
	for held: Array in [
		[event.shift_pressed, "shift"],
		[event.ctrl_pressed, "ctrl"],
		[event.alt_pressed, "alt"],
		[event.meta_pressed, "meta"],
	]:
		if held[0]:
			modifiers.append(held[1])
	if not modifiers.is_empty():
		spec["modifiers"] = modifiers
	if event.unicode != 0:
		spec["unicode"] = String.chr(event.unicode)
	return spec


func _button_spec(event: InputEventMouseButton, to_viewport: Transform2D) -> Dictionary:
	if not MOUSE_BUTTON_NAMES.has(event.button_index):
		return {}
	var point: Vector2 = to_viewport * event.position
	var spec: Dictionary = {
		"type": "mouse_button",
		"x": point.x,
		"y": point.y,
		"button": MOUSE_BUTTON_NAMES[event.button_index],
		"pressed": event.pressed,
	}
	if event.double_click:
		spec["doubleClick"] = true
	if (
		event.button_index >= MOUSE_BUTTON_WHEEL_UP
		and event.button_index <= MOUSE_BUTTON_WHEEL_RIGHT
	):
		spec["factor"] = event.factor
	return spec


## A pan gesture at its point in viewport coordinates, its delta as the event carries it: the
## viewport's own transforms leave a gesture's delta alone (InputEventPanGesture::xformed_by,
## core/input/input_event.cpp L1766-1776 in 4.7.2).
func _pan_spec(event: InputEventPanGesture, to_viewport: Transform2D) -> Dictionary:
	var point: Vector2 = to_viewport * event.position
	return {
		"type": "pan_gesture",
		"x": point.x,
		"y": point.y,
		"delta_x": event.delta.x,
		"delta_y": event.delta.y,
	}


func _motion_spec(event: InputEventMouseMotion, to_viewport: Transform2D) -> Dictionary:
	var point: Vector2 = to_viewport * event.position
	var relative: Vector2 = to_viewport.basis_xform(event.relative)
	return {
		"type": "mouse_motion",
		"x": point.x,
		"y": point.y,
		"relative_x": relative.x,
		"relative_y": relative.y,
		"button_mask": int(event.button_mask),
	}


func _pad_button_spec(event: InputEventJoypadButton) -> Dictionary:
	var names: PackedStringArray = _gamepad.JOY_BUTTON_NAMES
	var index: int = event.button_index
	if index < 0 or index >= names.size() or not _is_pad_device(event.device):
		return {}
	return {
		"type": "joypad_button",
		"button": names[index],
		"pressed": event.pressed,
		"device": event.device,
	}


func _pad_motion_spec(event: InputEventJoypadMotion) -> Dictionary:
	var names: PackedStringArray = _gamepad.JOY_AXIS_NAMES
	var index: int = event.axis
	if index < 0 or index >= names.size() or not _is_pad_device(event.device):
		return {}
	return {
		"type": "joypad_motion",
		"axis": names[index],
		"value": event.axis_value,
		"device": event.device,
	}


func _is_pad_device(device: int) -> bool:
	return device >= 0 and device <= _gamepad.MAX_JOY_DEVICE
