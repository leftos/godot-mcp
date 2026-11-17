extends RefCounted
## Node paths relative to a scene's root, as the headless ops read and join them: a SceneState
## lists its nodes as "." and "./A/B", a tool takes "." and "A/B", and an instanced scene's paths
## are relative to the node that instances it; and the refusal for a path the scene does not hold.

## A node or scene file tree that has no child at a path segment: scene, path, the node it was
## looked up from, the segment, that node's children, then a tail.
const NO_CHILD := "%s has no node %s: %s has no child '%s' (children: %s)%s"
## A unique-name segment the node it is looked up from (or that node's scene owner) does not
## hold: scene, path, who, segment, as the live bridge words it (godot_mcp_inspect.gd
## UNIQUE_SEGMENT_MISSING).
const UNIQUE_SEGMENT_MISSING := "%s has no node %s: %s has no node with the unique name '%s'."


## A node path relative to a scene's root, without "." or empty segments; the root is ".".
static func normalise_node_path(path: String) -> String:
	var names: PackedStringArray = []
	for part in path.split("/"):
		if not part.is_empty() and part != ".":
			names.append(part)
	return "." if names.is_empty() else "/".join(names)


## The path of relative (a scene's own node path) inside an instance placed at prefix.
static func join_node_path(prefix: String, relative: String) -> String:
	var inner: String = normalise_node_path(relative)
	if inner == ".":
		return prefix
	return inner if prefix == "." else prefix + "/" + inner


## Why the scene at scene_path has no node at path under root: the deepest node of the path that
## exists, the segment it lacks and that node's children, so the right path is one call away.
static func node_miss(root: Node, path: String, scene_path: String) -> String:
	var stop: Array = _deepest_existing(root, path)
	var parent: Node = stop[0]
	var where: String = "its root" if parent == root else str(root.get_path_to(parent))
	var segment: String = stop[1]
	if segment.begins_with("%"):
		var scope: String = "the scene" if parent == root else "%s's scene" % where
		return UNIQUE_SEGMENT_MISSING % [scene_path, path, scope, segment]
	return NO_CHILD % [scene_path, path, where, segment, _child_names(parent), "."]


## Why a scene file tree (operations.gd's {nodes, children}) has no node at path: the deepest node
## on it the tree lists, the segment it lacks and that node's children, then tail.
static func tree_miss(tree: Dictionary, path: String, scene_path: String, tail: String) -> String:
	var parent: String = "."
	var missing: String = path
	for segment: String in path.split("/", false):
		var next: String = join_node_path(parent, segment)
		if not tree["nodes"].has(next):
			missing = segment
			break
		parent = next
	var names: PackedStringArray = []
	var children: Array = tree["children"].get(parent, [])
	for child: String in children:
		names.append(tree["nodes"][child]["name"])
	var where: String = "its root" if parent == "." else parent
	return NO_CHILD % [scene_path, path, where, missing, _names_text(names), tail]


## [the deepest node of path under root that exists, the segment it lacks]: each segment but the
## last looked up under the one before it, as the live bridge's _deepest_ancestor walks a path, a
## "." segment read as the node itself; the last segment when each before it exists.
static func _deepest_existing(root: Node, path: String) -> Array:
	var segments: PackedStringArray = path.split("/", false)
	var parent: Node = root
	var missing: String = "" if segments.is_empty() else segments[segments.size() - 1]
	for index in range(segments.size() - 1):
		if segments[index] == ".":
			continue
		var next: Node = parent.get_node_or_null(NodePath(segments[index]))
		if next == null:
			missing = segments[index]
			break
		parent = next
	return [parent, missing]


## The names of node's children, as _names_text lists them.
static func _child_names(node: Node) -> String:
	var names: PackedStringArray = []
	for child: Node in node.get_children():
		names.append(str(child.name))
	return _names_text(names)


## The names of up to 10 of names joined by ", ", the count of the rest after them as "(+N)", or
## "none" when there are none.
static func _names_text(names: PackedStringArray) -> String:
	if names.is_empty():
		return "none"
	var shown: String = ", ".join(names.slice(0, 10))
	return shown if names.size() <= 10 else "%s (+%d)" % [shown, names.size() - 10]
