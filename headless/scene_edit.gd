extends RefCounted
## What the headless scene edits share: opening a scene as the editor does, finding a node by its
## path relative to the scene root, refusing an edit a save would lose, and saving it through
## scene_files.gd, which puts back the uids a --script save leaves out.
##
## A scene opens with GEN_EDIT_STATE_MAIN, so instanced and inherited scenes stay references when
## it is packed again (GEN_EDIT_STATE_DISABLED flattens an inherited scene and doubles its
## inherited connections: 4.7.2 packed_scene.cpp L233-236, L682). Only text scenes are written:
## outside the editor a binary scene's uid cannot be read back (see SceneFiles.uid_of).

const SceneFiles := preload("scene_files.gd")
const RES_PREFIX := "res://"
## How many base scenes deep an inherited scene is followed.
const MAX_BASE_DEPTH := 64

## The engine log operations.gd keeps (its ErrorLog, with count() and since(start)), so an edit
## can quote what a load it refuses logged; null when nothing set it.
static var engine_log: Object = null


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


## {node} for the node at path relative to root, or {error} saying the scene at scene_path has no
## such node.
static func node_or_error(root: Node, path: String, scene_path: String) -> Dictionary:
	var node: Node = find(root, path)
	if node == null:
		return {"error": "%s has no node %s." % [scene_path, path]}
	return {"node": node}


## Why a change to node (found at path) would not be saved, or "". A node inherited from the base
## scene saves its overrides; a node inside an instance saves them only when the instance is an
## editable instance (its children shown in the editor); otherwise as instance_refusal says.
static func unsaved_edit_refusal(root: Node, node: Node, path: String) -> String:
	var instance: Node = node.owner
	if node != root and instance != null and instance != root:
		if root.is_editable_instance(instance):
			return ""
	return instance_refusal(root, node, path)


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


## Why a save of the scene at scene_path is refused when it uses C# scripts, or "": prep holds the
## run's build state (build), the configuration built (buildConfiguration) and a failed build's
## quoted compiler errors (buildErrors), which the refusal quotes.
static func csharp_refusal(scene_path: String, uses_csharp: bool, prep: Dictionary) -> String:
	if str(prep.get("build", "")) != "failed" or not uses_csharp:
		return ""
	var configuration: String = str(prep.get("buildConfiguration", ""))
	var built: String = "C# build" if configuration.is_empty() else "%s C# build" % configuration
	var errors: String = str(prep.get("buildErrors", ""))
	var refusal: String = (
		"%s uses C# scripts and the project's %s failed; fix it first" % [scene_path, built]
	)
	if errors.is_empty():
		return refusal + " (validate lists the errors)."
	return "%s:\n%s" % [refusal, errors]


## Whether the scene file at path uses a C# script, directly or through a scene it instances or
## inherits (a base scene is among its dependencies), however deep.
static func file_uses_csharp(path: String) -> bool:
	return _scene_uses_csharp(path, {})


## Whether root or a node under it, instanced ones included, has a C# script, or is the root of an
## instanced scene whose file uses one: a C# script that cannot load while the build is red leaves
## its node with no script, so the instance's file is read instead.
static func tree_uses_csharp(root: Node) -> bool:
	var nodes: Array[Node] = root.find_children("*", "", true, false)
	nodes.append(root)
	var visited: Dictionary = {}
	for node in nodes:
		if _node_uses_csharp(node, visited):
			return true
	return false


## The res:// paths of the C# scripts among ResourceLoader.get_dependencies entries, which read
## "<path>" or, for a dependency saved with its UID, "<uid>::::<fallback path>", the second
## section always empty (4.7.2 doc/classes/ResourceLoader.xml).
static func csharp_dependencies(dependencies: PackedStringArray) -> PackedStringArray:
	return _dependency_paths(dependencies, [".cs"])


static func _node_uses_csharp(node: Node, visited: Dictionary) -> bool:
	var script := node.get_script() as Script
	if script != null and script.resource_path.ends_with(".cs"):
		return true
	return not node.scene_file_path.is_empty() and _scene_uses_csharp(node.scene_file_path, visited)


## Whether the scene file at path, or a scene among its dependencies followed recursively, has a C#
## script among its dependencies. visited holds the scenes already read, so a cycle ends.
static func _scene_uses_csharp(path: String, visited: Dictionary) -> bool:
	if visited.has(path) or not FileAccess.file_exists(path):
		return false
	visited[path] = true
	var dependencies: PackedStringArray = ResourceLoader.get_dependencies(path)
	if not csharp_dependencies(dependencies).is_empty():
		return true
	for scene in _dependency_paths(dependencies, [".tscn", ".scn"]):
		if _scene_uses_csharp(scene, visited):
			return true
	return false


## The res:// paths among ResourceLoader.get_dependencies entries that end in one of extensions.
static func _dependency_paths(
	dependencies: PackedStringArray, extensions: Array
) -> PackedStringArray:
	var found: PackedStringArray = []
	for dependency in dependencies:
		for part in dependency.split("::"):
			if (
				part.begins_with(RES_PREFIX)
				and _ends_with_any(part, extensions)
				and not found.has(part)
			):
				found.append(part)
	return found


static func _ends_with_any(text: String, suffixes: Array) -> bool:
	for suffix: String in suffixes:
		if text.ends_with(suffix):
			return true
	return false


## Packs root and saves it to the text scene at path with the uid uid, as SceneFiles.save_resource
## does: known holds the source's own ext_resource uids, and keep_layout keeps the text of every
## section the edit left alone. {uid, warning?} with uid as uid:// text, or {error}.
static func save(
	root: Node, path: String, uid: int, known: Dictionary, keep_layout: bool
) -> Dictionary:
	var packed := PackedScene.new()
	var error: int = packed.pack(root)
	if error != OK:
		return {"error": "%s could not be packed: %s" % [path, error_string(error)]}
	return SceneFiles.save_resource(packed, path, uid, known, keep_layout)


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
			return script_root(entry["path"])
	return null


## A new node of the script at path, a node of the class it extends; null when it does not load,
## cannot be instantiated or extends a class that is not a Node.
static func script_root(path: String) -> Node:
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
