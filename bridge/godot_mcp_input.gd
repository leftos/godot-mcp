extends Node
## The godot-mcp bridge's input player, a child of the bridge: plays the input tools' gestures
## (click, drag, type_text, key, mouse_button, hover, the gamepad gestures) and simulate_input's raw
## events over frames, with new event objects sent through Input. The injected pointer and the
## held buttons live on the bridge, whose _input keeps the real mouse out while they are in play.

const MIN_DRAG_STEPS := 3
const SETTLE_FRAMES := 2
## The gestures whose result says which Controls they hit, from _hits.
const HIT_GESTURES := ["click", "drag", "mouse_button", "hover"]
## The longest a hover waits for a tooltip, the server's own limit on timeoutMs.
const HOVER_TIMEOUT_CAP_MS := 10000
## The recording's movie frame rate, which run_project sets beside --write-movie.
const MOVIE_FPS_VARIABLE := "GODOT_MCP_MOVIE_FPS"
const PAUSED_TOOLTIP_WARNING := (
	"the game is paused and %s cannot process, so its tooltip timer never starts; "
	+ "resume, hover, then pause"
)
const MOUSE_BUTTONS := {
	"left": MOUSE_BUTTON_LEFT,
	"right": MOUSE_BUTTON_RIGHT,
	"middle": MOUSE_BUTTON_MIDDLE,
}
const MODIFIER_KEYS := {KEY_SHIFT: "shift", KEY_CTRL: "ctrl", KEY_ALT: "alt", KEY_META: "meta"}
## A US keyboard's shifted symbols, and at the same index the key that types each unshifted.
const SHIFTED_SYMBOLS := '~!@#$%^&*()_+{}|:"<>?'
const UNSHIFTED_KEYS := "`1234567890-=[]\\;',./"
## The refusals of an {element} target, each with the node's path in place of %s.
const HIDDEN_TARGET := "%s is hidden; get_ui_elements lists the visible Controls."
const FREED_TARGET := "%s is being freed; get_ui_elements lists the live Controls."
## A bare name more than one node has: the name, the count and at most MAX_LISTED_NODES paths.
const AMBIGUOUS_TARGET := "'%s' names %d nodes: %s; pass the full path."
const MAX_LISTED_NODES := 10
## An element whose centre a press would land elsewhere: the target's path, the point, the hit's
## path, the target's rect, and ", hit rect <rect>" (empty when nothing is hit).
const COVERED_TARGET := (
	"the centre of %s (%s) lands on %s, which covers it (target rect %s%s); "
	+ "click by {x, y} inside the target's visible part, or wait until nothing covers it."
)
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
## Answers {result: {pointer, heldButtonMask}}, to which a click, drag, mouse_button or hover adds
## the Controls it hit (a hover its tooltip too) and a pad gesture the gamepad's report (device,
## warning), or {error}. Takes
## the uiChanged baseline when none is pending.
func play(params: Dictionary) -> Dictionary:
	bridge._gesture_playing = true
	_hits = {}
	bridge._pads.report = {}
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
	_add_pad_report(result)
	return {"result": result}


## Adds the gamepad's report to result; a warning follows one result already has, after a space.
func _add_pad_report(result: Dictionary) -> void:
	var report: Dictionary = bridge._pads.report
	for key: String in report:
		if key == "warning" and result.has("warning"):
			result["warning"] = "%s %s" % [result["warning"], report["warning"]]
		else:
			result[key] = report[key]


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


## The movie frames a second of a recording, or 0 when the game does not record: Movie Maker
## runs (Engine.get_write_movie_path is not empty, doc/classes/Engine.xml in 4.7.2) and
## GODOT_MCP_MOVIE_FPS names its rate. Game time then advances one frame's worth per frame
## however slowly the game runs, so a played duration counts clip time.
func clip_fps() -> int:
	if Engine.get_write_movie_path().is_empty():
		return 0
	return maxi(0, int(OS.get_environment(MOVIE_FPS_VARIABLE)))


