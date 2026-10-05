extends Node
## The godot-mcp bridge's input target resolver, a child of the input player (godot_mcp_input.gd):
## turns a target ({element}, a Control or a 2D or 3D world node by path or unique bare name,
## with an optional offset inside a world node; {text, under?}, the visible Control showing a text
## (godot_mcp_text_targets.gd); either with an item drawn inside a list Control
## (godot_mcp_item_targets.gd); or {x, y}, a viewport point) into the point in the root's viewport
## coordinates a gesture aims at, or a String saying why the target cannot be used. The input
## player sends the motion and asks this module which Control the GUI hovers on the way in.

const TextTargets := preload("godot_mcp_text_targets.gd")
const ItemTargets := preload("godot_mcp_item_targets.gd")
const PopupTargets := preload("godot_mcp_popup_targets.gd")

## The refusals of an {element} target, each with the node's path in place of %s.
const HIDDEN_TARGET := (
	"%s is hidden; get_ui_elements lists the visible Controls, " + "get_scene_tree every node."
)
const FREED_TARGET := (
	"%s is being freed; get_ui_elements lists the live Controls, " + "get_scene_tree every node."
)
## A bare name more than one node has: the name, the count and at most MAX_LISTED_NODES paths.
const AMBIGUOUS_TARGET := "'%s' names %d nodes: %s; pass the full path."
const MAX_LISTED_NODES := 10
## A node with no point to aim at: the element as given and the node's class.
const PLAIN_NODE_TARGET := (
	"'%s' is a %s, neither a Control nor a 2D or 3D node, " + "so it has no point to aim at."
)
## The offset refusals: the node's path, and for a 2D node its class; a malformed offset as JSON.
const CONTROL_OFFSET := "offset aims inside a world node; %s is a Control."
const POPUP_OFFSET := "offset aims inside a world node; %s is a PopupMenu."
const OFFSET_2D_Z := "%s is a %s; offset takes x and y."
const BAD_OFFSET := "offset must be an object {x, y, z?} of numbers; got %s"
## A 3D node's refusals: the node's path, then its viewport's or its camera's.
const NO_CAMERA := "%s is in viewport %s, which has no current Camera3D."
const BEHIND_CAMERA := "%s is behind the camera %s."
## Off-screen: the node's path, the point, then the viewport's size, or the SubViewport's path and
## size.
const OFF_ROOT := "%s projects to (%s), outside the %s viewport."
const OFF_SUBVIEWPORT := "%s projects to (%s) in %s, outside its %s rect."
## Outside an embedded window the node is drawn in: the node's path, the point in the window's
## embedder's coordinates, the window's path and its rect there.
const OFF_WINDOW := "%s projects to (%s), outside the embedded window %s (rect %s)."
## A viewport on the way to the root that input cannot reach: the node's path and its.
const NO_CONTAINER := (
	"%s is in SubViewport %s, which has no SubViewportContainer parent; only a "
	+ "SubViewportContainer forwards input to a SubViewport."
)
const NATIVE_WINDOW := (
	"%s is in native window %s; " + "input reaches only the root and its embedded windows."
)
## A SubViewport on the way whose input is disabled: the node's path, the SubViewport's, and
## HOVER_STILL_AIMS for a world node ("" for a Control).
const INPUT_DISABLED := (
	"%s is in SubViewport %s, whose gui_disable_input is on, " + "so no input reaches it%s."
)
const HOVER_STILL_AIMS := "; hover and mouse_button move can still aim at a world node there"
## The pointer on the way into a SubViewport misses its container: the node's path, the point, the
## Control hit (or <nothing>), the container's path and the SubViewport's.
const CONTAINER_MISSED := (
	"%s projects to (%s), where the pointer lands on %s, not on %s, the "
	+ "SubViewportContainer that forwards input to %s."
)
## An embedded window takes a world node's event: the node's path, the point, the window's path.
const EMBEDDED_WINDOW := (
	"%s projects to (%s), inside the embedded window %s, which takes the event before "
	+ "physics picking."
)
## The GUI takes a world node's click: the node's path, the point, the Control's path, class and
## rect.
const GUI_TAKES_CLICK := (
	"%s projects to (%s), where %s (%s, mouse_filter Stop, rect %s) takes the click before "
	+ "physics picking; aim elsewhere with offset, or at that Control."
)

