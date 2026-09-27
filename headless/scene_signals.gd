extends RefCounted
## The headless signal ops scene_ops.gd applies to an open scene: get_node_signals, a read that
## never saves, and connect_signal and disconnect_signal. A scene's connections are its persistent
## ones, those its file saves; a script's own connect() is not one. A connection the scene gets from
## a scene it instances or inherits carries CONNECT_INHERITED and is saved by that scene, so its
## disconnection here would be undone on the next load (measured on 4.7.2); it is refused, naming
## that scene. Godot refuses a second connection of a signal to the same method whatever its binds
## (4.7.2 core/object/object.cpp L1557-1562, L1604: the slot is keyed by the unbound callable), and
## so does connect_signal, first.
##
## The packer drops a connection whose source is inside a non-editable instance (4.7.2
## scene/resources/packed_scene.cpp L1142-1143), so connect_signal refuses that source; a target
## there is saved by its path and found again on load (measured on 4.7.2), so it is allowed. Binds
## convert by the target method's parameters after the signal's own arguments, as call_method
## converts its arguments; an untyped parameter keeps the JSON value, an integral number as an int,
## as the editor's connect dialog saves one.

const SceneEdit := preload("scene_edit.gd")
const ScenePaths := preload("scene_paths.gd")
const SceneValues := preload("scene_values.gd")
const Json := preload("../bridge/godot_mcp_json.gd")
## Object::CONNECT_INHERITED (4.7.2 core/object/object.h, Object::ConnectFlags), which GDScript's
## ConnectFlags do not name: a connection from an instanced or inherited scene, which reads back
## with flags 34 (CONNECT_PERSIST | 32).
const INHERITED_FLAG := 32
## Where each end of a connection is, in the ends connect and disconnect work on: the path as
## given, and the node found there.
const END_KEYS: Array = [["from", "source"], ["to", "target"]]
## get_node_signals' warning for a node with a C# script, by the prep's build state: a C# class
## missing from the assembly lists none of its script's signals.
const BUILD_WARNINGS := {
	"failed": "%s's C# script is not built (the build failed), so its script signals are missing.",
	"skipped":
	(
		"%s's C# script may not be built (prepare never skips the build), "
		+ "so its script signals may be missing."
	),
}


