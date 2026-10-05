extends Node
## The godot-mcp bridge's gamepad, a child of the bridge: plays injected joypad button and axis
## events, tracks what the injected pads hold, and, when the run asks for shutOutRealGamepads,
## keeps the machine's own pads out of the game.
##
## Keeping them out: with input_devices/joypads/ignore_joypad_on_unfocused_application on (the
## injection override.cfg sets it), Godot drops the pad driver's input while the application is
## unfocused (core/input/input.cpp L1652, L1684 in 4.7.2), but never events sent through
## Input.parse_input_event. Only SceneTree's application focus-out notification marks the
## application unfocused (scene/main/scene_tree.cpp L934-947), and every node receives it too. A
## real focus-in marks it focused again, so each focus change is answered with a focus-out. Each
## focus-out clears pressed pad state (input.cpp L1600-1623), so the injected pads' held buttons
## and axes are sent again after it, and again when the OS takes focus from the window, which on
## Windows clears it once more after the focus-out was answered.

## The highest joypad device id; 0-15 are joypads (core/input/input_event.h L64-67 in 4.7.2).
const MAX_JOY_DEVICE := 15
## Godot's JoyButton names without JOY_BUTTON_, at their enum values: A is 0, TOUCHPAD 20
## (core/input/input_enums.h L80-102 in 4.7.2); JoyAxis likewise, LEFT_X 0 to TRIGGER_RIGHT 5.
const JOY_BUTTON_NAMES: PackedStringArray = [
	"A",
	"B",
	"X",
	"Y",
	"BACK",
	"GUIDE",
	"START",
	"LEFT_STICK",
	"RIGHT_STICK",
	"LEFT_SHOULDER",
	"RIGHT_SHOULDER",
	"DPAD_UP",
	"DPAD_DOWN",
	"DPAD_LEFT",
	"DPAD_RIGHT",
	"MISC1",
	"PADDLE1",
	"PADDLE2",
	"PADDLE3",
	"PADDLE4",
	"TOUCHPAD",
]
const JOY_AXIS_NAMES: PackedStringArray = [
	"LEFT_X", "LEFT_Y", "RIGHT_X", "RIGHT_Y", "TRIGGER_LEFT", "TRIGGER_RIGHT"
]
const NO_FREE_DEVICE := (
	"Every gamepad id 0-15 is held by a connected real pad; "
	+ "pass 'device' to inject on one of them."
)

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## Whether the machine's real pads are kept out of the game.
var real_pads_shut_out: bool = false
## What the playing gesture adds to its result: device, the id it injected on, and warning. The
## input player empties it as a gesture starts and merges it into the result.
var report: Dictionary = {}
## Reads whether the OS has the game's window focused; a test replaces it to fake a focus change.
var read_window_focused: Callable = DisplayServer.window_is_focused
## The buttons the injected pads hold, as Vector2i(device, button) keys.
var _held_buttons: Dictionary = {}
## The value each injected axis holds, keyed by Vector2i(device, axis).
var _axes: Dictionary = {}
var _reassert_queued: bool = false
## What read_window_focused said at the root Window's last focus signal.
var _window_focused: bool = false
var _sending_focus_out: bool = false
## The id pad events without a device play on, chosen by the first of them; -1 until then.
var _chosen: int = -1


## The id a pad event without a device injects on: previous while it is 0-15 and no real pad in
## connected holds it, else the lowest id no real pad holds; -1 when real pads hold them all.
## Input.get_connected_joypads lists only the pads the platform driver connected, never the
## injected ids (core/input/input.cpp L2291-2301 in 4.7.2).
static func choose_device(connected: Array, previous: int) -> int:
	if previous >= 0 and previous <= MAX_JOY_DEVICE and not previous in connected:
		return previous
	for device in MAX_JOY_DEVICE + 1:
		if not device in connected:
			return device
	return -1


## Marks the application unfocused now, and again after every application focus change from here on.
func shut_out_real_pads() -> void:
	real_pads_shut_out = true
	_window_focused = read_window_focused.call()
	var window: Window = get_tree().root
	window.focus_entered.connect(_on_window_focus_changed)
	window.focus_exited.connect(_on_window_focus_changed)
	_send_focus_out()


## Answers application focus changes with a focus-out, which the OS reports to SceneTree and it
## passes to every node (platform/windows/display_server_windows.cpp L5691-5700,
## scene/main/scene_tree.cpp L934-947 in 4.7.2). The root Window's focus signals get no focus-out:
## they also fire when an embedded popup takes focus (scene/main/viewport.cpp L465-470), and a
## focus-out sent then closes that popup as it opens (scene/gui/popup.cpp L114-121).
func _notification(what: int) -> void:
	if what == NOTIFICATION_APPLICATION_FOCUS_IN or what == NOTIFICATION_APPLICATION_FOCUS_OUT:
		_queue_reassert()


