extends "res://gd_test.gd"
## The tooltip spans' pure helpers (bridge/godot_mcp_tooltip_spans.gd): runs from a line's
## sampled tooltips, runs joined into spans, the pick, its refusals and the near-miss warning, on
## values built by hand, and the item target's shape refusals on a RichTextLabel. A RichTextLabel
## outside the tree lays out no line (get_line_count is 0 there), so the probe itself is covered
## by ItemTargetTests and InputTests.

var _spans: GDScript = load_bridge_script("godot_mcp_tooltip_spans.gd")
var _items: GDScript = load_bridge_script("godot_mcp_item_targets.gd")
var _targets_script: GDScript = load_bridge_script("godot_mcp_targets.gd")


func test_an_item_on_a_rich_text_label_takes_text_or_index() -> void:
	assert_eq(_items.shape_refusal("/root/Log", "RichTextLabel", {"text": "bonus"}), "", "text")
	assert_eq(_items.shape_refusal("/root/Log", "RichTextLabel", {"index": 0}), "", "index")
	assert_eq(
		_items.shape_refusal("/root/Log", "RichTextLabel", {"path": ["A"]}),
		"/root/Log is a RichTextLabel; item.path is for a Tree only",
		"path"
	)
	assert_eq(
		_items.shape_refusal("/root/Log", "RichTextLabel", {"text": "A", "column": 1}),
		"/root/Log is a RichTextLabel; item.column is for a Tree only",
		"column"
	)


func test_runs_of_cuts_a_line_where_its_tooltip_changes() -> void:
	var tips := PackedStringArray(["", "a", "a", "", "b", "b", "b", "a"])
	var answered := PackedFloat32Array([-1, 50, 50, -1, 50, 50, 50, 55])
	var runs: Array = _spans.runs_of(tips, answered, 10.0, 3, Vector2(40, 20))
	assert_eq(
		runs,
		[
			{"text": "a", "line": 3, "rect": Rect2(11, 40, 2, 20), "aim": Vector2(12, 50)},
			{"text": "b", "line": 3, "rect": Rect2(14, 40, 3, 20), "aim": Vector2(15.5, 50)},
			{"text": "a", "line": 3, "rect": Rect2(17, 40, 1, 20), "aim": Vector2(17.5, 55)},
		],
		"three runs, the last one ending at the line's end"
	)
	var none: Array = _spans.runs_of(
		PackedStringArray(["", ""]), PackedFloat32Array([-1, -1]), 0.0, 0, Vector2(0, 20)
	)
	assert_eq(none, [], "none")


func test_a_run_aims_at_its_first_stretch_answered_at_one_height() -> void:
	var tips := PackedStringArray(["x", "x", "x", "x", "x"])
	var answered := PackedFloat32Array([45, 45, 50, 50, 50])
	var runs: Array = _spans.runs_of(tips, answered, 0.0, 0, Vector2(30, 20))
	assert_eq(runs[0]["rect"], Rect2(0, 30, 5, 20), "one run over all five")
	assert_eq(runs[0]["aim"], Vector2(1, 45), "the centre of the first two, at their height")


func test_joined_carries_a_span_onto_the_next_line_it_wraps_to() -> void:
	var runs: Array = [
		{"text": "one", "line": 0, "rect": Rect2(10, 0, 20, 20), "aim": Vector2(20, 10)},
		{"text": "wrap", "line": 0, "rect": Rect2(50, 0, 40, 20), "aim": Vector2(70, 10)},
		{"text": "wrap", "line": 1, "rect": Rect2(0, 20, 30, 20), "aim": Vector2(15, 30)},
		{"text": "wrap", "line": 3, "rect": Rect2(0, 60, 30, 20), "aim": Vector2(15, 70)},
	]
	assert_eq(
		_spans.joined(runs),
		[
			{"text": "one", "rects": [Rect2(10, 0, 20, 20)], "aim": Vector2(20, 10)},
			{
				"text": "wrap",
				"rects": [Rect2(50, 0, 40, 20), Rect2(0, 20, 30, 20)],
				"aim": Vector2(70, 10)
			},
			{"text": "wrap", "rects": [Rect2(0, 60, 30, 20)], "aim": Vector2(15, 70)},
		],
		"the wrap joined across lines 0 and 1, a skipped line 2 ending it, each aimed at its first"
	)


func test_pick_finds_a_span_by_text_or_index() -> void:
	var spans: Array = [_span("bonus", 10), _span("malus", 30)]
	assert_eq(_spans.pick("/root/Log", spans, {"text": "bonus"}, _targets_script), 0, "by text")
	assert_eq(_spans.pick("/root/Log", spans, {"text": " malus "}, _targets_script), 1, "trimmed")
	assert_eq(_spans.pick("/root/Log", spans, {"index": 1.0}, _targets_script), 1, "a JSON index")


