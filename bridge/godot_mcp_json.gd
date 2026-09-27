extends RefCounted
## The godot-mcp bridge's JSON conversion, static functions only: to_json writes a Godot value as
## JSON, from_json reads JSON back as the type a property or parameter entry declares. The bridge
## loads this script and its modules call it through the bridge; it has no class_name, since the
## bridge is injected into other people's projects, where a global class name could collide.
##
## Every number arrives as a float, since JSON numbers always parse to one (core/io/json.cpp
## L390-396 in 4.7.2).

## The component keys of each vector-like type, in constructor order, as to_json writes them.
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
const NOT_CONVERTED: Array = [false, null]
## Why an array of an Object type is refused, with the element class in place of %s.
const OBJECT_ARRAY_REFUSAL := "arrays of Object types (here Array[%s]) cannot be set from JSON"
## Why a dictionary of an Object key or value type is refused, with the key and value types'
## names (see _side_name) in place of the two %s.
const OBJECT_DICTIONARY_REFUSAL := (
	"dictionaries with Object keys or values (here Dictionary[%s, %s]) " + "cannot be set from JSON"
)
## How deep built-in resources nest inside one another before one is read by its class alone,
## which ends a cycle of resources holding each other.
const MAX_RESOURCE_DEPTH := 8
## What separates a scene's path from a built-in resource's id in that resource's resource_path.
const SUB_RESOURCE_SEPARATOR := "::"
## The stored properties a built-in resource's reading leaves out: its path and id, read apart,
## and its script.
const SKIPPED_RESOURCE_PROPERTIES: Array[String] = [
	"resource_path", "resource_scene_unique_id", "script"
]
## Property list entries that head a section of the inspector rather than hold a value.
const SECTION_USAGE := PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP

## The node a Node's path is read relative to: set by the headless runner to the root of the
## scene it edits, whose nodes are in no scene tree; null in the running game, where a node reads
## as its path in the tree.
static var node_root: Node = null


## A JSON-safe copy of value: vectors, colours and rects become objects, containers are copied
## recursively, and anything else not JSON becomes its str(). A Node becomes its path (see
## _node_to_json), a Resource its path or its properties (see _resource_to_json), and any other
## Object {class, string}, its class and to_string().
static func to_json(value: Variant) -> Variant:
	return _to_json(value, 0)


## to_json at depth, the number of built-in resources value is nested in.
static func _to_json(value: Variant, depth: int) -> Variant:
	var json: Variant
	match typeof(value):
		TYPE_NIL, TYPE_BOOL, TYPE_INT, TYPE_STRING:
			json = value
		TYPE_FLOAT:
			json = value if is_finite(value) else str(value)
		TYPE_DICTIONARY:
			json = _dictionary_to_json(value, depth)
		TYPE_OBJECT:
			json = _object_to_json(value, depth)
		_:
			json = _other_to_json(value, depth)
	return json


## Vectors, colours and rects as objects, arrays and packed arrays as lists, anything else its
## str().
static func _other_to_json(value: Variant, depth: int) -> Variant:
	var json: Variant
	match typeof(value):
		TYPE_VECTOR2, TYPE_VECTOR2I:
			json = {"x": value.x, "y": value.y}
		TYPE_VECTOR3, TYPE_VECTOR3I:
			json = {"x": value.x, "y": value.y, "z": value.z}
		TYPE_COLOR:
			json = {"r": value.r, "g": value.g, "b": value.b, "a": value.a}
		TYPE_RECT2, TYPE_RECT2I:
			json = {
				"x": value.position.x,
				"y": value.position.y,
				"width": value.size.x,
				"height": value.size.y,
			}
		_:
			json = _array_to_json(value, depth) if typeof(value) >= TYPE_ARRAY else str(value)
	return json


static func _dictionary_to_json(values: Dictionary, depth: int) -> Dictionary:
	var json: Dictionary = {}
	for key: Variant in values:
		json[str(key)] = _to_json(values[key], depth)
	return json


