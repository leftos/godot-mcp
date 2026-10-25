extends RefCounted
## The headless node edits of an open scene, which scene_ops.gd dispatches and saves:
## delete_nodes, and the edits of one node, attach_script, duplicate_node, move_node and
## load_sprite, with resolve_position, the place in its parent's children a position (an index, or
## before or after a sibling) gives a node, which add_node shares. Each is
## apply_<op>(root, params, context) -> {result} or {error}, and changes nothing when it refuses.
##
## duplicate_node packs the node under a bare holder and instantiates the pack, so an instance in
## the copy stays an instance with its overrides (Node.duplicate bakes instances and doubles their
## connections: 4.7.2 scene/main/node.cpp L2789-2798). The holder is outside the scene, where a
## connection from the copied nodes to a node outside them has no common parent to be packed with
## (scene/resources/packed_scene.cpp L1200-1202): such a connection is taken off for the pack, put
## back, and made again from the copy, as the editor's duplicate keeps it. The pack's unique_ids
## are cleared before the copy is made, so the save gives the copy's nodes fresh ones
## (_clear_unique_ids).

const SceneEdit := preload("scene_edit.gd")
const SceneFiles := preload("scene_files.gd")
## The connection flags a copy keeps: those scripts can set, not the engine's own (an inherited
## connection's copy is the scene's own).
const SCRIPT_CONNECT_FLAGS := (
	CONNECT_DEFERRED | CONNECT_PERSIST | CONNECT_ONE_SHOT | CONNECT_REFERENCE_COUNTED
)
const ROOT_DUPLICATE_REFUSAL := (
	"The scene root cannot be duplicated; " + "save_scene with newPath copies the whole scene."
)
const ROOT_MOVE_REFUSAL := "the scene's root cannot be moved."
const MOVE_NEEDS_REFUSAL := "move_node needs options.parent, options.position or both."
## The keys a position takes, exactly one of them.
const POSITION_KEYS: Array[String] = ["index", "before", "after"]
const POSITION_KEYS_REFUSAL := "position takes exactly one of index, before or after; got %s."
const MOVE_INSTANCE_REFUSAL := (
	"%s is inside the instance of %s at %s, " + "so its move would not be saved. Edit %s instead."
)
const MOVE_INHERITED_REFUSAL := (
	"%s comes from the base scene %s, " + "so its move would not be saved. Edit %s instead."
)
const INDEX_RANGE_REFUSAL := (
	"position.index %d is out of range: %s has %d children once the node is placed, "
	+ "so index takes %d to %d."
)
## The edit context's key under which move_node records each node a move carried, with the path
## from the root it had before its first move in the edit, and add_node and duplicate_node record
## each node they create, with "" (no source in the file): {Node: path or ""}.
const MOVED_KEY := "moved"
## PROPERTY_HINT_NO_NODEPATH, which scripts are not given: the hint between PROPERTY_HINT_ONESHOT
## and PROPERTY_HINT_GROUP_ENABLE (4.7.2 core/object/property_info.h L80-82, core_constants.cpp
## L687-688).
const NO_NODEPATH_HINT := PROPERTY_HINT_ONESHOT + 1


