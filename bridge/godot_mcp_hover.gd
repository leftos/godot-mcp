@tool
extends Node
## The tooltip and point reader, the input player's (godot_mcp_input.gd) child: for a hover, finds
## the Control whose tooltip Godot shows at the pointer, waits for its tooltip popup to show and
## describes it; for every pointer gesture, reads and describes the Control under a point, and
## frees the tooltips a press must reach past. The hover gesture itself (the aim, hoveredOn, the
## warnings) stays in the input player.

const TooltipSpans := preload("godot_mcp_tooltip_spans.gd")

## The longest a hover waits for a tooltip, the server's own limit on timeoutMs.
const HOVER_TIMEOUT_CAP_MS := 10000

## The input player (godot_mcp_input.gd) and the target resolver (godot_mcp_targets.gd), set by
## the input player before this node enters the tree.
var gestures: Node
var targets: Node

## Whether the engine cancelled a tooltip (a ui_cancel press, a mouse button press, a gesture:
## scene/main/viewport.cpp L2297-2299, L2011, L2246 in 4.7.2) and no motion that would let the
## engine re-arm one has come since.
var _tooltip_cancelled: bool = false
## The Control hovered at the injected pointer on the first frame after that cancel, null until
## then.
var _cancelled_over: Control = null
## The injected pointer where the cancel stood, which a no-button motion must move more than 5 px
## from to let the engine restart its tooltip timer (scene/main/viewport.cpp L2138 in 4.7.2).
var _cancel_point: Vector2 = Vector2.ZERO
## Whether the first frame after the mark has recorded the Control under the pointer, so a null or
## freed one does not run that check again and take the next Control's tooltip: a freed object
## compares equal to null (core/variant/variant_op.h L484-488 in 4.7.2).
var _over_noted: bool = false


## The Control whose tooltip Godot shows at a viewport point over the hovered control, or null
## when none has one, picked as the viewport's _gui_get_tooltip does (scene/main/viewport.cpp
## L1566-1596 in 4.7.2): from the hovered Control up through its parent Controls, the first
## whose get_tooltip answers text at the point (its tooltip_text, or a script's _get_tooltip).
## That virtual is how a MenuBar answers a title's tooltip (scene/gui/menu_bar.cpp L989-995)
## and a PopupMenu an item's, its items drawn by its internal PopupMenuItems Control
## (scene/gui/popup_menu.cpp L3845-3851). The point is mapped into each Control through its own
## transform, an embedded window's included, as point_of maps a target's centre. The climb ends
## after a Control whose mouse filter, mouse_behavior_recursive applied, is Stop, or which is
## top-level.
func tooltip_owner(control: Control, point: Vector2) -> Control:
	var current: Control = control
	while current != null:
		var local: Vector2 = targets.viewport_transform(current).affine_inverse() * point
		if not current.get_tooltip(local).is_empty():
			return current
		if (
			current.get_mouse_filter_with_override() == Control.MOUSE_FILTER_STOP
			or current.is_set_as_top_level()
		):
			return null
		current = current.get_parent_control()
	return null


## The warning for a hover at a viewport point that found no tooltip over control: when control
## is a RichTextLabel with tooltip spans, the nearest span (TooltipSpans.probe_near, then
## near_miss_warning in viewport coordinates), then where the probe stopped when it ran out of
## samples; "" for a label with none whose probe ran to its end, and for any other Control.
func near_miss_warning(control: Control, point: Vector2) -> String:
	var label := control as RichTextLabel
	if label == null:
		return ""
	var path: String = str(label.get_path())
	var xform: Transform2D = targets.viewport_transform(label)
	var found: Dictionary = TooltipSpans.probe_near(label, xform.affine_inverse() * point)
	var shown: Array = TooltipSpans.placed(found["spans"], xform)
	var near_miss: String = TooltipSpans.near_miss_warning(path, point, shown, targets)
	var stopped: String = TooltipSpans.stopped_warning(path, found["stopped"], xform, targets)
	return TooltipSpans.and_then(near_miss, stopped)


## params.timeoutMs, else gui/timers/tooltip_delay_sec plus a second, at most
## HOVER_TIMEOUT_CAP_MS.
func tooltip_timeout_ms(params: Dictionary) -> int:
	if params.has("timeoutMs"):
		return int(params["timeoutMs"])
	var delay: float = float(ProjectSettings.get_setting("gui/timers/tooltip_delay_sec", 0.5))
	return mini(int(delay * 1000.0) + 1000, HOVER_TIMEOUT_CAP_MS)