static func _array_to_json(values: Variant, depth: int) -> Array:
	var json: Array = []
	for item: Variant in values:
		json.append(_to_json(item, depth))
	return json


## A Node as its path (_node_to_json), a Resource as its path or its properties
## (_resource_to_json), any other Object as {class, string}, and a freed one as "<freed object>".
static func _object_to_json(value: Variant, depth: int) -> Variant:
	if not is_instance_valid(value):
		# A native getter's null Ref is an invalid Object too; only str() tells it from a freed one.
		return null if str(value) == "<Object#null>" else "<freed object>"
	if value is Node:
		return _node_to_json(value)
	if value is Resource:
		return _resource_to_json(value, depth)
	return {"class": (value as Object).get_class(), "string": (value as Object).to_string()}


## With node_root set, the node's path relative to it ("." for the root itself), or null for a
## node outside it. Without, the node's path in the scene tree, or null for a node in none, whose
## get_path() logs an error (scene/main/node.cpp L2431 in 4.7.2).
static func _node_to_json(node: Node) -> Variant:
	if is_instance_valid(node_root):
		if node == node_root or node_root.is_ancestor_of(node):
			return str(node_root.get_path_to(node))
		return null
	return str(node.get_path()) if node.is_inside_tree() else null


## A resource saved in its own file as {resource, uid?, class}: its path, the UID the loader knows
## for it and its class, never its contents. A built-in one (a scene's sub-resource, its path
## "<scene>::<id>") or an unsaved one (no path) as {class, subResource?, properties}: its class,
## the id after "::" when built-in, and its stored properties that differ from the class's
## defaults (a script's properties when not null), resources among them read the same way. A
## built-in resource nested MAX_RESOURCE_DEPTH deep is read without its properties. The class is
## the resource's script class when it has one (_class_title), so the read shape converts back.
static func _resource_to_json(resource: Resource, depth: int) -> Dictionary:
	var path: String = resource.resource_path
	if not path.is_empty() and not path.contains(SUB_RESOURCE_SEPARATOR):
		return _external_resource_to_json(resource)
	var json: Dictionary = {"class": _class_title(resource)}
	if not path.is_empty():
		var id_start: int = path.find(SUB_RESOURCE_SEPARATOR) + SUB_RESOURCE_SEPARATOR.length()
		json["subResource"] = path.substr(id_start)
	if depth < MAX_RESOURCE_DEPTH:
		json["properties"] = _changed_properties(resource, depth + 1)
	return json


static func _external_resource_to_json(resource: Resource) -> Dictionary:
	var path: String = resource.resource_path
	var json: Dictionary = {"resource": path, "class": _class_title(resource)}
	var uid: int = ResourceLoader.get_resource_uid(path)
	if uid != ResourceUID.INVALID_ID:
		json["uid"] = ResourceUID.id_to_text(uid)
	return json


## The class a resource reads out as: the nearest global class name along its script and base
## scripts, or else its native class.
static func _class_title(resource: Resource) -> String:
	var script_classes: PackedStringArray = _script_class_names(resource)
	return script_classes[0] if not script_classes.is_empty() else resource.get_class()


## {name: value} for the resource's stored properties that differ from its class's default, a
## property the class does not declare (a script's) counting as defaulting to null.
static func _changed_properties(resource: Resource, depth: int) -> Dictionary:
	var properties: Dictionary = {}
	var class_title: String = resource.get_class()
	for info: Dictionary in resource.get_property_list():
		var property_name: String = info["name"]
		if not int(info["usage"]) & PROPERTY_USAGE_STORAGE:
			continue
		if property_name in SKIPPED_RESOURCE_PROPERTIES:
			continue
		var value: Variant = resource.get(property_name)
		var default: Variant = ClassDB.class_get_property_default_value(class_title, property_name)
		if typeof(value) != typeof(default) or value != default:
			properties[property_name] = _to_json(value, depth)
	return properties


