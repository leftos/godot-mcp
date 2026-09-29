extends Node
## The godot-mcp bridge's inspector, a child of the bridge: lists the scene tree, reads a node's
## properties, sets one, calls a method and captures a subtree's properties, for get_scene_tree,
## inspect_node, set_property, call_method and snapshot_subtree. Each handler returns its result
## as a Dictionary, or a String saying why it failed; the bridge replies with either. It finds
## nodes with the bridge's own _find_node, converts values to and from JSON, finds a property,
## picks the shown ones and compares a read-back with the JSON module the bridge loads
## (godot_mcp_json.gd), and never lists or reaches the bridge's own nodes.
##
## JSON goes in by the declared type, and a set is read back, because Godot does not refuse a
## wrong type: Object.set gives script no validity flag, a native setter given the wrong type
## fails silently (core/object/class_db.cpp L1569-1610 in 4.7.2), a typed GDScript member that
## fails Variant::construct is a silent false (modules/gdscript/gdscript.cpp L1536-1557; an
## untyped Array for an Array[int] member is one), and C# converts a Dictionary to Vector2()
## silently (core/variant/variant.cpp L1745-1761). Every number arrives as a float, since JSON
## numbers always parse to one (core/io/json.cpp L390-396).

## The bridge (godot_mcp_bridge.gd), this node's parent.
var _bridge: Node


func _ready() -> void:
	_bridge = get_parent()


func handle(command: String, params: Dictionary) -> Variant:
	var result: Variant = "unknown inspect command '%s'" % command
	match command:
		"scene_tree":
			result = scene_tree(params)
		"inspect_node":
			result = inspect_node(params)
		"set_property":
			result = set_property(params)
		"call_method":
			result = await call_method(params)
		"snapshot":
			result = snapshot(params)
	return result


## {nodes}: every node under params.root (the tree's root when empty), itself included, depth
## first to params.maxDepth levels below it (every level when negative), kept when it is of
## params.class or a subclass and in params.group, where those are set. The bridge's own
## subtree is left out.
func scene_tree(params: Dictionary) -> Variant:
	var root: Variant = get_tree().root
	var root_name: String = str(params.get("root", ""))
	if not root_name.is_empty():
		root = _resolve(root_name)
		if root is String:
			return root
	var filter: Dictionary = {
		"class": str(params.get("class", "")),
		"group": str(params.get("group", "")),
		"maxDepth": int(params.get("maxDepth", -1)),
	}
	var nodes: Array = []
	_collect(root, 0, filter, nodes)
	return {"nodes": nodes}


func _collect(node: Node, depth: int, filter: Dictionary, into: Array) -> void:
	if node == _bridge:
		return
	if _matches(node, filter):
		into.append(_describe(node))
	var max_depth: int = filter["maxDepth"]
	if max_depth >= 0 and depth >= max_depth:
		return
	for child in node.get_children():
		_collect(child, depth + 1, filter, into)


## Whether node is of filter.class or a subclass and in filter.group, where those are set.
static func _matches(node: Node, filter: Dictionary) -> bool:
	var class_name_filter: String = filter["class"]
	var group: String = filter["group"]
	var matches_class: bool = class_name_filter.is_empty() or node.is_class(class_name_filter)
	return matches_class and (group.is_empty() or node.is_in_group(group))


func _describe(node: Node) -> Dictionary:
	var entry: Dictionary = {
		"path": str(node.get_path()),
		"name": str(node.name),
		"class": node.get_class(),
		"childCount": node.get_child_count(),
	}
	var script_path: String = _script_path(node)
	if not script_path.is_empty():
		entry["script"] = script_path
	var groups: Array = _public_groups(node)
	if not groups.is_empty():
		entry["groups"] = groups
	return entry


## The node's groups, without Godot's internal ones, which start with _.
static func _public_groups(node: Node) -> Array:
	var groups: Array = []
	for group: StringName in node.get_groups():
		if not str(group).begins_with("_"):
			groups.append(str(group))
	return groups