## The frames ms of clip time takes at fps, rounded up; -1 when fps is 0 (no recording), where
## durations count real time.
static func clip_frames(ms: int, fps: int) -> int:
	if fps <= 0:
		return -1
	return ceili(float(ms) * float(fps) / 1000.0)


## How far a gesture played over duration_ms is at its step'th frame, elapsed_ms after it began,
## 0 to 1: step of frames in a recording (frames not -1, from clip_frames), else elapsed_ms of
## duration_ms.
static func played_progress(step: int, elapsed_ms: int, duration_ms: int, frames: int) -> float:
	if frames >= 0:
		return clampf(float(step) / maxf(float(frames), 1.0), 0.0, 1.0)
	return clampf(float(elapsed_ms) / maxf(float(duration_ms), 1.0), 0.0, 1.0)


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
	if HIT_GESTURES.has(gesture):
		return await _play_pointer_gesture(gesture, params)
	var error: String = "unknown gesture '%s'" % gesture
	match gesture:
		"type_text":
			error = await _play_text(str(params.get("text", "")))
		"key":
			error = await _play_key(params)
		"gamepad_button":
			error = await bridge._pads.play_button(params)
		"gamepad_axes":
			error = await bridge._pads.play_axes(params)
		"events":
			error = await _play_events(params.get("events"))
	return error


## Plays one of HIT_GESTURES, whose result says which Controls it hit.
func _play_pointer_gesture(gesture: String, params: Dictionary) -> String:
	var error: String = ""
	match gesture:
		"click":
			error = await _play_click(params)
		"drag":
			error = await _play_drag(params)
		"mouse_button":
			error = await _play_mouse_button(params)
		"hover":
			error = await _play_hover(params)
	return error


## Refuses a target that cannot be clicked before anything is sent, then dismisses tooltips and
## aims (resolving the target again, since the dismiss can take a frame) before the click.
func _play_click(params: Dictionary) -> String:
	var refusal: String = _refusal_of(params.get("target"))
	if not refusal.is_empty():
		return refusal
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	await _dismiss_tooltips()
	var point: Variant = _aim(params.get("target"), true)
	if point is String:
		return point
	await _click_at(_to_window(point), button, bool(params.get("doubleClick", false)))
	return ""


## Presses and releases at the point the pointer is already at, one frame apart; a double click
## follows with a second press marked double_click. The hits are the last press's and release's.
func _click_at(window_point: Vector2, button: int, double_click: bool) -> void:
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
## (the gesture's move) hovers the Control beneath. The engine cancels a tooltip on the press
## itself (L2011), not on the release; the bridge dismisses it first anyway, since that cancel's
## queue_free lands only after pressedOn has been read.
func _dismiss_tooltips() -> void:
	var dismissed: bool = false
	for window: Window in get_tree().root.get_embedded_subwindows():
		if window.visible and bridge._ui_snapshot.is_tooltip(window):
			window.queue_free()
			dismissed = true
	if dismissed:
		await get_tree().process_frame


## The Control under a viewport point as {path, class}, or null over none, read right after a
## mouse event there.
func _control_under(point: Vector2) -> Variant:
	return _describe(_hovered_control(point))


## A Control as {path, class}, or null for none.
func _describe(control: Control) -> Variant:
	if control == null:
		return null
	return {"path": str(control.get_path()), "class": control.get_class()}


## The Control under a viewport point, or null over none, read right after a mouse event there:
## the topmost visible embedded window holding the point (a popup) answers for it, else the
## root, each with the Control its GUI picked for its last mouse event.
func _hovered_control(point: Vector2) -> Control:
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
	return viewport.gui_get_hovered_control()


