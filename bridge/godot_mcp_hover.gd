extends Node
## The tooltip reader, the input player's (godot_mcp_input.gd) child: for a hover, finds the
## Control whose tooltip Godot shows at the pointer, waits for its tooltip popup to show and
## describes it. The hover gesture itself (the aim, hoveredOn, the warnings) stays in the input
## player.

const TooltipSpans := preload("godot_mcp_tooltip_spans.gd")

## The longest a hover waits for a tooltip, the server's own limit on timeoutMs.
const HOVER_TIMEOUT_CAP_MS := 10000

## The input player (godot_mcp_input.gd) and the target resolver (godot_mcp_targets.gd), set by
## the input player before this node enters the tree.
var gestures: Node
var targets: Node


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