func test_pick_refuses_an_unknown_text_or_index_listing_the_spans() -> void:
	var spans: Array = [_span("bonus", 10), _span("malus", 30)]
	var listing := "index 0 'bonus' at 10,0,8,20, index 1 'malus' at 30,0,8,20"
	assert_eq(
		_spans.pick("/root/Log", spans, {"text": "gold"}, _targets_script),
		"/root/Log has no tooltip span 'gold'; spans: " + listing,
		"an unknown text"
	)
	assert_eq(
		_spans.pick("/root/Log", spans, {"index": 2}, _targets_script),
		"/root/Log has no tooltip span 2; it has 2: " + listing,
		"an index out of range"
	)
	assert_eq(
		_spans.pick("/root/Log", [], {"text": "gold"}, _targets_script),
		"/root/Log has no tooltip span 'gold'; spans: none",
		"a label with none"
	)
	var many: Array = []
	for index in 12:
		many.append(_span("s%d" % index, index * 10))
	var refusal: String = _spans.pick("/root/Log", many, {"index": 12}, _targets_script)
	assert_true(refusal.ends_with("index 9 's9' at 90,0,8,20, …"), "ten listed: " + refusal)


func test_pick_refuses_a_text_two_spans_share_listing_their_indexes() -> void:
	var spans: Array = [_span("bonus", 10), _span("malus", 30), _span("bonus", 50)]
	assert_eq(
		_spans.pick("/root/Log", spans, {"text": "bonus"}, _targets_script),
		(
			"/root/Log has 2 tooltip spans reading 'bonus': index 0 at 10,0,8,20, "
			+ "index 2 at 50,0,8,20; narrow with item.index"
		),
		"two spans reading bonus"
	)


func test_near_miss_names_the_nearest_span_and_its_distance() -> void:
	var wrapped: Dictionary = {
		"text": "wrap", "rects": [Rect2(200, 0, 40, 20), Rect2(0, 20, 30, 20)]
	}
	var spans: Array = [{"text": "bonus", "rects": [Rect2(100, 50, 10, 20)]}, wrapped]
	assert_eq(
		_spans.near_miss_warning("/root/Log", Vector2(97, 60), spans, _targets_script),
		(
			"no tooltip at (97, 60) on /root/Log; its nearest tooltip span is index 0 'bonus' "
			+ "at 100,50,10,20, 3 px away; aim at it with item {index: 0}"
		),
		"three px left of bonus"
	)
	var near_second_line: String = _spans.near_miss_warning(
		"/root/Log", Vector2(34, 23), spans, _targets_script
	)
	assert_true(
		near_second_line.contains("index 1 'wrap'"), "wrap's second line: " + near_second_line
	)
	assert_true(
		near_second_line.contains(", 4 px away"), "to the nearest line: " + near_second_line
	)
	assert_eq(_spans.near_miss_warning("/root/Log", Vector2(1, 1), [], _targets_script), "", "none")


func test_placed_maps_every_rect_and_the_aim_through_the_transform() -> void:
	var spans: Array = [
		{
			"text": "wrap",
			"rects": [Rect2(50, 0, 40, 20), Rect2(0, 20, 30, 20)],
			"aim": Vector2(70, 10)
		}
	]
	var moved: Array = _spans.placed(spans, Transform2D(0.0, Vector2(100, 200)))
	assert_eq(
		moved,
		[
			{
				"text": "wrap",
				"rects": [Rect2(150, 200, 40, 20), Rect2(100, 220, 30, 20)],
				"aim": Vector2(170, 210)
			}
		],
		"shifted by the transform's origin"
	)
	assert_eq(spans[0]["rects"][0], Rect2(50, 0, 40, 20), "the spans given are left alone")


func test_stopped_warning_says_where_the_probe_ran_out_of_samples() -> void:
	var xform := Transform2D(0.0, Vector2(100, 200))
	var stopped: Dictionary = {"point": Vector2(12, 34.5), "samples": 1234, "ms": 250}
	assert_eq(
		_spans.stopped_warning("/root/Log", stopped, xform, _targets_script),
		(
			"the tooltip span probe of /root/Log stopped after 1234 samples in 250 ms, "
			+ "so spans past (112, 234.5) were not looked for."
		),
		"the samples, the time and the stop point in viewport coordinates"
	)
	assert_eq(_spans.stopped_warning("/root/Log", null, xform, _targets_script), "", "not stopped")


func test_and_then_joins_a_text_and_a_sentence() -> void:
	assert_eq(_spans.and_then("refused", "stopped."), "refused; stopped.", "both")
	assert_eq(_spans.and_then("refused", ""), "refused", "no sentence")
	assert_eq(_spans.and_then("", "stopped."), "stopped.", "no text")


func _span(text: String, x: float) -> Dictionary:
	return {"text": text, "rects": [Rect2(x, 0, 8, 20)]}
