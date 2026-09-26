extends RefCounted
## The headless edits of one node of an open scene, which scene_ops.gd dispatches and saves:
## attach_script, duplicate_node and load_sprite. Each is apply_<op>(root, params) -> {result} or
## {error}, and changes nothing when it refuses.
##
## duplicate_node packs the node under a bare holder and instantiates the pack, so an instance in
## the copy stays an instance with its overrides (Node.duplicate bakes instances and doubles their
## connections: 4.7.2 scene/main/node.cpp L2789-2798). The holder is outside the scene, where a
## connection from the copied nodes to a node outside them has no common parent to be packed with
## (scene/resources/packed_scene.cpp L1200-1202): such a connection is taken off for the pack, put
## back, and made again from the copy, as the editor's duplicate keeps it.

const SceneEdit := preload("scene_edit.gd")
## The connection flags a copy keeps: those scripts can set, not the engine's own (an inherited
## connection's copy is the scene's own).
const SCRIPT_CONNECT_FLAGS := (
	CONNECT_DEFERRED | CONNECT_PERSIST | CONNECT_ONE_SHOT | CONNECT_REFERENCE_COUNTED
)
const ROOT_DUPLICATE_REFUSAL := (
	"The scene root cannot be duplicated; " + "save_scene with newPath copies the whole scene."
)


