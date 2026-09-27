# gdlint: disable=max-public-methods, private-method-call
# Each test is a public method the runner finds by its test_ prefix, so the count is the coverage;
# the script-class tests call the module's private helpers, which take a class list or a hint.
extends "res://gd_test.gd"
## The bridge's JSON conversion (bridge/godot_mcp_json.gd): to_json writing Godot values as JSON,
## and from_json reading JSON back as a declared type, each converter and its refusal. Its
## functions are static, called on the loaded script.

const REFUSED: Array = [false, null]
## A saved resource of this project, a script, for the Resource conversions.
const SCRIPT_PATH := "res://gd_test.gd"
## A script-class Resource of this project, loaded by path.
const TEST_RESOURCE_PATH := "res://json_test_resource.gd"
const SCRIPT_INFO: Dictionary = {
	"type": TYPE_OBJECT, "hint": PROPERTY_HINT_RESOURCE_TYPE, "hint_string": "Script"
}

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
	var refusal: String = "arrays of Object types (here Array[Node]) cannot be set from JSON"
	assert_eq(_json.from_json([], node_info), [false, null, refusal], "an Array of objects")


func test_from_json_refuses_an_exported_array_of_objects() -> void:
	# An exported Array[Node2D]'s entry, as 4.7.2 gdscript_parser.cpp L4977-4985 writes it.
	var exported: Dictionary = {
		"type": TYPE_ARRAY,
		"hint": PROPERTY_HINT_TYPE_STRING,
		"hint_string": "%d/%d:Node2D" % [TYPE_OBJECT, PROPERTY_HINT_NODE_TYPE],
	}
	var refusal: String = "arrays of Object types (here Array[Node2D]) cannot be set from JSON"
	assert_eq(_json.from_json(["T"], exported), [false, null, refusal], "an exported Array[Node2D]")


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


func test_from_json_refuses_a_dictionary_of_objects() -> void:
	var node_value: Dictionary = {
		"type": TYPE_DICTIONARY,
		"hint": PROPERTY_HINT_DICTIONARY_TYPE,
		"hint_string": "int;Node",
	}
	var node_key: Dictionary = {
		"type": TYPE_DICTIONARY,
		"hint": PROPERTY_HINT_DICTIONARY_TYPE,
		"hint_string": "Node;int",
	}
	var value_refusal: Array = [false, null, _dictionary_refusal("int", "Node")]
	var key_refusal: Array = [false, null, _dictionary_refusal("Node", "int")]
	assert_eq(_json.from_json({"1": null}, node_value), value_refusal, "a Node value type")
	assert_eq(_json.from_json({}, node_value), value_refusal, "an empty object, a Node value type")
	assert_eq(_json.from_json({"1": null}, node_key), key_refusal, "a Node key type")
	assert_eq(_json.from_json({}, node_key), key_refusal, "an empty object, a Node key type")


func test_exported_int_array_converts_typed() -> void:
	var converted: Array = _json.from_json([1.0, 2.0], _exported(TYPE_ARRAY, "2:"))
	assert_eq(converted, [true, [1, 2]], "an exported Array[int] from floats")
	if converted[0]:
		var typed: Array = converted[1]
		assert_true(typed.is_typed() and typed.get_typed_builtin() == TYPE_INT, "typed int")
	assert_eq(_json.from_json([1.5], _exported(TYPE_ARRAY, "2:")), REFUSED, "an element no int")


func test_exported_vector2_array_converts_typed() -> void:
	var points: Array = [{"x": 1.0, "y": 2.0}, {"x": 3.0, "y": 4.0}]
	var converted: Array = _json.from_json(points, _exported(TYPE_ARRAY, "5:"))
	assert_eq(converted, [true, [Vector2(1, 2), Vector2(3, 4)]], "an exported Array[Vector2]")
	if converted[0]:
		var typed: Array = converted[1]
		assert_true(typed.get_typed_builtin() == TYPE_VECTOR2, "typed Vector2")


func test_exported_enum_array_keeps_the_element_hint() -> void:
	var spec: String = "%d/%d:Up:0,Down:1" % [TYPE_INT, PROPERTY_HINT_ENUM]
	var converted: Array = _json.from_json([0.0, 1.0], _exported(TYPE_ARRAY, spec))
	assert_eq(converted, [true, [0, 1]], "an exported Array of an enum")
	if converted[0]:
		var typed: Array = converted[1]
		assert_true(typed.get_typed_builtin() == TYPE_INT, "typed int")
	var element: Dictionary = {
		"type": TYPE_INT, "hint": PROPERTY_HINT_ENUM, "hint_string": "Up:0,Down:1"
	}
	assert_eq(_json._spec_info(spec), element, "split at the first colon, the hint kept")


