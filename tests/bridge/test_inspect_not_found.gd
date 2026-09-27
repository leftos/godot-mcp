extends "res://gd_test.gd"
## The inspector's not-found refusal (bridge/godot_mcp_inspect.gd) with the caller's own hint.
##
## A bare name is the form tested here: the path form reads the live scene tree through the
## bridge, and the inspector this harness builds has no bridge to read it from, so the path form
## is left to the C# integration tests that pin inspect_node's and click's text.

const PREFIX := "No node named 'Nowhere' anywhere under /root in the running game; "
const SCENE_HINT := "get_scene_tree lists the nodes' paths"
const UI_HINT := "get_ui_elements lists the Controls' paths and names"

var _inspect_script: GDScript = load_bridge_script("godot_mcp_inspect.gd")


func test_a_bare_name_takes_the_scene_tree_hint() -> void:
	var inspect: Node = _inspect_script.new()
	var refusal: String = inspect.not_found("Nowhere", SCENE_HINT)
	assert_eq(refusal, PREFIX + SCENE_HINT + ".", "the scene-tree hint")
	inspect.free()


func test_a_bare_name_takes_the_ui_elements_hint() -> void:
	var inspect: Node = _inspect_script.new()
	var refusal: String = inspect.not_found("Nowhere", UI_HINT)
	assert_eq(refusal, PREFIX + UI_HINT + ".", "the ui-elements hint")
	inspect.free()
