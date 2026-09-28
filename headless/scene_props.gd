extends RefCounted
## The headless property edits scene_ops.gd applies to an open scene: add_node,
## set_node_properties and get_node_properties. Values convert through scene_values.gd (a node as
## its path from the scene's root), and the JSON module (bridge/godot_mcp_json.gd) supplies the
## rules these share with the running game's inspector: which properties a read shows, how a
## property is found, and how a read-back is compared with the value set. Godot does not refuse a
## wrong type (see bridge/godot_mcp_inspect.gd's header), so every set is read back.

const SceneEdit := preload("scene_edit.gd")
const SceneNodes := preload("scene_nodes.gd")
const ScenePaths := preload("scene_paths.gd")
const SceneValues := preload("scene_values.gd")
const Json := preload("../bridge/godot_mcp_json.gd")
const SCENE_PREFIX := "res://"


## Adds a node of params.nodeType named params.nodeName under params.parent, at params.position
## among its children (SceneNodes.resolve_position) or last, with params.properties set on it
## before it is added: {result: {path, type, index, instance?, script?}}, or {error} naming every
## failing property or the bad position, with nothing added. The new node is owned by the scene's
## root; an instanced scene's own nodes keep the instance's owners.
static func apply_add_node(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var parent_path: String = str(params.get("parent", "."))
	var node_name: String = str(params.get("nodeName", ""))
	var scene: String = context["scene"]
	var refusal: String = _add_refusal(root, parent_path, node_name, scene)
	if refusal.is_empty():
		refusal = _reserved_key(params.get("properties", {}))
	if not refusal.is_empty():
		return {"error": refusal}
	var made: Dictionary = _new_node(str(params.get("nodeType", "")), context)
	if made.has("error"):
		return made
	var node: Node = made.node
	node.name = node_name
	var parent: Node = SceneEdit.find(root, parent_path)
	var placed: Dictionary = SceneNodes.placement(parent, node, params.get("position"))
	if placed.has("error"):
		node.free()
		return placed
	var properties: Variant = params.get("properties", {})
	var failures: PackedStringArray = _set_all(
		node,
		_child_path(root, parent, node_name),
		properties if properties is Dictionary else {},
		root
	)
	if not failures.is_empty():
		node.free()
		return {"error": " ".join(failures)}
	parent.add_child(node)
	node.owner = root
	if placed.has("index"):
		parent.move_child(node, placed["index"])
	return {"result": _added_result(root, node, made)}


## add_node's result for node, added as made ({type, instance?, script?}) says: {path, type, index,
## instance?, script?}.
static func _added_result(root: Node, node: Node, made: Dictionary) -> Dictionary:
	var result: Dictionary = {
		"path": String(root.get_path_to(node)), "type": made.type, "index": node.get_index()
	}
	if made.has("instance"):
		result["instance"] = made.instance
	if made.has("script"):
		result["script"] = made.script
	return result


## Sets each of params.updates ({nodePath, property, value}) and reads it back:
## {result: {results: [{nodePath, property, before, after}]}}, or {error} naming every failing
## entry. Every entry is found and converted before any is set.
static func apply_set_node_properties(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var scene: String = context["scene"]
	var planned: Array = []
	var failures: PackedStringArray = []
	for update: Dictionary in params.get("updates", []):
		var plan: Dictionary = _plan_update(root, update, scene)
		if plan.has("error"):
			failures.append(plan.error)
		else:
			planned.append(plan)
	if not failures.is_empty():
		return {"error": " ".join(failures)}
	var results: Array = []
	for plan: Dictionary in planned:
		var done: Dictionary = _set_checked(
			plan.node, plan.nodePath, plan.property, plan.value, root
		)
		if done.has("error"):
			failures.append(done.error)
			continue
		(
			results
			. append(
				{
					"nodePath": plan.nodePath,
					"property": plan.property,
					"before": done.before,
					"after": done.after,
				}
			)
		)
	if not failures.is_empty():
		return {"error": " ".join(failures)}
	return {"result": {"results": results}}


## Reads each of params.nodes ({nodePath, properties?, changedOnly?}):
## {result: {results: [{nodePath, type, script?, properties} | {nodePath, error}]}}. A node or
## property that is missing gives its own entry an error.
static func apply_get_node_properties(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var results: Array = []
	for query: Dictionary in params.get("nodes", []):
		results.append(_read_node(root, query, context["scene"]))
	return {"result": {"results": results}}


## The names of the properties the scene file at scene_path stores for the node at node_path
## (relative to its root), in file order: the scene's own entry for it laid over the entries of
## the instanced and inherited scenes it comes from, as get_scene_file_tree merges them.
static func stored_names(scene_path: String, node_path: String) -> Array:
	var names: Array = []
	var scene := ResourceLoader.load(scene_path) as PackedScene
	if scene != null:
		_add_stored(scene.get_state(), ".", ScenePaths.normalise_node_path(node_path), names, 0)
	return names


static func _add_refusal(
	root: Node, parent_path: String, node_name: String, scene: String
) -> String:
	var found: Dictionary = SceneEdit.node_or_error(root, parent_path, scene)
	if found.has("error"):
		return found["error"]
	var parent: Node = found["node"]
	var refusal: String = SceneEdit.unsaved_edit_refusal(root, parent, parent_path)
	if not refusal.is_empty():
		return refusal
	if node_name.validate_node_name() != node_name:
		return 'nodeName %s holds a character no node name may hold: . : @ / " %%.' % node_name
	if parent.has_node(NodePath(node_name)):
		var named: String = "The scene root" if parent == root else parent_path
		return "%s already has a child named %s." % [named, node_name]
	return ""


## {node, type, instance?, script?} for a Node class, a script class_name, a script path (a .gd or
## .cs file, making a node of the class it extends with the script attached) or a res:// scene
## (instanced as an instance, so it saves as one), or {error}.
static func _new_node(type: String, context: Dictionary) -> Dictionary:
	if type.ends_with(".gd") or type.ends_with(".cs"):
		return _script_node(type, context)
	if type.begins_with(SCENE_PREFIX):
		return _instance_of(type, context["scene"])
	var node: Node = SceneEdit.new_root(type)
	if node == null:
		return {"error": _not_a_node_type(type)}
	return {"node": node, "type": type}


## {node, type, script} for the script at path: a node of the class the script extends, with the
## script attached. It must compile and extend a Node class; a C# script is refused while the
## prep's C# build failed.
static func _script_node(path: String, context: Dictionary) -> Dictionary:
	var refusal: String = SceneEdit.csharp_refusal(context["scene"], path.ends_with(".cs"), context)
	if not refusal.is_empty():
		return {"error": refusal}
	var node: Node = SceneEdit.script_root(path)
	if node == null:
		var message: String = (
			"nodeType '%s' is a script that cannot make a node: it must compile and extend a Node "
			+ "class that can be instanced."
		)
		return {"error": message % path}
	return {"node": node, "type": node.get_class(), "script": path}


static func _instance_of(path: String, scene: String) -> Dictionary:
	var packed := ResourceLoader.load(path) as PackedScene
	if packed == null:
		return {"error": _not_a_node_type(path)}
	if _contains_scene(packed, scene, 0):
		return {"error": "%s is or instances %s, which cannot contain itself." % [path, scene]}
	var node: Node = packed.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	if node == null:
		return {"error": "%s loaded but could not be instantiated." % path}
	return {"node": node, "type": node.get_class(), "instance": path}


## Whether packed is the scene at scene_path or instances it at any depth.
## Why properties would set what add_node sets itself (the name, the owner), or "".
static func _reserved_key(properties: Variant) -> String:
	if not properties is Dictionary:
		return ""
	if (properties as Dictionary).has("name"):
		return "Set the node's name with nodeName, not options.properties."
	if (properties as Dictionary).has("owner"):
		return "add_node sets the node's owner itself; leave owner out of options.properties."
	return ""


## Whether two res:// paths name one file, ignoring case where the file system does.
static func _same_file(path: String, other: String) -> bool:
	if OS.get_name() in ["Windows", "macOS"]:
		return path.to_lower() == other.to_lower()
	return path == other


static func _contains_scene(packed: PackedScene, scene_path: String, depth: int) -> bool:
	if _same_file(packed.resource_path, scene_path):
		return true
	var state: SceneState = packed.get_state()
	for index in state.get_node_count():
		var instance: PackedScene = state.get_node_instance(index)
		if instance == null or depth >= SceneEdit.MAX_BASE_DEPTH:
			continue
		if _contains_scene(instance, scene_path, depth + 1):
			return true
	return false


static func _not_a_node_type(type: String) -> String:
	return "nodeType '%s' is not a Node class, a script class_name or a scene." % type


## The path a node named node_name gets under parent, relative to root.
static func _child_path(root: Node, parent: Node, node_name: String) -> String:
	return node_name if parent == root else "%s/%s" % [root.get_path_to(parent), node_name]


## Converts and sets each of properties ({name: value}) on node, reading each back; the reasons
## the ones that fail do.
static func _set_all(
	node: Node, path: String, properties: Dictionary, root: Node
) -> PackedStringArray:
	var failures: PackedStringArray = []
	for key: Variant in properties:
		var converted: Dictionary = _converted(node, path, str(key), properties[key], root)
		if converted.has("error"):
			failures.append(converted.error)
			continue
		var done: Dictionary = _set_checked(node, path, str(key), converted.value, root)
		if done.has("error"):
			failures.append(done.error)
	return failures


## {node, nodePath, property, value} for one update, the node found and the value converted, or
## {error}: a missing node, a node whose change would not be saved, an unknown property or a value
## that does not convert.
static func _plan_update(root: Node, update: Dictionary, scene: String) -> Dictionary:
	var path: String = str(update.get("nodePath", ""))
	var found: Dictionary = SceneEdit.node_or_error(root, path, scene)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	var refusal: String = SceneEdit.unsaved_edit_refusal(root, node, path)
	if not refusal.is_empty():
		return {"error": refusal}
	var property: String = str(update.get("property", ""))
	var converted: Dictionary = _converted(node, path, property, update.get("value"), root)
	if converted.has("error"):
		return converted
	return {"node": node, "nodePath": path, "property": property, "value": converted.value}


## {value}: value converted by the property's declared type (an untyped property's by the type of
## the value it holds, unless that is null), as the running game's set_property converts it; or
## {error}.
static func _converted(
	node: Node, path: String, property: String, value: Variant, root: Node
) -> Dictionary:
	var info: Dictionary = Json.property_info(node, property)
	if info.is_empty():
		return {"error": "%s has no property %s." % [path, property]}
	var held: Variant = node.get(property)
	if info.type == TYPE_NIL and held != null:
		info = {"type": typeof(held)}
	var converted: Array = SceneValues.from_json(value, info, root)
	if not converted[0] and converted.size() > 2:
		return {"error": "Property '%s' on '%s': %s." % [property, path, converted[2]]}
	if not converted[0]:
		return {
			"error":
			(
				"Property '%s' on '%s' is %s; %s does not convert to it."
				% [property, path, Json.type_name(info), JSON.stringify(value)]
			)
		}
	return {"value": converted[1]}


## Sets value and reads it back: {before, after} as JSON, or {error} when the read-back differs by
## the inspector's rule.
static func _set_checked(
	node: Node, path: String, property: String, value: Variant, root: Node
) -> Dictionary:
	var before: Variant = node.get(property)
	node.set(property, value)
	var after: Variant = node.get(property)
	if not Json.same(after, value):
		var read: String = JSON.stringify(SceneValues.to_json(after, root))
		return {
			"error":
			(
				"Property '%s' on '%s' did not take the value: it read %s after the set."
				% [property, path, read]
			)
		}
	return {"before": SceneValues.to_json(before, root), "after": SceneValues.to_json(after, root)}


static func _read_node(root: Node, query: Dictionary, scene: String) -> Dictionary:
	var path: String = str(query.get("nodePath", ""))
	var found: Dictionary = SceneEdit.node_or_error(root, path, scene)
	if found.has("error"):
		return {"nodePath": path, "error": found["error"]}
	var node: Node = found["node"]
	var names: Variant = _names_to_read(root, node, path, query, scene)
	if names is String:
		return {"nodePath": path, "error": names}
	var entry: Dictionary = {"nodePath": path, "type": node.get_class()}
	var script: Script = node.get_script() as Script
	if script != null:
		entry["script"] = script.resource_path
	var properties: Dictionary = {}
	for property: String in names:
		properties[property] = SceneValues.to_json(node.get(property), root)
	entry["properties"] = properties
	return entry


## The names query reads: its properties list (each checked to exist), else the ones the
## inspector shows (script variables and editor-visible properties); with changedOnly, only those
## the scene file stores, all of them when the list is empty. A String when a property is missing.
static func _names_to_read(
	root: Node, node: Node, path: String, query: Dictionary, scene: String
) -> Variant:
	var wanted: Array = []
	for property: Variant in query.get("properties", []):
		if Json.property_info(node, str(property)).is_empty():
			return "%s has no property %s." % [path, property]
		wanted.append(str(property))
	var names: Array = wanted if not wanted.is_empty() else _shown_names(node)
	if query.get("changedOnly", false) == true:
		var stored: Array = stored_names(scene, String(root.get_path_to(node)))
		names = stored if wanted.is_empty() else wanted.filter(func(n: String): return n in stored)
	return names


static func _shown_names(node: Node) -> Array:
	var names: Array = []
	for info: Dictionary in node.get_property_list():
		if Json.is_shown(info):
			names.append(info.name)
	return names


## Adds to names the properties state stores for node_path, where state is placed at prefix;
## an instanced scene's state first, so the instancing scene's entries land over it.
static func _add_stored(
	state: SceneState, prefix: String, node_path: String, names: Array, depth: int
) -> void:
	for index in state.get_node_count():
		var path: String = ScenePaths.join_node_path(prefix, String(state.get_node_path(index)))
		var instance: PackedScene = state.get_node_instance(index)
		if instance != null and depth < SceneEdit.MAX_BASE_DEPTH and _holds(path, node_path):
			_add_stored(instance.get_state(), path, node_path, names, depth + 1)
		if path == node_path:
			_add_names(state, index, names)


static func _add_names(state: SceneState, index: int, names: Array) -> void:
	for property in state.get_node_property_count(index):
		var property_name: String = String(state.get_node_property_name(index, property))
		if not names.has(property_name):
			names.append(property_name)


## Whether the node at node_path is the one at path or under it.
static func _holds(path: String, node_path: String) -> bool:
	return path == "." or node_path == path or node_path.begins_with(path + "/")
