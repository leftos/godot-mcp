extends RefCounted
## The headless property edits scene_ops.gd applies to an open scene: add_node,
## set_node_properties and get_node_properties. Values convert through scene_values.gd (a node as
## its path from the scene's root), and the JSON module (bridge/godot_mcp_json.gd) supplies the
## rules these share with the running game's inspector: which properties a read shows, how a
## property is found, and how a read-back is compared with the value set. Godot does not refuse a
## wrong type (see bridge/godot_mcp_inspect.gd's header), so every set is read back.

const SceneEdit := preload("scene_edit.gd")
const SceneFiles := preload("scene_files.gd")
const SceneNodes := preload("scene_nodes.gd")
const ScenePaths := preload("scene_paths.gd")
const SceneValues := preload("scene_values.gd")
const Json := preload("../bridge/godot_mcp_json.gd")
const SCENE_PREFIX := "res://"
## The Control properties the editor shows only in Anchors mode (_anchor_refusal).
const ANCHOR_PROPERTIES: PackedStringArray = [
	"anchor_left", "anchor_top", "anchor_right", "anchor_bottom"
]
## The Control properties Godot saves no value of under a Container, which places the node itself
## (scene/gui/control.cpp L568-571): _container_refusal.
const CONTAINER_PLACED_PROPERTIES: PackedStringArray = [
	"anchor_left",
	"anchor_top",
	"anchor_right",
	"anchor_bottom",
	"offset_left",
	"offset_top",
	"offset_right",
	"offset_bottom",
	"grow_horizontal",
	"grow_vertical",
	"anchors_preset",
]
## Why a property path through a resource file is refused (_not_owned): the path that reaches it,
## its file, the path's first name, its class and its file again.
const EXTERNAL_RESOURCE := (
	"%s is %s, a resource file this scene only references; set %s to a "
	+ '{type: "%s", ...} value to give the node its own, or edit %s'
)
## Why a property path through a base or instanced scene's built-in resource is refused: the path
## that reaches it, that scene's file and the path's first name.
const BASE_RESOURCE := "%s belongs to the base scene %s; set %s whole to override it"
## Why a property path ending in a resource's resource_path is refused: the path.
const RESOURCE_PATH_LEAF := "%s names a resource's file path, which a scene edit cannot change"
## Why a property path through a node is refused: the path that reaches the node.
const NODE_IN_PATH := "%s holds a node; set the property on that node's own path"