## Refuses either end before anything is sent; after the tooltip dismiss, resolves the end and
## aims at the start, the only end hit-tested: what is dragged may cover the drop point.
func _play_drag(params: Dictionary) -> String:
	var refusal: String = _refusal_of(params.get("from"))
	if not refusal.is_empty():
		return "from: %s" % refusal
	refusal = _refusal_of(params.get("to"))
	if not refusal.is_empty():
		return "to: %s" % refusal
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	var duration_ms: int = maxi(0, int(params.get("durationMs", 300)))
	await _dismiss_tooltips()
	var end: Variant = _resolve_point(params.get("to"))
	if end is String:
		return "to: %s" % end
	var start: Variant = _aim(params.get("from"), true)
	if start is String:
		return "from: %s" % start
	await _drag(_to_window(start), _to_window(end), duration_ms, button)
	return ""


## Presses at start, where the pointer already is, then sends one motion a frame along the
## straight line to end for duration_ms (in a recording, its clip frames; and at least
## MIN_DRAG_STEPS frames), each carrying the held button in its button_mask and its step as
## relative: Godot's viewport starts a drag only from motions with LEFT in the mask whose
## relatives add up past gui/common/drag_threshold. Records whether the GUI was dragging after
## any motion, and whether the release dropped it.
func _drag(start: Vector2, end: Vector2, duration_ms: int, button: int) -> void:
	var root: Window = get_tree().root
	var gui_drag_started: bool = false
	_send_and_record(start, button, true, false)
	var frames: int = clip_frames(duration_ms, clip_fps())
	var began: int = Time.get_ticks_msec()
	var step: int = 0
	var progress: float = 0.0
	while progress < 1.0:
		await get_tree().process_frame
		step += 1
		progress = played_progress(step, Time.get_ticks_msec() - began, duration_ms, frames)
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


## A press or a move is hit-tested; a release is not, since Godot sends it to the Control that
## took the press wherever the pointer is (scene/main/viewport.cpp L2019-2025 in 4.7.2).
func _play_mouse_button(params: Dictionary) -> String:
	var refusal: String = _refusal_of(params.get("target"))
	if not refusal.is_empty():
		return refusal
	var button: int = _parse_button(params.get("button", "left"))
	if button == 0:
		return _unknown_button(params.get("button"))
	var action: String = str(params.get("action", "press"))
	if not action in ["press", "release", "move"]:
		return "unknown mouse_button action '%s'; use press, release or move" % action
	if action == "press":
		await _dismiss_tooltips()
	var point: Variant = _aim(params.get("target"), action != "release")
	if point is String:
		return point
	if action == "move":
		await _settle_hover(point)
		return ""
	_send_and_record(_to_window(point), button, action == "press", false)
	await get_tree().process_frame
	return ""


## After _aim has moved the pointer to a viewport point, carrying the held buttons in the
## motion's button_mask and pressing nothing, waits a frame and records the Control under it as
## hoveredOn. Returns that Control, or null over none.
func _settle_hover(point: Vector2) -> Control:
	await get_tree().process_frame
	var control: Control = _hovered_control(point)
	_hits["hoveredOn"] = _describe(control)
	return control


## Moves to the target as mouse_button's move does; then, when params.tooltip is not false and
## a Control has a tooltip for the pointer (the hovered one or an ancestor, as _tooltip_owner
## finds it), waits for it to show. Records tooltip ({text, x, y, width, height, owner}, or
## null) and a warning when a tooltip was due and none showed.
func _play_hover(params: Dictionary) -> String:
	var point: Variant = _aim(params.get("target"), true)
	if point is String:
		return point
	var control: Control = await _settle_hover(point)
	_hits["tooltip"] = null
	if control == null or not bool(params.get("tooltip", true)):
		return ""
	var tooltip_owner: Control = _tooltip_owner(control, point)
	if tooltip_owner == null:
		return ""
	# The tooltip timer starts only from a motion over a Control that can process
	# (scene/main/viewport.cpp L2117, L2136 in 4.7.2), so a pausable one in a paused tree
	# never shows its tooltip, while gui_get_hovered_control still names it.
	if not control.can_process():
		_hits["warning"] = PAUSED_TOOLTIP_WARNING % str(control.get_path())
		return ""
	var timeout_ms: int = _tooltip_timeout_ms(params)
	var popup: Window = await _await_tooltip(tooltip_owner, timeout_ms)
	if popup == null:
		_hits["warning"] = "no tooltip showed within %d ms" % timeout_ms
	else:
		var tooltip: Dictionary = _describe_tooltip(popup)
		tooltip["owner"] = _describe(tooltip_owner)
		_hits["tooltip"] = tooltip
	return ""