## Attaches the script at params.script to the node at params.nodePath: {result: {path, script,
## previous?}}, or {error} with nothing changed. The script must compile and extend the node's
## class or a parent class of it; a C# script is refused while the prep's C# build failed.
static func apply_attach_script(root: Node, params: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	var script_path: String = params.get("script", "")
	var build: String = str(params.get("build", ""))
	var refusal: String = SceneEdit.csharp_refusal(
		params.get("scene", ""), script_path.ends_with(".cs"), build
	)
	var loaded: Dictionary = (
		{"error": refusal} if not refusal.is_empty() else _load_script(script_path)
	)
	if loaded.has("error"):
		return loaded
	var script: Script = loaded["script"]
	var base: StringName = script.get_instance_base_type()
	if not ClassDB.is_parent_class(node.get_class(), base):
		var facts: Array = [script_path, base, found["path"], node.get_class()]
		return {"error": "%s extends %s, so it cannot be attached to %s, a %s." % facts}
	var result: Dictionary = {"path": found["path"], "script": _resource_facts(script)}
	var previous := node.get_script() as Script
	if previous != null:
		result["previous"] = _resource_facts(previous)
	node.set_script(script)
	return {"result": result}


## Copies the node at params.nodePath, with its children, right after it under its parent or last
## under params.parent, named params.newName or as the editor names a duplicate (copy_name):
## {result: {originalPath, newPath}}, or {error} with nothing changed.
static func apply_duplicate_node(root: Node, params: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params)
	if found.has("error"):
		return found
	var source: Node = found["node"]
	if source == root:
		return {"error": ROOT_DUPLICATE_REFUSAL}
	var parent: Dictionary = _copy_parent(root, source, params)
	if parent.has("error"):
		return parent
	var given: String = str(params.get("newName", ""))
	var named: Dictionary = _copy_name(root, parent["node"], source, given)
	if named.has("error"):
		return named
	var copied: Dictionary = _copy_of(source, root)
	if copied.has("error"):
		return copied
	var copy: Node = copied["node"]
	copy.name = named["name"]
	_place(copy, parent["node"], source, copied["owned"], root)
	_connect_outbound(copy, copied["outbound"], SCRIPT_CONNECT_FLAGS)
	return {"result": {"originalPath": found["path"], "newPath": String(root.get_path_to(copy))}}


## Sets the texture of the node at params.nodePath to the Texture2D at params.texture: {result:
## {path, texture}}, or {error} with nothing changed.
static func apply_load_sprite(root: Node, params: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	if not _has_texture_2d(node):
		var facts: Array = [found["path"], node.get_class()]
		return {"error": "%s is a %s, which has no Texture2D texture property." % facts}
	var loaded: Dictionary = _load_texture(params.get("texture", ""))
	if loaded.has("error"):
		return loaded
	node.set("texture", loaded["texture"])
	var result: Dictionary = {"path": found["path"], "texture": _resource_facts(loaded["texture"])}
	return {"result": result}


## The name the editor gives a copy of a node named name among siblings named taken: name itself
## when free, else its trailing number counted up (Sprite2 gives Sprite3, Box09 gives Box10) or,
## without one, 2 added (Sprite gives Sprite2), until the name is free (4.7.2
## scene/main/node.cpp L1605-1655, the editor's default of no separator).
static func copy_name(name: String, taken: PackedStringArray) -> String:
	if not taken.has(name):
		return name
	var digits: int = 0
	while digits < name.length() and name[name.length() - 1 - digits].is_valid_int():
		digits += 1
	var stem: String = name.left(name.length() - digits)
	var number: String = name.right(digits) if digits > 0 else "1"
	var attempt: String = name
	while taken.has(attempt):
		number = str(number.to_int() + 1).pad_zeros(number.length())
		attempt = stem + number
	return attempt


## Whether a property-list entry is a texture property that takes a Texture2D: an Object
## property hinted with a resource type that is Texture2D or a parent class of it.
static func takes_texture_2d(entry: Dictionary) -> bool:
	if entry.get("name") != "texture" or entry.get("type") != TYPE_OBJECT:
		return false
	if entry.get("hint") != PROPERTY_HINT_RESOURCE_TYPE:
		return false
	for type: String in str(entry.get("hint_string", "")).split(","):
		if ClassDB.is_parent_class("Texture2D", type.strip_edges()):
			return true
	return false


## {node, path} for the node at params.nodePath, path relative to root; or {error} for a missing
## node or one inside an instance.
static func _editable_node(root: Node, params: Dictionary) -> Dictionary:
	var given: String = params.get("nodePath", "")
	var node: Node = SceneEdit.find(root, given)
	if node == null:
		return {"error": "%s has no node %s." % [params.get("scene", ""), given]}
	var refusal: String = SceneEdit.instance_refusal(root, node, given)
	if not refusal.is_empty():
		return {"error": refusal}
	return {"node": node, "path": String(root.get_path_to(node))}


static func _has_texture_2d(node: Node) -> bool:
	for entry: Dictionary in node.get_property_list():
		if takes_texture_2d(entry):
			return true
	return false


## {script} for the script at path, which must compile; or {error} quoting what the load logged.
static func _load_script(path: String) -> Dictionary:
	var start: int = _log_count()
	var script := ResourceLoader.load(path) as Script
	if script == null:
		return {"error": "%s did not load as a script: %s" % [path, _logged_since(start)]}
	if not script.can_instantiate():
		return {"error": "%s cannot be instantiated: %s" % [path, _logged_since(start)]}
	return {"script": script}


## {texture} for the Texture2D at path, or {error} quoting what the load logged.
static func _load_texture(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {"error": "%s does not exist." % path}
	var start: int = _log_count()
	var resource: Resource = ResourceLoader.load(path)
	if resource == null:
		return {"error": "%s did not load: %s" % [path, _logged_since(start)]}
	if not resource is Texture2D:
		return {"error": "%s is a %s, not a Texture2D." % [path, resource.get_class()]}
	return {"texture": resource}


## {resource, uid?} for a resource saved in its own file.
static func _resource_facts(resource: Resource) -> Dictionary:
	var facts: Dictionary = {"resource": resource.resource_path}
	var uid: int = SceneEdit.uid_of(resource.resource_path)
	if uid != ResourceUID.INVALID_ID:
		facts["uid"] = ResourceUID.id_to_text(uid)
	return facts


static func _log_count() -> int:
	return 0 if SceneEdit.engine_log == null else SceneEdit.engine_log.call("count")


## The distinct errors (not warnings) the engine logged from entry start on, joined with "; ".
static func _logged_since(start: int) -> String:
	var messages: PackedStringArray = []
	var engine: Object = SceneEdit.engine_log
	var entries: Array = [] if engine == null else engine.call("since", start)
	for entry: Dictionary in entries:
		if entry["type"] == "error" and not messages.has(entry["message"]):
			messages.append(entry["message"])
	return "Godot logged no error" if messages.is_empty() else "; ".join(messages)


## {node}: the parent a copy of source goes under, params.parent or source's own; or {error} for a
## missing parent or one inside an instance (an instance's root may be the parent).
static func _copy_parent(root: Node, source: Node, params: Dictionary) -> Dictionary:
	var given: String = params.get("parent", "")
	if given.is_empty():
		return {"node": source.get_parent()}
	var parent: Node = SceneEdit.find(root, given)
	if parent == null:
		return {"error": "%s has no node %s." % [params.get("scene", ""), given]}
	var refusal: String = SceneEdit.instance_refusal(root, parent, given)
	return {"node": parent} if refusal.is_empty() else {"error": refusal}


## {name} for a copy of source under parent: given, or copy_name of source's name when given is
## empty; or {error} for a given name that is not a valid node name or that a child has.
static func _copy_name(root: Node, parent: Node, source: Node, given: String) -> Dictionary:
	var taken: PackedStringArray = []
	for child in parent.get_children():
		taken.append(String(child.name))
	if given.is_empty():
		return {"name": copy_name(String(source.name), taken)}
	if given.validate_node_name() != given:
		return {"error": "'%s' is not a valid node name: it cannot hold . : @ / \" or %%." % given}
	if taken.has(given):
		var label: String = (
			String(root.name) if parent == root else String(root.get_path_to(parent))
		)
		return {"error": "%s already has a child named %s." % [label, given]}
	return {"name": given}


## {node, owned, outbound} for a copy of source, the nodes of it root must own, and the
## connections from source's nodes to nodes outside it; or {error}. source moves for a moment
## under a bare holder that owns it and what root owns below it, is packed there and put back
## where it was, its outbound connections taken off for the pack and made again.
static func _copy_of(source: Node, root: Node) -> Dictionary:
	var parent: Node = source.get_parent()
	var index: int = source.get_index()
	var owned: Array[Node] = _owned_by(source, root)
	var outbound: Array = _outbound(source, owned)
	for entry: Dictionary in outbound:
		source.get_node(entry["path"]).disconnect(entry["signal"], entry["callable"])
	var holder := Node.new()
	_move(source, holder, owned, holder)
	var packed := PackedScene.new()
	var error: int = packed.pack(holder)
	_move(source, parent, owned, root)
	parent.move_child(source, index)
	holder.free()
	_connect_outbound(source, outbound, ~0)
	var path: String = String(root.get_path_to(source))
	if error != OK:
		return {"error": "%s could not be copied: %s" % [path, error_string(error)]}
	var copy_holder: Node = packed.instantiate(PackedScene.GEN_EDIT_STATE_MAIN)
	if copy_holder == null:
		return {"error": "%s was packed, but its copy could not be instantiated." % path}
	var copy: Node = copy_holder.get_child(0)
	var copy_owned: Array[Node] = _owned_by(copy, copy_holder)
	_move(copy, null, copy_owned, null)
	copy_holder.free()
	return {"node": copy, "owned": copy_owned, "outbound": outbound}


## node and the nodes below it whose owner is owner.
static func _owned_by(node: Node, owner: Node) -> Array[Node]:
	var owned: Array[Node] = [node]
	for below: Node in node.find_children("*", "", true, false):
		if below.owner == owner:
			owned.append(below)
	return owned


## The persistent connections from the nodes of owned to nodes outside source, each as {path
## (from source), signal, callable, flags}.
static func _outbound(source: Node, owned: Array[Node]) -> Array:
	var found: Array = []
	for node: Node in owned:
		for info: Dictionary in node.get_signal_list():
			for connection: Dictionary in node.get_signal_connection_list(info["name"]):
				if _leaves(source, connection):
					var entry: Dictionary = {
						"path": source.get_path_to(node),
						"signal": info["name"],
						"callable": connection["callable"],
						"flags": connection["flags"],
					}
					found.append(entry)
	return found


## Whether connection is persistent and targets a node outside source.
static func _leaves(source: Node, connection: Dictionary) -> bool:
	var target := (connection["callable"] as Callable).get_object() as Node
	var persistent: bool = (int(connection["flags"]) & CONNECT_PERSIST) != 0
	return persistent and target != null and target != source and not source.is_ancestor_of(target)


## Makes each connection of outbound again from the node at its path under top, with its flags
## masked by kept.
static func _connect_outbound(top: Node, outbound: Array, kept: int) -> void:
	for entry: Dictionary in outbound:
		var node: Node = top.get_node(entry["path"])
		node.connect(entry["signal"], entry["callable"], int(entry["flags"]) & kept)


## Moves node from its parent to parent (out of the tree when null), clearing the owner of owned
## first and setting it to owner after, so no move leaves an owner that is not an ancestor.
static func _move(node: Node, parent: Node, owned: Array[Node], owner: Node) -> void:
	for each: Node in owned:
		each.owner = null
	node.get_parent().remove_child(node)
	if parent != null:
		parent.add_child(node)
	for each: Node in owned:
		each.owner = owner


## Adds copy under parent, right after source when that is its parent, and gives root the nodes
## of it in owned.
static func _place(copy: Node, parent: Node, source: Node, owned: Array[Node], root: Node) -> void:
	parent.add_child(copy)
	if parent == source.get_parent():
		parent.move_child(copy, source.get_index() + 1)
	for node: Node in owned:
		node.owner = root