## The tooltip popup showing for tooltip_owner, checked now and then on each process_frame,
## which fires paused or not (scene/main/scene_tree.cpp L649, L713 in 4.7.2), until one shows or
## timeout_ms of real time passes (in a recording, its clip frames): the tooltip timer, once
## started, ignores pause and the time scale (viewport.cpp L2144-2146). Null when none showed.
func await_tooltip(tooltip_owner: Control, timeout_ms: int) -> Window:
	var until: int = Time.get_ticks_msec() + timeout_ms
	var frames: int = gestures.clip_frames(timeout_ms, gestures.clip_fps())
	var waited: int = 0
	var popup: Window = _showing_tooltip(tooltip_owner)
	while popup == null and gestures.within(waited, frames, until):
		await get_tree().process_frame
		waited += 1
		if not is_instance_valid(tooltip_owner):
			return null
		popup = _showing_tooltip(tooltip_owner)
	return popup


## A visible tooltip popup: an embedded subwindow of the root, or a Window under tooltip_owner
## when subwindows are not embedded, since Godot parents the popup to the Control whose tooltip
## it shows (scene/main/viewport.cpp L1687 in 4.7.2); null when none shows.
func _showing_tooltip(tooltip_owner: Control) -> Window:
	var candidates: Array = []
	candidates.append_array(get_tree().root.get_embedded_subwindows())
	candidates.append_array(tooltip_owner.get_children(true))
	for node: Node in candidates:
		if (
			node is Window
			and (node as Window).visible
			and gestures.bridge._ui_snapshot.is_tooltip(node)
		):
			return node as Window
	return null


## A tooltip popup as {text, x, y, width, height} in viewport coordinates; text is its Label's,
## null for a custom tooltip without one. A popup that is not embedded has a screen position.
func describe_tooltip(popup: Window) -> Dictionary:
	var rect := Rect2(Vector2(popup.position), Vector2(popup.size))
	if not popup.is_embedded():
		var origin := Vector2(popup.position - get_tree().root.position)
		var top_left: Vector2 = gestures.to_viewport(origin)
		rect = Rect2(top_left, gestures.to_viewport(origin + Vector2(popup.size)) - top_left)
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
func dismiss_tooltips() -> void:
	var dismissed: bool = false
	for window: Window in get_tree().root.get_embedded_subwindows():
		if window.visible and gestures.bridge._ui_snapshot.is_tooltip(window):
			window.queue_free()
			dismissed = true
	if dismissed:
		await get_tree().process_frame


## Marks the tooltip the engine cancels from an event the window saw, injected ones included, and
## clears the mark on a motion that would let the engine re-arm one. Touches no tree, since the
## unit tests call it outside one.
func note_input(event: InputEvent) -> void:
	if _swallowed(event):
		return
	if event is InputEventMouseMotion:
		if _motion_clears(event):
			_clear_mark()
		return
	if _cancels_tooltip(event):
		_tooltip_cancelled = true
		_cancelled_over = null
		_over_noted = false
		_cancel_point = gestures.bridge._pointer


## Whether the bridge swallowed this event: while a gesture plays or injected input holds a button,
## its _input marks a real mouse's events handled (godot_mcp_bridge.gd), so the engine never sees
## them and none of them cancels a tooltip.
func _swallowed(event: InputEvent) -> bool:
	return (
		(gestures.bridge._gesture_playing or gestures.bridge._held_mask != 0)
		and gestures.bridge._is_real_pointer_event(event)
	)


## Whether a motion would let the engine re-arm a tooltip: a real mouse's hands the pointer back,
## and the engine starts its timer only on a no-button motion more than 5 px from where it stood
## (scene/main/viewport.cpp L2117, L2138 in 4.7.2). A held-button motion restarts nothing, and a
## motion within the 5 px leaves the timer running.
func _motion_clears(event: InputEventMouseMotion) -> bool:
	if event.device != gestures.bridge.INJECTED_DEVICE:
		return true
	return event.button_mask == 0 and event.position.distance_squared_to(_cancel_point) > 25.0


## Clears the mark, the Control it held and the note that the first frame recorded one.
func _clear_mark() -> void:
	_tooltip_cancelled = false
	_cancelled_over = null
	_over_noted = false


## Whether the engine cancels a tooltip on this event: any mouse button press (scene/main/
## viewport.cpp L2011 in 4.7.2), any gesture (L2246), or a ui_cancel press that is no echo
## (L2297-2299). is_action_pressed on an action the InputMap lacks logs an error, so the InputMap
## is asked first.
func _cancels_tooltip(event: InputEvent) -> bool:
	if event is InputEventMouseButton:
		return (event as InputEventMouseButton).pressed
	if event is InputEventGesture:
		return true
	if not event.is_action_type():
		return false
	return InputMap.has_action(&"ui_cancel") and event.is_action_pressed(&"ui_cancel")


