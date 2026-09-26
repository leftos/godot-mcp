extends RefCounted
## What the headless scene edits share: opening a scene as the editor does, finding a node by its
## path relative to the scene root, refusing an edit a save would lose, and saving with the uids
## a --script save leaves out put back.
##
## A scene opens with GEN_EDIT_STATE_MAIN, so instanced and inherited scenes stay references when
## it is packed again (GEN_EDIT_STATE_DISABLED flattens an inherited scene and doubles its
## inherited connections: 4.7.2 packed_scene.cpp L233-236, L682). A --script save writes neither
## the header uid nor any ext_resource's uid= (resource_saver.cpp L285-294), so save sets the one
## and writes the others back. Only text scenes are written: outside the editor a binary scene's
## uid cannot be read back (see uid_of).

const RES_PREFIX := "res://"
const EXT_TAG := "[ext_resource "
const PATH_ATTRIBUTE := ' path="'
const UID_ATTRIBUTE := ' uid="'
## How many base scenes deep an inherited scene is followed.
const MAX_BASE_DEPTH := 64


## {root} for the scene at path instantiated for editing, or {error}.
static func open(path: String) -> Dictionary:
	var scene := ResourceLoader.load(path) as PackedScene
	if scene == null:
		return {"error": "%s did not load as a scene" % path}
	var root: Node = scene.instantiate(PackedScene.GEN_EDIT_STATE_MAIN)
	if root == null:
		return {"error": "%s loaded but could not be instantiated" % path}
	return {"root": root}


## The node at path, relative to root ("." is root itself), or null.
static func find(root: Node, path: String) -> Node:
	return root.get_node_or_null(NodePath(path))


## Why an edit of node (found at path) would be lost, or "": a node whose owner is not the scene's
## root belongs to an instanced scene, whose file holds it. An instance's own root may be edited.
static func instance_refusal(root: Node, node: Node, path: String) -> String:
	if node == root or node.owner == root:
		return ""
	var instance: Node = node.owner
	if instance == null:
		return "%s is not saved with the scene: it has no owner." % path
	var file: String = instance.scene_file_path
	return (
		"%s is inside the instance of %s at %s; its changes would not be saved. Edit %s instead."
		% [path, file, String(root.get_path_to(instance)), file]
	)


## Why deleting node (found at path) from the scene at scene_path would be undone on reload, or
## "": a node the scene inherits from its base scene is recreated from that base. The editor
## refuses it too (4.7.2 editor/docks/scene_tree_dock.cpp ~L2307-2316). An inherited node's
## values may still be overridden, so this is the delete's refusal only.
static func inherited_delete_refusal(
	scene_path: String, root: Node, node: Node, path: String
) -> String:
	var base: String = inherited_from(scene_path, String(root.get_path_to(node)))
	if base.is_empty():
		return ""
	return (
		"%s is inherited from %s; its deletion would not be saved. Edit %s instead."
		% [path, base, base]
	)


## The deepest base scene of the scene at scene_path whose state lists node_path (a path
## relative to the root, without "./"), following a base that itself inherits; "" when none does.
static func inherited_from(scene_path: String, node_path: String) -> String:
	var found: String = ""
	var base: PackedScene = _base_of(ResourceLoader.load(scene_path) as PackedScene)
	var depth: int = 0
	while base != null and depth < MAX_BASE_DEPTH:
		if _state_lists(base.get_state(), node_path):
			found = base.resource_path
		base = _base_of(base)
		depth += 1
	return found


## The uid to save path with: the one the file at path has, else a new one.
static func uid_for(path: String) -> int:
	var id: int = uid_of(path) if FileAccess.file_exists(path) else ResourceUID.INVALID_ID
	return id if id != ResourceUID.INVALID_ID else ResourceUID.create_id()


## The uid of the file at path, or INVALID_ID. Outside the editor ResourceLoader.get_resource_uid
## only looks the path up in ResourceUID's cache (4.7.2 core/io/resource_loader.cpp L1412-1416),
## which is empty in a project with no .godot/uid_cache.bin, for text and binary files alike
## (measured); so the uid is read from the file too: a text scene's or resource's header, a
## script's <path>.uid, an imported file's <path>.import.
static func uid_of(path: String) -> int:
	var id: int = ResourceLoader.get_resource_uid(path)
	if id != ResourceUID.INVALID_ID:
		return id
	var text: String = _uid_text_of(path)
	return ResourceUID.INVALID_ID if text.is_empty() else ResourceUID.text_to_id(text)


