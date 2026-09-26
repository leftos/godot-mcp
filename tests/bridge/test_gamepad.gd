extends "res://gd_test.gd"
## The gamepad's parsing (bridge/godot_mcp_gamepad.gd): button, axis and device names and values.
## The node is never added to the tree; only functions that do not need it are called.
# gdlint: disable=private-method-call

var _pads_script: GDScript = load_bridge_script("godot_mcp_gamepad.gd")


func test_parse_button_takes_names_in_any_case() -> void:
	var pads: Node = _pads_script.new()
	assert_eq(pads._parse_button("a"), JOY_BUTTON_A, "a")
	assert_eq(pads._parse_button("Dpad_Down"), JOY_BUTTON_DPAD_DOWN, "Dpad_Down")
	assert_eq(pads._parse_button("TOUCHPAD"), JOY_BUTTON_PADDLE4 + 1, "TOUCHPAD, the last")
	pads.free()


func test_parse_button_refuses_unknown_names_and_non_strings() -> void:
	var pads: Node = _pads_script.new()
	assert_eq(pads._parse_button("JOY_BUTTON_A"), -1, "a prefixed name")
	assert_eq(pads._parse_button(""), -1, "an empty name")
	assert_eq(pads._parse_button(0), -1, "a number")
	assert_eq(pads._parse_button(null), -1, "null")
	pads.free()


func test_parse_axis_takes_names_in_any_case() -> void:
	var pads: Node = _pads_script.new()
	assert_eq(pads._parse_axis("left_x"), JOY_AXIS_LEFT_X, "left_x")
	assert_eq(pads._parse_axis("Right_Y"), JOY_AXIS_RIGHT_Y, "Right_Y")
	assert_eq(pads._parse_axis("TRIGGER_RIGHT"), JOY_AXIS_TRIGGER_RIGHT, "TRIGGER_RIGHT")
	assert_eq(pads._parse_axis("TRIGGER"), -1, "an unknown name")
	assert_eq(pads._parse_axis(1), -1, "a number")
	pads.free()


func test_parse_device_takes_integral_ids_0_to_15() -> void:
	var pads: Node = _pads_script.new()
	assert_eq(pads._parse_device(0), 0, "int 0")
	assert_eq(pads._parse_device(15.0), 15, "a JSON float 15")
	assert_eq(pads._parse_device(16), -1, "16 is past the pads")
	assert_eq(pads._parse_device(-1), -1, "negative")
	assert_eq(pads._parse_device(1.5), -1, "not integral")
	assert_eq(pads._parse_device("1"), -1, "a string")
	pads.free()


func test_axis_value_range_is_per_axis() -> void:
	var pads: Node = _pads_script.new()
	assert_eq(pads._check_axis_value(JOY_AXIS_LEFT_X, -1.0), "", "a stick takes -1")
	assert_eq(pads._check_axis_value(JOY_AXIS_LEFT_X, 1), "", "a stick takes an int 1")
	assert_eq(pads._check_axis_value(JOY_AXIS_TRIGGER_LEFT, 0.0), "", "a trigger takes 0")
	assert_eq(
		pads._check_axis_value(JOY_AXIS_TRIGGER_LEFT, -0.5),
		"axis TRIGGER_LEFT takes a value from 0 to 1; got -0.5",
		"a trigger refuses a negative value"
	)
	assert_eq(
		pads._check_axis_value(JOY_AXIS_RIGHT_Y, 1.5),
		"axis RIGHT_Y takes a value from -1 to 1; got 1.5",
		"a stick refuses past 1"
	)
	assert_true(not pads._check_axis_value(JOY_AXIS_LEFT_Y, "1").is_empty(), "a string is refused")
	pads.free()


func test_read_axis_target_adds_the_axis_as_a_float() -> void:
	var pads: Node = _pads_script.new()
	var targets: Dictionary = {}
	assert_eq(pads._read_axis_target({"axis": "left_y", "value": 1}, targets), "", "no error")
	assert_eq(targets, {JOY_AXIS_LEFT_Y: 1.0}, "the axis at its value")
	assert_true(targets[JOY_AXIS_LEFT_Y] is float, "as a float")
	pads.free()


func test_read_axis_target_says_why_it_cannot() -> void:
	var pads: Node = _pads_script.new()
	var targets: Dictionary = {}
	assert_eq(
		pads._read_axis_target(["left_x", 1], targets),
		"an axis target must be an object {axis, value}",
		"not an object"
	)
	var unknown: String = pads._read_axis_target({"axis": "up", "value": 1}, targets)
	assert_true(unknown.begins_with("unknown gamepad axis 'up'; the axes are LEFT_X, "), unknown)
	assert_eq(targets, {}, "nothing is added")
	pads.free()


func test_unknown_button_lists_every_button() -> void:
	var pads: Node = _pads_script.new()
	var message: String = pads._unknown_button("Z")
	assert_true(message.begins_with("unknown gamepad button 'Z'; the buttons are A, B, "), message)
	assert_true(message.ends_with("PADDLE4, TOUCHPAD"), message)
	pads.free()
