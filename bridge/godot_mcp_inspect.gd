extends Node
## The godot-mcp bridge's inspector, a child of the bridge: lists the scene tree, reads a node's
## properties, sets one and calls a method, for get_scene_tree, inspect_node, set_property and
## call_method. Each handler returns its result as a Dictionary, or a String saying why it
## failed; the bridge replies with either. It finds nodes and writes values as JSON with the
## bridge's own _find_node and _to_json, and never lists or reaches the bridge's own nodes.
##
## JSON goes in by the declared type, and a set is read back, because Godot does not refuse a
## wrong type: Object.set gives script no validity flag, a native setter given the wrong type
## fails silently (core/object/class_db.cpp L1569-1610 in 4.7.2), a typed GDScript member that
## fails Variant::construct is a silent false (modules/gdscript/gdscript.cpp L1536-1557; an
## untyped Array for an Array[int] member is one), and C# converts a Dictionary to Vector2()
## silently (core/variant/variant.cpp L1745-1761). Every number arrives as a float, since JSON
## numbers always parse to one (core/io/json.cpp L390-396).

## The component keys of each vector-like type, in constructor order, as _to_json writes them.
const VECTOR_KEYS := {
	TYPE_VECTOR2: ["x", "y"],
	TYPE_VECTOR2I: ["x", "y"],
	TYPE_VECTOR3: ["x", "y", "z"],
	TYPE_VECTOR3I: ["x", "y", "z"],
	TYPE_VECTOR4: ["x", "y", "z", "w"],
	TYPE_VECTOR4I: ["x", "y", "z", "w"],
	TYPE_RECT2: ["x", "y", "width", "height"],
	TYPE_RECT2I: ["x", "y", "width", "height"],
}
const INTEGER_VECTORS: Array[int] = [TYPE_VECTOR2I, TYPE_VECTOR3I, TYPE_VECTOR4I, TYPE_RECT2I]
## The element type of each packed array.
const PACKED_ELEMENTS := {
	TYPE_PACKED_BYTE_ARRAY: TYPE_INT,
	TYPE_PACKED_INT32_ARRAY: TYPE_INT,
	TYPE_PACKED_INT64_ARRAY: TYPE_INT,
	TYPE_PACKED_FLOAT32_ARRAY: TYPE_FLOAT,
	TYPE_PACKED_FLOAT64_ARRAY: TYPE_FLOAT,
	TYPE_PACKED_STRING_ARRAY: TYPE_STRING,
	TYPE_PACKED_VECTOR2_ARRAY: TYPE_VECTOR2,
	TYPE_PACKED_VECTOR3_ARRAY: TYPE_VECTOR3,
	TYPE_PACKED_COLOR_ARRAY: TYPE_COLOR,
	TYPE_PACKED_VECTOR4_ARRAY: TYPE_VECTOR4,
}
## Property list entries that head a section of the inspector rather than hold a value.
const SECTION_USAGE := PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP
const NOT_CONVERTED: Array = [false, null]

## The bridge (godot_mcp_bridge.gd), this node's parent.
var _bridge: Node
## Each Variant type by its name ("int", "Vector2"), as a typed container's hint string names it.
var _type_by_name: Dictionary = {"Variant": TYPE_NIL}
## The converter from JSON for each Variant type: func(value, info) -> [ok, converted].
var _converters: Dictionary = {}
## The constructor of each vector-like type from its numbers.
var _vector_builders: Dictionary = {}