## The bridge (godot_mcp_bridge.gd), set by the input player before this node enters the tree.
var bridge: Node


## Why a target cannot be used, or "" when it can; nothing is sent.
func refusal_of(target: Variant) -> String:
	var resolved: Variant = resolve_target(target)
	return resolved if resolved is String else ""


## Why a target cannot take a press, or "" when it can: refusal_of's reasons, and a SubViewport on
## the way whose gui_disable_input is on (disabled_refusal). Nothing is sent.
func press_refusal_of(target: Variant) -> String:
	var resolved: Variant = resolve_target(target)
	if resolved is String:
		return resolved
	if resolved is Dictionary:
		return disabled_refusal(resolved)
	return ""


## Why a drag cannot start or end at a target, or "" when it can: press_refusal_of's reasons, and
## an item in a popup. Nothing is sent.
func drag_refusal_of(target: Variant) -> String:
	var resolved: Variant = resolve_target(target)
	if resolved is Dictionary and (resolved as Dictionary).has("probe"):
		return PopupTargets.DRAG_REFUSED
	return press_refusal_of(target)


## Why no event reaches an aim's node through a SubViewport whose gui_disable_input is on, or ""
## when none on the way has it: its container forwards nothing to it
## (scene/gui/subviewport_container.cpp L230-235 in 4.7.2). A world node may still be aimed at
## by a hover, which presses nothing.
func disabled_refusal(aim: Dictionary) -> String:
	var disabled: String = aim["input_disabled"]
	if disabled.is_empty():
		return ""
	var hover_note: String = "" if aim["kind"] == "control" else HOVER_STILL_AIMS
	return INPUT_DISABLED % [str((aim["node"] as Node).get_path()), disabled, hover_note]


## An element's point in the root's viewport coordinates; a point target is its own point.
func point_of(resolved: Variant) -> Vector2:
	if resolved is Dictionary:
		return resolved["point"]
	return resolved


## The aim an {element} or {text} target names or the Vector2 viewport point a target {x, y}
## names; a String instead says why the target cannot be used. An aim is {node, kind ("control",
## "node2d" or "node3d"), point (in the root's viewport coordinates), levels (the
## SubViewportContainers on the way in, outermost first, each {container, viewport}), window (the
## outermost embedded Window the node is drawn in, or null),
## input_disabled (the path of a SubViewport on the way whose input is disabled, or ""), for a
## text target matched ({by: "text", text: the shown text matched}), and for an item target item
## (the item it resolved to, _aim_at_item), and for an item in a popup probe, whose point the
## gesture places later (_aim_at_popup_item)}. A blank text is no text.
func resolve_target(target: Variant) -> Variant:
	if not target is Dictionary:
		return "a target must be an object {element}, {text} or {x, y}"
	var spec: Dictionary = target
	if spec.has("element"):
		return _resolve_element(str(spec["element"]), spec.get("offset"), spec.get("item"))
	if not str(spec.get("text", "")).strip_edges().is_empty():
		return _resolve_text(str(spec["text"]), spec.get("under"), spec.get("item"))
	if spec.has("x") and spec.has("y"):
		return Vector2(float(spec["x"]), float(spec["y"]))
	return "a target needs element, text, or both x and y; got %s" % JSON.stringify(spec)


## The aim at the one visible Control at or under the node under names (the root when null) that
## shows text, with matched, or at the item it draws that item names (null for none); or a String
## saying why there is none. An under no node or several nodes have is refused as an element
## naming it would be.
func _resolve_text(text: String, under: Variant, item: Variant) -> Variant:
	var start: Variant = _text_start(under)
	if start is String:
		return start
	var entry: Variant = TextTargets.find(self, start, text)
	if entry is String:
		return entry
	var control: Control = entry["control"]
	var aim: Variant = (
		_aim_of(control, "control", Vector3.ZERO) if item == null else _aim_at_item(control, item)
	)
	if aim is Dictionary:
		aim["matched"] = {"by": "text", "text": entry["shown"]}
	return aim


