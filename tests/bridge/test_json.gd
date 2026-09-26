extends "res://gd_test.gd"
## The bridge's JSON conversion (bridge/godot_mcp_json.gd): to_json writing Godot values as JSON,
## and from_json reading JSON back as a declared type, each converter and its refusal. Its
## functions are static, called on the loaded script.

const REFUSED: Array = [false, null]

var _json: GDScript = load_bridge_script("godot_mcp_json.gd")


func test_to_json_keeps_json_values() -> void:
	assert_eq(_json.to_json(null), null, "null")
	assert_eq(_json.to_json(true), true, "a bool")
	assert_eq(_json.to_json(7), 7, "an int")
	assert_eq(_json.to_json(2.5), 2.5, "a float")
	assert_eq(_json.to_json("text"), "text", "a string")


func test_to_json_turns_non_finite_floats_into_strings() -> void:
	assert_eq(_json.to_json(INF), "inf", "infinity")
	assert_eq(_json.to_json(NAN), "nan", "not a number")


func test_to_json_turns_vectors_colours_and_rects_into_objects() -> void:
	assert_eq(_json.to_json(Vector2(1.5, -2)), {"x": 1.5, "y": -2.0}, "a Vector2")
	assert_eq(_json.to_json(Vector2i(3, 4)), {"x": 3, "y": 4}, "a Vector2i")
	assert_eq(_json.to_json(Vector3(1, 2, 3)), {"x": 1.0, "y": 2.0, "z": 3.0}, "a Vector3")
	assert_eq(
		_json.to_json(Color(1, 0.5, 0, 1)), {"r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0}, "a Color"
	)
	assert_eq(
		_json.to_json(Rect2(1, 2, 30, 40)),
		{"x": 1.0, "y": 2.0, "width": 30.0, "height": 40.0},
		"a Rect2"
	)


func test_to_json_converts_containers_recursively() -> void:
	var value: Dictionary = {1: [Vector2i(1, 2), "a"], "packed": PackedInt32Array([5, 6])}
	var expected: Dictionary = {"1": [{"x": 1, "y": 2}, "a"], "packed": [5, 6]}
	assert_eq(_json.to_json(value), expected, "keys become strings, arrays and packed arrays lists")


func test_to_json_describes_objects() -> void:
	var described: Variant = _json.to_json(RefCounted.new())
	assert_true(described is Dictionary, "an object becomes an object")
	if described is Dictionary:
		assert_eq(described["class"], "RefCounted", "with its class")
	assert_eq(_json.to_json(StringName("n")), "n", "anything else becomes its str()")


func test_from_json_takes_any_value_untyped_and_only_a_bool_as_bool() -> void:
	assert_eq(_json.from_json([1, "a"], {}), [true, [1, "a"]], "an entry with no type")
	assert_eq(_json.from_json(null, {"type": TYPE_NIL}), [true, null], "null, untyped")
	assert_eq(_json.from_json(true, {"type": TYPE_BOOL}), [true, true], "a bool")
	assert_eq(_json.from_json(1.0, {"type": TYPE_BOOL}), [false, 1.0], "a number is no bool")
	assert_eq(_json.from_json({}, {"type": TYPE_OBJECT}), REFUSED, "an Object from nothing")


func test_from_json_reads_an_int_from_an_integral_number() -> void:
	var converted: Array = _json.from_json(3.0, {"type": TYPE_INT})
	assert_eq(converted, [true, 3], "a JSON float 3")
	assert_true(converted[1] is int, "as an int")
	assert_eq(_json.from_json(2.5, {"type": TYPE_INT}), REFUSED, "a non-integral float")
	assert_eq(_json.from_json("3", {"type": TYPE_INT}), REFUSED, "a string")


func test_from_json_reads_a_float_from_a_number() -> void:
	var converted: Array = _json.from_json(2, {"type": TYPE_FLOAT})
	assert_eq(converted, [true, 2.0], "an int")
	assert_true(converted[1] is float, "as a float")
	assert_eq(_json.from_json(2.5, {"type": TYPE_FLOAT}), [true, 2.5], "a float")
	assert_eq(_json.from_json("2.5", {"type": TYPE_FLOAT}), REFUSED, "a string")


func test_from_json_reads_text_types_from_a_string() -> void:
	assert_eq(_json.from_json("a", {"type": TYPE_STRING}), [true, "a"], "a String")
	assert_eq(
		_json.from_json("n", {"type": TYPE_STRING_NAME}), [true, StringName("n")], "a StringName"
	)
	assert_eq(
		_json.from_json("A/B", {"type": TYPE_NODE_PATH}), [true, NodePath("A/B")], "a NodePath"
	)
	assert_eq(_json.from_json(5, {"type": TYPE_STRING}), REFUSED, "a number is no String")
	assert_eq(_json.from_json(null, {"type": TYPE_NODE_PATH}), REFUSED, "null is no NodePath")


func test_from_json_reads_vectors_from_their_components() -> void:
	var vector2: Dictionary = {"x": 1.5, "y": -2.0}
	assert_eq(_json.from_json(vector2, {"type": TYPE_VECTOR2}), [true, Vector2(1.5, -2)], "Vector2")
	var vector2i: Dictionary = {"x": 3.0, "y": 4.0}
	assert_eq(
		_json.from_json(vector2i, {"type": TYPE_VECTOR2I}), [true, Vector2i(3, 4)], "a Vector2i"
	)
	var vector3: Dictionary = {"x": 1.0, "y": 2.0, "z": 3.0}
	assert_eq(
		_json.from_json(vector3, {"type": TYPE_VECTOR3}), [true, Vector3(1, 2, 3)], "a Vector3"
	)
	assert_eq(_json.from_json({"x": 1.0}, {"type": TYPE_VECTOR2}), REFUSED, "a missing y")
	assert_eq(
		_json.from_json({"x": 1.5, "y": 2.0}, {"type": TYPE_VECTOR2I}),
		REFUSED,
		"a non-integral component of a Vector2i"
	)
	assert_eq(_json.from_json([1.0, 2.0], {"type": TYPE_VECTOR2}), REFUSED, "an array")
	var rect: Dictionary = {"x": 1.0, "y": 2.0, "width": 30.0, "height": 40.0}
	assert_eq(_json.from_json(rect, {"type": TYPE_RECT2}), [true, Rect2(1, 2, 30, 40)], "a Rect2")
	var rect_i: Dictionary = {"x": 1.0, "y": 2.0, "width": 30.5, "height": 40.0}
	assert_eq(
		_json.from_json(rect_i, {"type": TYPE_RECT2I}),
		REFUSED,
		"a non-integral component of a Rect2i"
	)


func test_from_json_reads_a_color_from_channels_or_html() -> void:
	var rgb: Dictionary = {"r": 1.0, "g": 0.5, "b": 0.0}
	assert_eq(_json.from_json(rgb, {"type": TYPE_COLOR}), [true, Color(1, 0.5, 0, 1)], "a is 1")
	var rgba: Dictionary = {"r": 0.0, "g": 0.0, "b": 1.0, "a": 0.5}
	assert_eq(_json.from_json(rgba, {"type": TYPE_COLOR}), [true, Color(0, 0, 1, 0.5)], "rgba")
	assert_eq(_json.from_json("#ff0000", {"type": TYPE_COLOR}), [true, Color(1, 0, 0, 1)], "html")
	var text_channel: Dictionary = {"r": "1", "g": 0.0, "b": 0.0}
	assert_eq(_json.from_json(text_channel, {"type": TYPE_COLOR}), REFUSED, "a text channel")
	assert_eq(_json.from_json("red?", {"type": TYPE_COLOR}), REFUSED, "a string not html")


func test_from_json_builds_a_typed_array_and_keeps_an_untyped_one() -> void:
	var typed_info: Dictionary = {
		"type": TYPE_ARRAY, "hint": PROPERTY_HINT_ARRAY_TYPE, "hint_string": "int"
	}
	var converted: Array = _json.from_json([1.0, 2.0], typed_info)
	assert_eq(converted, [true, [1, 2]], "an Array[int] from floats")
	assert_true(converted[0] and (converted[1] as Array).get_typed_builtin() == TYPE_INT, "typed")
	assert_eq(_json.from_json([1.5], typed_info), REFUSED, "an element that is no int")
	var untyped: Array = _json.from_json([1.5, "a"], {"type": TYPE_ARRAY})
	assert_eq(untyped, [true, [1.5, "a"]], "an untyped array as it is")
	assert_eq(_json.from_json({}, {"type": TYPE_ARRAY}), REFUSED, "an object is no Array")
	var node_info: Dictionary = {
		"type": TYPE_ARRAY, "hint": PROPERTY_HINT_ARRAY_TYPE, "hint_string": "Node"
	}
	assert_eq(_json.from_json([], node_info), REFUSED, "an Array of objects")


func test_from_json_builds_a_dictionary_with_typed_keys() -> void:
	var info: Dictionary = {
		"type": TYPE_DICTIONARY, "hint": PROPERTY_HINT_DICTIONARY_TYPE, "hint_string": "int;String"
	}
	var converted: Array = _json.from_json({"1": "a", "2.0": "b"}, info)
	assert_eq(converted, [true, {1: "a", 2: "b"}], "numeric keys read from their text")
	if converted[0]:
		var typed: Dictionary = converted[1]
		assert_true(typed.get_typed_key_builtin() == TYPE_INT, "int keys")
		assert_true(typed.get_typed_value_builtin() == TYPE_STRING, "String values")
	assert_eq(_json.from_json({"x": "a"}, info), REFUSED, "a key that is no int")
	assert_eq(_json.from_json({"1": 2.0}, info), REFUSED, "a value that is no String")
	var untyped: Array = _json.from_json({"k": 1.0}, {"type": TYPE_DICTIONARY})
	assert_eq(untyped, [true, {"k": 1.0}], "an untyped dictionary as it is")
	assert_eq(_json.from_json([], {"type": TYPE_DICTIONARY}), REFUSED, "an array is no Dictionary")


func test_from_json_builds_a_packed_array_element_by_element() -> void:
	var points: Array = [{"x": 1.0, "y": 2.0}, {"x": 3.0, "y": 4.0}]
	var converted: Array = _json.from_json(points, {"type": TYPE_PACKED_VECTOR2_ARRAY})
	var expected := PackedVector2Array([Vector2(1, 2), Vector2(3, 4)])
	assert_eq(converted, [true, expected], "a PackedVector2Array")
	assert_true(converted[1] is PackedVector2Array, "packed")
	var broken: Array = [{"x": 1.0}]
	assert_eq(
		_json.from_json(broken, {"type": TYPE_PACKED_VECTOR2_ARRAY}), REFUSED, "a broken element"
	)
	assert_eq(
		_json.from_json({}, {"type": TYPE_PACKED_VECTOR2_ARRAY}), REFUSED, "an object is no array"
	)
	var ints: Array = _json.from_json([1.0, 2.0], {"type": TYPE_PACKED_INT32_ARRAY})
	assert_eq(ints, [true, PackedInt32Array([1, 2])], "a PackedInt32Array from floats")
	assert_true(ints[1] is PackedInt32Array, "packed ints")
	assert_eq(
		_json.from_json([1.5], {"type": TYPE_PACKED_INT32_ARRAY}), REFUSED, "a non-integral int"
	)