func _ready() -> void:
	_bridge = get_parent()
	for type in TYPE_MAX:
		_type_by_name[type_string(type)] = type
	_converters = {
		TYPE_NIL: func(value: Variant, _info: Dictionary) -> Array: return [true, value],
		TYPE_BOOL: func(value: Variant, _info: Dictionary) -> Array: return [value is bool, value],
		TYPE_INT: _int_from_json,
		TYPE_FLOAT: _float_from_json,
		TYPE_STRING: _text_from_json,
		TYPE_STRING_NAME: _text_from_json,
		TYPE_NODE_PATH: _text_from_json,
		TYPE_COLOR: _color_from_json,
		TYPE_ARRAY: _array_from_json,
		TYPE_DICTIONARY: _dictionary_from_json,
	}
	for type: int in VECTOR_KEYS:
		_converters[type] = _vector_from_json
	for type: int in PACKED_ELEMENTS:
		_converters[type] = _packed_from_json
	_vector_builders = {
		TYPE_VECTOR2: func(n: Array) -> Variant: return Vector2(n[0], n[1]),
		TYPE_VECTOR2I: func(n: Array) -> Variant: return Vector2i(n[0], n[1]),
		TYPE_VECTOR3: func(n: Array) -> Variant: return Vector3(n[0], n[1], n[2]),
		TYPE_VECTOR3I: func(n: Array) -> Variant: return Vector3i(n[0], n[1], n[2]),
		TYPE_VECTOR4: func(n: Array) -> Variant: return Vector4(n[0], n[1], n[2], n[3]),
		TYPE_VECTOR4I: func(n: Array) -> Variant: return Vector4i(n[0], n[1], n[2], n[3]),
		TYPE_RECT2: func(n: Array) -> Variant: return Rect2(n[0], n[1], n[2], n[3]),
		TYPE_RECT2I: func(n: Array) -> Variant: return Rect2i(n[0], n[1], n[2], n[3]),
	}


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
	var groups: Array = []
	for group: StringName in node.get_groups():
		if not str(group).begins_with("_"):
			groups.append(str(group))
	if not groups.is_empty():
		entry["groups"] = groups
	return entry


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
		if _property_info(node, str(property_name)).is_empty():
			return _no_property(node, str(property_name))
		properties[str(property_name)] = _bridge._to_json(node.get(str(property_name)))
	return properties


## {name: value} for the node's script variables and the properties the inspector shows.
func _shown_properties(node: Node) -> Dictionary:
	var properties: Dictionary = {}
	for info: Dictionary in node.get_property_list():
		if _is_shown(info):
			properties[info["name"]] = _bridge._to_json(node.get(info["name"]))
	return properties


static func _is_shown(info: Dictionary) -> bool:
	var usage: int = info["usage"]
	if usage & SECTION_USAGE:
		return false
	return usage & (PROPERTY_USAGE_SCRIPT_VARIABLE | PROPERTY_USAGE_EDITOR) != 0