## The Control whose tooltip Godot shows at a viewport point over the hovered control, or null
## when none has one, picked as the viewport's _gui_get_tooltip does (scene/main/viewport.cpp
## L1566-1596 in 4.7.2): from the hovered Control up through its parent Controls, the first
## whose get_tooltip answers text at the point (its tooltip_text, or a script's _get_tooltip);
## the climb ends after a Control whose mouse filter, mouse_behavior_recursive applied, is Stop,
## or which is top-level.
func _tooltip_owner(control: Control, point: Vector2) -> Control:
	var current: Control = control
	while current != null:
		var local: Vector2 = current.get_global_transform_with_canvas().affine_inverse() * point
		if not current.get_tooltip(local).is_empty():
			return current
		if (
			current.get_mouse_filter_with_override() == Control.MOUSE_FILTER_STOP
			or current.is_set_as_top_level()
		):
			return null
		current = current.get_parent_control()
	return null


## params.timeoutMs, else gui/timers/tooltip_delay_sec plus a second, at most
## HOVER_TIMEOUT_CAP_MS.
func _tooltip_timeout_ms(params: Dictionary) -> int:
	if params.has("timeoutMs"):
		return int(params["timeoutMs"])
	var delay: float = float(ProjectSettings.get_setting("gui/timers/tooltip_delay_sec", 0.5))
	return mini(int(delay * 1000.0) + 1000, HOVER_TIMEOUT_CAP_MS)


## The tooltip popup showing for tooltip_owner, checked now and then on each process_frame,
## which fires paused or not (scene/main/scene_tree.cpp L649, L713 in 4.7.2), until one shows or
## timeout_ms of real time passes (in a recording, its clip frames): the tooltip timer, once
## started, ignores pause and the time scale (viewport.cpp L2144-2146). Null when none showed.
func _await_tooltip(tooltip_owner: Control, timeout_ms: int) -> Window:
	var until: int = Time.get_ticks_msec() + timeout_ms
	var frames: int = clip_frames(timeout_ms, clip_fps())
	var waited: int = 0
	var popup: Window = _showing_tooltip(tooltip_owner)
	while popup == null and _within(waited, frames, until):
		await get_tree().process_frame
		waited += 1
		if not is_instance_valid(tooltip_owner):
			return null
		popup = _showing_tooltip(tooltip_owner)
	return popup


## Whether a wait that has waited frames of its frames (in a recording; -1 otherwise) is still
## within its limit, else whether Time.get_ticks_msec is before until_ms.
static func _within(waited: int, frames: int, until_ms: int) -> bool:
	if frames >= 0:
		return waited < frames
	return Time.get_ticks_msec() < until_ms


## A visible tooltip popup: an embedded subwindow of the root, or a Window under tooltip_owner
## when subwindows are not embedded, since Godot parents the popup to the Control whose tooltip
## it shows (scene/main/viewport.cpp L1687 in 4.7.2); null when none shows.
func _showing_tooltip(tooltip_owner: Control) -> Window:
	var candidates: Array = []
	candidates.append_array(get_tree().root.get_embedded_subwindows())
	candidates.append_array(tooltip_owner.get_children(true))
	for node: Node in candidates:
		if node is Window and (node as Window).visible and bridge._ui_snapshot.is_tooltip(node):
			return node as Window
	return null