## [true, value] with a JSON value converted to the type a property or parameter entry declares
## ({type, hint, hint_string}), the reverse of to_json, or [false, null] when it does not
## convert; a TYPE_BOOL entry given anything but a bool is refused as [false, value], an array or
## a dictionary of an Object type as [false, null, reason] (see _array_from_json and
## _dictionary_from_json). TYPE_NIL
## (an untyped parameter, or an untyped property holding null) takes the JSON value as it is. A
## Resource-typed entry (PROPERTY_HINT_RESOURCE_TYPE) converts as _resource_from_json says; any
## other Object converts from nothing.
static func from_json(value: Variant, info: Dictionary) -> Array:
	var type: int = int(info.get("type", TYPE_NIL))
	if type == TYPE_NIL:
		return [true, value]
	if type == TYPE_OBJECT:
		return _resource_from_json(value, info)
	if VECTOR_KEYS.has(type):
		return _vector_from_json(value, info)
	if PACKED_ELEMENTS.has(type):
		return _packed_from_json(value, info)
	return _single_from_json(value, info, type)


## The conversions of the types that are neither vector-like nor packed arrays.
static func _single_from_json(value: Variant, info: Dictionary, type: int) -> Array:
	var converted: Array = NOT_CONVERTED
	match type:
		TYPE_BOOL:
			converted = [value is bool, value]
		TYPE_INT:
			converted = _int_from_json(value)
		TYPE_FLOAT:
			converted = _float_from_json(value)
		TYPE_STRING, TYPE_STRING_NAME, TYPE_NODE_PATH:
			converted = _text_from_json(value, info)
		TYPE_COLOR:
			converted = _color_from_json(value)
		TYPE_ARRAY:
			converted = _array_from_json(value, info)
		TYPE_DICTIONARY:
			converted = _dictionary_from_json(value, info)
	return converted


static func _int_from_json(value: Variant) -> Array:
	return [true, int(value)] if _is_number(value, true) else NOT_CONVERTED


static func _float_from_json(value: Variant) -> Array:
	return [true, float(value)] if _is_number(value, false) else NOT_CONVERTED


static func _text_from_json(value: Variant, info: Dictionary) -> Array:
	return [true, type_convert(value, info["type"])] if value is String else NOT_CONVERTED


static func _vector_from_json(value: Variant, info: Dictionary) -> Array:
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
	return [true, _build_vector(type, numbers)]


## The vector-like value of type from its numbers, in constructor order.
static func _build_vector(type: int, n: Array) -> Variant:
	var built: Variant = null
	match type:
		TYPE_VECTOR2:
			built = Vector2(n[0], n[1])
		TYPE_VECTOR2I:
			built = Vector2i(n[0], n[1])
		TYPE_VECTOR3:
			built = Vector3(n[0], n[1], n[2])
		TYPE_VECTOR3I:
			built = Vector3i(n[0], n[1], n[2])
		TYPE_VECTOR4:
			built = Vector4(n[0], n[1], n[2], n[3])
		TYPE_VECTOR4I:
			built = Vector4i(n[0], n[1], n[2], n[3])
		TYPE_RECT2:
			built = Rect2(n[0], n[1], n[2], n[3])
		TYPE_RECT2I:
			built = Rect2i(n[0], n[1], n[2], n[3])
	return built


## A Color from {r, g, b, a?} (a is 1 when left out) or an HTML string such as "#rrggbb[aa]".
static func _color_from_json(value: Variant) -> Array:
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


## An Array from a JSON array; a typed one (see _element_info) with each element converted by its
## element entry and the array built typed, since an untyped Array does not set an Array[int]. An
## array of an Object type, a Resource one included, is refused as [false, null, reason], since no
## JSON value converts to an Object.
static func _array_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Array:
		return NOT_CONVERTED
	var element: Dictionary = _element_info(info)
	var element_type: int = int(element.get("type", TYPE_NIL))
	if element_type == TYPE_OBJECT:
		return [false, null, OBJECT_ARRAY_REFUSAL % str(element.get("class_name", ""))]
	if element_type == TYPE_NIL:
		return [true, value]
	var items: Variant = _convert_all(value, element)
	if items == null:
		return NOT_CONVERTED
	return [true, Array(items, element_type, &"", null)]