## {node, nodeCount, nodes}: every node of params.node's subtree (the current scene's root when
## params.node is empty), keyed by its path from that node ("." for itself), each with the
## properties the inspector shows (only those of params.properties it has, when given), less
## params.ignore, and its groups as "groups". The bridge's own nodes are left out; a subtree of
## more than params.maxNodes nodes is refused with its count.
func snapshot(params: Dictionary) -> Variant:
	var found: Variant = _snapshot_root(str(params.get("node", "")))
	if found is String:
		return found
	var root: Node = found
	var members: Array[Node] = []
	_subtree(root, members)
	var max_nodes: int = int(params.get("maxNodes", 2000))
	if members.size() > max_nodes:
		return (
			(
				"'%s' has %d nodes in its subtree, more than maxNodes (%d); snapshot a smaller "
				+ "subtree, one of its children, or raise maxNodes."
			)
			% [root.get_path(), members.size(), max_nodes]
		)
	var filter: Dictionary = {
		"only": _names(params.get("properties")), "ignore": _names(params.get("ignore"))
	}
	var nodes: Dictionary = {}
	for member: Node in members:
		nodes[str(root.get_path_to(member))] = _snapshot_node(member, filter)
	return {"node": str(root.get_path()), "nodeCount": members.size(), "nodes": nodes}


## The node a snapshot starts from: node_name resolved as the other tools resolve it, or the
## current scene's root when it is empty; a String saying why there is none.
func _snapshot_root(node_name: String) -> Variant:
	if not node_name.is_empty():
		return _resolve(node_name)
	var scene: Node = get_tree().current_scene
	if scene == null:
		return "The game has no current scene; pass node, a path get_scene_tree lists."
	return scene


## node and every descendant, depth first, into; the bridge's own subtree is skipped.
func _subtree(node: Node, into: Array[Node]) -> void:
	if node == _bridge:
		return
	into.append(node)
	for child: Node in node.get_children():
		_subtree(child, into)


## One node's entry in a snapshot: its shown properties or filter.only's, less filter.ignore,
## with its groups, sorted, as "groups" unless filter.only leaves them out.
func _snapshot_node(node: Node, filter: Dictionary) -> Dictionary:
	var only: Array = filter["only"]
	var properties: Dictionary = (
		_shown_properties(node) if only.is_empty() else _present_properties(node, only)
	)
	if only.is_empty() or only.has("groups"):
		var groups: Array = _public_groups(node)
		groups.sort()
		properties["groups"] = groups
	for property_name: String in filter["ignore"]:
		properties.erase(property_name)
	return properties


## {name: value} for each of names the node has; the others are skipped.
func _present_properties(node: Node, names: Array) -> Dictionary:
	var properties: Dictionary = {}
	for property_name: String in names:
		if _bridge._json.has_property(node, property_name):
			properties[property_name] = _bridge._json.to_json(node.get(property_name))
	return properties


## value's elements as Strings when it is an Array, else none.
static func _names(value: Variant) -> Array:
	var names: Array = []
	if value is Array:
		for element: Variant in value:
			names.append(str(element))
	return names


## {path, class, script?, properties}: params.properties by name, or else the node's script
## variables and the properties the editor's inspector shows.
func inspect_node(params: Dictionary) -> Variant:
	var found: Variant = _resolve(str(params.get("node", "")))
	if found is String:
		return found
	var node: Node = found
	var names: Variant = params.get("properties")
	var properties: Variant
	if names is Array and not (names as Array).is_empty():
		properties = _named_properties(node, names)
	else:
		properties = _shown_properties(node)
	if properties is String:
		return properties
	var result: Dictionary = {
		"path": str(node.get_path()), "class": node.get_class(), "properties": properties
	}
	var script_path: String = _script_path(node)
	if not script_path.is_empty():
		result["script"] = script_path
	return result


## {name: value} for each of names, or a String naming the first property the node lacks.
func _named_properties(node: Node, names: Array) -> Variant:
	var properties: Dictionary = {}
	for property_name: Variant in names:
		if not _bridge._json.has_property(node, str(property_name)):
			return _no_property(node, str(property_name))
		properties[str(property_name)] = _bridge._json.to_json(node.get(str(property_name)))
	return properties


## {name: value} for the node's script variables and the properties the inspector shows.
func _shown_properties(node: Node) -> Dictionary:
	var properties: Dictionary = {}
	for info: Dictionary in node.get_property_list():
		if _bridge._json.is_shown(info):
			properties[info["name"]] = _bridge._json.to_json(node.get(info["name"]))
	return properties