## The aim at the item a list Control draws: the list's aim at the item's point (its local point
## through the drawing Control's canvas transform, then carried out as a Control's own point is),
## with item, the item as aimed_at reports it, and drawer, the Control that draws it, which the
## hit check requires the press to land on (lands_on). kind stays "control", so the input module
## checks the hit as a Control's; aimed_at reports "item". An OptionButton's or MenuButton's
## popup item is _aim_at_popup_item's. A String says why there is none.
func _aim_at_item(node: Node, item: Variant) -> Variant:
	if PopupTargets.popup_of(node) != null:
		return _aim_at_popup_item(node, item)
	var placed: Variant = ItemTargets.resolve(self, node, item)
	if placed is String:
		return placed
	var drawer: Control = placed["drawer"]
	var own: Vector2 = drawer.get_global_transform_with_canvas() * (placed["local"] as Vector2)
	var aim: Variant = _aim_from(node, "control", own)
	if aim is Dictionary:
		aim["item"] = placed["report"]
		aim["drawer"] = drawer
	return aim


## The aim at an item of node's popup (PopupTargets.resolve), with no point yet: the popup's own
## aim (its viewport's levels and window), whose point the gesture places on the item by probing
## (godot_mcp_popup_targets.gd), with node, the Control or PopupMenu the target named, as its
## node; probe, the resolved item; item, the item as aimed_at reports it; and drawer, the popup's
## items Control, which the hit check requires the press to land on. A String says why there is
## none; nothing is sent.
func _aim_at_popup_item(node: Node, item: Variant) -> Variant:
	var probe: Variant = PopupTargets.resolve(node, item)
	if probe is String:
		return probe
	var aim: Variant = _aim_from(probe["popup"], "control", Vector2.ZERO)
	if aim is Dictionary:
		aim["node"] = node
		aim["probe"] = probe
		aim["item"] = probe["report"]
		aim["drawer"] = probe["items"]
	return aim


## An item of a PopupMenu named itself, a Window rather than a CanvasItem; an offset is refused.
func _resolve_popup(node: PopupMenu, offset: Variant, item: Variant) -> Variant:
	if offset != null:
		return POPUP_OFFSET % str(node.get_path())
	return _aim_at_popup_item(node, item)


## The node a text target's scan starts at: the root when under is null, else the node under
## names, refused as an element naming it would be when it is hidden or being freed; a plain Node
## (no visibility of its own) is scanned. A String says why there is none.
func _text_start(under: Variant) -> Variant:
	if under == null:
		return get_tree().root
	var found: Variant = _find_input_node(str(under))
	if found is String:
		return found
	var kind: String = kind_of(found)
	var refusal: String = "" if kind.is_empty() else _live_refusal(str(under), found, kind)
	return found if refusal.is_empty() else refusal


## The aim at the live, visible node an element names, or at the item it draws that item names
## (null for none); or a String saying why there is none.
func _resolve_element(element: String, offset: Variant, item: Variant) -> Variant:
	var found: Variant = _find_input_node(element)
	if found is String:
		return found
	var node: Node = found
	if node is PopupMenu and item != null:
		return _resolve_popup(node, offset, item)
	var kind: String = kind_of(node)
	var refusal: String = _live_refusal(element, node, kind)
	if refusal.is_empty():
		refusal = offset_refusal(offset, kind, str(node.get_path()), node.get_class())
	if not refusal.is_empty():
		return refusal
	if item != null:
		return _aim_at_item(node, item)
	return _aim_of(node, kind, offset_vector(offset))


## What a node is aimed at as: "control", "node2d" (any other CanvasItem), "node3d", or "" for a
## node with no transform to aim through.
static func kind_of(node: Node) -> String:
	if node is Control:
		return "control"
	if node is CanvasItem:
		return "node2d"
	if node is Node3D:
		return "node3d"
	return ""