## Answers a focus change once its notifications have all been delivered: deferred, so the nodes
## after the bridge see the real change before the focus-out that follows it.
func _queue_reassert() -> void:
	if not real_pads_shut_out or _sending_focus_out or _reassert_queued:
		return
	_reassert_queued = true
	_reassert.call_deferred()


## Sends the held injected pad state again when the OS takes focus from the window: Windows
## releases pressed input then, after the application focus-out was answered, and before the
## root Window's focus_exited (display_server_windows.cpp L6948-6962 in 4.7.2). An embedded
## popup taking focus fires the signal too but leaves the window focused, and sends nothing.
func _on_window_focus_changed() -> void:
	var focused: bool = read_window_focused.call()
	var lost: bool = _window_focused and not focused
	_window_focused = focused
	if lost:
		_resend_held()


func _reassert() -> void:
	_reassert_queued = false
	_send_focus_out()
	_resend_held()


func _resend_held() -> void:
	for key: Vector2i in _held_buttons:
		_send_button_event(key.x, key.y, true)
	for key: Vector2i in _axes:
		if _axes[key] != 0.0:
			_send_motion_event(key.x, key.y, _axes[key])


func _send_focus_out() -> void:
	_sending_focus_out = true
	get_tree().notification(MainLoop.NOTIFICATION_APPLICATION_FOCUS_OUT)
	_sending_focus_out = false


func play_button(params: Dictionary) -> String:
	var button: int = _parse_button(params.get("button"))
	if button < 0:
		return _unknown_button(params.get("button"))
	var device: Variant = _device_for(params)
	if device is String:
		return device
	report["device"] = device
	var action: String = str(params.get("action", "tap"))
	if not action in ["tap", "press", "release"]:
		return "unknown gamepad_button action '%s'; use tap, press or release" % action
	if action != "release":
		_set_button(device, button, true)
	if action == "tap":
		await get_tree().process_frame
	if action != "press":
		_set_button(device, button, false)
	return ""


## Moves every axis in params.axes ({axis, value}) from the value it holds to its value, over
## params.durationMs; with params.release, sends 0.0 on each a frame after they arrive.
func play_axes(params: Dictionary) -> String:
	var device: Variant = _device_for(params)
	if device is String:
		return device
	report["device"] = device
	if not params.get("axes") is Array:
		return "axes must be an array of {axis, value}"
	var targets: Dictionary = {}
	for spec: Variant in params["axes"]:
		var error: String = _read_axis_target(spec, targets)
		if not error.is_empty():
			return error
	await _sweep_axes(device, targets, maxi(0, int(params.get("durationMs", 0))))
	if bool(params.get("release", false)):
		await get_tree().process_frame
		for axis: int in targets:
			_set_axis(device, axis, 0.0)
	return ""


## A joypad button event; with pressed omitted, a press and a release one frame apart.
func play_raw_button(spec: Dictionary) -> String:
	var button: int = _parse_button(spec.get("button"))
	if button < 0:
		return _unknown_button(spec.get("button"))
	var device: Variant = _device_for(spec)
	if device is String:
		return device
	if spec.has("pressed"):
		_set_button(device, button, bool(spec["pressed"]))
		return ""
	_set_button(device, button, true)
	await get_tree().process_frame
	_set_button(device, button, false)
	return ""


func play_raw_motion(spec: Dictionary) -> String:
	var targets: Dictionary = {}
	var error: String = _read_axis_target(spec, targets)
	if not error.is_empty():
		return error
	var device: Variant = _device_for(spec)
	if device is String:
		return device
	for axis: int in targets:
		_set_axis(device, axis, targets[axis])
	return ""


## The id a pad event plays on, or a String saying why it has none. With no device, the chosen
## id, kept from call to call and put in report as device; with one that a real pad holds, a
## warning in report.
func _device_for(params: Dictionary) -> Variant:
	if not params.has("device"):
		_chosen = choose_device(Input.get_connected_joypads(), _chosen)
		if _chosen < 0:
			return NO_FREE_DEVICE
		report["device"] = _chosen
		return _chosen
	var device: int = _parse_device(params["device"])
	if device < 0:
		return _bad_device(params["device"])
	if device in Input.get_connected_joypads():
		_warn_real_pad(device)
	return device


## Adds to report's warning, once per id, that device is a connected real pad.
func _warn_real_pad(device: int) -> void:
	var sentence: String = (
		"Device %d is a connected real pad (%s); its own input mixes with what is injected there."
		% [device, Input.get_joy_name(device)]
	)
	var warning: String = str(report.get("warning", ""))
	if warning.contains(sentence):
		return
	report["warning"] = sentence if warning.is_empty() else "%s %s" % [warning, sentence]


