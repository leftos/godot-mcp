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


func test_pending_with_a_due_time_carries_it() -> void:
	assert_eq(
		_hover_script.classify_game_tooltip({"pending": true, "dueMs": 2000}),
		{"kind": "pending", "dueMs": 2000.0},
		"an int dueMs"
	)
	assert_eq(
		_hover_script.classify_game_tooltip({"pending": true, "dueMs": 0.5}),
		{"kind": "pending", "dueMs": 0.5},
		"a fractional dueMs"
	)
	assert_eq(
		_hover_script.classify_game_tooltip({"pending": true, "dueMs": 0}),
		{"kind": "pending", "dueMs": 0.0},
		"a dueMs of 0"
	)


func test_a_due_time_that_is_not_a_number_from_0_up_is_bad() -> void:
	for due: Variant in [-1, "2000", null, true]:
		var state: Dictionary = {"pending": true, "dueMs": due}
		assert_eq(
			_hover_script.classify_game_tooltip(state), {"kind": "bad_due"}, JSON.stringify(state)
		)


func test_text_wins_over_a_bad_due_time() -> void:
	var state: Dictionary = {"text": "a", "pending": true, "dueMs": "x"}
	assert_eq(
		_hover_script.classify_game_tooltip(state),
		{"kind": "showing", "tooltip": {"text": "a", "state": state}},
		"text with a bad dueMs"
	)


func test_the_due_timeout() -> void:
	assert_eq(
		_hover_script.due_timeout_ms(1500, 0, 0.0), 1500, "a due time worth less than the limit"
	)
	assert_eq(
		_hover_script.due_timeout_ms(1500, 0, 2000.0), 3000, "a due time worth more than the limit"
	)
	assert_eq(
		_hover_script.due_timeout_ms(1500, 500, 2000.0), 3500, "a due time plus the elapsed wait"
	)
	assert_eq(_hover_script.due_timeout_ms(1500, 0, 9500.0), 10000, "a due time past the cap")
	assert_eq(_hover_script.due_timeout_ms(3000, 0, 100.0), 3000, "a limit already above the need")
	assert_eq(
		_hover_script.due_timeout_ms(1500, 0, 0.4), 1500, "a due time rounded up still under it"
	)
	assert_eq(
		_hover_script.due_timeout_ms(10000, 9000, 5000.0), 10000, "a late due time at the cap"
	)
	assert_eq(_hover_script.due_timeout_ms(1500, 0, 1e19), 10000, "a due time too large to count")


func test_pending_reads_merge_keeping_the_first_path_and_the_largest_due_time() -> void:
	var merged: Dictionary = {}
	merged = _hover_script._with_pending(merged, {"pending": "/root/A", "dueMs": 300.0})
	merged = _hover_script._with_pending(merged, {"pending": "/root/B"})
	merged = _hover_script._with_pending(merged, {"pending": "/root/C", "dueMs": 900.0})
	assert_eq(merged, {"pending": "/root/A", "dueMs": 900.0}, "three pending reads")


func test_the_waited_time_in_a_recording_is_clip_time() -> void:
	assert_eq(_hover_script._waited_ms(30, 60, 0), 500, "30 frames at 60 fps")
	assert_eq(_hover_script._waited_ms(0, 60, 0), 0, "no frames yet")
	assert_true(
		_hover_script._waited_ms(0, 0, Time.get_ticks_msec()) >= 0, "ticks with no recording"
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