## The value of key="..." in line, where key starts the line or follows a space; "" when absent.
static func quoted_value(line: String, key: String) -> String:
	var marker: String = key + '="'
	var start: int = 0 if line.begins_with(marker) else line.find(" " + marker)
	if start < 0:
		return ""
	start = line.find(marker, start) + marker.length()
	var end: int = line.find('"', start)
	return line.substr(start, end - start) if end > start else ""


## Why a save of the scene at scene_path is refused when it uses C# scripts, or "".
static func csharp_refusal(scene_path: String, uses_csharp: bool, build: String) -> String:
	if build != "failed" or not uses_csharp:
		return ""
	return (
		"%s uses C# scripts and the project's C# build failed; fix it first (validate lists the errors)."
		% scene_path
	)


## Whether the scene file at path uses a C# script directly.
static func file_uses_csharp(path: String) -> bool:
	if not FileAccess.file_exists(path):
		return false
	return not csharp_dependencies(ResourceLoader.get_dependencies(path)).is_empty()


## Whether root or a node under it, instanced ones included, has a C# script.
static func tree_uses_csharp(root: Node) -> bool:
	var nodes: Array[Node] = root.find_children("*", "", true, false)
	nodes.append(root)
	for node in nodes:
		var script := node.get_script() as Script
		if script != null and script.resource_path.ends_with(".cs"):
			return true
	return false


## The res:// paths of the C# scripts among ResourceLoader.get_dependencies entries, which read
## "<path>[::<type>]" or, for a dependency saved with its UID, "<uid>::<type>::<path>".
static func csharp_dependencies(dependencies: PackedStringArray) -> PackedStringArray:
	var found: PackedStringArray = []
	for dependency in dependencies:
		for part in dependency.split("::"):
			if part.begins_with(RES_PREFIX) and part.ends_with(".cs") and not found.has(part):
				found.append(part)
	return found


## Writes root to the text scene at path with the uid uid, creating missing folders, and gives
## each ext_resource its uid back: the one in known (res:// path to uid:// text, the source's own
## tags) first, else the one its file records. {uid} as uid:// text, or {error}.
static func save(root: Node, path: String, uid: int, known: Dictionary) -> Dictionary:
	var error: int = DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	if error != OK and error != ERR_ALREADY_EXISTS:
		return {"error": "cannot create the folder of %s: %s" % [path, error_string(error)]}
	var packed := PackedScene.new()
	error = packed.pack(root)
	if error != OK:
		return {"error": "%s could not be packed: %s" % [path, error_string(error)]}
	error = ResourceSaver.save(packed, path)
	if error != OK:
		return {"error": "%s could not be saved: %s" % [path, error_string(error)]}
	error = ResourceSaver.set_uid(path, uid)
	if error == OK:
		error = _restore_ext_uids(path, known)
	if error != OK:
		var reason: String = error_string(error)
		return {
			"error":
			(
				"%s was saved, but its uids could not be written back (%s): it has none now; save it again."
				% [path, reason]
			)
		}
	return {"uid": ResourceUID.id_to_text(uid)}


## The uid of each ext_resource tag in a scene's text that carries one, by its path.
static func ext_uids_in(text: String) -> Dictionary:
	var uids: Dictionary = {}
	for line in text.split("\n"):
		var path: String = ext_resource_path(line)
		var uid: String = quoted_value(line, "uid")
		if not path.is_empty() and not uid.is_empty():
			uids[path] = uid
	return uids


## text with uid="<uids[path]>" added to each ext_resource tag that has no uid and whose path is
## in uids (res:// path to uid:// text); every other line as it was.
static func with_ext_uids(text: String, uids: Dictionary) -> String:
	var lines: PackedStringArray = text.split("\n")
	for index in lines.size():
		var line: String = lines[index]
		var path: String = ext_resource_path(line)
		if path.is_empty() or line.contains(UID_ATTRIBUTE) or not uids.has(path):
			continue
		lines[index] = line.replace(
			PATH_ATTRIBUTE, '%s%s"%s' % [UID_ATTRIBUTE, uids[path], PATH_ATTRIBUTE]
		)
	return "\n".join(lines)


