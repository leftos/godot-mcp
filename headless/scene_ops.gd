extends RefCounted
## The headless scene edits operations.gd dispatches: create_scene, save_scene, and the edits that
## open a scene, apply one change and save it (delete_nodes).
##
## Each edit is apply_<op>(root, params) -> {result} or {error}, on a scene already open, and
## changes nothing when it refuses; opening and saving are separate, so several edits can be
## applied to one open scene and saved once. A new edit is registered in apply alone. A scene that
## uses C# scripts is not saved while the prep's C# build failed (params.build, which the server
## adds to every request).

const SceneEdit := preload("scene_edit.gd")
const SceneProps := preload("scene_props.gd")


## The reply to op, {ok, result} or {ok: false, error}; every op but create_scene and save_scene
## is an edit, and apply refuses one it does not know.
static func run(op: String, params: Dictionary) -> Dictionary:
	match op:
		"create_scene":
			return create_scene(params)
		"save_scene":
			return save_scene(params)
		"get_node_properties":
			return read_scene(op, params)
	return edit_scene(op, params)


## Applies the edit op to the open scene rooted at root: {result} or {error}.
static func apply(op: String, root: Node, params: Dictionary) -> Dictionary:
	match op:
		"delete_nodes":
			return apply_delete_nodes(root, params)
		"add_node":
			return SceneProps.apply_add_node(root, params)
		"set_node_properties":
			return SceneProps.apply_set_node_properties(root, params)
		"get_node_properties":
			return SceneProps.apply_get_node_properties(root, params)
	return {"error": "unknown operation '%s'" % op}


## Writes a new scene at params.scene holding one root of params.rootType, named params.rootName
## or after the file; a replaced file's uid is kept, a new file gets a new one.
static func create_scene(params: Dictionary) -> Dictionary:
	var scene_path: String = params.get("scene", "")
	var type: String = params.get("rootType", "")
	var root: Node = SceneEdit.new_root(type)
	if root == null:
		return _fail("rootType '%s' is not a Node class or a script class_name." % type)
	var given: Variant = params.get("rootName", "")
	var named: bool = given is String and not (given as String).is_empty()
	root.name = given if named else SceneEdit.root_name_for(scene_path)
	var facts: Dictionary = {"name": String(root.name), "type": type}
	var saved: Dictionary = _save_checked(root, "", scene_path, params)
	root.free()
	if saved.has("error"):
		return _fail(saved["error"])
	return {"ok": true, "result": {"scenePath": scene_path, "root": facts, "uid": saved["uid"]}}


## Opens params.scene and saves it to params.target (the scene itself for a save in place).
static func save_scene(params: Dictionary) -> Dictionary:
	var scene_path: String = params.get("scene", "")
	var target: String = params.get("target", scene_path)
	var opened: Dictionary = SceneEdit.open(scene_path)
	if opened.has("error"):
		return _fail(opened["error"])
	var root: Node = opened["root"]
	var saved: Dictionary = _save_checked(root, scene_path, target, params)
	root.free()
	if saved.has("error"):
		return _fail(saved["error"])
	var result: Dictionary = {"scenePath": scene_path, "savedTo": target, "uid": saved["uid"]}
	return {"ok": true, "result": result}


## Opens params.scene, applies the edit op and saves the scene in place, keeping its uid; nothing
## is saved when the edit refuses.
static func edit_scene(op: String, params: Dictionary) -> Dictionary:
	var scene_path: String = params.get("scene", "")
	var opened: Dictionary = SceneEdit.open(scene_path)
	if opened.has("error"):
		return _fail(opened["error"])
	var root: Node = opened["root"]
	var applied: Dictionary = apply(op, root, params)
	if not applied.has("error"):
		var saved: Dictionary = _save_checked(root, scene_path, scene_path, params)
		if saved.has("error"):
			applied = saved
	root.free()
	if applied.has("error"):
		return _fail(applied["error"])
	return {"ok": true, "result": applied["result"]}


## Opens params.scene, applies the read op and frees the scene without saving it.
static func read_scene(op: String, params: Dictionary) -> Dictionary:
	var opened: Dictionary = SceneEdit.open(params.get("scene", ""))
	if opened.has("error"):
		return _fail(opened["error"])
	var root: Node = opened["root"]
	var applied: Dictionary = apply(op, root, params)
	root.free()
	if applied.has("error"):
		return _fail(applied["error"])
	return {"ok": true, "result": applied["result"]}


## Deletes the nodes at params.nodePaths, each with its children: {result: {deleted}}, or {error}
## naming every path that cannot be deleted, with nothing deleted.
static func apply_delete_nodes(root: Node, params: Dictionary) -> Dictionary:
	var paths: Array = params.get("nodePaths", [])
	var refusals: PackedStringArray = []
	for path: String in paths:
		var refusal: String = _delete_refusal(root, path, params.get("scene", ""))
		if not refusal.is_empty():
			refusals.append(refusal)
	if not refusals.is_empty():
		return {"error": " ".join(refusals)}
	for path: String in paths:
		# Null when a path listed earlier deleted an ancestor of it.
		var node: Node = SceneEdit.find(root, path)
		if node != null:
			node.get_parent().remove_child(node)
			node.free()
	return {"result": {"deleted": paths}}


static func _delete_refusal(root: Node, path: String, scene_path: String) -> String:
	var node: Node = SceneEdit.find(root, path)
	if node == null:
		return "%s has no node %s." % [scene_path, path]
	if node == root:
		return "The scene root cannot be deleted; create a new scene instead."
	var refusal: String = SceneEdit.instance_refusal(root, node, path)
	if refusal.is_empty():
		refusal = SceneEdit.inherited_delete_refusal(scene_path, root, node, path)
	return refusal


## Saves root to target with the uid target has (a new one for a new file), unless the scene uses
## C# scripts while the build failed: {uid} or {error}. source is the file root was opened from,
## "" for a new scene; its own ext_resource uids are the ones written back first.
static func _save_checked(
	root: Node, source: String, target: String, params: Dictionary
) -> Dictionary:
	var uses_csharp: bool = SceneEdit.tree_uses_csharp(root)
	var known: Dictionary = {}
	if not source.is_empty():
		uses_csharp = uses_csharp or SceneEdit.file_uses_csharp(source)
		known = SceneEdit.ext_uids_in(FileAccess.get_file_as_string(source))
	var named: String = target if source.is_empty() else source
	var refusal: String = SceneEdit.csharp_refusal(named, uses_csharp, str(params.get("build", "")))
	if not refusal.is_empty():
		return {"error": refusal}
	return SceneEdit.save(root, target, SceneEdit.uid_for(target), known)


static func _fail(message: String) -> Dictionary:
	return {"ok": false, "error": message}
