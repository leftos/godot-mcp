extends "res://gd_test.gd"
## The tooltip reader's game-tooltip side (bridge/godot_mcp_hover.gd classify_game_tooltip and
## _read_game_tooltip): what an mcp_tooltip node's state value says, the tooltip it builds, and
## one node's read, with no tree.
# gdlint: disable=private-method-call

var _hover_script: GDScript = load_bridge_script("godot_mcp_hover.gd")


func test_null_and_an_empty_dictionary_are_none() -> void:
	assert_eq(_hover_script.classify_game_tooltip(null), {"kind": "none"}, "null")
	assert_eq(_hover_script.classify_game_tooltip({}), {"kind": "none"}, "an empty Dictionary")


func test_pending_true_is_pending() -> void:
	assert_eq(
		_hover_script.classify_game_tooltip({"pending": true}), {"kind": "pending"}, "pending: true"
	)


func test_a_null_text_is_showing_with_no_text() -> void:
	var state: Dictionary = {"text": null}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": {"text": null, "state": state}},
		"text null"
	)


func test_a_whole_rect_is_taken() -> void:
	var state: Dictionary = {"text": "a", "x": 1, "y": 2, "width": 3, "height": 4.5}
	var tooltip: Dictionary = {
		"text": "a", "x": 1, "y": 2, "width": 3, "height": 4.5, "state": state
	}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": tooltip},
		"text with all four rect keys"
	)


func test_a_partial_rect_is_left_out() -> void:
	var state: Dictionary = {"text": "a", "x": 1}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": {"text": "a", "state": state}},
		"text with only x"
	)


func test_neither_text_nor_pending_is_malformed() -> void:
	assert_eq(
		_hover_script.classify_game_tooltip({"label": "x"}), {"kind": "malformed"}, "a label key"
	)
	assert_eq(_hover_script.classify_game_tooltip(5), {"kind": "malformed"}, "a non-Dictionary")


func test_pending_must_be_true_and_text_a_string_or_null() -> void:
	for state: Dictionary in [{"pending": false}, {"pending": 1}, {"text": 5}]:
		assert_eq(
			_hover_script.classify_game_tooltip(state), {"kind": "malformed"}, JSON.stringify(state)
		)


func test_text_wins_over_pending() -> void:
	var state: Dictionary = {"text": "a", "pending": true}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": {"text": "a", "state": state}},
		"text with pending: true"
	)


func test_a_rect_with_a_string_number_is_left_out() -> void:
	var state: Dictionary = {"text": "a", "x": "10", "y": 2, "width": 3, "height": 4}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": {"text": "a", "state": state}},
		"x a String"
	)


## A tooltip node freed inside its own read is skipped, with no owner to describe.
func test_a_tooltip_node_freed_inside_its_state_read_is_skipped() -> void:
	var bridge: Node = load_bridge_script("godot_mcp_bridge.gd").new()
	var gestures: Node = load_bridge_script("godot_mcp_input.gd").new()
	var hover: Node = _hover_script.new()
	gestures.bridge = bridge
	hover.gestures = gestures
	var state: Node = FreeingState.new()
	bridge._state = state
	var node: Node = Node.new()
	assert_eq(
		hover._read_game_tooltip(node, {"extension": null, "csharpError": null}),
		{},
		"a node freed in its own read is skipped"
	)
	assert_true(not is_instance_valid(node), "the read freed the node")
	hover.free()
	gestures.free()
	bridge.free()
	state.free()


## A stand-in state reader that frees the node it is asked to read, then answers a showing
## tooltip.
class FreeingState:
	extends Node

	func read_one(
		node: Node, _max_depth: int, _extension: Variant, _csharp_error: Variant
	) -> Dictionary:
		node.free()
		return {"path": "/root/T", "class": "Node", "state": {"text": "x"}}