## Why a node cannot be aimed at before its point is known, or "" when it can.
func _live_refusal(element: String, node: Node, kind: String) -> String:
	if kind.is_empty():
		return PLAIN_NODE_TARGET % [element, node.get_class()]
	if node.is_queued_for_deletion():
		return FREED_TARGET % str(node.get_path())
	var visible: bool = (
		(node as Node3D).is_visible_in_tree()
		if kind == "node3d"
		else (node as CanvasItem).is_visible_in_tree()
	)
	if not visible:
		return HIDDEN_TARGET % str(node.get_path())
	return ""


## Why an offset cannot be used on a node of kind (path and node_class name it), or "" when it
## can or there is none (null).
static func offset_refusal(
	offset: Variant, kind: String, path: String, node_class: String
) -> String:
	if offset == null:
		return ""
	if kind == "control":
		return CONTROL_OFFSET % path
	if _malformed_offset(offset):
		return BAD_OFFSET % JSON.stringify(offset)
	if kind == "node2d" and (offset as Dictionary).has("z"):
		return OFFSET_2D_Z % [path, node_class]
	return ""


static func _malformed_offset(offset: Variant) -> bool:
	if not offset is Dictionary:
		return true
	var spec: Dictionary = offset
	for key: String in ["x", "y"]:
		if not spec.has(key) or not _is_number(spec[key]):
			return true
	return spec.has("z") and not _is_number(spec["z"])


static func _is_number(value: Variant) -> bool:
	return value is int or value is float


## An offset as a Vector3, z 0 when left out; the origin when there is none (null).
static func offset_vector(offset: Variant) -> Vector3:
	if not offset is Dictionary:
		return Vector3.ZERO
	var spec: Dictionary = offset
	return Vector3(float(spec["x"]), float(spec["y"]), float(spec.get("z", 0.0)))


## The aim at a node: its point in its own viewport, carried out to the root's coordinates; a
## world node's point is refused in a native window and off-screen.
func _aim_of(node: Node, kind: String, offset: Vector3) -> Variant:
	var own: Variant = _own_point(node, kind, offset)
	if own is String:
		return own
	return _aim_from(node, kind, own)


## The aim at a node whose point in its own viewport is own, carried out to the root's
## coordinates; a world node's point is refused in a native window and off-screen.
func _aim_from(node: Node, kind: String, own: Vector2) -> Variant:
	var carried: Dictionary = _carry_out(node.get_viewport())
	var refusal: String = _carry_refusal(node, kind, carried)
	if not refusal.is_empty():
		return refusal
	var levels: Array[Dictionary] = carried["levels"]
	var point: Vector2 = (carried["xform"] as Transform2D) * own
	if kind != "control":
		refusal = _off_screen(node, point, levels, carried["windows"])
		if not refusal.is_empty():
			return refusal
	return {
		"node": node,
		"kind": kind,
		"point": point,
		"levels": levels,
		"window": _outermost(carried["windows"]),
		"input_disabled": _disabled_viewport(levels),
	}


## The last of the embedded windows a walk passed, the one its embedder, normally the root, lists;
## null when it passed none.
static func _outermost(windows: Array[Window]) -> Window:
	return null if windows.is_empty() else windows[-1]


## Why the walk out of a node's viewport cannot carry its point to the root, or "": a SubViewport
## with no container; for a world node, a native window, whose events never pass the root. A
## Control in a native window keeps the mapping it always had.
func _carry_refusal(node: Node, kind: String, carried: Dictionary) -> String:
	var stray: Viewport = carried["stray"]
	if stray != null:
		return NO_CONTAINER % [str(node.get_path()), str(stray.get_path())]
	var native: Window = carried["native"]
	if native != null and kind != "control":
		return NATIVE_WINDOW % [str(node.get_path()), str(native.get_path())]
	return ""


