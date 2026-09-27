extends "res://gd_test.gd"
## The input player's pure helpers (bridge/godot_mcp_input.gd), on an instance never added to
## the tree.
# gdlint: disable=private-method-call

var _input_script: GDScript = load_bridge_script("godot_mcp_input.gd")


func test_parse_button_takes_names_and_numbers() -> void:
	var gestures: Node = _input_script.new()
	assert_eq(gestures._parse_button("Left"), MOUSE_BUTTON_LEFT, "Left, in any case")
	assert_eq(gestures._parse_button("middle"), MOUSE_BUTTON_MIDDLE, "middle")
	assert_eq(gestures._parse_button(2.0), 2, "a JSON float 2")
	assert_eq(gestures._parse_button(4), 0, "4 is not a button the tools take")
	assert_eq(gestures._parse_button("back"), 0, "an unknown name")
	assert_eq(gestures._parse_button(null), 0, "null")
	gestures.free()