## The entry an array entry declares its elements to be, {type: TYPE_NIL} when untyped. A
## PROPERTY_HINT_ARRAY_TYPE hint (a method parameter or a variable not exported, @GlobalScope
## PropertyHint in 4.7.2) names the element type (see _type_info); an exported array's
## PROPERTY_HINT_TYPE_STRING hint carries its spec (see _spec_info), "24/34:Node2D" for an
## Array[Node2D] (4.7.2 modules/gdscript/gdscript_parser.cpp L4977-4985).
static func _element_info(info: Dictionary) -> Dictionary:
	var hint_string: String = str(info.get("hint_string", ""))
	match int(info.get("hint", PROPERTY_HINT_NONE)):
		PROPERTY_HINT_ARRAY_TYPE:
			return _type_info(hint_string)
		PROPERTY_HINT_TYPE_STRING:
			return _spec_info(hint_string)
	return {"type": TYPE_NIL}


## The entry {type, hint, hint_string} one PROPERTY_HINT_TYPE_STRING spec
## "<type>[/<hint>]:<hint_string>" declares, type and hint as numbers, split at the first ":"
## since an enum's hint string ("Up:0,Down:1") holds more; an Object type's class, its hint
## string, also as class_name, as _type_info gives it. An empty spec is TYPE_NIL, untyped.
static func _spec_info(spec: String) -> Dictionary:
	var colon: int = spec.find(":")
	var head: String = spec if colon < 0 else spec.substr(0, colon)
	var hint_string: String = "" if colon < 0 else spec.substr(colon + 1)
	var type: int = int(head.get_slice("/", 0))
	var hint: int = int(head.get_slice("/", 1)) if head.contains("/") else PROPERTY_HINT_NONE
	var element: Dictionary = {"type": type, "hint": hint, "hint_string": hint_string}
	if type == TYPE_OBJECT:
		element["class_name"] = hint_string
	return element


## A Dictionary from a JSON object; a typed one (see _dictionary_sides) with each key and value
## converted and the dictionary built typed. An Object key or value type, a Resource one included,
## is refused as [false, null, reason], since no JSON value converts to an Object. JSON keys are
## strings, so a numeric key is read from its text.
static func _dictionary_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Dictionary:
		return NOT_CONVERTED
	var sides: Array = _dictionary_sides(info)
	if sides.is_empty():
		return [true, value]
	var key_info: Dictionary = sides[0]
	var value_info: Dictionary = sides[1]
	if key_info["type"] == TYPE_OBJECT or value_info["type"] == TYPE_OBJECT:
		var names: Array = [_side_name(key_info), _side_name(value_info)]
		return [false, null, OBJECT_DICTIONARY_REFUSAL % names]
	var entries: Variant = _convert_entries(value, key_info, value_info)
	if entries == null:
		return NOT_CONVERTED
	var typed := Dictionary(entries, key_info["type"], &"", null, value_info["type"], &"", null)
	return [true, typed]


## [key entry, value entry] a dictionary entry declares, or [] when untyped. A
## PROPERTY_HINT_DICTIONARY_TYPE hint (a method parameter or a variable not exported) reads
## "key;value", each a type's name (see _type_info), the value Variant when left out; an exported
## dictionary's PROPERTY_HINT_TYPE_STRING hint reads "<key spec>;<value spec>", each a spec as
## _spec_info reads it, "0:" (TYPE_NIL) for a Variant side (4.7.2
## modules/gdscript/gdscript_parser.cpp L4866-4947).
static func _dictionary_sides(info: Dictionary) -> Array:
	var hint_string: String = str(info.get("hint_string", ""))
	var split: int = hint_string.find(";")
	var key_spec: String = hint_string if split < 0 else hint_string.substr(0, split)
	var value_spec: String = "" if split < 0 else hint_string.substr(split + 1)
	match int(info.get("hint", PROPERTY_HINT_NONE)):
		PROPERTY_HINT_DICTIONARY_TYPE:
			return [_type_info(key_spec), _type_info("Variant" if split < 0 else value_spec)]
		PROPERTY_HINT_TYPE_STRING:
			return [_spec_info(key_spec), _spec_info(value_spec)]
	return []