## A node's point in its own viewport's coordinates: a Control's rect centre; a 2D node's origin
## plus offset through its canvas transform, which holds a Camera2D's and a CanvasLayer's
## (scene/main/canvas_item.cpp L183-192 in 4.7.2); a 3D node's through its viewport's camera.
func _own_point(node: Node, kind: String, offset: Vector3) -> Variant:
	if kind == "control":
		var control := node as Control
		return control.get_global_transform_with_canvas() * (control.size / 2.0)
	if kind == "node2d":
		var item := node as CanvasItem
		return item.get_global_transform_with_canvas() * Vector2(offset.x, offset.y)
	return _projected(node as Node3D, offset)


## A 3D node's origin plus offset (in its local space) projected by its viewport's current camera,
## in that viewport's coordinates (unproject_position scales by the camera's viewport's visible
## rect, scene/3d/camera_3d.cpp L482-504 in 4.7.2), or a String when it has no camera or the point
## is behind it: unproject_position mirrors such a point onto the screen, and is_position_behind
## (L458-462) counts one nearer than the near plane as behind.
func _projected(node: Node3D, offset: Vector3) -> Variant:
	var viewport: Viewport = node.get_viewport()
	var camera: Camera3D = viewport.get_camera_3d()
	if camera == null:
		return NO_CAMERA % [str(node.get_path()), str(viewport.get_path())]
	var world: Vector3 = node.global_transform * offset
	if camera.is_position_behind(world):
		return BEHIND_CAMERA % [str(node.get_path()), str(camera.get_path())]
	return camera.unproject_position(world)


## How a point in viewport's coordinates reaches the root's: {xform, levels, windows, stray,
## native}. Each step out applies the viewport's final transform, as the engine's input
## localisation inverts it (scene/main/viewport.cpp L1467-1472, L3420 in 4.7.2), then, out of an
## embedded Window, its position, which is in its embedder's coordinates (the inverse of the
## embedder's routing into it, L3311), and the walk goes on from that embedder, past any viewport
## between; out of a SubViewport, container_transform through its SubViewportContainer parent.
## levels lists the containers passed, outermost first; windows the embedded Windows passed,
## innermost first. The walk stops at a SubViewport with no container parent (stray) or a Window
## that is not embedded (native); each is null when the walk reached the root.
func _carry_out(viewport: Viewport) -> Dictionary:
	var xform := Transform2D.IDENTITY
	var levels: Array[Dictionary] = []
	var windows: Array[Window] = []
	var root: Window = get_tree().root
	while viewport != null and viewport != root:
		var parent: Node = viewport.get_parent()
		if viewport is Window:
			var window := viewport as Window
			if not window.is_embedded():
				return _carried(xform, levels, windows, null, window)
			xform = (
				Transform2D(0.0, Vector2(window.position)) * window.get_final_transform() * xform
			)
			windows.append(window)
			viewport = _embedder_of(window)
			continue
		if not parent is SubViewportContainer:
			return _carried(xform, levels, windows, viewport, null)
		var container := parent as SubViewportContainer
		xform = (
			container_transform(
				viewport.get_final_transform(),
				container.stretch,
				container.stretch_shrink,
				container.get_global_transform_with_canvas()
			)
			* xform
		)
		levels.push_front({"container": container, "viewport": viewport})
		viewport = container.get_viewport()
	return _carried(xform, levels, windows, null, null)


static func _carried(
	xform: Transform2D,
	levels: Array[Dictionary],
	windows: Array[Window],
	stray: Viewport,
	native: Window
) -> Dictionary:
	return {"xform": xform, "levels": levels, "windows": windows, "stray": stray, "native": native}


## The viewport an embedded Window is embedded in: the first viewport up from its parent's that
## embeds subwindows (scene/main/window.cpp L1477-1489 in 4.7.2), normally the root.
func _embedder_of(window: Window) -> Viewport:
	var viewport: Viewport = window.get_parent().get_viewport()
	while viewport != null and not viewport.gui_embed_subwindows:
		var parent: Node = viewport.get_parent()
		viewport = parent.get_viewport() if parent != null else null
	return viewport if viewport != null else get_tree().root