## {path, property, before, after}: params.value converted by the property's declared type (an
## untyped property's by the type of the value it holds, unless that is null), set, and read
## back; a read-back that differs from the converted value puts the old value back and fails.
func set_property(params: Dictionary) -> Variant:
	var found: Variant = _resolve(str(params.get("node", "")))
	if found is String:
		return found
	var node: Node = found
	var property_name: String = str(params.get("property", ""))
	var info: Dictionary = _bridge._json.property_info(node, property_name)
	if info.is_empty():
		if not _bridge._json.has_property(node, property_name):
			return _no_property(node, property_name)
		info = {"type": TYPE_NIL}
	var before: Variant = node.get(property_name)
	if info["type"] == TYPE_NIL and before != null:
		info = {"type": typeof(before)}
	var raw: Variant = params.get("value")
	var converted: Array = _bridge._json.from_json(raw, info)
	if not converted[0]:
		return _not_converted(node, property_name, info, raw, converted)
	node.set(property_name, converted[1])
	var after: Variant = node.get(property_name)
	if not _bridge._json.same(after, converted[1]):
		node.set(property_name, before)
		return (
			(
				"Property '%s' on '%s' did not take the value: it read %s after the set, "
				+ "so it was put back to %s."
			)
			% [property_name, node.get_path(), _json_text(after), _json_text(before)]
		)
	return {
		"path": str(node.get_path()),
		"property": property_name,
		"before": _bridge._json.to_json(before),
		"after": _bridge._json.to_json(after),
	}


## Why set_property's value did not convert: from_json's reason when converted carries one, else
## the property's declared type and the value given.
func _not_converted(
	node: Node, property_name: String, info: Dictionary, raw: Variant, converted: Array
) -> String:
	if converted.size() > 2:
		return "Property '%s' on '%s': %s." % [property_name, node.get_path(), converted[2]]
	return (
		"Property '%s' on '%s' is %s; %s does not convert to it."
		% [property_name, node.get_path(), _bridge._json.type_name(info), JSON.stringify(raw)]
	)


## {path, method, value}: the method called with params.args converted by its parameters' declared
## types, and awaited when it is a coroutine. A call Godot refuses logs "Error calling method from
## 'callv'" and returns null (core/object/object.cpp L750-766 in 4.7.2); the logger locates that
## error at the callv line below, in this file's call_method, and the server fails the call on
## an error located there alone, which reaches it ahead of this reply.
func call_method(params: Dictionary) -> Variant:
	var found: Variant = _resolve(str(params.get("node", "")))
	if found is String:
		return found
	var node: Node = found
	var method: String = str(params.get("method", ""))
	var path: String = str(node.get_path())
	if not node.has_method(method):
		return "Node '%s' has no method '%s'." % [path, method]
	var given: Array = params.get("args") if params.get("args") is Array else []
	var args: Variant = _method_args(node, method, given)
	if args is String:
		return args
	var value: Variant = node.callv(method, args)
	# A GDScript coroutine returns a GDScriptFunctionState at its first await, and awaiting that
	# waits for its completed signal and gives the function's return value
	# (modules/gdscript/gdscript_vm.cpp L2586-2598 in 4.7.2).
	if value is Object and is_instance_valid(value):
		if (value as Object).is_class("GDScriptFunctionState"):
			value = await value
	return {"path": path, "method": method, "value": _bridge._json.to_json(value)}


## The arguments converted by the method's declared parameter types, or a String saying why they
## cannot be.
func _method_args(node: Node, method: String, given: Array) -> Variant:
	var info: Dictionary = _method_info(node, method)
	var count_error: String = _count_error(node, method, info, given.size())
	if not count_error.is_empty():
		return count_error
	var declared: Array = info.get("args", [])
	var args: Array = []
	for index in given.size():
		var parameter: Dictionary = declared[index] if index < declared.size() else {}
		var converted: Array = _bridge._json.from_json(given[index], parameter)
		if not converted[0] and converted.size() > 2:
			var reason: Array = [index + 1, method, node.get_path(), converted[2]]
			return "Argument %d of '%s' on '%s': %s." % reason
		if not converted[0]:
			return (
				"Argument %d of '%s' on '%s' is %s; %s does not convert to it."
				% [
					index + 1,
					method,
					node.get_path(),
					_bridge._json.type_name(parameter),
					JSON.stringify(given[index])
				]
			)
		args.append(converted[1])
	return args