## A tooltip popup as {text, x, y, width, height} in viewport coordinates; text is its Label's,
## null for a custom tooltip without one. A popup that is not embedded has a screen position.
func _describe_tooltip(popup: Window) -> Dictionary:
	var rect := Rect2(Vector2(popup.position), Vector2(popup.size))
	if not popup.is_embedded():
		var origin := Vector2(popup.position - get_tree().root.position)
		var top_left: Vector2 = _to_viewport(origin)
		rect = Rect2(top_left, _to_viewport(origin + Vector2(popup.size)) - top_left)
	var label: Label = _find_label(popup)
	var text: Variant = null
	if label != null:
		text = label.text
	return {
		"text": text,
		"x": rect.position.x,
		"y": rect.position.y,
		"width": rect.size.x,
		"height": rect.size.y,
	}


## The first Label under node, depth first, internal children included.
func _find_label(node: Node) -> Label:
	for child: Node in node.get_children(true):
		if child is Label:
			return child as Label
		var found: Label = _find_label(child)
		if found != null:
			return found
	return null


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


## Resolves a target and moves the pointer to its viewport point: the motion the gesture sends
## anyway, which also makes Godot hit-test that point. With checks_hit, an {element} target is
## then refused unless the Control Godot hovers there is one that takes its press (_lands_on).
## Returns the viewport point, or a String saying why the target was refused.
func _aim(target: Variant, checks_hit: bool) -> Variant:
	var resolved: Variant = _resolve_target(target)
	if resolved is String:
		return resolved
	var point: Vector2 = _point_of(resolved)
	_move_to(_to_window(point))
	if checks_hit and resolved is Control:
		var refusal: String = _hit_refusal(resolved as Control, point)
		if not refusal.is_empty():
			return refusal
	return point


## Why a target cannot be used, or "" when it can; nothing is sent.
func _refusal_of(target: Variant) -> String:
	var resolved: Variant = _resolve_target(target)
	return resolved if resolved is String else ""


## The viewport point a target names, or a String saying why it cannot be used; nothing is sent.
func _resolve_point(target: Variant) -> Variant:
	var resolved: Variant = _resolve_target(target)
	if resolved is String:
		return resolved
	return _point_of(resolved)


## An element's point is its centre in the root's viewport coordinates; a point target is its own
## point.
func _point_of(resolved: Variant) -> Vector2:
	if resolved is Control:
		var control := resolved as Control
		return _viewport_transform(control) * (control.size / 2.0)
	return resolved


## The Control a target {element} names or the Vector2 viewport point a target {x, y} names; a
## String instead says why the target cannot be used.
func _resolve_target(target: Variant) -> Variant:
	if not target is Dictionary:
		return "a target must be an object {element} or {x, y}"
	var spec: Dictionary = target
	if spec.has("element"):
		return _resolve_element(str(spec["element"]))
	if spec.has("x") and spec.has("y"):
		return Vector2(float(spec["x"]), float(spec["y"]))
	return "a target needs element, or both x and y; got %s" % JSON.stringify(spec)


## The live, visible Control an element names, or a String saying why there is none.
func _resolve_element(element: String) -> Variant:
	var found: Variant = _find_input_node(element)
	if found is String:
		return found
	var node: Node = found
	if not node is Control:
		return (
			"'%s' is a %s, not a Control, so it has no rect to aim at" % [element, node.get_class()]
		)
	if node.is_queued_for_deletion():
		return FREED_TARGET % str(node.get_path())
	if not (node as Control).is_visible_in_tree():
		return HIDDEN_TARGET % str(node.get_path())
	return node


## The node an element names: a path through the bridge's _find_node, or the one node of a bare
## name. A bare name no node has, or more than one node has, is a String saying so. Other tools
## keep _find_node's first match; an input target must be the node the caller means.
func _find_input_node(element: String) -> Variant:
	var named: Array[Node] = []
	if element.contains("/"):
		var node: Node = bridge._find_node(element)
		if node != null:
			named.append(node)
	else:
		named = _nodes_named(element)
	if named.is_empty():
		return bridge._inspect.not_found(
			element, "get_ui_elements lists the Controls' paths and names"
		)
	if named.size() > 1:
		return _ambiguous(element, named)
	return named[0]