## Reads the signals of the node at params.nodePath: {result: {path, type, signals: [{name, args,
## connections: [{target, method, binds?, inherited?}]}], warning?}}, or {error} for a missing
## node. warning says a C# script's signals are, or may be, missing, while the build failed or
## was skipped.
static func apply_get_node_signals(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var found: Dictionary = SceneEdit.node_or_error(
		root, str(params.get("nodePath", "")), context["scene"]
	)
	if found.has("error"):
		return found
	var node: Node = found["node"]
	var path: String = String(root.get_path_to(node))
	var result: Dictionary = {
		"path": path, "type": node.get_class(), "signals": _signals_of(node, root)
	}
	var warning: String = BUILD_WARNINGS.get(context["build"], "")
	if not warning.is_empty() and not _csharp_script(root, node, context["scene"]).is_empty():
		result["warning"] = warning % path
	return {"result": result}


## Connects params.signal of the node at params.nodePath to params.target's method, persistently,
## with target.binds converted and bound: {result: {from, signal, target, method}}, or {error}
## with nothing changed.
static func apply_connect_signal(root: Node, params: Dictionary, context: Dictionary) -> Dictionary:
	var ends: Dictionary = _ends(root, params, context["scene"])
	if ends.has("error"):
		return ends
	var refusal: String = _connect_refusal(root, ends, context)
	if not refusal.is_empty():
		return {"error": refusal}
	var given: Dictionary = params.get("target", {})
	var bound: Dictionary = _binds(root, ends, given.get("binds", []))
	if bound.has("error"):
		return bound
	var callable := Callable(ends["target"], ends["method"])
	if not (bound["binds"] as Array).is_empty():
		callable = callable.bindv(bound["binds"])
	var error: int = (ends["source"] as Node).connect(ends["signal"], callable, CONNECT_PERSIST)
	if error != OK:
		var facts: Array = _labels(ends) + [error_string(error)]
		return {"error": "%s.%s could not be connected to %s.%s: %s" % facts}
	return {"result": _facts(root, ends)}


## Removes the persistent connection of params.signal of the node at params.nodePath to
## params.target's method, whatever its binds: {result: {from, signal, target, method}}, or
## {error} with nothing changed.
static func apply_disconnect_signal(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var ends: Dictionary = _ends(root, params, context["scene"])
	if ends.has("error"):
		return ends
	var connection: Dictionary = _persistent_connection(ends)
	if connection.is_empty():
		return {"error": "%s.%s is not connected to %s.%s." % _labels(ends)}
	if is_inherited(connection["flags"]):
		var facts: Array = _labels(ends) + [_home(root, ends, context["scene"])]
		return {"error": "%s.%s → %s.%s comes from %s; disconnect it there." % facts}
	(ends["source"] as Node).disconnect(ends["signal"], connection["callable"])
	return {"result": _facts(root, ends)}


## The path of target from root ("." for root itself), or null for anything that is not root or
## a node under it.
static func target_path(root: Node, target: Object) -> Variant:
	if not target is Node:
		return null
	var node: Node = target
	if node != root and not root.is_ancestor_of(node):
		return null
	return String(root.get_path_to(node))


## Whether connection flags mark a connection from an instanced or inherited scene.
static func is_inherited(flags: int) -> bool:
	return flags & INHERITED_FLAG != 0


## The res:// path of node's C# script, or "". A C# script whose class is missing from the
## assembly (unbuilt, or the build failed) is never set on the node, which then has no script
## (measured on 4.7.2), so the script the scene files store for it is read then.
static func _csharp_script(root: Node, node: Node, scene_path: String) -> String:
	var script: Variant = node.get_script()
	if script == null:
		var scene := ResourceLoader.load(scene_path) as PackedScene
		var node_path: String = ScenePaths.normalise_node_path(String(root.get_path_to(node)))
		script = null if scene == null else _stored_script(scene.get_state(), ".", node_path, 0)
	var path: String = (script as Script).resource_path if script is Script else ""
	return path if path.ends_with(".cs") else ""


## The script the scene state placed at prefix stores for the node at node_path, or null: an
## instanced scene's entry first, the instancing scene's laid over it, as a load lays them.
static func _stored_script(
	state: SceneState, prefix: String, node_path: String, depth: int
) -> Variant:
	var script: Variant = null
	for index in state.get_node_count():
		var path: String = ScenePaths.join_node_path(prefix, String(state.get_node_path(index)))
		var instance: PackedScene = state.get_node_instance(index)
		if instance != null and depth < SceneEdit.MAX_BASE_DEPTH and _holds(path, node_path):
			var inner: Variant = _stored_script(instance.get_state(), path, node_path, depth + 1)
			script = script if inner == null else inner
		if path == node_path:
			var own: Variant = _script_property(state, index)
			script = script if own == null else own
	return script


## The script state's node at index stores, or null.
static func _script_property(state: SceneState, index: int) -> Variant:
	for property in state.get_node_property_count(index):
		if state.get_node_property_name(index, property) == &"script":
			return state.get_node_property_value(index, property)
	return null


## Whether the node at node_path is the one at path or under it.
static func _holds(path: String, node_path: String) -> bool:
	return path == "." or node_path == path or node_path.begins_with(path + "/")


## node's signals, each {name, args, connections}.
static func _signals_of(node: Node, root: Node) -> Array:
	var signals: Array = []
	for info: Dictionary in node.get_signal_list():
		var signal_name: String = info["name"]
		var entry: Dictionary = {
			"name": signal_name,
			"args":
			(info["args"] as Array).map(func(arg: Dictionary) -> String: return arg["name"]),
			"connections": _connections(node, signal_name, root),
		}
		signals.append(entry)
	return signals


## The persistent connections of node's signal signal_name, each {target, method, binds?,
## inherited?}.
static func _connections(node: Node, signal_name: String, root: Node) -> Array:
	var listed: Array = []
	for connection: Dictionary in node.get_signal_connection_list(signal_name):
		var flags: int = connection["flags"]
		if flags & CONNECT_PERSIST:
			listed.append(_connection_entry(connection["callable"], flags, root))
	return listed


static func _connection_entry(callable: Callable, flags: int, root: Node) -> Dictionary:
	var entry: Dictionary = {
		"target": target_path(root, callable.get_object()), "method": String(callable.get_method())
	}
	var binds: Array = callable.get_bound_arguments()
	if not binds.is_empty():
		entry["binds"] = SceneValues.to_json(binds, root)
	if is_inherited(flags):
		entry["inherited"] = true
	return entry


## {from, to, signal, method, source, target} for params: the two paths as given, and the nodes
## found there; or {error} for a missing node, the source's first.
static func _ends(root: Node, params: Dictionary, scene: String) -> Dictionary:
	var given: Dictionary = params.get("target", {})
	var ends: Dictionary = {
		"from": str(params.get("nodePath", "")),
		"to": str(given.get("nodePath", "")),
		"signal": str(params.get("signal", "")),
		"method": str(given.get("method", "")),
	}
	for keys: Array in END_KEYS:
		var found: Dictionary = SceneEdit.node_or_error(root, ends[keys[0]], scene)
		if found.has("error"):
			return found
		ends[keys[1]] = found["node"]
	return ends


## Why connect_signal refuses ends, or "": a source inside an instance, whose connection would
## not be saved; a target with no owner, which is not saved; a missing signal; a missing method
## (a C# one missing while the C# build failed says so); a signal already connected to the method.
static func _connect_refusal(root: Node, ends: Dictionary, context: Dictionary) -> String:
	var source: Node = ends["source"]
	var target: Node = ends["target"]
	var refusal: String = _unsaved_end_refusal(root, ends)
	if not refusal.is_empty():
		return refusal
	if not source.has_signal(ends["signal"]):
		return "%s has no signal %s." % [ends["from"], ends["signal"]]
	if not target.has_method(ends["method"]):
		return _missing_method(root, ends, context)
	if source.is_connected(ends["signal"], Callable(target, ends["method"])):
		return "%s.%s is already connected to %s.%s." % _labels(ends)
	return ""


## Why a connection between ends would not be saved, or "": its source inside a non-editable
## instance, or its target a node with no owner.
static func _unsaved_end_refusal(root: Node, ends: Dictionary) -> String:
	var refusal: String = SceneEdit.unsaved_edit_refusal(root, ends["source"], ends["from"])
	var target: Node = ends["target"]
	if refusal.is_empty() and target != root and target.owner == null:
		refusal = SceneEdit.instance_refusal(root, target, ends["to"])
	return refusal


static func _missing_method(root: Node, ends: Dictionary, context: Dictionary) -> String:
	var csharp: bool = not _csharp_script(root, ends["target"], context["scene"]).is_empty()
	var refusal: String = SceneEdit.csharp_refusal(context["scene"], csharp, context)
	return (
		refusal if not refusal.is_empty() else "%s has no method %s." % [ends["to"], ends["method"]]
	)


## {binds}: given converted by the target method's parameters after the signal's own arguments;
## or {error} for a count outside the method's, or a bind that does not convert.
static func _binds(root: Node, ends: Dictionary, given: Array) -> Dictionary:
	var info: Dictionary = _method_info(ends["target"], ends["method"])
	var passed: int = _signal_arg_count(ends["source"], ends["signal"])
	var count_error: String = _count_error(ends, info, passed, given.size())
	if not count_error.is_empty():
		return {"error": count_error}
	var declared: Array = info.get("args", [])
	var binds: Array = []
	for index in given.size():
		var at: int = passed + index
		var parameter: Dictionary = declared[at] if at < declared.size() else {}
		var converted: Array = _bind_from_json(given[index], parameter, root)
		if not converted[0] and converted.size() > 2:
			var reason: Array = [at + 1, ends["method"], ends["to"], converted[2]]
			return {"error": "Argument %d of '%s' on '%s': %s." % reason}
		if not converted[0]:
			var facts: Array = [
				at + 1,
				ends["method"],
				ends["to"],
				Json.type_name(parameter),
				JSON.stringify(given[index]),
			]
			return {
				"error": "Argument %d of '%s' on '%s' is %s; %s does not convert to it." % facts
			}
		binds.append(converted[1])
	return {"binds": binds}


## [true, value] with a bind converted to parameter's type, or [false, null], or [false, null,
## reason] for a type no JSON converts to (an array of Objects). An untyped
## parameter keeps the JSON value, an integral number as an int.
static func _bind_from_json(value: Variant, parameter: Dictionary, root: Node) -> Array:
	var untyped: bool = int(parameter.get("type", TYPE_NIL)) == TYPE_NIL
	if untyped and value is float and value == floorf(value):
		return [true, int(value)]
	return SceneValues.from_json(value, parameter, root)


## Why the signal's passed arguments and bound ones do not fit the method, or "": their sum must
## lie between the method's required and total parameter counts (get_method_argument_count less
## the defaulted ones, as call_method counts), unless the method is vararg.
static func _count_error(ends: Dictionary, info: Dictionary, passed: int, bound: int) -> String:
	if int(info.get("flags", 0)) & METHOD_FLAG_VARARG:
		return ""
	var takes: int = (ends["target"] as Node).get_method_argument_count(ends["method"])
	var required: int = takes - (info.get("default_args", []) as Array).size()
	if passed + bound >= required and passed + bound <= takes:
		return ""
	var range_text: String = str(takes) if required == takes else "%d to %d" % [required, takes]
	var facts: Array = [ends["to"], ends["method"], range_text, ends["signal"], passed, bound]
	return "%s.%s takes %s arguments; %s passes %d and binds %d." % facts


## The method's entry in node's method list, or {}.
static func _method_info(node: Node, method: String) -> Dictionary:
	for info: Dictionary in node.get_method_list():
		if info["name"] == method:
			return info
	return {}


## How many arguments node's signal signal_name passes.
static func _signal_arg_count(node: Node, signal_name: String) -> int:
	for info: Dictionary in node.get_signal_list():
		if info["name"] == signal_name:
			return (info["args"] as Array).size()
	return 0


## The persistent connection from ends' source's signal to its target's method, or {}.
static func _persistent_connection(ends: Dictionary) -> Dictionary:
	var source: Node = ends["source"]
	for connection: Dictionary in source.get_signal_connection_list(ends["signal"]):
		var callable: Callable = connection["callable"]
		var persistent: bool = int(connection["flags"]) & CONNECT_PERSIST != 0
		var same_method: bool = String(callable.get_method()) == ends["method"]
		if persistent and same_method and callable.get_object() == ends["target"]:
			return connection
	return {}


## The scene file that saves the inherited connection in ends: the first scene, from the nearest
## node holding both ends up to root, whose own state lists it.
static func _home(root: Node, ends: Dictionary, scene_path: String) -> String:
	var holder: Node = _holder(ends["source"], ends["target"])
	while holder != null:
		for packed: PackedScene in _scenes_of(holder, root, scene_path):
			if _lists(packed.get_state(), holder, ends):
				return packed.resource_path
		holder = null if holder == root else holder.owner
	return "the scene it is inherited from"


## The nearest node that is or holds both source and target.
static func _holder(source: Node, target: Node) -> Node:
	var holder: Node = source
	while holder != null and holder != target and not holder.is_ancestor_of(target):
		holder = holder.get_parent()
	return holder


## The scenes whose own state holder's nodes come from: the scene's base scenes for root (none
## when it inherits nothing), an instance root's scene and its bases, and none for another node.
static func _scenes_of(holder: Node, root: Node, scene_path: String) -> Array:
	var packed: PackedScene = null
	if holder == root:
		packed = _base_of(ResourceLoader.load(scene_path) as PackedScene)
	elif not holder.scene_file_path.is_empty():
		packed = ResourceLoader.load(holder.scene_file_path) as PackedScene
	var scenes: Array = []
	while packed != null and scenes.size() < SceneEdit.MAX_BASE_DEPTH:
		scenes.append(packed)
		packed = _base_of(packed)
	return scenes


## The scene an inherited scene's root instances, or null.
static func _base_of(scene: PackedScene) -> PackedScene:
	if scene == null or scene.get_state().get_node_count() == 0:
		return null
	return scene.get_state().get_node_instance(0)


## Whether state, a scene placed at holder, lists the connection in ends.
static func _lists(state: SceneState, holder: Node, ends: Dictionary) -> bool:
	var wanted: Array = [
		ScenePaths.normalise_node_path(String(holder.get_path_to(ends["source"]))),
		ends["signal"],
		ScenePaths.normalise_node_path(String(holder.get_path_to(ends["target"]))),
		ends["method"],
	]
	for index in state.get_connection_count():
		var listed: Array = [
			ScenePaths.normalise_node_path(String(state.get_connection_source(index))),
			String(state.get_connection_signal(index)),
			ScenePaths.normalise_node_path(String(state.get_connection_target(index))),
			String(state.get_connection_method(index)),
		]
		if listed == wanted:
			return true
	return false


static func _labels(ends: Dictionary) -> Array:
	return [ends["from"], ends["signal"], ends["to"], ends["method"]]


static func _facts(root: Node, ends: Dictionary) -> Dictionary:
	return {
		"from": String(root.get_path_to(ends["source"])),
		"signal": ends["signal"],
		"target": String(root.get_path_to(ends["target"])),
		"method": ends["method"],
	}
