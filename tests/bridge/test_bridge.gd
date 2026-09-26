extends "res://gd_test.gd"
## The bridge's pure helpers (bridge/godot_mcp_bridge.gd), on an instance never added to the
## tree. Constructing it runs _init, whose endpoint lookup finds no GODOT_MCP_* variables and no
## attach file here, so it registers no logger; _ready, which frees a bridge that is off, never
## runs outside the tree.
# gdlint: disable=private-method-call

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")


func test_to_json_keeps_json_values() -> void:
	var bridge: Node = _bridge_script.new()
	assert_eq(bridge._to_json(null), null, "null")
	assert_eq(bridge._to_json(true), true, "a bool")
	assert_eq(bridge._to_json(7), 7, "an int")
	assert_eq(bridge._to_json(2.5), 2.5, "a float")
	assert_eq(bridge._to_json("text"), "text", "a string")
	bridge.free()


func test_to_json_turns_non_finite_floats_into_strings() -> void:
	var bridge: Node = _bridge_script.new()
	assert_eq(bridge._to_json(INF), "inf", "infinity")
	assert_eq(bridge._to_json(NAN), "nan", "not a number")
	bridge.free()


func test_to_json_turns_vectors_colours_and_rects_into_objects() -> void:
	var bridge: Node = _bridge_script.new()
	assert_eq(bridge._to_json(Vector2(1.5, -2)), {"x": 1.5, "y": -2.0}, "a Vector2")
	assert_eq(bridge._to_json(Vector2i(3, 4)), {"x": 3, "y": 4}, "a Vector2i")
	assert_eq(bridge._to_json(Vector3(1, 2, 3)), {"x": 1.0, "y": 2.0, "z": 3.0}, "a Vector3")
	assert_eq(
		bridge._to_json(Color(1, 0.5, 0, 1)), {"r": 1.0, "g": 0.5, "b": 0.0, "a": 1.0}, "a Color"
	)
	assert_eq(
		bridge._to_json(Rect2(1, 2, 30, 40)),
		{"x": 1.0, "y": 2.0, "width": 30.0, "height": 40.0},
		"a Rect2"
	)
	bridge.free()


func test_to_json_converts_containers_recursively() -> void:
	var bridge: Node = _bridge_script.new()
	var value: Dictionary = {1: [Vector2i(1, 2), "a"], "packed": PackedInt32Array([5, 6])}
	var expected: Dictionary = {"1": [{"x": 1, "y": 2}, "a"], "packed": [5, 6]}
	assert_eq(
		bridge._to_json(value), expected, "keys become strings, arrays and packed arrays lists"
	)
	bridge.free()


func test_to_json_describes_objects() -> void:
	var bridge: Node = _bridge_script.new()
	var described: Variant = bridge._to_json(RefCounted.new())
	assert_true(described is Dictionary, "an object becomes an object")
	if described is Dictionary:
		assert_eq(described["class"], "RefCounted", "with its class")
	assert_eq(bridge._to_json(StringName("n")), "n", "anything else becomes its str()")
	bridge.free()


func test_parse_button_takes_names_and_numbers() -> void:
	var bridge: Node = _bridge_script.new()
	assert_eq(bridge._parse_button("Left"), MOUSE_BUTTON_LEFT, "Left, in any case")
	assert_eq(bridge._parse_button("middle"), MOUSE_BUTTON_MIDDLE, "middle")
	assert_eq(bridge._parse_button(2.0), 2, "a JSON float 2")
	assert_eq(bridge._parse_button(4), 0, "4 is not a button the tools take")
	assert_eq(bridge._parse_button("back"), 0, "an unknown name")
	assert_eq(bridge._parse_button(null), 0, "null")
	bridge.free()