## Adds {axis, value} to targets as JoyAxis -> value; a String says why it cannot.
func _read_axis_target(spec: Variant, targets: Dictionary) -> String:
	if not spec is Dictionary:
		return "an axis target must be an object {axis, value}"
	var axis: int = _parse_axis((spec as Dictionary).get("axis"))
	if axis < 0:
		return _unknown_axis((spec as Dictionary).get("axis"))
	var value: Variant = (spec as Dictionary).get("value")
	var error: String = _check_axis_value(axis, value)
	if error.is_empty():
		targets[axis] = float(value)
	return error


## Sends each axis at its target at once, or, over duration_ms, one event per axis a frame along
## the straight line from the value it holds; in a recording over duration_ms of clip time, its
## movie frames (the input player's clip_frames).
func _sweep_axes(device: int, targets: Dictionary, duration_ms: int) -> void:
	if duration_ms == 0:
		for axis: int in targets:
			_set_axis(device, axis, targets[axis])
		return
	var starts: Dictionary = {}
	for axis: int in targets:
		starts[axis] = float(_axes.get(Vector2i(device, axis), 0.0))
	var gestures: Node = bridge._gestures
	var frames: int = gestures.clip_frames(duration_ms, gestures.clip_fps())
	var began: int = Time.get_ticks_msec()
	var step: int = 0
	var progress: float = 0.0
	while progress < 1.0:
		await get_tree().process_frame
		step += 1
		progress = gestures.played_progress(
			step, Time.get_ticks_msec() - began, duration_ms, frames
		)
		for axis: int in targets:
			var value: float = targets[axis]
			if progress < 1.0:
				value = lerpf(starts[axis], targets[axis], progress)
			_set_axis(device, axis, value)


## Lets go of everything the injected pads hold, through the events a gesture sends: each held
## button released and each axis away from rest set back to 0.
func release_all() -> void:
	for held: Vector2i in _held_buttons.keys():
		_set_button(held.x, held.y, false)
	for key: Vector2i in _axes.keys():
		if _axes[key] != 0.0:
			_set_axis(key.x, key.y, 0.0)


func _set_button(device: int, button: int, pressed: bool) -> void:
	if pressed:
		_held_buttons[Vector2i(device, button)] = true
	else:
		_held_buttons.erase(Vector2i(device, button))
	_send_button_event(device, button, pressed)


func _set_axis(device: int, axis: int, value: float) -> void:
	_axes[Vector2i(device, axis)] = value
	_send_motion_event(device, axis, value)


## A joypad event carries its pad's device id, not the bridge's injected mouse mark: Godot keys
## the pad state and the actions by device, and the bridge's _input filters only mouse events.
func _send_button_event(device: int, button: int, pressed: bool) -> void:
	var event := InputEventJoypadButton.new()
	event.device = device
	event.button_index = button as JoyButton
	event.pressed = pressed
	event.pressure = 1.0 if pressed else 0.0
	bridge._gestures.dispatch(event)


func _send_motion_event(device: int, axis: int, value: float) -> void:
	var event := InputEventJoypadMotion.new()
	event.device = device
	event.axis = axis as JoyAxis
	event.axis_value = value
	bridge._gestures.dispatch(event)


## The JoyButton a name like A or DPAD_DOWN stands for, in any case; -1 for an unknown name.
func _parse_button(value: Variant) -> int:
	return JOY_BUTTON_NAMES.find(str(value).to_upper()) if value is String else -1


func _unknown_button(value: Variant) -> String:
	var names: String = ", ".join(JOY_BUTTON_NAMES)
	return "unknown gamepad button '%s'; the buttons are %s" % [str(value), names]


## The JoyAxis a name like LEFT_X or TRIGGER_RIGHT stands for, in any case; -1 for an unknown name.
func _parse_axis(value: Variant) -> int:
	return JOY_AXIS_NAMES.find(str(value).to_upper()) if value is String else -1


func _unknown_axis(value: Variant) -> String:
	return "unknown gamepad axis '%s'; the axes are %s" % [str(value), ", ".join(JOY_AXIS_NAMES)]


## Empty when value is a number in the axis's range: -1 to 1 for a stick, 0 to 1 for a trigger.
func _check_axis_value(axis: int, value: Variant) -> String:
	var low: int = 0 if axis >= JOY_AXIS_TRIGGER_LEFT else -1
	if (value is float or value is int) and float(value) >= low and float(value) <= 1.0:
		return ""
	return "%s takes values from %d to 1; got %s." % [JOY_AXIS_NAMES[axis], low, str(value)]


## A device id 0-15 as an int; -1 for anything else. JSON numbers arrive as floats.
func _parse_device(value: Variant) -> int:
	if not (value is float or value is int) or float(value) != floorf(float(value)):
		return -1
	var device: int = int(value)
	return device if device >= 0 and device <= MAX_JOY_DEVICE else -1


func _bad_device(value: Variant) -> String:
	return (
		"device %s is not a joypad id; the pads are 0 (the first) to %d" % [value, MAX_JOY_DEVICE]
	)