## Deletes the nodes at params.nodePaths, each with its children: {result: {deleted}}, or {error}
## naming every path that cannot be deleted, with nothing deleted.
static func apply_delete_nodes(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var paths: Array = params.get("nodePaths", [])
	var refusals: PackedStringArray = []
	for path: String in paths:
		var refusal: String = _delete_refusal(root, path, context["scene"])
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


## Attaches the script at params.script to the node at params.nodePath: {result: {path, script,
## previous?, kept?, dropped?, uidFilesWritten?}}, or {error} with nothing changed. The script must
## compile and extend the node's class or a parent class of it; a C# script is refused while the
## prep's C# build failed. The values the previous script stored carry over as the editor carries
## them (_carry_script_values); kept and dropped name them. A script with no uid is given the one
## the editor would give it (SceneFiles.uid_or_new), and uidFilesWritten names the .uid files
## written.
static func apply_attach_script(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params, context["scene"])
	if found.has("error"):
		return found
	var node: Node = found["node"]
	var script_path: String = params.get("script", "")
	var refusal: String = SceneEdit.csharp_refusal(
		context["scene"], script_path.ends_with(".cs"), context
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
	var written: PackedStringArray = []
	var result: Dictionary = {"path": found["path"], "script": _resource_facts(script, written)}
	_swap_script(node, script, result, written)
	SceneFiles.note_written(result, written)
	return {"result": result}


## Copies the node at params.nodePath, with its children, right after it under its parent or last
## under params.parent, named params.newName or as the editor names a duplicate (copy_name):
## {result: {originalPath, newPath}}, or {error} with nothing changed.
static func apply_duplicate_node(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params, context["scene"])
	if found.has("error"):
		return found
	var source: Node = found["node"]
	if source == root:
		return {"error": ROOT_DUPLICATE_REFUSAL}
	var given_parent: String = str(params.get("parent", ""))
	var parent: Dictionary = _copy_parent(root, source, given_parent, context["scene"])
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
	note_created(copy, context)
	_connect_outbound(copy, copied["outbound"], SCRIPT_CONNECT_FLAGS)
	return {"result": {"originalPath": found["path"], "newPath": String(root.get_path_to(copy))}}


## Moves the node at params.nodePath to params.position among its siblings, or under params.parent
## (at params.position there, else last), keeping its global transform unless
## params.keepGlobalTransform is false, and keeping the scene root the owner of it and of the nodes
## below it the root owned: {result: {path, previousPath, index, rewrittenPaths}}, or {error} with
## nothing changed. A stored NodePath of the scene's nodes that pointed at a node, into or out of
## what the move carried, is rewritten to resolve to that node again, as the editor's reparent
## rewrites it (_rebase_node_paths); rewrittenPaths lists each as {node, property, from, to}, []
## when none. A move under another parent records in context, under MOVED_KEY, the path each node
## it carries had before its first move in the edit (moved_paths reads them for the save).
static func apply_move_node(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	if params.get("parent") == null and params.get("position") == null:
		return {"error": MOVE_NEEDS_REFUSAL}
	var planned: Dictionary = _plan_move(root, params, context["scene"])
	if planned.has("error"):
		return planned
	var node: Node = planned["node"]
	var parent: Node = planned["parent"]
	var previous: String = String(root.get_path_to(node))
	var rewritten: Array = []
	if parent != node.get_parent():
		var held: Array = _held_node_paths(root)
		_note_sources(node, root, context)
		_reparent(node, parent, root, bool(params.get("keepGlobalTransform", true)))
		rewritten = _rebase_node_paths(held, root)
	if planned.has("index"):
		parent.move_child(node, planned["index"])
	var result: Dictionary = {
		"path": String(root.get_path_to(node)),
		"previousPath": previous,
		"index": node.get_index(),
		"rewrittenPaths": rewritten,
	}
	return {"result": result}


## Sets the texture of the node at params.nodePath to the Texture2D at params.texture: {result:
## {path, texture}}, or {error} with nothing changed.
static func apply_load_sprite(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var found: Dictionary = _editable_node(root, params, context["scene"])
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
	var written: PackedStringArray = []
	var texture: Dictionary = _resource_facts(loaded["texture"], written)
	var result: Dictionary = {"path": found["path"], "texture": texture}
	SceneFiles.note_written(result, written)
	return {"result": result}


## {source path: path now} for each node a move_node of the edit in context carried (MOVED_KEY)
## that is still in the scene under root, one back at the path it had before its first move
## included (mapped to that same path, so a carried node over it does not displace it): the save
## keys a carried node's source record and sections by the path it has now. A node the edit
## created (recorded with "") has no source and is left out.
static func moved_paths(root: Node, context: Dictionary) -> Dictionary:
	var renames: Dictionary = {}
	var moved: Dictionary = context.get(MOVED_KEY, {})
	for key: Variant in moved:
		if not is_instance_valid(key) or String(moved[key]).is_empty():
			continue
		var node := key as Node
		if root.is_ancestor_of(node):
			renames[moved[key]] = String(root.get_path_to(node))
	return renames


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
static func _editable_node(root: Node, params: Dictionary, scene: String) -> Dictionary:
	var given: String = params.get("nodePath", "")
	var found: Dictionary = SceneEdit.node_or_error(root, given, scene)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	var refusal: String = SceneEdit.instance_refusal(root, node, given)
	if not refusal.is_empty():
		return {"error": refusal}
	return {"node": node, "path": String(root.get_path_to(node))}


static func _has_texture_2d(node: Node) -> bool:
	for entry: Dictionary in node.get_property_list():
		if takes_texture_2d(entry):
			return true
	return false


## Sets script on node. When the node had a script, result gains previous, and the values that
## script stored carry over to the new one (_carry_script_values), kept and dropped naming them.
## A .uid file written for the previous script is appended to written (_resource_facts).
static func _swap_script(
	node: Node, script: Script, result: Dictionary, written: PackedStringArray
) -> void:
	var previous := node.get_script() as Script
	if previous == null:
		node.set_script(script)
		return
	result["previous"] = _resource_facts(previous, written)
	var stored: Array = _stored_script_values(node, previous)
	node.set_script(script)
	result.merge(_carry_script_values(node, script, stored))


## [name, value] for each property script declares with PROPERTY_USAGE_STORAGE, read from node, as
## the editor stores them before it changes a script (4.7.2 editor/docks/inspector_dock.cpp
## L631-650, core/object/script_instance.cpp L61-73): Object.set_script keeps no value of the
## script it replaces (core/object/object.cpp L979-991).
static func _stored_script_values(node: Node, script: Script) -> Array:
	var stored: Array = []
	for entry: Dictionary in script.get_script_property_list():
		if entry["usage"] & PROPERTY_USAGE_STORAGE:
			stored.append([entry["name"], node.get(entry["name"])])
	return stored


## Sets on node each stored [name, value] the node's new script declares and takes, as the editor's
## apply_script_properties does (inspector_dock.cpp L652-683; see _takes_value): {kept, dropped},
## the names set and the names not.
static func _carry_script_values(node: Node, script: Script, stored: Array) -> Dictionary:
	var declared: Dictionary = {}
	for entry: Dictionary in script.get_script_property_list():
		declared[entry["name"]] = entry
	var kept: Array = []
	var dropped: Array = []
	for pair: Array in stored:
		var name: String = pair[0]
		if declared.has(name) and _takes_value(node.get(name), declared[name], pair[1]):
			node.set(name, pair[1])
			kept.append(name)
		else:
			dropped.append(name)
	return {"kept": kept, "dropped": dropped}


## Whether a new script's property entry, now holding current, takes value: when current has
## value's type, or when the property holds an Object and value is an Object of the class its hint
## names, or has a script whose global class, or a base script's, is that name.
static func _takes_value(current: Variant, entry: Dictionary, value: Variant) -> bool:
	if typeof(current) == typeof(value):
		return true
	if typeof(value) != TYPE_OBJECT or entry["type"] != TYPE_OBJECT:
		return false
	if not is_instance_valid(value):
		return false
	var object: Object = value
	var hint: String = entry["hint_string"]
	return object.is_class(hint) or _script_class_is(object.get_script() as Script, hint)


## Whether script, or a script it extends, declares the global class name.
static func _script_class_is(script: Script, name: String) -> bool:
	while script != null:
		if script.get_global_name() == name:
			return true
		script = script.get_base_script()
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


## {resource, uid?} for a resource saved in its own file; a script with no uid is given one
## (SceneFiles.uid_or_new), and the .uid file written is appended to written.
static func _resource_facts(resource: Resource, written: PackedStringArray) -> Dictionary:
	var facts: Dictionary = {"resource": resource.resource_path}
	var uid: int = SceneFiles.uid_or_new(resource.resource_path, written)
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


## {node}: the parent a copy of source goes under, given (a path from root) or, when empty,
## source's own; or {error} for a missing parent or one inside an instance (an instance's root may
## be the parent).
static func _copy_parent(root: Node, source: Node, given: String, scene: String) -> Dictionary:
	if given.is_empty():
		return {"node": source.get_parent()}
	var found: Dictionary = SceneEdit.node_or_error(root, given, scene)
	if found.has("error"):
		return found
	var parent: Node = found["node"]
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
	var packing: Dictionary = SceneEdit.pack_native(holder)
	_move(source, parent, owned, root)
	parent.move_child(source, index)
	holder.free()
	_connect_outbound(source, outbound, ~0)
	var path: String = String(root.get_path_to(source))
	if packing.has("error"):
		return {"error": "%s could not be copied: %s" % [path, packing["error"]]}
	var packed: PackedScene = packing["packed"]
	_clear_unique_ids(packed)
	var copy_holder: Node = SceneEdit.instantiate_native(packed, PackedScene.GEN_EDIT_STATE_MAIN)
	if copy_holder == null:
		return {"error": "%s was packed, but its copy could not be instantiated." % path}
	var copy: Node = copy_holder.get_child(0)
	var copy_owned: Array[Node] = _owned_by(copy, copy_holder)
	_move(copy, null, copy_owned, null)
	copy_holder.free()
	return {"node": copy, "owned": copy_owned, "outbound": outbound}


## Sets every unique_id packed stores to unassigned (0, 4.7.2 scene/main/node.h L144), so the
## nodes instantiated from it carry none, as the editor's Duplicate leaves its copy: Node._duplicate
## (scene/main/node.cpp L2777) never copies the id, which only set_unique_scene_id (L2117) sets and
## script cannot reach. Instantiating sets each node the stored id (packed_scene.cpp L357-360), and
## the next save gives each node without one a fresh id (ResourceUID.create_id, kept positive) that
## no node saved before it has (packed_scene.cpp L1099-1123). A node inside an instance keeps its
## own scene's id: instantiating takes that node's id over the stored one (L301-307).
static func _clear_unique_ids(packed: PackedScene) -> void:
	var bundled: Dictionary = packed.get("_bundled")
	if not bundled.has("node_ids"):
		return
	var ids: PackedInt32Array = bundled["node_ids"]
	ids.fill(0)
	bundled["node_ids"] = ids
	packed.set("_bundled", bundled)


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


static func _delete_refusal(root: Node, path: String, scene_path: String) -> String:
	var found: Dictionary = SceneEdit.node_or_error(root, path, scene_path)
	if found.has("error"):
		return found["error"]
	var node: Node = found["node"]
	if node == root:
		return "The scene root cannot be deleted; create a new scene instead."
	var refusal: String = SceneEdit.instance_refusal(root, node, path)
	if refusal.is_empty():
		refusal = SceneEdit.inherited_delete_refusal(scene_path, root, node, path)
	return refusal


## Why node (found at path, not the root) cannot be moved, or "": it is inside an instance, or it
## comes from a base scene; the saved file records the move of neither (4.7.2 packed_scene.cpp
## L491, L766-776).
static func _move_held_refusal(root: Node, node: Node, path: String, scene_path: String) -> String:
	var instance: Node = node.owner
	if instance == null:
		return "%s is not saved with the scene: it has no owner." % path
	if instance != root:
		var file: String = instance.scene_file_path
		var facts: Array = [path, file, String(root.get_path_to(instance)), file]
		return MOVE_INSTANCE_REFUSAL % facts
	var base: String = SceneEdit.inherited_from(scene_path, String(root.get_path_to(node)))
	return "" if base.is_empty() else MOVE_INHERITED_REFUSAL % [path, base, base]


## {node, parent, index?}: the node at params.nodePath, the parent it moves under (its own when
## params.parent is absent) and the index params.position gives it there; or {error}.
static func _plan_move(root: Node, params: Dictionary, scene: String) -> Dictionary:
	var path: String = str(params.get("nodePath", ""))
	var found: Dictionary = SceneEdit.node_or_error(root, path, scene)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	if node == root:
		return {"error": ROOT_MOVE_REFUSAL}
	var refusal: String = _move_held_refusal(root, node, path, scene)
	if not refusal.is_empty():
		return {"error": refusal}
	var target: Dictionary = _move_target(root, node, params.get("parent"), path, scene)
	if target.has("error"):
		return target
	var placed: Dictionary = placement(target["node"], node, params.get("position"))
	if placed.has("error"):
		return placed
	placed["node"] = node
	placed["parent"] = target["node"]
	return placed


## {node}: the parent node moves under, given (a path from root) or, when null, its own; or {error}
## for a missing parent, one add_node could not add to, node itself or a node below it, or a parent
## with a child of node's name.
static func _move_target(
	root: Node, node: Node, given: Variant, path: String, scene: String
) -> Dictionary:
	if given == null:
		return {"node": node.get_parent()}
	var parent_path: String = str(given)
	var found: Dictionary = SceneEdit.node_or_error(root, parent_path, scene)
	if found.has("error"):
		return found
	var parent: Node = found["node"]
	var refusal: String = SceneEdit.unsaved_edit_refusal(root, parent, parent_path)
	if refusal.is_empty():
		refusal = _parent_refusal(root, node, parent, parent_path, path)
	return {"node": parent} if refusal.is_empty() else {"error": refusal}


## Why node (found at path) cannot move under parent (found at parent_path), or "": parent is node
## or below it, or another parent already has a child of node's name (add_node's wording).
static func _parent_refusal(
	root: Node, node: Node, parent: Node, parent_path: String, path: String
) -> String:
	if parent == node or node.is_ancestor_of(parent):
		return "%s cannot move under itself or its own child %s." % [path, parent_path]
	if parent != node.get_parent() and parent.has_node(NodePath(String(node.name))):
		var named: String = "The scene root" if parent == root else parent_path
		return "%s already has a child named %s." % [named, node.name]
	return ""


## Records in context, under MOVED_KEY, node and each node below it (an instance's own and internal
## ones included) as created by the edit, with "": none has a source in the file. Called once each
## is under its parent.
static func note_created(node: Node, context: Dictionary) -> void:
	var moved: Dictionary = context.get_or_add(MOVED_KEY, {})
	var created: Array[Node] = [node]
	created.append_array(node.find_children("*", "", true, false))
	for each: Node in created:
		moved[each] = ""


## Records in context, under MOVED_KEY, the path from root node and each node below it have now,
## unless the edit recorded one (or "", for a node it created) for it before.
static func _note_sources(node: Node, root: Node, context: Dictionary) -> void:
	var moved: Dictionary = context.get_or_add(MOVED_KEY, {})
	var carried: Array[Node] = [node]
	carried.append_array(node.find_children("*", "", true, false))
	for each: Node in carried:
		if not moved.has(each):
			moved[each] = String(root.get_path_to(each))


## The values of the stored properties of root and every node under it, an instance's own nodes
## included (a value set there is saved as the instance node's override), that hold a NodePath
## which resolves to a node, read before a move: each {holder, property, value, targets}, targets
## the node each NodePath in value (an Array's or Dictionary's values included, in order) resolves
## to, null for one that resolves to none. As the editor's reparent reads them (4.7.2
## editor/docks/scene_tree_dock.cpp L2122-2216, every node: L2396-2399), a property hinted
## PROPERTY_HINT_NO_NODEPATH is left out; a resource's NodePaths and animation tracks are not read.
static func _held_node_paths(root: Node) -> Array:
	var held: Array = []
	var nodes: Array[Node] = [root]
	nodes.append_array(root.find_children("*", "", true, false))
	for holder: Node in nodes:
		for entry: Dictionary in holder.get_property_list():
			if not _stores_node_paths(entry):
				continue
			var value: Variant = holder.get(entry["name"])
			var targets: Array = []
			_collect_targets(holder, value, targets)
			if targets.count(null) < targets.size():
				var record: Dictionary = {
					"holder": holder,
					"property": String(entry["name"]),
					"value": value,
					"targets": targets,
				}
				held.append(record)
	return held


## Whether a property-list entry is a stored NodePath, Array or Dictionary not hinted
## PROPERTY_HINT_NO_NODEPATH.
static func _stores_node_paths(entry: Dictionary) -> bool:
	if (int(entry["usage"]) & PROPERTY_USAGE_STORAGE) == 0:
		return false
	if entry["hint"] == NO_NODEPATH_HINT:
		return false
	return entry["type"] in [TYPE_NODE_PATH, TYPE_ARRAY, TYPE_DICTIONARY]


## Appends to targets the node each NodePath in value (an Array's or Dictionary's values included,
## in order) resolves to from holder, or null.
static func _collect_targets(holder: Node, value: Variant, targets: Array) -> void:
	match typeof(value):
		TYPE_NODE_PATH:
			targets.append(_target_of(holder, value))
		TYPE_ARRAY:
			for element: Variant in value:
				_collect_targets(holder, element, targets)
		TYPE_DICTIONARY:
			for element: Variant in (value as Dictionary).values():
				_collect_targets(holder, element, targets)


## The node path resolves to from holder, or null: an empty or absolute path resolves to none, as
## an edited scene is outside the scene tree.
static func _target_of(holder: Node, path: NodePath) -> Node:
	if path.is_empty() or path.is_absolute():
		return null
	return holder.get_node_or_null(path)


## Sets each property of held (_held_node_paths) whose NodePaths no longer resolve to the nodes
## they did to the value whose NodePaths do, each the path from its holder to its node with its
## subnames kept: [{node, property, from, to}], one per NodePath rewritten, node the holder's path
## from root. A NodePath that still resolves to its node is left as it is.
static func _rebase_node_paths(held: Array, root: Node) -> Array:
	var rewritten: Array = []
	for record: Dictionary in held:
		var holder: Node = record["holder"]
		var changes: Array = []
		var targets: Array = (record["targets"] as Array).duplicate()
		var value: Variant = _rebased(holder, record["value"], targets, changes)
		if changes.is_empty():
			continue
		holder.set(record["property"], value)
		var at: String = String(root.get_path_to(holder))
		for change: Array in changes:
			var entry: Dictionary = {
				"node": at, "property": record["property"], "from": change[0], "to": change[1]
			}
			rewritten.append(entry)
	return rewritten


## value with each NodePath in it (an Array's or Dictionary's values included) rebased from holder
## to the node targets, taken from its front in order, names (_rebased_path); changes gains
## [from, to] for each one rewritten. An Array or Dictionary is copied, so value stays as it is.
static func _rebased(holder: Node, value: Variant, targets: Array, changes: Array) -> Variant:
	match typeof(value):
		TYPE_NODE_PATH:
			return _rebased_path(holder, value, targets.pop_front() as Node, changes)
		TYPE_ARRAY:
			var array: Array = (value as Array).duplicate()
			for index in array.size():
				array[index] = _rebased(holder, array[index], targets, changes)
			return array
		TYPE_DICTIONARY:
			var dictionary: Dictionary = (value as Dictionary).duplicate()
			for key: Variant in dictionary:
				dictionary[key] = _rebased(holder, dictionary[key], targets, changes)
			return dictionary
	return value


## path, or, when it no longer resolves from holder to target, the path from holder to target with
## path's subnames, as the editor rewrites it (4.7.2 editor/docks/scene_tree_dock.cpp L2077-2120);
## changes gains [from, to] for a rewritten one. A null target leaves path as it is.
static func _rebased_path(holder: Node, path: NodePath, target: Node, changes: Array) -> NodePath:
	if target == null or holder.get_node_or_null(path) == target:
		return path
	var text: String = String(holder.get_path_to(target))
	if path.get_subname_count() > 0:
		text += ":" + String(path.get_concatenated_subnames())
	changes.append([String(path), text])
	return NodePath(text)


## Moves node under parent, keeping its global transform when keep, and gives root back node and
## the nodes below it root owned; a node owned by an instance's root keeps that owner. A Node3D's
## global transform is composed here: outside the scene tree, where an edited scene is, getting
## Node3D.global_transform fails and returns Transform3D.IDENTITY (4.7.2 doc/classes/Node3D.xml
## L318).
static func _reparent(node: Node, parent: Node, root: Node, keep: bool) -> void:
	var owned: Array[Node] = _owned_by(node, root)
	if node is Node3D and keep and not (node as Node3D).top_level:
		var global: Transform3D = _global_3d(node)
		node.reparent(parent, false)
		(node as Node3D).transform = _global_3d(parent).affine_inverse() * global
	else:
		node.reparent(parent, keep)
	for each: Node in owned:
		each.owner = root


## node's transform composed with those of the Node3D parents above it, as Node3D composes its
## global transform: up to the first parent that is not a Node3D or that is top_level.
static func _global_3d(node: Node) -> Transform3D:
	var composed := Transform3D.IDENTITY
	var at: Node = node
	while at is Node3D:
		composed = (at as Node3D).transform * composed
		if (at as Node3D).top_level:
			break
		at = at.get_parent()
	return composed


## {index} for position ({index | before | after}) among parent's children, as
## resolve_position says, when position is not null; {} when it is; or {error}.
static func placement(parent: Node, node: Node, position: Variant) -> Dictionary:
	if position == null:
		return {}
	return resolve_position(parent, node, position if position is Dictionary else {})


## {index}: the to_index Node.move_child takes to put node at position among parent's children,
## node being one of them by then (it may be already); or {error}. position holds exactly one of
## index (a negative one counting from the end, -1 last), before or after (a sibling's name):
## before a sibling is its index once node is taken out, after it the index past that.
static func resolve_position(parent: Node, node: Node, position: Dictionary) -> Dictionary:
	var keys: PackedStringArray = []
	for key: Variant in position:
		if position[key] != null:
			keys.append(str(key))
	if keys.size() != 1 or not POSITION_KEYS.has(keys[0]):
		var got: String = "none" if keys.is_empty() else ", ".join(keys)
		return {"error": POSITION_KEYS_REFUSAL % got}
	if keys[0] == "index":
		return _index_position(parent, node, position["index"])
	return _sibling_position(parent, node, keys[0], str(position[keys[0]]))


static func _index_position(parent: Node, node: Node, given: Variant) -> Dictionary:
	var count: int = parent.get_child_count() + (0 if node.get_parent() == parent else 1)
	var numeric: bool = typeof(given) == TYPE_INT or typeof(given) == TYPE_FLOAT
	if not numeric or float(int(given)) != float(given):
		return {"error": "position.index takes an integer; got %s." % JSON.stringify(given)}
	var index: int = int(given)
	if index < -count or index >= count:
		var facts: Array = [index, _parent_label(parent), count, -count, count - 1]
		return {"error": INDEX_RANGE_REFUSAL % facts}
	return {"index": index + count if index < 0 else index}


static func _sibling_position(parent: Node, node: Node, key: String, name: String) -> Dictionary:
	var sibling: Node = parent.get_node_or_null(NodePath(name))
	if sibling == null or sibling.get_parent() != parent:
		return {
			"error": "position.%s names no child %s of %s." % [key, name, _parent_label(parent)]
		}
	if sibling == node:
		return {"error": "position.%s names the node being moved." % key}
	var index: int = sibling.get_index()
	if node.get_parent() == parent and node.get_index() < index:
		index -= 1
	return {"index": index + 1 if key == "after" else index}


## node as a refusal names it: its path from the top of its tree, the scene's root, or "the scene
## root" for the root itself.
static func _parent_label(node: Node) -> String:
	var top: Node = node
	while top.get_parent() != null:
		top = top.get_parent()
	return "the scene root" if top == node else String(top.get_path_to(node))
