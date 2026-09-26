extends RefCounted
## The headless scene edits operations.gd dispatches: create_scene, save_scene, and the edits that
## open a scene, apply one change and save it: delete_nodes and the edits of one node
## (attach_script, duplicate_node, load_sprite) in scene_nodes.gd, add_node and the property ops
## (set_node_properties, and get_node_properties, a read) in scene_props.gd, the signal ops
## (get_node_signals, a read, connect_signal, disconnect_signal) in scene_signals.gd, and
## export_mesh_library in scene_mesh.gd, which never saves the scene and writes a file of its own.
##
## Each edit is apply_<op>(root, params, context) -> {result} or {error}, on a scene already open,
## and changes nothing when it refuses; opening and saving are separate, so several edits can be
## applied to one open scene and saved once. context is {scene, build}: the res:// path of the
## scene the edit is applied to, and the prep's C# build state (params.build, which the server adds
## to every request); an edit reads them there, never in its own params. A new op is registered in
## EDIT_MODULES alone, and in READ_OPS too when it never saves. A scene that uses C# scripts is
## not saved while the build failed.

const SceneEdit := preload("scene_edit.gd")
const SceneFiles := preload("scene_files.gd")
const SceneNodes := preload("scene_nodes.gd")
const SceneProps := preload("scene_props.gd")
const SceneSignals := preload("scene_signals.gd")
const SceneMesh := preload("scene_mesh.gd")
## The module whose apply_<op>(root, params, context) applies each op on an open scene.
const EDIT_MODULES := {
	"delete_nodes": SceneNodes,
	"attach_script": SceneNodes,
	"duplicate_node": SceneNodes,
	"load_sprite": SceneNodes,
	"add_node": SceneProps,
	"set_node_properties": SceneProps,
	"get_node_properties": SceneProps,
	"get_node_signals": SceneSignals,
	"connect_signal": SceneSignals,
	"disconnect_signal": SceneSignals,
	"export_mesh_library": SceneMesh,
}
## The ops in EDIT_MODULES that never save the open scene: the reads, and export_mesh_library,
## which writes a file of its own.
const READ_OPS: Array[String] = ["get_node_properties", "get_node_signals", "export_mesh_library"]


## The reply to op, {ok, result} or {ok: false, error}; every op but create_scene and save_scene
## is applied to an open scene, saved unless it is in READ_OPS, and apply refuses one it does not
## know.
static func run(op: String, params: Dictionary) -> Dictionary:
	match op:
		"create_scene":
			return create_scene(params)
		"save_scene":
			return save_scene(params)
	if op in READ_OPS:
		return read_scene(op, params)
	return edit_scene(op, params)


## Applies the op to the open scene rooted at root, in context ({scene, build}): {result} or
## {error}.
static func apply(op: String, root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var module: Variant = EDIT_MODULES.get(op)
	if module == null:
		return {"error": "unknown operation '%s'" % op}
	return (module as GDScript).call("apply_" + op, root, params, context)


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
	var applied: Dictionary = apply(op, root, params, _context_of(params))
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
	var applied: Dictionary = apply(op, root, params, _context_of(params))
	root.free()
	if applied.has("error"):
		return _fail(applied["error"])
	return {"ok": true, "result": applied["result"]}


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
		known = SceneFiles.ext_uids_in(FileAccess.get_file_as_string(source))
	var named: String = target if source.is_empty() else source
	var refusal: String = SceneEdit.csharp_refusal(named, uses_csharp, str(params.get("build", "")))
	if not refusal.is_empty():
		return {"error": refusal}
	return SceneEdit.save(root, target, SceneFiles.uid_for(target), known)


## The context an edit of the scene params.scene is applied in: {scene, build}.
static func _context_of(params: Dictionary) -> Dictionary:
	return {"scene": str(params.get("scene", "")), "build": str(params.get("build", ""))}


static func _fail(message: String) -> Dictionary:
	return {"ok": false, "error": message}
