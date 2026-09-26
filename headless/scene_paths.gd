extends RefCounted
## Node paths relative to a scene's root, as the headless ops read and join them: a SceneState
## lists its nodes as "." and "./A/B", a tool takes "." and "A/B", and an instanced scene's paths
## are relative to the node that instances it.


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