## Why count arguments do not fit the method, or "". The count is checked against
## get_method_argument_count (core/object/object.cpp L655-728 in 4.7.2) less the defaulted
## parameters; a vararg method takes any count.
static func _count_error(node: Node, method: String, info: Dictionary, count: int) -> String:
	if int(info.get("flags", 0)) & METHOD_FLAG_VARARG:
		return ""
	var takes: int = node.get_method_argument_count(method)
	var required: int = takes - (info.get("default_args", []) as Array).size()
	if count >= required and count <= takes:
		return ""
	var range_text: String = str(takes) if required == takes else "%d to %d" % [required, takes]
	return (
		"Method '%s' on '%s' takes %s arguments; %d %s given."
		% [method, node.get_path(), range_text, count, "was" if count == 1 else "were"]
	)


## The node a tool names, or a String saying why there is none to reach: missing, or the bridge
## or a node inside it.
func _resolve(node_name: String) -> Variant:
	var node: Node = _bridge._find_node(node_name)
	if node == null:
		return not_found(node_name, "get_scene_tree lists the nodes' paths")
	if node == _bridge or _bridge.is_ancestor_of(node):
		return (
			"'%s' is part of the godot-mcp bridge, which the inspection tools do not reach."
			% node.get_path()
		)
	return node


## The refusal for a path or bare name that names no node: a bare name was searched for
## everywhere under /root; a path names the base it is read from, the deepest node on it that
## exists, the name that node lacks and up to 10 of its children. hint, the text of the clause
## after the last '; ' and without its period, says where to look instead. The C# helper's
## Targets.NotFound spells the same text.
func not_found(node_name: String, hint: String) -> String:
	var tail: String = "; %s." % hint
	if not node_name.contains("/"):
		return "No node named '%s' anywhere under /root in the running game%s" % [node_name, tail]
	var stop: Array = _deepest_ancestor(node_name)
	var parent: Node = stop[0]
	var from: String = "" if node_name.begins_with("/") else "a path is read from /root, and "
	var where: String = "/" if parent == null else str(parent.get_path())
	var children: String = "root" if parent == null else _child_list(parent)
	return (
		"No node '%s' in the running game: %s%s has no child '%s' (children: %s)%s"
		% [node_name, from, where, stop[1], children, tail]
	)


## [the deepest node on the path that exists, null for /; the name it lacks]
func _deepest_ancestor(node_name: String) -> Array:
	var root: Window = _bridge.get_tree().root
	var segments: PackedStringArray = node_name.split("/", false)
	var first: int = 0
	if node_name.begins_with("/"):
		if segments.is_empty() or segments[0] != str(root.name):
			return [null, "" if segments.is_empty() else segments[0]]
		first = 1
	var parent: Node = root
	for i: int in range(first, segments.size() - 1):
		var next: Node = parent.get_node_or_null(NodePath(segments[i]))
		if next == null:
			return [parent, segments[i]]
		parent = next
	return [parent, segments[segments.size() - 1]]


## The names of up to 10 of the node's children, the bridge left out, and a count of the rest.
func _child_list(parent: Node) -> String:
	var names: PackedStringArray = []
	for child: Node in parent.get_children():
		if child != _bridge:
			names.append(str(child.name))
	if names.is_empty():
		return "none"
	var shown: String = ", ".join(names.slice(0, 10))
	return shown if names.size() <= 10 else "%s (+%d)" % [shown, names.size() - 10]


func _json_text(value: Variant) -> String:
	return JSON.stringify(_bridge._json.to_json(value))


static func _method_info(node: Node, method: String) -> Dictionary:
	for info: Dictionary in node.get_method_list():
		if info["name"] == method:
			return info
	return {}


static func _script_path(node: Node) -> String:
	var node_script := node.get_script() as Script
	return node_script.resource_path if node_script != null else ""


static func _no_property(node: Node, property_name: String) -> String:
	return "Node '%s' has no property '%s'." % [node.get_path(), property_name]
