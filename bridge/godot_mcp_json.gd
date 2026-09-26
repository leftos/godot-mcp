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


## A JSON-safe copy of value: vectors, colours and rects become objects, a Node its path, any
## other Object its class and to_string(), containers recursively, anything else its str().
static func to_json(value: Variant) -> Variant:
	var json: Variant
	match typeof(value):
		TYPE_NIL, TYPE_BOOL, TYPE_INT, TYPE_STRING:
			json = value
		TYPE_FLOAT:
			json = value if is_finite(value) else str(value)
		TYPE_DICTIONARY:
			json = _dictionary_to_json(value)
		TYPE_OBJECT:
			json = _object_to_json(value)
		_:
			json = _other_to_json(value)
	return json


## Vectors, colours and rects as objects, arrays and packed arrays as lists, anything else its
## str().
static func _other_to_json(value: Variant) -> Variant:
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
			json = _array_to_json(value) if typeof(value) >= TYPE_ARRAY else str(value)
	return json


static func _dictionary_to_json(values: Dictionary) -> Dictionary:
	var json: Dictionary = {}
	for key: Variant in values:
		json[str(key)] = to_json(values[key])
	return json


static func _array_to_json(values: Variant) -> Array:
	var json: Array = []
	for item: Variant in values:
		json.append(to_json(item))
	return json


static func _object_to_json(value: Variant) -> Variant:
	if not is_instance_valid(value):
		return "<freed object>"
	if value is Node:
		return str((value as Node).get_path())
	return {"class": (value as Object).get_class(), "string": (value as Object).to_string()}


## [true, value] with a JSON value converted to the type a property or parameter entry declares
## ({type, hint, hint_string}), the reverse of to_json, or [false, null] when it does not
## convert; a TYPE_BOOL entry given anything but a bool is refused as [false, value]. TYPE_NIL
## (an untyped parameter, or an untyped property holding null) takes the JSON value as it is; an
## Object converts from nothing.
static func from_json(value: Variant, info: Dictionary) -> Array:
	var type: int = int(info.get("type", TYPE_NIL))
	if type == TYPE_NIL:
		return [true, value]
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


## An Array from a JSON array; a typed one (PROPERTY_HINT_ARRAY_TYPE, whose hint string names the
## element type, @GlobalScope PropertyHint in 4.7.2) with each element converted and the array
## built typed, since an untyped Array does not set an Array[int].
static func _array_from_json(value: Variant, info: Dictionary) -> Array:
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
## each key and value converted and the dictionary built typed, an Object key or value type refused
## as _array_from_json refuses an Object element, since no JSON value converts to an Object. JSON
## keys are strings, so a numeric key is read from its text.
static func _dictionary_from_json(value: Variant, info: Dictionary) -> Array:
	if not value is Dictionary:
		return NOT_CONVERTED
	if int(info.get("hint", PROPERTY_HINT_NONE)) != PROPERTY_HINT_DICTIONARY_TYPE:
		return [true, value]
	var types: PackedStringArray = str(info["hint_string"]).split(";")
	var key_info: Dictionary = _type_info(types[0])
	var value_info: Dictionary = _type_info(types[1] if types.size() > 1 else "Variant")
	if key_info["type"] == TYPE_OBJECT or value_info["type"] == TYPE_OBJECT:
		return NOT_CONVERTED
	var entries: Variant = _convert_entries(value, key_info, value_info)
	if entries == null:
		return NOT_CONVERTED
	var typed := Dictionary(entries, key_info["type"], &"", null, value_info["type"], &"", null)
	return [true, typed]


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


static func _is_number(value: Variant, integral: bool) -> bool:
	if not (value is float or value is int):
		return false
	return not integral or float(value) == floorf(float(value))
