extends "res://gd_test.gd"
## The UI snapshot's difference (bridge/godot_mcp_ui_snapshot.gd), on hand-made snapshots: what
## wait_for {uiChanged} reports, and when it counts as no change.

var _snapshot_script: GDScript = load_bridge_script("godot_mcp_ui_snapshot.gd")


func test_identical_snapshots_are_no_change() -> void:
	var before: Dictionary = _snapshot(
		["/root/Main", "/root/Main/Button"], "/root/Main/Button", null
	)
	var after: Dictionary = _snapshot(
		["/root/Main", "/root/Main/Button"], "/root/Main/Button", null
	)
	assert_eq(_snapshot_script.diff(before, after), {}, "the same controls, focus and popup")
	assert_eq(
		_snapshot_script.diff({"controls": {}, "focus": null, "popup": null}, _empty()), {}, "empty"
	)


func test_diff_lists_appeared_and_disappeared_controls() -> void:
	var before: Dictionary = _snapshot(["/root/Main", "/root/Main/Old"], null, null)
	var after: Dictionary = _snapshot(
		["/root/Main", "/root/Main/New", "/root/Main/Newer"], null, null
	)
	var change: Dictionary = _snapshot_script.diff(before, after)
	assert_eq(change["appeared"], ["/root/Main/New", "/root/Main/Newer"], "appeared, in order")
	assert_eq(change["disappeared"], ["/root/Main/Old"], "disappeared")
	assert_eq(change["appearedCount"], 2, "appearedCount")
	assert_eq(change["disappearedCount"], 1, "disappearedCount")
	assert_true(not change.has("focus"), "focus unchanged, so left out")
	assert_true(not change.has("popup"), "popup unchanged, so left out")


func test_diff_caps_each_list_at_twenty_with_full_counts() -> void:
	var many: Array = []
	for index in 25:
		many.append("/root/Main/Item%d" % index)
	var gone: Array = []
	for index in 22:
		gone.append("/root/Main/Gone%d" % index)
	var change: Dictionary = _snapshot_script.diff(
		_snapshot(gone, null, null), _snapshot(many, null, null)
	)
	assert_eq(change["appeared"].size(), 20, "20 appeared listed")
	assert_eq(change["appeared"][0], "/root/Main/Item0", "the first listed first")
	assert_eq(change["appearedCount"], 25, "all 25 counted")
	assert_eq(change["disappeared"].size(), 20, "20 disappeared listed")
	assert_eq(change["disappearedCount"], 22, "all 22 counted")


func test_diff_reports_a_focus_move_alone() -> void:
	var before: Dictionary = _snapshot(["/root/Main/A", "/root/Main/B"], "/root/Main/A", null)
	var after: Dictionary = _snapshot(["/root/Main/A", "/root/Main/B"], "/root/Main/B", null)
	var change: Dictionary = _snapshot_script.diff(before, after)
	assert_eq(change["focus"], {"before": "/root/Main/A", "after": "/root/Main/B"}, "focus")
	assert_eq(change["appeared"], [], "nothing appeared")
	assert_eq(change["disappearedCount"], 0, "nothing disappeared")
	assert_true(not change.has("popup"), "popup unchanged, so left out")


func test_diff_reports_focus_lost_and_a_popup_opened() -> void:
	var before: Dictionary = _snapshot(["/root/Main"], "/root/Main", null)
	var after: Dictionary = _snapshot(["/root/Main"], null, "/root/Popup")
	var change: Dictionary = _snapshot_script.diff(before, after)
	assert_eq(change["focus"], {"before": "/root/Main", "after": null}, "focus to none")
	assert_eq(change["popup"], {"before": null, "after": "/root/Popup"}, "a popup opened")
	assert_eq(change["appearedCount"], 0, "no control appeared")


func _snapshot(paths: Array, focus: Variant, popup: Variant) -> Dictionary:
	var controls: Dictionary = {}
	for path: String in paths:
		controls[path] = true
	return {"controls": controls, "focus": focus, "popup": popup}


func _empty() -> Dictionary:
	return _snapshot([], null, null)