## Drops the tooltip the engine re-arms after cancelling one while the injected pointer stays put,
## the game's own re-pick included: with no tooltip shown, over != gui.tooltip_control holds for a
## motion of zero distance and a new tooltip_delay timer starts (scene/main/viewport.cpp L2138,
## L2142-2146 in 4.7.2). That timer fires in process_timers, after the whole SceneTree process
## (scene/main/scene_tree.cpp L713, L729), and frame_pre_draw comes after both, so the popup is
## hidden here before it is ever drawn, then freed as dismiss_tooltips frees one. The mark is
## cleared instead on a first frame the engine never cancelled on, on a pointer that left the
## Control, and wherever the bridge's own re-pick is not what decides the hover.
func drop_rearmed_tooltip() -> void:
	if not _tooltip_cancelled:
		return
	if not _drops_here():
		_clear_mark()
		return
	var point: Vector2 = gestures.to_viewport(gestures.bridge._pointer)
	var over: Control = _hovered_under_tooltips(point)
	if not _over_noted:
		_note_cancelled_over(over, point)
		return
	if over == null or over != _cancelled_over:
		_clear_mark()
		return
	_drop_tooltip_at(over, point)


## Whether this bridge's own re-pick decides the hover: it owns the pointer and repick_hover is on.
## With repickHover off the game's own re-picks decide it (decision 39), and a pointer a person's
## real mouse took back gets plain Godot behaviour.
func _drops_here() -> bool:
	return gestures.bridge._owns_pointer and gestures.repick_hover


## Records the Control the pointer hovers on the first frame after the cancel, and clears the mark
## when that Control is null, or when a visible tooltip for its owner is not queued for free: the
## engine re-arms only with an over (scene/main/viewport.cpp L2104, L2136 in 4.7.2), and every
## cancel frees the showing popup (L1561-1562), whose delete queue empties before frame_pre_draw
## (scene/main/scene_tree.cpp L735), so a popup still standing means a game's _input handled the
## Escape. A re-armed tooltip cannot show yet: this runs before the re-pick that would start its
## timer.
func _note_cancelled_over(over: Control, point: Vector2) -> void:
	_over_noted = true
	_cancelled_over = over
	if over == null:
		_clear_mark()
		return
	var tooltip_owner_control: Control = tooltip_owner(over, point)
	if tooltip_owner_control == null:
		return
	var popup: Window = _owner_tooltip(tooltip_owner_control)
	if popup != null and not popup.is_queued_for_deletion():
		_clear_mark()


## Drops the tooltip the engine re-armed for the Control the pointer stayed on: hidden before it is
## drawn, then freed as dismiss_tooltips frees one, its predelete clearing the viewport's pointer
## to it (scene/main/viewport.cpp L771-774, L1561-1563 in 4.7.2).
func _drop_tooltip_at(over: Control, point: Vector2) -> void:
	var tooltip_owner_control: Control = tooltip_owner(over, point)
	if tooltip_owner_control == null:
		return
	var popup: Window = _owner_tooltip(tooltip_owner_control)
	if popup == null:
		return
	popup.hide()
	popup.queue_free()


## The visible tooltip popup the engine parented to tooltip_owner_control (scene/main/viewport.cpp
## L1687 in 4.7.2), found in the root's embedded subwindows or the Control's children, or null.
## Another Control's tooltip lying over the pointer has a different parent, so the drop never takes
## it for this owner's.
func _owner_tooltip(tooltip_owner_control: Control) -> Window:
	var candidates: Array = []
	candidates.append_array(get_tree().root.get_embedded_subwindows())
	candidates.append_array(tooltip_owner_control.get_children(true))
	for node: Node in candidates:
		if (
			node is Window
			and (node as Window).visible
			and node.get_parent() == tooltip_owner_control
			and gestures.bridge._ui_snapshot.is_tooltip(node)
		):
			return node as Window
	return null


## The Control under a viewport point, as hovered_control finds it, but with every visible tooltip
## window skipped: the border-hug fallback (scene/main/viewport.cpp L1715, L1726 in 4.7.2) can
## place a tooltip over the pointer, and it must not hide the Control beneath.
func _hovered_under_tooltips(point: Vector2) -> Control:
	var viewport: Viewport = get_tree().root
	var windows: Array[Window] = get_tree().root.get_embedded_subwindows()
	for index in range(windows.size() - 1, -1, -1):
		var window: Window = windows[index]
		if (
			window.visible
			and not gestures.bridge._ui_snapshot.is_tooltip(window)
			and Rect2(Vector2(window.position), Vector2(window.size)).has_point(point)
		):
			viewport = window
			break
	return viewport.gui_get_hovered_control()


## The Control under a viewport point as {path, class}, or null over none, read right after a
## mouse event there.
func control_under(point: Vector2) -> Variant:
	return describe(hovered_control(point))


## A Control as {path, class}, or null for none.
func describe(control: Control) -> Variant:
	if control == null:
		return null
	return {"path": str(control.get_path()), "class": control.get_class()}


## The Control under a viewport point, or null over none, read right after a mouse event there:
## the topmost visible embedded window holding the point (a popup) answers for it, else the
## root, each with the Control its GUI picked for its last mouse event.
func hovered_control(point: Vector2) -> Control:
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