## The name a dictionary refusal gives one side's entry: Variant when untyped, else type_name's.
static func _side_name(side: Dictionary) -> String:
	return "Variant" if side["type"] == TYPE_NIL else type_name(side)


## Every key and value converted, or null when one does not convert.
static func _convert_entries(
	values: Dictionary, key_info: Dictionary, value_info: Dictionary
) -> Variant:
	var entries: Dictionary = {}
	for key: Variant in values:
		var converted_key: Array = _key_from_json(key, key_info)
		var converted_value: Array = from_json(values[key], value_info)
		if not (converted_key[0] and converted_value[0]):
			return null
		entries[converted_key[1]] = converted_value[1]
	return entries


static func _key_from_json(key: Variant, key_info: Dictionary) -> Array:
	var json_key: Variant = key
	if key_info["type"] in [TYPE_INT, TYPE_FLOAT] and str(key).is_valid_float():
		json_key = str(key).to_float()
	return from_json(json_key, key_info)


## A packed array from a JSON array, each element converted by the packed element type.
static func _packed_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Array:
		return NOT_CONVERTED
	var items: Variant = _convert_all(value, {"type": PACKED_ELEMENTS[info["type"]]})
	if items == null:
		return NOT_CONVERTED
	return [true, type_convert(items, info["type"])]


## Every value converted by element, or null when one does not convert.
static func _convert_all(values: Array, element: Dictionary) -> Variant:
	var items: Array = []
	for item: Variant in values:
		var converted: Array = from_json(item, element)
		if not converted[0]:
			return null
		items.append(converted[1])
	return items


## The entry {type} for a type a typed container's hint string names: Variant, a Variant type's
## name, an enum ("Node.ProcessMode", stored as int), or else a class, an Object.
static func _type_info(type_name: String) -> Dictionary:
	if type_name == "Variant":
		return {"type": TYPE_NIL}
	for type in TYPE_MAX:
		if type_string(type) == type_name:
			return {"type": type}
	return {"type": TYPE_INT if type_name.contains(".") else TYPE_OBJECT, "class_name": type_name}


## A Resource for a Resource-typed entry, of a class its hint_string names ("A,B"; any Resource
## when empty; a script class by its class_name): null clears it; a "res://" or "uid://" path, or
## to_json's {resource} (its other keys ignored), loads the saved resource; {type, ...} makes a
## new resource of the class type and sets each other key as a property, converted by the new
## resource's own entry for it, so a property named "type" cannot be set in this shape;
## to_json's {class, properties, subResource?} (subResource ignored) makes a new resource of
## class with properties set the same way. The class is a ClassDB class or a script class of the
## project (see _new_script_resource). Anything else, a missing or unloadable path, a class that
## does not fit, an unknown property or a value that does not convert is refused, as is every
## Object entry that is not Resource-typed.
static func _resource_from_json(value: Variant, info: Dictionary) -> Array:
	if int(info.get("hint", PROPERTY_HINT_NONE)) != PROPERTY_HINT_RESOURCE_TYPE:
		return NOT_CONVERTED
	if typeof(value) == TYPE_NIL:
		return [true, null]
	var hint_string: String = str(info.get("hint_string", ""))
	if value is Dictionary:
		return _resource_from_dictionary(value, hint_string)
	return _loaded_resource(value, hint_string)