func test_exported_resource_array_is_refused_with_reason() -> void:
	var spec: String = "%d/%d:Texture2D" % [TYPE_OBJECT, PROPERTY_HINT_RESOURCE_TYPE]
	var refusal: String = "arrays of Object types (here Array[Texture2D]) cannot be set from JSON"
	assert_eq(
		_json.from_json([], _exported(TYPE_ARRAY, spec)),
		[false, null, refusal],
		"an exported Array[Texture2D]"
	)


func test_exported_typed_dictionary_converts_typed() -> void:
	var converted: Array = _json.from_json({"a": 1.0}, _exported(TYPE_DICTIONARY, "4:;2:"))
	assert_eq(converted, [true, {"a": 1}], "an exported Dictionary[String, int]")
	if converted[0]:
		var typed: Dictionary = converted[1]
		assert_true(typed.get_typed_key_builtin() == TYPE_STRING, "String keys")
		assert_true(typed.get_typed_value_builtin() == TYPE_INT, "int values")
	var untyped_values: Array = _json.from_json({"a": "b"}, _exported(TYPE_DICTIONARY, "4:;0:"))
	assert_eq(untyped_values, [true, {"a": "b"}], "a Variant value side takes any value")
	assert_eq(
		_json.from_json({"a": "b"}, _exported(TYPE_DICTIONARY, "4:;2:")), REFUSED, "a value no int"
	)


func test_exported_dictionary_with_object_value_is_refused_with_reason() -> void:
	var spec: String = "%d:;%d/%d:Node2D" % [TYPE_INT, TYPE_OBJECT, PROPERTY_HINT_NODE_TYPE]
	assert_eq(
		_json.from_json({"1": null}, _exported(TYPE_DICTIONARY, spec)),
		[false, null, _dictionary_refusal("int", "Node2D")],
		"an exported Dictionary[int, Node2D]"
	)


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


func test_external_resource_reads_as_path_and_uid() -> void:
	var expected: Dictionary = {"resource": SCRIPT_PATH, "class": "GDScript"}
	var uid: int = ResourceLoader.get_resource_uid(SCRIPT_PATH)
	if uid != ResourceUID.INVALID_ID:
		expected["uid"] = ResourceUID.id_to_text(uid)
	assert_eq(_json.to_json(load(SCRIPT_PATH)), expected, "a saved resource as its path, not more")


func test_unsaved_resource_reads_class_and_changed_properties() -> void:
	var shape := RectangleShape2D.new()
	shape.size = Vector2(10, 20)
	var expected: Dictionary = {
		"class": "RectangleShape2D", "properties": {"size": {"x": 10.0, "y": 20.0}}
	}
	assert_eq(_json.to_json(shape), expected, "only the properties that differ from the default")


func test_builtin_resource_reads_sub_resource_id() -> void:
	var shape := RectangleShape2D.new()
	shape.resource_path = "res://x.tscn::Box_1"
	var expected: Dictionary = {
		"class": "RectangleShape2D", "subResource": "Box_1", "properties": {}
	}
	assert_eq(_json.to_json(shape), expected, "a scene's sub-resource with its id")


func test_nested_resources_stop_at_depth_cap() -> void:
	var top := AtlasTexture.new()
	var texture := top
	for _level in 10:
		var next := AtlasTexture.new()
		texture.atlas = next
		texture = next
	var described: Variant = _json.to_json(top)
	var depth: int = 0
	while described is Dictionary and (described as Dictionary).has("properties"):
		described = described["properties"].get("atlas")
		depth += 1
	assert_eq(depth, 8, "eight levels read with their properties")
	assert_eq(described, {"class": "AtlasTexture"}, "the ninth by its class alone")


func test_node_reads_relative_to_node_root() -> void:
	var root := Node.new()
	var child := Node.new()
	child.name = "Child"
	root.add_child(child)
	var leaf := Node.new()
	leaf.name = "Leaf"
	child.add_child(leaf)
	_json.node_root = root
	assert_eq(_json.to_json(leaf), "Child/Leaf", "a node under the root, relative to it")
	assert_eq(_json.to_json(root), ".", "the root itself")
	_json.node_root = null
	root.free()