## One step out of a SubViewport through its SubViewportContainer: the SubViewport's final
## transform, then the scale the container's forwarding undoes (it sends events in scaled by
## 1/stretch_shrink when stretch is on and the shrink is over 1, scene/gui/subviewport_container.cpp
## L221-224 in 4.7.2), then the container's canvas transform, which places its local space in its
## own viewport.
static func container_transform(
	viewport_final: Transform2D, stretch: bool, shrink: int, container_canvas: Transform2D
) -> Transform2D:
	var scale := Transform2D.IDENTITY
	if stretch and shrink > 1:
		scale = Transform2D.IDENTITY.scaled(Vector2(shrink, shrink))
	return container_canvas * scale * viewport_final


## The path of the outermost SubViewport on the way in whose input is disabled, or "".
static func _disabled_viewport(levels: Array[Dictionary]) -> String:
	for level: Dictionary in levels:
		var viewport: Viewport = level["viewport"]
		if viewport.gui_disable_input:
			return str(viewport.get_path())
	return ""


## Why a world node's point is off-screen, or "": point, in the root's coordinates, must lie in each
## SubViewport's visible rect on the way out (innermost first, in that SubViewport's own
## coordinates), in each embedded window's grab rect (window_grab_rect, in its embedder's
## coordinates) and in the root's visible rect. A container's rect always covers its SubViewport's
## (a stretched one sizes the SubViewport to it, an unstretched one is never smaller than it), so
## it needs no check of its own. No point is clamped: a clamped click lands on something else.
func _off_screen(
	node: Node, point: Vector2, levels: Array[Dictionary], windows: Array[Window]
) -> String:
	var path: String = str(node.get_path())
	for index in range(levels.size() - 1, -1, -1):
		var viewport: Viewport = levels[index]["viewport"]
		var carried: Transform2D = _carry_out(viewport)["xform"]
		var inside: Vector2 = carried.affine_inverse() * point
		var size: Vector2 = viewport.get_visible_rect().size
		if not viewport.get_visible_rect().has_point(inside):
			return (
				OFF_SUBVIEWPORT
				% [path, point_text(inside), str(viewport.get_path()), size_text(size)]
			)
	for window: Window in windows:
		var embedder: Viewport = _embedder_of(window)
		var there: Vector2 = (_carry_out(embedder)["xform"] as Transform2D).affine_inverse() * point
		var rect: Rect2 = window_grab_rect(window)
		if not rect.has_point(there):
			return OFF_WINDOW % [path, point_text(there), str(window.get_path()), rect_text(rect)]
	var root: Window = get_tree().root
	if not root.get_visible_rect().has_point(point):
		return OFF_ROOT % [path, point_text(point), size_text(root.get_visible_rect().size)]
	return ""


## An aim as a result reports it: {x, y, kind, path, class}, x and y in the root's viewport
## coordinates, kind "item" for an item target, plus matched for a text target, item for an item
## target, opened when the gesture opened the item's popup, and viewport, its viewport's path,
## when that is neither the root nor the node itself (a PopupMenu).
func aimed_at(aim: Dictionary) -> Dictionary:
	var node: Node = aim["node"]
	var point: Vector2 = aim["point"]
	var described: Dictionary = {
		"x": snappedf(point.x, 0.01),
		"y": snappedf(point.y, 0.01),
		"kind": "item" if aim.has("item") else aim["kind"],
		"path": str(node.get_path()),
		"class": node.get_class(),
	}
	if aim.has("matched"):
		described["matched"] = aim["matched"]
	if aim.has("item"):
		described["item"] = aim["item"]
	if aim.get("opened", false):
		described["opened"] = true
	var viewport: Viewport = node.get_viewport()
	if viewport != get_tree().root and viewport != node:
		described["viewport"] = str(viewport.get_path())
	return described