## A Resource from {resource}, {type, ...} or {class, properties, subResource?}, as
## _resource_from_json says.
static func _resource_from_dictionary(values: Dictionary, hint_string: String) -> Array:
	if values.has("resource"):
		return _loaded_resource(values["resource"], hint_string)
	if values.has("type"):
		var properties: Dictionary = values.duplicate()
		properties.erase("type")
		return _new_resource_from_json(values["type"], properties, hint_string)
	var read_properties: Variant = values.get("properties", {})
	if not (values.has("class") and read_properties is Dictionary):
		return NOT_CONVERTED
	return _new_resource_from_json(values["class"], read_properties, hint_string)


## [true, the resource saved at path] when it loads and fits hint_string.
static func _loaded_resource(path: Variant, hint_string: String) -> Array:
	if not _is_resource_path(path) or not ResourceLoader.exists(path):
		return NOT_CONVERTED
	var resource: Resource = load(path) as Resource
	if resource == null or not _fits_hint(resource, hint_string):
		return NOT_CONVERTED
	return [true, resource]


static func _is_resource_path(path: Variant) -> bool:
	if not path is String:
		return false
	return (path as String).begins_with("res://") or (path as String).begins_with("uid://")


## [true, a new resource] of the class type_name, with properties set on it.
static func _new_resource_from_json(
	type_name: Variant, properties: Dictionary, hint_string: String
) -> Array:
	var resource: Resource = _new_resource(type_name)
	if resource == null:
		return NOT_CONVERTED
	if not _fits_hint(resource, hint_string) or not _set_properties(resource, properties):
		return NOT_CONVERTED
	return [true, resource]


## A new resource of the class type_name, a ClassDB class or else a script class of the project,
## or null when it names no instantiable Resource class.
static func _new_resource(type_name: Variant) -> Resource:
	if not type_name is String:
		return null
	if ClassDB.class_exists(type_name):
		return ClassDB.instantiate(type_name) if _is_resource_class(type_name) else null
	return _new_script_resource(type_name, ProjectSettings.get_global_class_list())


static func _is_resource_class(type_name: String) -> bool:
	return ClassDB.is_parent_class(type_name, "Resource") and ClassDB.can_instantiate(type_name)


## A new resource of the script class type_name, looked up in classes (the entries {class, base,
## path} of ProjectSettings.get_global_class_list()), or null when classes has no such class, its
## script does not instantiate, or its bases, followed through classes to the first ClassDB
## class, do not reach Resource.
static func _new_script_resource(type_name: String, classes: Array) -> Resource:
	var entry: Dictionary = _global_class_entry(type_name, classes)
	if entry.is_empty() or not _extends_resource(entry, classes):
		return null
	var script: Script = load(str(entry.get("path", ""))) as Script
	if script == null or not script.can_instantiate():
		return null
	return script.new() as Resource


static func _global_class_entry(type_name: String, classes: Array) -> Dictionary:
	for entry: Dictionary in classes:
		if str(entry.get("class", "")) == type_name:
			return entry
	return {}


## Whether the script class entry's bases, followed through classes, reach a ClassDB class that
## is a Resource; a chain longer than classes (a cycle) does not.
static func _extends_resource(entry: Dictionary, classes: Array) -> bool:
	var base: String = str(entry.get("base", ""))
	for _step in classes.size() + 1:
		if ClassDB.class_exists(base):
			return ClassDB.is_parent_class(base, "Resource")
		base = str(_global_class_entry(base, classes).get("base", ""))
	return false


## Whether every key of properties names a property of resource and converts to it; each that
## does is set.
static func _set_properties(resource: Resource, properties: Dictionary) -> bool:
	for key: Variant in properties:
		var info: Dictionary = property_info(resource, str(key))
		if info.is_empty():
			return false
		var converted: Array = from_json(properties[key], info)
		if not converted[0]:
			return false
		resource.set(str(key), converted[1])
	return true


## The property's entry in the object's property list, or {} when it has none of that name.
static func property_info(object: Object, property_name: String) -> Dictionary:
	for info: Dictionary in object.get_property_list():
		if info["name"] == property_name and not int(info["usage"]) & SECTION_USAGE:
			return info
	return {}


