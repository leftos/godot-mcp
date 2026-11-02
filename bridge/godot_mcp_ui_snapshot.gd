@tool
extends RefCounted
## The UI snapshot wait_for {uiChanged} compares: the visible Controls get_ui_elements lists, by
## path, the focus owner and the top popup, leaving out the bridge's own nodes, tooltips and drag
## previews; and the difference between two snapshots. Static functions called on the script
## itself.

## How many paths appeared and disappeared each list; their counts are the full totals.
const MAX_LISTED := 20
## The theme type variation Viewport gives the PopupPanel of every tooltip it shows, a child of
## the Control whose tooltip it is (scene/main/viewport.cpp L1660-1662, L1687 in 4.7.2).
const TOOLTIP_VARIATION := &"TooltipPanel"


## The UI of bridge's game now: {controls: {path: true}, focus, popup}, focus and popup a node
## path or null. previews holds the instance ids of the drag previews the input module saw added.
static func capture(bridge: Node, previews: Dictionary) -> Dictionary:
	var root: Window = bridge.get_tree().root
	var skip := func(node: Node) -> bool:
		return node == bridge or is_tooltip(node) or previews.has(node.get_instance_id())
	var controls: Array[Control] = []
	gather_controls(root, true, skip, controls)
	var paths: Dictionary = {}
	for control in controls:
		paths[str(control.get_path())] = true
	var popup: Window = top_popup(root)
	return {
		"controls": paths,
		"focus": _path_or_null(_focus_owner(root, popup)),
		"popup": _path_or_null(popup),
	}


## Appends every Control under node, depth first: the Controls get_ui_elements lists, and the UI
## snapshot wait_for {uiChanged} compares. An invisible Control hides its subtree when
## visible_only is set, and so does any node skip (when valid) returns true for.
static func gather_controls(
	node: Node, visible_only: bool, skip: Callable, into: Array[Control]
) -> void:
	if skip.is_valid() and skip.call(node):
		return
	var control := node as Control
	if control != null:
		if visible_only and not control.is_visible_in_tree():
			return
		into.append(control)
	for child in node.get_children():
		gather_controls(child, visible_only, skip, into)


## A Control as get_ui_elements lists it: {path, name, class, rect, visible[, text, items...,
## disabled, tooltip]}, its rect in bridge's viewport coordinates.
static func describe_control(bridge: Node, control: Control) -> Dictionary:
	var rect: Rect2 = bridge._gestures._targets.viewport_rect(control)
	var element: Dictionary = {
		"path": str(control.get_path()),
		"name": str(control.name),
		"class": control.get_class(),
		"rect": bridge._json.to_json(rect),
		"visible": control.is_visible_in_tree(),
	}
	var shown: Variant = bridge._shown_text.shown_text(control)
	if shown != null:
		element["text"] = shown
	var listed: Variant = bridge._item_targets.listed_items(control)
	if listed != null:
		element.merge(listed)
	if control is BaseButton:
		element["disabled"] = (control as BaseButton).disabled
	if not control.tooltip_text.is_empty():
		element["tooltip"] = control.tooltip_text
	return element


## What changed from before to after, two captures: {appeared, disappeared, appearedCount,
## disappearedCount}, at most MAX_LISTED paths in each list, plus focus {before, after} when the
## focus owner changed and popup {before, after} when the top popup did. Empty when nothing did.
static func diff(before: Dictionary, after: Dictionary) -> Dictionary:
	var appeared: PackedStringArray = _missing_from(after["controls"], before["controls"])
	var disappeared: PackedStringArray = _missing_from(before["controls"], after["controls"])
	var focus_moved: bool = before["focus"] != after["focus"]
	var popup_changed: bool = before["popup"] != after["popup"]
	if appeared.is_empty() and disappeared.is_empty() and not focus_moved and not popup_changed:
		return {}
	var change: Dictionary = {
		"appeared": Array(appeared.slice(0, MAX_LISTED)),
		"disappeared": Array(disappeared.slice(0, MAX_LISTED)),
		"appearedCount": appeared.size(),
		"disappearedCount": disappeared.size(),
	}
	if focus_moved:
		change["focus"] = {"before": before["focus"], "after": after["focus"]}
	if popup_changed:
		change["popup"] = {"before": before["popup"], "after": after["popup"]}
	return change


## Whether node is a tooltip's PopupPanel.
static func is_tooltip(node: Node) -> bool:
	return node is PopupPanel and (node as PopupPanel).theme_type_variation == TOOLTIP_VARIATION


## The last visible embedded window of root that is not a tooltip, the one drawn on top; null
## when there is none.
static func top_popup(root: Window) -> Window:
	var windows: Array[Window] = root.get_embedded_subwindows()
	for index in range(windows.size() - 1, -1, -1):
		if windows[index].visible and not is_tooltip(windows[index]):
			return windows[index]
	return null


## The top popup's focus owner when it has one, else the root's.
static func _focus_owner(root: Window, popup: Window) -> Control:
	var owner: Control = popup.gui_get_focus_owner() if popup != null else null
	return owner if owner != null else root.gui_get_focus_owner()


## The paths in paths that others lacks, in paths' order.
static func _missing_from(paths: Dictionary, others: Dictionary) -> PackedStringArray:
	var missing := PackedStringArray()
	for path: String in paths:
		if not others.has(path):
			missing.append(path)
	return missing


static func _path_or_null(node: Node) -> Variant:
	return str(node.get_path()) if node != null else null