## The path="..." of an ext_resource tag line, or "" for any other line.
static func ext_resource_path(line: String) -> String:
	if not line.begins_with(EXT_TAG):
		return ""
	var start: int = line.find(PATH_ATTRIBUTE)
	if start < 0:
		return ""
	start += PATH_ATTRIBUTE.length()
	var end: int = line.find('"', start)
	return line.substr(start, end - start) if end > start else ""


## The root name for a new scene at path: its file name in PascalCase (player_ship.tscn and
## player-ship.tscn give PlayerShip; Level1.tscn stays Level1), or Root when that is empty.
static func root_name_for(path: String) -> String:
	var words: PackedStringArray = []
	var base: String = path.get_file().get_basename().replace("-", "_").replace(" ", "_")
	for word in base.split("_", false):
		words.append(word.left(1).to_upper() + word.substr(1))
	return "Root" if words.is_empty() else "".join(words)


## A new node of type, a Node class or a script's class_name whose base is a Node class; null
## for any other name.
static func new_root(type: String) -> Node:
	if ClassDB.class_exists(type):
		if ClassDB.is_parent_class(type, "Node") and ClassDB.can_instantiate(type):
			return ClassDB.instantiate(type) as Node
		return null
	for entry: Dictionary in ProjectSettings.get_global_class_list():
		if entry["class"] == type:
			return _script_class_root(entry["path"])
	return null


static func _script_class_root(path: String) -> Node:
	var script := load(path) as Script
	if script == null or not script.can_instantiate():
		return null
	if not ClassDB.is_parent_class(script.get_instance_base_type(), "Node"):
		return null
	return script.new() as Node


## The scene an inherited scene's root instances, or null for a scene that inherits nothing.
static func _base_of(scene: PackedScene) -> PackedScene:
	if scene == null or scene.get_state().get_node_count() == 0:
		return null
	return scene.get_state().get_node_instance(0)


## Whether state has a node at node_path; a state's paths read "." or "./A/B".
static func _state_lists(state: SceneState, node_path: String) -> bool:
	for index in state.get_node_count():
		var listed: String = String(state.get_node_path(index))
		if listed == node_path or listed.trim_prefix("./") == node_path:
			return true
	return false


## The uid:// text the file at path records for itself, or "".
static func _uid_text_of(path: String) -> String:
	if path.ends_with(".tscn") or path.ends_with(".tres"):
		var file := FileAccess.open(path, FileAccess.READ)
		return "" if file == null else quoted_value(file.get_line(), "uid")
	for sidecar: String in [path + ".uid", path + ".import"]:
		var found: String = _sidecar_uid(sidecar)
		if not found.is_empty():
			return found
	return ""


## The uid:// text of a script's .uid file (the whole file) or an .import file (its uid="...").
static func _sidecar_uid(sidecar: String) -> String:
	if not FileAccess.file_exists(sidecar):
		return ""
	for line in FileAccess.get_file_as_string(sidecar).split("\n"):
		var trimmed: String = line.strip_edges()
		if trimmed.begins_with("uid://"):
			return trimmed
		if trimmed.begins_with('uid="'):
			return quoted_value(trimmed, "uid")
	return ""


## Writes each ext_resource's uid back into the text scene at path, from known first, else from
## the file the tag names; an error code.
static func _restore_ext_uids(path: String, known: Dictionary) -> int:
	var text: String = FileAccess.get_file_as_string(path)
	var uids: Dictionary = known.duplicate()
	for line in text.split("\n"):
		var ext_path: String = ext_resource_path(line)
		if ext_path.is_empty() or uids.has(ext_path):
			continue
		var id: int = uid_of(ext_path)
		if id != ResourceUID.INVALID_ID:
			uids[ext_path] = ResourceUID.id_to_text(id)
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return FileAccess.get_open_error()
	file.store_string(with_ext_uids(text, uids))
	file.close()
	return OK