func test_null_ref_reads_null_and_a_freed_object_reads_freed() -> void:
	var sprite := Sprite2D.new()
	var empty: Variant = sprite.texture
	assert_eq(typeof(empty), TYPE_OBJECT, "a native getter's empty Ref is an Object")
	assert_eq(_json.to_json(empty), null, "a null Ref at the top level")
	assert_eq(_json.to_json([empty]), [null], "a null Ref in an Array")
	assert_eq(_json.to_json({"texture": empty}), {"texture": null}, "a null Ref in a Dictionary")
	sprite.free()
	var gone := Node.new()
	gone.free()
	assert_eq(_json.to_json(gone), "<freed object>", "a freed object")


func test_node_outside_node_root_reads_null() -> void:
	var outer := Node.new()
	var root := Node.new()
	root.name = "Root"
	outer.add_child(root)
	var stranger := Node.new()
	_json.node_root = root
	assert_eq(_json.to_json(outer), null, "the root's parent")
	assert_eq(_json.to_json(stranger), null, "a node in no tree of the root's")
	_json.node_root = null
	outer.free()
	stranger.free()


func test_node_off_tree_without_root_reads_null() -> void:
	var node := Node.new()
	assert_eq(_json.to_json(node), null, "a node in no scene tree, with no error logged")
	node.free()


func test_resource_from_path_string() -> void:
	var script: Resource = load(SCRIPT_PATH)
	assert_eq(_json.from_json(SCRIPT_PATH, SCRIPT_INFO), [true, script], "a res:// path")
	var any_info: Dictionary = _resource_info("")
	assert_eq(_json.from_json(SCRIPT_PATH, any_info), [true, script], "any Resource, no hint")
	var either: Dictionary = _resource_info("Texture2D,Script")
	assert_eq(_json.from_json(SCRIPT_PATH, either), [true, script], "one of the hinted classes")


func test_resource_from_read_shape() -> void:
	var read: Dictionary = {"resource": SCRIPT_PATH, "uid": "uid://unused", "class": "GDScript"}
	assert_eq(_json.from_json(read, SCRIPT_INFO), [true, load(SCRIPT_PATH)], "to_json's shape")


