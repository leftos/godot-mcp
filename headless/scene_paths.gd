extends RefCounted
## Node paths relative to a scene's root, as the headless ops read and join them: a SceneState
## lists its nodes as "." and "./A/B", a tool takes "." and "A/B", and an instanced scene's paths
## are relative to the node that instances it; and the refusal for a path the scene does not hold.


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
	return (
		"%s has no node %s: %s has no child '%s' (children: %s)."
		% [scene_path, path, where, stop[1], _child_names(parent)]
	)


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


## The names of up to 10 of node's children joined by ", ", the count of the rest after them as
## "(+N)", or "none" for a node without children.
static func _child_names(node: Node) -> String:
	var names: PackedStringArray = []
	for child: Node in node.get_children():
		names.append(str(child.name))
	if names.is_empty():
		return "none"
	var shown: String = ", ".join(names.slice(0, 10))
	return shown if names.size() <= 10 else "%s (+%d)" % [shown, names.size() - 10]