## {path, property, before, after}: params.value converted by the property's declared type (an
## untyped property's by the type of the value it holds, unless that is null), set, and read
## back; a read-back that differs from the converted value puts the old value back and fails.
func set_property(params: Dictionary) -> Variant:
	var found: Variant = _resolve(str(params.get("node", "")))
	if found is String:
		return found
	var node: Node = found
	var property_name: String = str(params.get("property", ""))
	var info: Dictionary = _property_info(node, property_name)
	if info.is_empty():
		return _no_property(node, property_name)
	var before: Variant = node.get(property_name)
	if info["type"] == TYPE_NIL and before != null:
		info = {"type": typeof(before)}
	var raw: Variant = params.get("value")
	var converted: Array = from_json(raw, info)
	if not converted[0]:
		return (
			"Property '%s' on '%s' is %s; %s does not convert to it."
			% [property_name, node.get_path(), _type_name(info), JSON.stringify(raw)]
		)
	node.set(property_name, converted[1])
	var after: Variant = node.get(property_name)
	if not _same(after, converted[1]):
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
		"before": _bridge._to_json(before),
		"after": _bridge._to_json(after),
	}


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
	return {"path": path, "method": method, "value": _bridge._to_json(value)}


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
		var converted: Array = from_json(given[index], parameter)
		if not converted[0]:
			return (
				"Argument %d of '%s' on '%s' is %s; %s does not convert to it."
				% [
					index + 1,
					method,
					node.get_path(),
					_type_name(parameter),
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


## [true, value] with a JSON value converted to the type a property or parameter entry declares
## ({type, hint, hint_string}), the reverse of the bridge's _to_json, or [false, null] when it
## does not convert. TYPE_NIL (an untyped parameter, or an untyped property holding null) takes
## the JSON value as it is; an Object converts from nothing.
func from_json(value: Variant, info: Dictionary) -> Array:
	var converter: Callable = _converters.get(int(info.get("type", TYPE_NIL)), _not_converted)
	return converter.call(value, info)


func _not_converted(_value: Variant, _info: Dictionary) -> Array:
	return NOT_CONVERTED


func _int_from_json(value: Variant, _info: Dictionary) -> Array:
	return [true, int(value)] if _is_number(value, true) else NOT_CONVERTED


func _float_from_json(value: Variant, _info: Dictionary) -> Array:
	return [true, float(value)] if _is_number(value, false) else NOT_CONVERTED


func _text_from_json(value: Variant, info: Dictionary) -> Array:
	return [true, type_convert(value, info["type"])] if value is String else NOT_CONVERTED


func _vector_from_json(value: Variant, info: Dictionary) -> Array:
	var type: int = info["type"]
	var keys: Array = VECTOR_KEYS[type]
	var numbers: Array = []
	if value is Dictionary:
		for key: String in keys:
			var number: Variant = (value as Dictionary).get(key)
			if _is_number(number, type in INTEGER_VECTORS):
				numbers.append(int(number) if type in INTEGER_VECTORS else float(number))
	if numbers.size() != keys.size():
		return NOT_CONVERTED
	return [true, (_vector_builders[type] as Callable).call(numbers)]


## A Color from {r, g, b, a?} (a is 1 when left out) or an HTML string such as "#rrggbb[aa]".
func _color_from_json(value: Variant, _info: Dictionary) -> Array:
	if value is String and Color.html_is_valid(value):
		return [true, Color.html(value)]
	if not value is Dictionary:
		return NOT_CONVERTED
	var channels: Dictionary = value
	var alpha: Variant = channels.get("a", 1.0)
	for channel: Variant in [channels.get("r"), channels.get("g"), channels.get("b"), alpha]:
		if not _is_number(channel, false):
			return NOT_CONVERTED
	return [true, Color(channels["r"], channels["g"], channels["b"], alpha)]


## An Array from a JSON array; a typed one (PROPERTY_HINT_ARRAY_TYPE, whose hint string names the
## element type, @GlobalScope PropertyHint in 4.7.2) with each element converted and the array
## built typed, since an untyped Array does not set an Array[int].
func _array_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Array:
		return NOT_CONVERTED
	if int(info.get("hint", PROPERTY_HINT_NONE)) != PROPERTY_HINT_ARRAY_TYPE:
		return [true, value]
	var element: Dictionary = _type_info(str(info["hint_string"]))
	var items: Variant = _convert_all(value, element)
	if items == null or element["type"] == TYPE_OBJECT:
		return NOT_CONVERTED
	return [true, Array(items, element["type"], &"", null)]


## A Dictionary from a JSON object; a typed one (PROPERTY_HINT_DICTIONARY_TYPE, "key;value") with
## each key and value converted and the dictionary built typed. JSON keys are strings, so a
## numeric key is read from its text.
func _dictionary_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Dictionary:
		return NOT_CONVERTED
	if int(info.get("hint", PROPERTY_HINT_NONE)) != PROPERTY_HINT_DICTIONARY_TYPE:
		return [true, value]
	var types: PackedStringArray = str(info["hint_string"]).split(";")
	var key_info: Dictionary = _type_info(types[0])
	var value_info: Dictionary = _type_info(types[1] if types.size() > 1 else "Variant")
	var entries: Variant = _convert_entries(value, key_info, value_info)
	if entries == null:
		return NOT_CONVERTED
	var typed := Dictionary(entries, key_info["type"], &"", null, value_info["type"], &"", null)
	return [true, typed]


## Every key and value converted, or null when one does not convert.
func _convert_entries(values: Dictionary, key_info: Dictionary, value_info: Dictionary) -> Variant:
	var entries: Dictionary = {}
	for key: Variant in values:
		var converted_key: Array = _key_from_json(key, key_info)
		var converted_value: Array = from_json(values[key], value_info)
		if not (converted_key[0] and converted_value[0]):
			return null
		entries[converted_key[1]] = converted_value[1]
	return entries


func _key_from_json(key: Variant, key_info: Dictionary) -> Array:
	var json_key: Variant = key
	if key_info["type"] in [TYPE_INT, TYPE_FLOAT] and str(key).is_valid_float():
		json_key = str(key).to_float()
	return from_json(json_key, key_info)


## A packed array from a JSON array, each element converted by the packed element type.
func _packed_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Array:
		return NOT_CONVERTED
	var items: Variant = _convert_all(value, {"type": PACKED_ELEMENTS[info["type"]]})
	if items == null:
		return NOT_CONVERTED
	return [true, type_convert(items, info["type"])]


## Every value converted by element, or null when one does not convert.
func _convert_all(values: Array, element: Dictionary) -> Variant:
	var items: Array = []
	for item: Variant in values:
		var converted: Array = from_json(item, element)
		if not converted[0]:
			return null
		items.append(converted[1])
	return items


## The entry {type} for a type a typed container's hint string names: a Variant type's name, an
## enum ("Node.ProcessMode", stored as int), or else a class, an Object.
func _type_info(type_name: String) -> Dictionary:
	if _type_by_name.has(type_name):
		return {"type": _type_by_name[type_name]}
	return {"type": TYPE_INT if type_name.contains(".") else TYPE_OBJECT, "class_name": type_name}


## The node a tool names, or a String saying why there is none to reach: missing, or the bridge
## or a node inside it.
func _resolve(node_name: String) -> Variant:
	var node: Node = _bridge._find_node(node_name)
	if node == null:
		return (
			"No node '%s' in the running game; get_scene_tree lists the nodes' paths." % node_name
		)
	if node == _bridge or _bridge.is_ancestor_of(node):
		return (
			"'%s' is part of the godot-mcp bridge, which the inspection tools do not reach."
			% node.get_path()
		)
	return node


func _json_text(value: Variant) -> String:
	return JSON.stringify(_bridge._to_json(value))


static func _is_number(value: Variant, integral: bool) -> bool:
	if not (value is float or value is int):
		return false
	return not integral or float(value) == floorf(float(value))


## Whether the property reads back what was set. A native float property may store 32 bits, so
## a float compares approximately; anything else must match in type and value.
static func _same(after: Variant, value: Variant) -> bool:
	if typeof(after) == TYPE_FLOAT and typeof(value) == TYPE_FLOAT:
		return is_equal_approx(after, value)
	return typeof(after) == typeof(value) and after == value


## The property's entry in the node's property list, or {} when it has none of that name.
static func _property_info(node: Node, property_name: String) -> Dictionary:
	for info: Dictionary in node.get_property_list():
		if info["name"] == property_name and not int(info["usage"]) & SECTION_USAGE:
			return info
	return {}


static func _method_info(node: Node, method: String) -> Dictionary:
	for info: Dictionary in node.get_method_list():
		if info["name"] == method:
			return info
	return {}


## The type a property or parameter entry declares: its class for an object, else the Variant
## type's name.
static func _type_name(info: Dictionary) -> String:
	var type: int = info.get("type", TYPE_NIL)
	var class_title: String = str(info.get("class_name", ""))
	if type == TYPE_OBJECT and not class_title.is_empty():
		return class_title
	return type_string(type)


static func _script_path(node: Node) -> String:
	var node_script := node.get_script() as Script
	return node_script.resource_path if node_script != null else ""


static func _no_property(node: Node, property_name: String) -> String:
	return "Node '%s' has no property '%s'." % [node.get_path(), property_name]