## Whether a property-list entry is one the inspector shows: a script variable or an editor
## property, not a section heading.
static func is_shown(info: Dictionary) -> bool:
	var usage: int = info["usage"]
	if usage & SECTION_USAGE:
		return false
	return usage & (PROPERTY_USAGE_SCRIPT_VARIABLE | PROPERTY_USAGE_EDITOR) != 0


## Whether a property reads back what was set. A native float property may store 32 bits, so
## a float compares approximately; anything else must match in type and value. A String and a
## StringName compare by their text, since Godot declares some properties as one and reads them
## back as the other. A Packed array and an Array, or two kinds of Packed array, compare element
## by element by this same rule, since Godot declares some properties as a Packed array and reads
## them back as an Array.
static func same(after: Variant, value: Variant) -> bool:
	if typeof(value) == TYPE_NIL:
		return _is_null(after)
	if typeof(after) == TYPE_FLOAT and typeof(value) == TYPE_FLOAT:
		return is_equal_approx(after, value)
	if _both_text(after, value):
		return after == value
	if _both_arrays(after, value):
		return _same_elements(after, value)
	return typeof(after) == typeof(value) and after == value


## Whether a read-back value is null. A cleared native Object property reads back as a null Ref,
## which is TYPE_OBJECT, not TYPE_NIL.
static func _is_null(after: Variant) -> bool:
	return typeof(after) == TYPE_NIL or (typeof(after) == TYPE_OBJECT and after == null)


## Whether both values are text, a type pair a property's declared kind and its read-back kind
## may differ by: a String and a StringName.
static func _both_text(after: Variant, value: Variant) -> bool:
	var kinds: Array[int] = [TYPE_STRING, TYPE_STRING_NAME]
	return typeof(after) in kinds and typeof(value) in kinds


## Whether both values are arrays of differing kinds, the other type pair a property's declared
## kind and its read-back kind may differ by: a Packed array and an Array, or two Packed arrays.
static func _both_arrays(after: Variant, value: Variant) -> bool:
	return typeof(after) != typeof(value) and _is_array(after) and _is_array(value)


## Whether the value is an Array or a Packed array.
static func _is_array(value: Variant) -> bool:
	return typeof(value) == TYPE_ARRAY or PACKED_ELEMENTS.has(typeof(value))


## Whether two array-likes have the same size and every pair of elements is the same.
static func _same_elements(after: Variant, value: Variant) -> bool:
	if after.size() != value.size():
		return false
	for index in after.size():
		if not same(after[index], value[index]):
			return false
	return true


## The type a property or parameter entry declares: its class for an object, else the Variant
## type's name.
static func type_name(info: Dictionary) -> String:
	var type: int = info.get("type", TYPE_NIL)
	var class_title: String = str(info.get("class_name", ""))
	if type == TYPE_OBJECT and not class_title.is_empty():
		return class_title
	return type_string(type)


## Whether the resource is of one of the classes hint_string lists ("A,B"), a native class or the
## class_name of its script or of one of that script's bases, or hint_string is empty.
static func _fits_hint(resource: Resource, hint_string: String) -> bool:
	if hint_string.is_empty():
		return true
	var script_classes: PackedStringArray = _script_class_names(resource)
	for class_title: String in hint_string.split(","):
		var wanted: String = class_title.strip_edges()
		if resource.is_class(wanted) or wanted in script_classes:
			return true
	return false


## The global class names of the resource's script and its base scripts, nearest first.
static func _script_class_names(resource: Resource) -> PackedStringArray:
	var names: PackedStringArray = []
	var script: Script = resource.get_script() as Script
	while script != null:
		var global_name: String = str(script.get_global_name())
		if not global_name.is_empty():
			names.append(global_name)
		script = script.get_base_script()
	return names


static func _is_number(value: Variant, integral: bool) -> bool:
	if not (value is float or value is int):
		return false
	return not integral or float(value) == floorf(float(value))