func test_resource_from_type_dictionary_sets_properties() -> void:
	var box: Dictionary = {
		"type": "StyleBoxFlat",
		"bg_color": {"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0},
		"corner_radius_top_left": 4.0,
	}
	var converted: Array = _json.from_json(box, _resource_info("StyleBox"))
	assert_true(converted[0] and converted[1] is StyleBoxFlat, "a new StyleBoxFlat")
	if converted[1] is StyleBoxFlat:
		assert_eq(converted[1].bg_color, Color(1, 0, 0, 1), "its colour")
		assert_eq(converted[1].corner_radius_top_left, 4, "its corner radius")
	var nested: Dictionary = {
		"type": "ShaderMaterial", "render_priority": 3.0, "next_pass": {"type": "ShaderMaterial"}
	}
	var material: Array = _json.from_json(nested, _resource_info("Material"))
	assert_true(material[0] and material[1] is ShaderMaterial, "a new ShaderMaterial")
	if material[1] is ShaderMaterial:
		assert_eq(material[1].render_priority, 3, "its render priority")
		assert_true(material[1].next_pass is ShaderMaterial, "a nested resource")


func test_resource_null_clears() -> void:
	assert_eq(_json.from_json(null, SCRIPT_INFO), [true, null], "null")


func test_resource_of_wrong_class_is_refused() -> void:
	var texture_info: Dictionary = _resource_info("Texture2D")
	assert_eq(_json.from_json(SCRIPT_PATH, texture_info), REFUSED, "a Script for a Texture2D")
	var box: Dictionary = {"type": "StyleBoxFlat"}
	assert_eq(_json.from_json(box, texture_info), REFUSED, "a StyleBoxFlat for a Texture2D")
	assert_eq(_json.from_json({"type": "Node"}, _resource_info("")), REFUSED, "no Resource")
	assert_eq(_json.from_json({"type": "Nope"}, _resource_info("")), REFUSED, "no such class")
	assert_eq(
		_json.from_json({"type": "Shape2D"}, _resource_info("")), REFUSED, "an abstract class"
	)


func test_missing_resource_path_is_refused() -> void:
	assert_eq(_json.from_json("res://nope.tres", SCRIPT_INFO), REFUSED, "a missing file")
	assert_eq(_json.from_json("gd_test.gd", SCRIPT_INFO), REFUSED, "no res:// or uid://")
	assert_eq(_json.from_json({"resource": 5.0}, SCRIPT_INFO), REFUSED, "a path that is no text")
	assert_eq(_json.from_json(5.0, SCRIPT_INFO), REFUSED, "a number")


func test_type_dictionary_with_unknown_property_is_refused() -> void:
	var info: Dictionary = _resource_info("StyleBox")
	var unknown: Dictionary = {"type": "StyleBoxFlat", "nope": 1.0}
	assert_eq(_json.from_json(unknown, info), REFUSED, "an unknown property")
	var wrong: Dictionary = {"type": "StyleBoxFlat", "bg_color": "not a colour"}
	assert_eq(_json.from_json(wrong, info), REFUSED, "a value that does not convert")


func test_script_class_resource_fits_its_hint() -> void:
	var resource: Resource = load(TEST_RESOURCE_PATH).new()
	assert_true(_json._fits_hint(resource, "JsonTestResource"), "its class_name")
	assert_true(_json._fits_hint(resource, "Texture2D, JsonTestResource"), "one of several")
	assert_true(_json._fits_hint(resource, "Resource"), "its native class")
	assert_true(not _json._fits_hint(resource, "Texture2D"), "another class")
	var subclass := GDScript.new()
	subclass.source_code = 'extends "%s"\n' % TEST_RESOURCE_PATH
	subclass.reload()
	var derived: Resource = subclass.new()
	assert_true(_json._fits_hint(derived, "JsonTestResource"), "its base script's class_name")


func test_type_dictionary_builds_a_script_class_resource() -> void:
	var classes: Array = [
		{"class": "JsonTestResource", "base": "JsonTestBase", "path": TEST_RESOURCE_PATH},
		{"class": "JsonTestBase", "base": "Resource", "path": "res://none.gd"},
		{"class": "JsonTestNode", "base": "Node", "path": TEST_RESOURCE_PATH},
		{"class": "LoopA", "base": "LoopB", "path": TEST_RESOURCE_PATH},
		{"class": "LoopB", "base": "LoopA", "path": TEST_RESOURCE_PATH},
	]
	var made: Variant = _json._new_script_resource("JsonTestResource", classes)
	assert_true(
		made is Resource and (made as Resource).get_script() == load(TEST_RESOURCE_PATH),
		"a new resource of the class, its bases followed to Resource"
	)
	assert_eq(_json._new_script_resource("JsonTestNode", classes), null, "a class of a Node")
	assert_eq(_json._new_script_resource("LoopA", classes), null, "a cycle of bases")
	assert_eq(_json._new_script_resource("Nope", classes), null, "no such class")
	var unknown: Dictionary = {"type": "JsonTestResourceNotInTheProject"}
	assert_eq(_json.from_json(unknown, _resource_info("")), REFUSED, "a class nowhere")


func test_from_json_finds_script_class_through_global_class_list() -> void:
	var read: Dictionary = {"type": "JsonTestResource", "amount": 3.0}
	var converted: Array = _json.from_json(read, _resource_info("JsonTestResource"))
	assert_true(converted[0] and converted[1] is Resource, "a new resource of the class_name")
	if converted[1] is Resource:
		var script: Script = (converted[1] as Resource).get_script()
		assert_eq(str(script.get_global_name()), "JsonTestResource", "the script's global name")
		assert_eq((converted[1] as Resource).get("amount"), 3, "its amount")


func test_from_json_finds_derived_script_class_through_its_base() -> void:
	var read: Dictionary = {"type": "JsonTestDerivedResource", "amount": 2.0, "label": "x"}
	var converted: Array = _json.from_json(read, _resource_info("JsonTestResource"))
	assert_true(converted[0] and converted[1] is Resource, "a new resource of the derived class")
	if converted[1] is Resource:
		assert_eq((converted[1] as Resource).get("amount"), 2, "its inherited amount")
		assert_eq((converted[1] as Resource).get("label"), "x", "its own label")


func test_builtin_read_shape_round_trips() -> void:
	var shape := RectangleShape2D.new()
	shape.size = Vector2(10, 20)
	shape.resource_path = "res://y.tscn::Box_2"
	var converted: Array = _json.from_json(_json.to_json(shape), _resource_info("Shape2D"))
	assert_true(converted[0] and converted[1] is RectangleShape2D, "a new RectangleShape2D")
	if converted[1] is RectangleShape2D:
		assert_eq(converted[1].size, Vector2(10, 20), "its size")
		assert_true(converted[1] != shape, "a new resource, not the one read")
	var broken: Dictionary = {"class": "RectangleShape2D", "properties": 5.0}
	assert_eq(_json.from_json(broken, _resource_info("Shape2D")), REFUSED, "properties no object")


func test_script_class_resource_reads_its_class_name() -> void:
	var resource: Resource = load(TEST_RESOURCE_PATH).new()
	resource.set("amount", 3)
	var expected: Dictionary = {"class": "JsonTestResource", "properties": {"amount": 3}}
	assert_eq(_json.to_json(resource), expected, "an unsaved one by its class_name")
	var saved: Resource = load(TEST_RESOURCE_PATH).new()
	saved.resource_path = "res://json_test_saved.tres"
	var saved_expected: Dictionary = {
		"resource": "res://json_test_saved.tres", "class": "JsonTestResource"
	}
	assert_eq(_json.to_json(saved), saved_expected, "a saved one by its class_name")


func test_script_class_builtin_round_trips() -> void:
	var resource: Resource = load(TEST_RESOURCE_PATH).new()
	resource.set("amount", 3)
	resource.resource_path = "res://z.tscn::Res_1"
	var read: Dictionary = _json.to_json(resource)
	var classes: Array = [
		{"class": "JsonTestResource", "base": "Resource", "path": TEST_RESOURCE_PATH}
	]
	var made: Variant = _json._new_script_resource(str(read.get("class")), classes)
	assert_true(made is Resource, "a new resource of the class read")
	if made is Resource:
		var properties: Dictionary = read.get("properties", {})
		assert_true(_json._set_properties(made, properties), "the properties read set on it")
		assert_eq((made as Resource).get("amount"), 3, "its amount")
		assert_true(_json._fits_hint(made, "JsonTestResource"), "of the class read")


func test_same_compares_a_read_back_by_type_and_value() -> void:
	assert_true(_json.same(0.1 + 0.2, 0.3), "floats approximately")
	assert_true(not _json.same(1.0, 1), "a float is not an int")
	assert_true(_json.same(Vector2(1, 2), Vector2(1, 2)), "an equal vector")
	assert_true(not _json.same(Vector2(1, 2), Vector2(2, 1)), "a different vector")
	assert_true(_json.same(null, null), "null reads back as null")
	var empty_ref: Variant = Sprite2D.new()
	var cleared: Variant = (empty_ref as Sprite2D).texture
	(empty_ref as Sprite2D).free()
	assert_true(_json.same(cleared, null), "a cleared native Object property is null")
	assert_true(not _json.same(0, null), "an int is not null")


func test_same_compares_a_string_and_a_string_name_by_text() -> void:
	assert_true(_json.same(StringName("a"), "a"), "a StringName read back for a String set")
	assert_true(_json.same("a", StringName("a")), "a String read back for a StringName set")
	assert_true(not _json.same(StringName("a"), "b"), "different text")
	assert_true(not _json.same(NodePath("a"), "a"), "a NodePath is not text")


func test_is_shown_keeps_script_and_editor_properties() -> void:
	assert_true(_json.is_shown({"usage": PROPERTY_USAGE_SCRIPT_VARIABLE}), "a script variable")
	assert_true(_json.is_shown({"usage": PROPERTY_USAGE_DEFAULT}), "an editor property")
	assert_true(not _json.is_shown({"usage": PROPERTY_USAGE_STORAGE}), "stored, not shown")
	var heading: int = PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_EDITOR
	assert_true(not _json.is_shown({"usage": heading}), "a section heading")
	var group: int = PROPERTY_USAGE_GROUP | PROPERTY_USAGE_EDITOR
	assert_true(not _json.is_shown({"usage": group}), "a group heading")


func test_type_name_names_a_class_or_a_variant_type() -> void:
	assert_eq(_json.type_name({"type": TYPE_INT}), "int", "a Variant type")
	assert_eq(_json.type_name({"type": TYPE_VECTOR2}), "Vector2", "a vector")
	assert_eq(_json.type_name({}), "Nil", "no type")
	assert_eq(_json.type_name(_resource_info("Texture2D")), "Object", "an object without class")
	var texture: Dictionary = {"type": TYPE_OBJECT, "class_name": "Texture2D"}
	assert_eq(_json.type_name(texture), "Texture2D", "an object's class")


## The entry of an exported typed container of type, as 4.7.2 gdscript_parser.cpp writes it: a
## PROPERTY_HINT_TYPE_STRING hint whose hint string is spec.
static func _exported(type: int, spec: String) -> Dictionary:
	return {"type": type, "hint": PROPERTY_HINT_TYPE_STRING, "hint_string": spec}


## The reason a dictionary of an Object key or value type is refused.
static func _dictionary_refusal(key_type: String, value_type: String) -> String:
	return (
		"dictionaries with Object keys or values (here Dictionary[%s, %s]) cannot be set from JSON"
		% [key_type, value_type]
	)


## The entry of a Resource-typed property whose hint string is hint.
static func _resource_info(hint: String) -> Dictionary:
	return {"type": TYPE_OBJECT, "hint": PROPERTY_HINT_RESOURCE_TYPE, "hint_string": hint}
