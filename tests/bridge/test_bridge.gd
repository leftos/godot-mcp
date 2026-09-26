extends "res://gd_test.gd"
## The bridge's pure helpers (bridge/godot_mcp_bridge.gd), on an instance never added to the
## tree. Constructing it runs _init, whose endpoint lookup finds no GODOT_MCP_* variables and no
## attach file here, so it registers no logger; _ready, which frees a bridge that is off, never
## runs outside the tree.
# gdlint: disable=private-method-call

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")


func test_parse_button_takes_names_and_numbers() -> void:
	var bridge: Node = _bridge_script.new()
	assert_eq(bridge._parse_button("Left"), MOUSE_BUTTON_LEFT, "Left, in any case")
	assert_eq(bridge._parse_button("middle"), MOUSE_BUTTON_MIDDLE, "middle")
	assert_eq(bridge._parse_button(2.0), 2, "a JSON float 2")
	assert_eq(bridge._parse_button(4), 0, "4 is not a button the tools take")
	assert_eq(bridge._parse_button("back"), 0, "an unknown name")
	assert_eq(bridge._parse_button(null), 0, "null")
	bridge.free()