## Adds a node of params.nodeType named params.nodeName under params.parent, at params.position
## among its children (SceneNodes.resolve_position) or last, with params.properties set on it once
## it is under its parent, as the editor sets them (layout_mode first, since Godot computes its
## value from the parent): {result: {path, type, index, instance?, script?}}, or {error} naming
## every failing property or the bad position, with nothing added. The new node is owned by the
## scene's root; an instanced scene's own nodes keep the instance's owners.
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
	parent.add_child(node)
	var properties: Variant = params.get("properties", {})
	var failures: PackedStringArray = _set_all(
		node,
		_child_path(root, parent, node_name),
		properties if properties is Dictionary else {},
		root,
		"layout_mode"
	)
	if not failures.is_empty():
		parent.remove_child(node)
		node.free()
		return {"error": " ".join(failures)}
	node.owner = root
	SceneNodes.note_created(node, context)
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
## {result: {results: [{nodePath, property, before, after}]}}, in the updates' order, or {error}
## naming every failing entry of the first phase that fails (nothing is saved then). The updates
## run in phases (_phases): every entry of a phase is found and converted before any is set, so a
## path is planned only once the updates that set its head or another prefix of it have been set.
static func apply_set_node_properties(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var updates: Array = params.get("updates", [])
	var results: Array = []
	results.resize(updates.size())
	for phase: Array in _phases(updates):
		var failures: PackedStringArray = _apply_phase(root, updates, phase, context, results)
		if not failures.is_empty():
			return {"error": " ".join(failures)}
	return {"result": {"results": results}}


## The indices of updates in the order of the phases they run in: first every update no other
## update of the same node sets a prefix of (its head, or more of its path), then the rest by how
## many names their paths hold, fewest first, each count a phase of its own.
static func _phases(updates: Array) -> Array:
	var by_depth: Dictionary = {}
	for index in updates.size():
		var depth: int = 1 if _is_independent(updates, index) else _depth(updates[index])
		if not by_depth.has(depth):
			by_depth[depth] = []
		(by_depth[depth] as Array).append(index)
	var depths: Array = by_depth.keys()
	depths.sort()
	return depths.map(func(depth: int) -> Array: return by_depth[depth])


## Whether no other update of updates[index]'s node sets a strict prefix of its property path.
static func _is_independent(updates: Array, index: int) -> bool:
	var update: Dictionary = updates[index]
	var property: String = str(update.get("property", ""))
	for other: Dictionary in updates:
		var same_node: bool = str(other.get("nodePath", "")) == str(update.get("nodePath", ""))
		if same_node and property.begins_with(str(other.get("property", "")) + ":"):
			return false
	return true


## How many names update's property path holds, counted from 2, since an update that depends on
## another has a path of two names at least.
static func _depth(update: Dictionary) -> int:
	return maxi(2, str(update.get("property", "")).get_slice_count(":"))


## Plans the updates at phase's indices, then, when all plan, sets each and puts its result at its
## index of results: the failures, empty when every one took.
static func _apply_phase(
	root: Node, updates: Array, phase: Array, context: Dictionary, results: Array
) -> PackedStringArray:
	var planned: Array = []
	var failures: PackedStringArray = []
	for index: int in phase:
		var plan: Dictionary = _plan_update(root, updates[index], context["scene"])
		if plan.has("error"):
			failures.append(plan.error)
		else:
			plan["index"] = index
			planned.append(plan)
	if not failures.is_empty():
		return failures
	for plan: Dictionary in planned:
		var done: Dictionary = _set_checked(
			plan.node, plan.nodePath, plan.property, plan.value, root
		)
		if done.has("error"):
			failures.append(done.error)
			continue
		results[plan.index] = {
			"nodePath": plan.nodePath,
			"property": plan.property,
			"before": done.before,
			"after": done.after,
		}
	return failures


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
		return {"error": SceneFiles.load_failure(path, _not_a_node_type(path))}
	if _contains_scene(packed, scene, 0):
		return {"error": "%s is or instances %s, which cannot contain itself." % [path, scene]}
	var node: Node = SceneEdit.instantiate_native(packed, PackedScene.GEN_EDIT_STATE_INSTANCE)
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
## the ones that fail do. first, when properties holds it, is set before the rest: add_node sets
## layout_mode first, Godot computing its value from the node's parent.
static func _set_all(
	node: Node, path: String, properties: Dictionary, root: Node, first: String
) -> PackedStringArray:
	var failures: PackedStringArray = []
	for key: Variant in _key_order(properties, first):
		var converted: Dictionary = _converted(node, path, str(key), properties[key], root)
		if converted.has("error"):
			failures.append(converted.error)
			continue
		var done: Dictionary = _set_checked(node, path, str(key), converted.value, root)
		if done.has("error"):
			failures.append(done.error)
	return failures


## properties' keys, first (when properties holds it) taken out and put in front.
static func _key_order(properties: Dictionary, first: String) -> Array:
	var keys: Array = properties.keys()
	if keys.has(first):
		keys.erase(first)
		keys.push_front(first)
	return keys


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
	var resolved: Dictionary = _resolved(node, path, property, root)
	if resolved.has("error"):
		return resolved
	var info: Dictionary = resolved.info
	var held: Variant = _get_prop(node, property)
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


## What property, a name or a path (Json.resolve_path), reaches on node: resolve_path's
## {holder, property, info, objects}, or {error} for a name that names nothing, a path into a
## value no path reaches into, or a path through an object this scene does not own
## (_foreign_object).
static func _resolved(node: Node, path: String, property: String, root: Node) -> Dictionary:
	var resolved: Dictionary = Json.resolve_path(node, property, true)
	if resolved.has("missing"):
		return {"error": "%s has no property %s." % [path, resolved.missing]}
	var refusal: String = resolved.get("error", "")
	if refusal.is_empty() and resolved.holder is Resource and resolved.property == "resource_path":
		refusal = RESOURCE_PATH_LEAF % property
	if refusal.is_empty():
		refusal = _foreign_object(root, property, resolved.objects)
	if not refusal.is_empty():
		return {"error": "Property '%s' on '%s': %s." % [property, path, refusal]}
	return resolved


## Why a set through objects ([{path, object}], the Objects a property path passes through) would
## not be saved with the scene, the first object's reason: "" when the scene owns every one.
static func _foreign_object(root: Node, property: String, objects: Array) -> String:
	var first: String = property.get_slice(":", 0)
	for passed: Dictionary in objects:
		var refusal: String = _not_owned(root, first, passed.path, passed.object)
		if not refusal.is_empty():
			return refusal
	return ""


## Why object, reached by the path reached whose first name is first, is not this scene's to change:
## a node (its own path sets it, where the instance rules apply); a resource saved in its own
## file, which the scene saver writes as an ExtResource and never saves (4.7.2
## scene/resources/resource_format_text.cpp L1661-1672); or a built-in resource of another scene
## (its resource_path is that scene's path, "::" and its id: resource_format_text.cpp L569,
## L631-636), which this scene's node holds from its base or instanced scene and whose change
## pack would not store, the node holding the base's own object (packed_scene.cpp L980-996). A
## resource with no path (one made in this edit, or a local-to-scene copy of a base or instanced
## scene's resource, which saves as an override of the whole resource) and a built-in one of the
## scene at root's scene_file_path (packed_scene.cpp L2521-2522) are the scene's: "".
static func _not_owned(root: Node, first: String, reached: String, object: Object) -> String:
	if object is Node:
		return NODE_IN_PATH % reached
	var resource := object as Resource
	if resource == null or resource.resource_path.is_empty():
		return ""
	var file: String = resource.resource_path.get_slice(Json.SUB_RESOURCE_SEPARATOR, 0)
	if not resource.resource_path.contains(Json.SUB_RESOURCE_SEPARATOR):
		return EXTERNAL_RESOURCE % [reached, file, reached, resource.get_class(), file]
	if _same_file(file, root.scene_file_path):
		return ""
	return BASE_RESOURCE % [reached, file, first]


## Sets value and reads it back: {before, after} as JSON, or {error} when the read-back differs by
## the inspector's rule.
static func _set_checked(
	node: Node, path: String, property: String, value: Variant, root: Node
) -> Dictionary:
	var refusal: String = _anchor_refusal(node, path, property, value)
	if not refusal.is_empty():
		return {"error": refusal}
	var before: Variant = _get_prop(node, property)
	_set_prop(node, property, value)
	var after: Variant = _get_prop(node, property)
	if not Json.same_set(after, value):
		var read: String = JSON.stringify(SceneValues.to_json(after, root))
		var failed: String = (
			"Property '%s' on '%s' did not take the value: it read %s after the set."
			% [property, path, read]
		)
		return {"error": failed + _layout_hint(node, property)}
	return {"before": SceneValues.to_json(before, root), "after": SceneValues.to_json(after, root)}


## The value of property on node: a property path (Json.resolve_path) as _get_path reads it; an
## engine property of the node's class through ClassDB, so a script member of the same name (a C#
## field) does not hide it and the read agrees with what a save stores (SceneEdit.pack_native);
## any other property through node.get.
static func _get_prop(node: Node, property: String) -> Variant:
	if property.contains(":"):
		return _get_path(node, property)
	if _is_engine_property(node, property):
		return ClassDB.class_get_property(node, property)
	return node.get(property)


## Sets property on node to value, a property path as _set_path sets it, an engine property of the
## node's class through ClassDB and any other through node.set, as _get_prop reads it.
static func _set_prop(node: Node, property: String, value: Variant) -> void:
	if property.contains(":"):
		_set_path(node, property, value)
	elif _is_engine_property(node, property):
		ClassDB.class_set_property(node, property, value)
	else:
		node.set(property, value)


## A property path's value on node: its head read as _get_prop reads a name, so a script member
## of the same name does not hide an engine property (get_indexed asks the script instance first:
## core/object/object.cpp L460 in 4.7.2), then the rest read on the Object the head holds, or the
## one component of the value it holds.
static func _get_path(node: Node, property: String) -> Variant:
	var head: String = property.get_slice(":", 0)
	var rest: String = property.substr(head.length() + 1)
	var held: Variant = _get_prop(node, head)
	if held is Object:
		return (held as Object).get_indexed(NodePath(rest))
	return Json.value_member(held, rest)[1]


## Sets a property path on node to value, as _get_path reads it: the rest set on the Object the
## head holds, or the component changed on a copy of the value it holds and the copy set back.
static func _set_path(node: Node, property: String, value: Variant) -> void:
	var head: String = property.get_slice(":", 0)
	var rest: String = property.substr(head.length() + 1)
	var held: Variant = _get_prop(node, head)
	if held is Object:
		(held as Object).set_indexed(NodePath(rest), value)
		return
	var changed: Variant = held
	changed[rest] = value
	_set_prop(node, head, changed)


## Whether property is one of the properties node's native class declares, other than script.
static func _is_engine_property(node: Node, property: String) -> bool:
	return Json.is_native_property(node, property)


## Why a Control's layout_mode or anchors_preset did not take, appended to the read-back failure:
## "" for any other property or node. Godot computes layout_mode from the node's parent
## (scene/gui/control.cpp _get_layout_mode), and anchors_preset takes only in Anchors mode.
static func _layout_hint(node: Node, property: String) -> String:
	if not (node is Control):
		return ""
	if property == "anchors_preset":
		return " anchors_preset takes only with layout_mode 1 (Anchors); set layout_mode 1 first."
	if property != "layout_mode":
		return ""
	var parent: Node = node.get_parent()
	if parent is Container:
		return " Under a Container, layout_mode is 2 (Container) and cannot be set."
	if parent is Control:
		return " Under a Control, layout_mode takes 0 (Position) or 1 (Anchors)."
	return " With no Control parent, layout_mode is 3 (Uncontrolled) and cannot be set."


## Why setting a placement property on a Control is refused: "" when it takes. Under a Container,
## _container_refusal decides. Under any other Control, the editor shows anchor_* only in Anchors
## mode, so a Control the anchors would leave in Position mode saves as layout_mode = 0 beside
## anchor_right = 1.0: a node the editor cannot make (scene/gui/control.cpp). A zero anchor, or
## the value the anchor already holds, changes nothing and takes.
static func _anchor_refusal(node: Node, path: String, property: String, value: Variant) -> String:
	if not (node is Control):
		return ""
	var parent: Node = node.get_parent()
	if parent is Container:
		return _container_refusal(node, path, property, value)
	if not (property in ANCHOR_PROPERTIES) or not (parent is Control):
		return ""
	if ClassDB.class_get_property(node, "layout_mode") != 0:
		return ""
	if is_zero_approx(float(value)) or Json.same_set(_get_prop(node, property), value):
		return ""
	var message: String = (
		"Property '%s' on '%s' was refused: %s takes only with layout_mode 1 (Anchors), the mode "
		+ "the editor stores anchors in; set layout_mode 1 first (a full rect is layout_mode 1, "
		+ "anchors_preset 15, grow_horizontal/grow_vertical 2)."
	)
	return message % [property, path, property]


## Why setting property on node, a child of a Container, is refused: "" when it takes. The
## container places the node, and Godot strips the storage of its anchors, offsets, grow
## directions and anchors preset (scene/gui/control.cpp L568-571), so such a set would read back
## and be lost at save. The value the node already holds changes nothing and takes.
static func _container_refusal(
	node: Node, path: String, property: String, value: Variant
) -> String:
	if not (property in CONTAINER_PLACED_PROPERTIES):
		return ""
	if Json.same_set(_get_prop(node, property), value):
		return ""
	var message: String = (
		"Property '%s' on '%s' was refused: under a Container, layout_mode is 2 (Container) and "
		+ "Godot saves no anchor_*, offset_*, grow_* or anchors_preset; the container places the "
		+ "node (set size_flags_horizontal/size_flags_vertical instead)."
	)
	return message % [property, path]


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
		properties[property] = SceneValues.to_json(_get_prop(node, property), root)
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
		var resolved: Dictionary = Json.resolve_path(node, str(property), true)
		if resolved.has("missing"):
			return "%s has no property %s." % [path, resolved.missing]
		if resolved.has("error"):
			return "Property '%s' on '%s': %s." % [property, path, resolved.error]
		wanted.append(str(property))
	var names: Array = wanted if not wanted.is_empty() else _shown_names(node)
	if query.get("changedOnly", false) == true:
		var stored: Array = stored_names(scene, String(root.get_path_to(node)))
		names = (
			stored
			if wanted.is_empty()
			else wanted.filter(func(n: String): return n.get_slice(":", 0) in stored)
		)
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
