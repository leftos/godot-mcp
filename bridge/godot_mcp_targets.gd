extends Node
## The godot-mcp bridge's input target resolver, a child of the input player (godot_mcp_input.gd):
## turns a target ({element}, a Control by path or unique bare name, or {x, y}, a viewport point)
## into the point a gesture aims at, or a String saying why the target cannot be used. The input
## player keeps the hit check and sends the motion.

## The refusals of an {element} target, each with the node's path in place of %s.
const HIDDEN_TARGET := "%s is hidden; get_ui_elements lists the visible Controls."
const FREED_TARGET := "%s is being freed; get_ui_elements lists the live Controls."
## A bare name more than one node has: the name, the count and at most MAX_LISTED_NODES paths.
const AMBIGUOUS_TARGET := "'%s' names %d nodes: %s; pass the full path."
const MAX_LISTED_NODES := 10

## The bridge (godot_mcp_bridge.gd), set by the input player before this node enters the tree.
var bridge: Node


## Why a target cannot be used, or "" when it can; nothing is sent.
func refusal_of(target: Variant) -> String:
	var resolved: Variant = resolve_target(target)
	return resolved if resolved is String else ""


## The viewport point a target names, or a String saying why it cannot be used; nothing is sent.
func resolve_point(target: Variant) -> Variant:
	var resolved: Variant = resolve_target(target)
	if resolved is String:
		return resolved
	return point_of(resolved)


## An element's point is its centre in the root's viewport coordinates; a point target is its own
## point.
func point_of(resolved: Variant) -> Vector2:
	if resolved is Control:
		var control := resolved as Control
		return viewport_transform(control) * (control.size / 2.0)
	return resolved


## The Control a target {element} names or the Vector2 viewport point a target {x, y} names; a
## String instead says why the target cannot be used.
func resolve_target(target: Variant) -> Variant:
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


## The node an element names: a path or a unique name (%Rows) through the bridge's _find_node, or
## the one node of a bare name. A bare name no node has, or more than one node has, is a String
## saying so. Other tools keep _find_node's first match; an input target must be the node the
## caller means.
func _find_input_node(element: String) -> Variant:
	var named: Array[Node] = []
	if element.contains("/") or element.begins_with("%"):
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


## Whether a press on hit reaches target: hit is the target, a descendant of it, or, for a
## target that ignores the mouse, the nearest ancestor that takes its clicks.
func lands_on(hit: Control, target: Control) -> bool:
	if hit == null:
		return false
	return hit == target or target.is_ancestor_of(hit) or hit == receiver(target)


## The target, or its nearest Control ancestor when it ignores the mouse, that takes a click
## aimed at it; null when neither it nor any Control above it does.
func receiver(target: Control) -> Control:
	var node: Node = target
	while node is Control:
		if (node as Control).mouse_filter != Control.MOUSE_FILTER_IGNORE:
			return node as Control
		node = node.get_parent()
	return null


## A Control's rect in the root's viewport coordinates, the bounding box when it is rotated.
func viewport_rect(control: Control) -> Rect2:
	return viewport_transform(control) * Rect2(Vector2.ZERO, control.size)


## A Control's transform into the root's viewport coordinates, the ones input points use: its
## canvas transform, a CanvasLayer's or the viewport's canvas transform included
## (scene/main/canvas_item.cpp L183-192 in 4.7.2), then, for a Control inside an embedded window,
## the window's position and final transform: the inverse of the root's routing of a point into
## that window (scene/main/viewport.cpp L3311).
func viewport_transform(control: Control) -> Transform2D:
	var xform: Transform2D = control.get_global_transform_with_canvas()
	var window: Window = control.get_viewport() as Window
	if window != null and window != get_tree().root and window.is_embedded():
		xform = Transform2D(0.0, Vector2(window.position)) * window.get_final_transform() * xform
	return xform