## Every node of that name, breadth first from the root, in _find_node's order.
func _nodes_named(node_name: String) -> Array[Node]:
	var named: Array[Node] = []
	var queue: Array[Node] = [get_tree().root]
	while not queue.is_empty():
		var node: Node = queue.pop_front()
		if str(node.name) == node_name:
			named.append(node)
		queue.append_array(node.get_children())
	return named


func _ambiguous(node_name: String, named: Array[Node]) -> String:
	var paths := PackedStringArray()
	for node: Node in named.slice(0, MAX_LISTED_NODES):
		paths.append(str(node.get_path()))
	if named.size() > MAX_LISTED_NODES:
		paths.append("…")
	return AMBIGUOUS_TARGET % [node_name, named.size(), ", ".join(paths)]


## Why a press at point would miss target, or "" when it would not: Godot hovers, on a mouse
## event, the Control gui_find_control picks, the same pick a press makes when no other button
## is held (scene/main/viewport.cpp L3522-3524, L3331 and L1941 in 4.7.2), and the aim's motion
## has just been flushed there. A target that takes no clicks, itself or through an ancestor,
## may land on nothing.
func _hit_refusal(target: Control, point: Vector2) -> String:
	var hit: Control = _hovered_control(point)
	if _lands_on(hit, target) or (hit == null and _receiver(target) == null):
		return ""
	var hit_path: String = "<nothing>"
	var hit_rect: String = ""
	if hit != null:
		hit_path = str(hit.get_path())
		hit_rect = ", hit rect %s" % _rect_text(_viewport_rect(hit))
	return (
		COVERED_TARGET
		% [
			str(target.get_path()),
			"%s, %s" % [_num(point.x), _num(point.y)],
			hit_path,
			_rect_text(_viewport_rect(target)),
			hit_rect,
		]
	)


## Whether a press on hit reaches target: hit is the target, a descendant of it, or, for a
## target that ignores the mouse, the nearest ancestor that takes its clicks.
func _lands_on(hit: Control, target: Control) -> bool:
	if hit == null:
		return false
	return hit == target or target.is_ancestor_of(hit) or hit == _receiver(target)


## The target, or its nearest Control ancestor when it ignores the mouse, that takes a click
## aimed at it; null when neither it nor any Control above it does.
func _receiver(target: Control) -> Control:
	var node: Node = target
	while node is Control:
		if (node as Control).mouse_filter != Control.MOUSE_FILTER_IGNORE:
			return node as Control
		node = node.get_parent()
	return null


## A Control's rect in the root's viewport coordinates, the bounding box when it is rotated.
func _viewport_rect(control: Control) -> Rect2:
	return _viewport_transform(control) * Rect2(Vector2.ZERO, control.size)


## A Control's transform into the root's viewport coordinates, the ones input points use: its
## canvas transform, a CanvasLayer's or the viewport's canvas transform included
## (scene/main/canvas_item.cpp L183-192 in 4.7.2), then, for a Control inside an embedded window,
## the window's position and final transform: the inverse of the root's routing of a point into
## that window (scene/main/viewport.cpp L3311).
func _viewport_transform(control: Control) -> Transform2D:
	var xform: Transform2D = control.get_global_transform_with_canvas()
	var window: Window = control.get_viewport() as Window
	if window != null and window != get_tree().root and window.is_embedded():
		xform = Transform2D(0.0, Vector2(window.position)) * window.get_final_transform() * xform
	return xform


func _rect_text(rect: Rect2) -> String:
	return (
		"%s,%s,%s,%s"
		% [_num(rect.position.x), _num(rect.position.y), _num(rect.size.x), _num(rect.size.y)]
	)


## A coordinate with at most one decimal, and none when it is whole.
func _num(value: float) -> String:
	var rounded: float = snappedf(value, 0.1)
	if rounded == roundf(rounded):
		return str(int(rounded))
	return str(rounded)


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
## flushes it at once so accumulated input neither merges nor delays it. A running capture
## records it as sent first.
func _dispatch(event: InputEvent) -> void:
	bridge._capture.sent(event)
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