## Why an embedded window of the root takes a world node's event at its point, or "" when none
## does: the root forwards an event inside a visible embedded window's rect, its title bar and
## resize margin included, to that window, and clears physics picking while the pointer is over
## one (scene/main/viewport.cpp L3284-3300, L790-795 in 4.7.2). The root hands the pointer to the
## topmost window at the point, its list's last first (L3283), so the walk goes from the top down
## and ends at the window the node is drawn in, or one holding it: those below cannot take it.
func covering_window_refusal(aim: Dictionary) -> String:
	var node: Node = aim["node"]
	var point: Vector2 = aim["point"]
	var own: Window = aim["window"]
	var windows: Array[Window] = get_tree().root.get_embedded_subwindows()
	for index in range(windows.size() - 1, -1, -1):
		var window: Window = windows[index]
		if own != null and (window == own or window.is_ancestor_of(own)):
			return ""
		if window.visible and window_grab_rect(window).has_point(point):
			return EMBEDDED_WINDOW % [str(node.get_path()), point_text(point), window.get_path()]
	return ""


## An embedded window's grab_rect in its embedder's coordinates, from its theme's title height and
## resize margin.
func window_grab_rect(window: Window) -> Rect2:
	var rect := Rect2(Vector2(window.position), Vector2(window.size))
	var title_height: int = window.get_theme_constant(&"title_height")
	var margin: int = window.get_theme_constant(&"resize_margin")
	return grab_rect(rect, window.borderless, title_height, margin)


## The rect in which an embedded window takes the pointer: its own, and for a window with a border
## its title bar above and its resize margin around (scene/main/viewport.cpp L3290-3300 in 4.7.2).
static func grab_rect(rect: Rect2, borderless: bool, title_height: int, margin: int) -> Rect2:
	if borderless:
		return rect
	return Rect2(
		rect.position - Vector2(margin, title_height + margin),
		rect.size + Vector2(margin * 2, title_height + margin * 2)
	)


## The Control the GUI hovers in an aim's own viewport after the aim's motion, from root_hit, the
## one the root (or a popup over the point) hovers: through each SubViewportContainer on the way
## in, which must itself be the hovered Control, since only the container's own gui_input
## forwards the event (scene/gui/subviewport_container.cpp L203-228 in 4.7.2), the next is its
## SubViewport's hovered Control, which the root's mouse-over updates in the same motion
## (scene/main/viewport.cpp L3398-3421). A SubViewport whose input is disabled takes no event, so
## the walk ends there with nothing hovered. Returns the Control or null, or a String when the
## pointer lands elsewhere on the way.
func inner_hit(aim: Dictionary, root_hit: Control) -> Variant:
	var hit: Control = root_hit
	for level: Dictionary in aim["levels"]:
		var container: Control = level["container"]
		var viewport: Viewport = level["viewport"]
		if hit != container:
			var hit_path: String = "<nothing>" if hit == null else str(hit.get_path())
			return (
				CONTAINER_MISSED
				% [
					str((aim["node"] as Node).get_path()),
					point_text(aim["point"]),
					hit_path,
					str(container.get_path()),
					str(viewport.get_path()),
				]
			)
		if viewport.gui_disable_input:
			return null
		hit = viewport.gui_get_hovered_control()
	return hit


## Why the GUI would take a press at a world node's point before physics picking, or "" when it
## would not: the first Control from hit up that stops the event (stopping_control); scroll says
## the event is a wheel or pan.
func world_hit_refusal(aim: Dictionary, hit: Control, scroll: bool) -> String:
	var stop: Control = stopping_control(hit, scroll)
	if stop == null:
		return ""
	return (
		GUI_TAKES_CLICK
		% [
			str((aim["node"] as Node).get_path()),
			point_text(aim["point"]),
			str(stop.get_path()),
			stop.get_class(),
			rect_text(viewport_rect(stop)),
		]
	)


## The Control that marks a pointer event handled when the GUI hands it to hit, or null when none
## does and the event goes on to physics picking. Walked as the viewport's _gui_call_input walks
## it (scene/main/viewport.cpp L1740-1783 in 4.7.2): from hit up through the parent canvas items,
## a Control whose effective filter is Ignore skipped, the first whose filter is Stop taking the
## event, and a top-level item ending the walk before its own filter counts. A scroll passes a
## Stop Control whose force_pass_scroll_events is on (L1764), which every Control has by default
## (scene/gui/control.h L286).
static func stopping_control(hit: Control, scroll: bool) -> Control:
	var item: CanvasItem = hit
	while item != null:
		if item.is_set_as_top_level():
			return null
		var control := item as Control
		if (
			control != null
			and control.get_mouse_filter_with_override() == Control.MOUSE_FILTER_STOP
			and not (scroll and control.is_force_pass_scroll_events())
		):
			return control
		item = item.get_parent() as CanvasItem
	return null


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
			element,
			"get_ui_elements lists the Controls' paths and names, get_scene_tree every node's"
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


## Whether a press on hit reaches a Control aim's node: hit is the node, a descendant of it, or,
## for a node that ignores the mouse, the nearest ancestor that takes its clicks; or, for a text
## match of a node that acts on no press of its own and has an owner, a Control of its own scene
## instance (same_instance_hit). An item aim lands only on the Control that draws the item: a
## list's scroll bars are its children, and a press on one selects nothing.
func lands_on(hit: Control, aim: Dictionary) -> bool:
	if hit == null:
		return false
	if aim.has("item"):
		return hit == aim["drawer"]
	var target: Control = aim["node"]
	return (
		hit == target
		or target.is_ancestor_of(hit)
		or hit == receiver(target)
		or (aim.has("matched") and same_instance_hit(hit, target))
	)


## Whether hit, the Control a press lands on, belongs to target's scene instance, for a target
## that acts on no press of its own (_reads_only) and has an owner: hit is that owner or has it
## too, in the target's own window (an embedded popup's Control, its owner the scene root, is
## another window's), and is not itself the root of another scene instanced inside it (a modal's
## backdrop in the HUD, whose owner is the HUD too, keeps the click from the HUD's label). A card's
## title Label beside the Button that takes the card's clicks is pressed through that Button; a Play
## Button under its own scene's ConfirmQuit Panel is not, since the Panel presses nothing the match
## names.
static func same_instance_hit(hit: Control, target: Control) -> bool:
	var instance: Node = target.owner
	if instance == null or not _reads_only(target) or hit.get_window() != target.get_window():
		return false
	return hit == instance or (hit.owner == instance and hit.scene_file_path.is_empty())


## Whether a Control acts on no press of its own: it ignores the mouse, or it is a text Control (a
## Label or a RichTextLabel, which shows the text an agent aims at and presses nothing whatever its
## mouse filter, a RichTextLabel keeping the Control default Stop). A Button, LineEdit or CheckBox
## acts on a press, so a same-instance Control covering one must not take the click.
static func _reads_only(control: Control) -> bool:
	return (
		control.get_mouse_filter_with_override() == Control.MOUSE_FILTER_IGNORE
		or control is Label
		or control is RichTextLabel
	)


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


## A canvas item's transform into the root's viewport coordinates, the ones input points use: its
## canvas transform, a CanvasLayer's or the viewport's canvas transform included
## (scene/main/canvas_item.cpp L183-192 in 4.7.2), then each step out of its viewport to the root
## (_carry_out): an embedded window's position and final transform, a SubViewportContainer's.
func viewport_transform(item: CanvasItem) -> Transform2D:
	var carried: Dictionary = _carry_out(item.get_viewport())
	return (carried["xform"] as Transform2D) * item.get_global_transform_with_canvas()


## A point as "x, y", each coordinate as num gives it.
static func point_text(point: Vector2) -> String:
	return "%s, %s" % [num(point.x), num(point.y)]


## A size as "WxH", each as num gives it.
static func size_text(size: Vector2) -> String:
	return "%sx%s" % [num(size.x), num(size.y)]


## A rect as "x,y,w,h", each as num gives it.
static func rect_text(rect: Rect2) -> String:
	return (
		"%s,%s,%s,%s"
		% [num(rect.position.x), num(rect.position.y), num(rect.size.x), num(rect.size.y)]
	)


## A coordinate with at most one decimal, and none when it is whole.
static func num(value: float) -> String:
	var rounded: float = snappedf(value, 0.1)
	if rounded == roundf(rounded):
		return str(int(rounded))
	return str(rounded)
