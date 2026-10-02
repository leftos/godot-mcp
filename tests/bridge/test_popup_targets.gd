extends "res://gd_test.gd"
## The popup item targets' pure helpers (bridge/godot_mcp_popup_targets.gd): the shape refusals,
## the pick by index or text with its near misses and ambiguity, the separator and disabled
## refusals, the search that filters a popup (its own or an ancestor's), a refusal after the
## popup opened, the items area, and the probe's bisection over a fake get_focused_item layout
## (found, -1 readings stepped over, budget run out). Opening, probing and holding need a running
## popup and are covered by PopupTargetTests.

var _popups: GDScript = load_bridge_script("godot_mcp_popup_targets.gd")


func test_popup_of_is_the_popup_itself_or_a_buttons() -> void:
	var menu := PopupMenu.new()
	var option := OptionButton.new()
	var menu_button := MenuButton.new()
	var plain := Button.new()
	assert_eq(_popups.popup_of(menu), menu, "a PopupMenu")
	assert_eq(_popups.popup_of(option), option.get_popup(), "an OptionButton")
	assert_eq(_popups.popup_of(menu_button), menu_button.get_popup(), "a MenuButton")
	assert_eq(_popups.popup_of(plain), null, "a Button")
	for node: Node in [menu, option, menu_button, plain]:
		node.free()


func test_shape_refusal_takes_text_or_index_and_refuses_a_trees_keys() -> void:
	assert_eq(_popups.shape_refusal("/root/W", "OptionButton", {"text": "A"}), "", "text")
	assert_eq(_popups.shape_refusal("/root/W", "MenuButton", {"index": 2}), "", "index")
	assert_eq(
		_popups.shape_refusal("/root/W", "OptionButton", {"path": ["A"]}),
		"/root/W is a OptionButton; a popup item takes text or index, not item.path",
		"path"
	)
	assert_eq(
		_popups.shape_refusal("/root/P", "PopupMenu", {"text": "A", "column": 1}),
		"/root/P is a PopupMenu; a popup item takes text or index, not item.column",
		"column"
	)
	assert_eq(
		_popups.shape_refusal("/root/P", "PopupMenu", {}),
		"item must be an object with one of text, index or a non-empty path; got {}",
		"no key"
	)


func test_pick_finds_an_item_by_index_or_by_its_trimmed_text() -> void:
	var texts := PackedStringArray(["Sword", "", " Bow ", "Axe"])
	var separators: Array[bool] = [false, true, false, false]
	var named := "the popup of /root/Weapon"
	assert_eq(_popups.pick(named, texts, separators, {"index": 1}), 1, "a separator's index")
	assert_eq(_popups.pick(named, texts, separators, {"text": "Bow"}), 2, "trimmed text")
	assert_eq(
		_popups.pick(named, texts, separators, {"index": 4}),
		"the popup of /root/Weapon has no item 4; it has 4",
		"an index out of range"
	)
	assert_eq(
		_popups.pick(named, texts, separators, {"text": "bow"}),
		"the popup of /root/Weapon has no item 'bow'; items: 'Sword', ' Bow ', 'Axe'",
		"case kept; the separator left out of the listing"
	)


func test_pick_lists_the_items_reading_the_same_text() -> void:
	var texts := PackedStringArray(["Deal", "Pass", "Deal"])
	var separators: Array[bool] = [false, false, false]
	assert_eq(
		_popups.pick("/root/Menu", texts, separators, {"text": "Deal"}),
		"/root/Menu has 2 items reading 'Deal': index 0, index 2; narrow with item.index",
		"ambiguous"
	)


func test_item_refusal_refuses_a_separator_and_a_disabled_item() -> void:
	var named := "the popup of /root/Menu"
	assert_eq(
		_popups.item_refusal(named, _popups.label_of("Save", 3), false, true),
		"'Save' in the popup of /root/Menu is disabled; Godot ignores a press on it",
		"disabled"
	)
	assert_eq(
		_popups.item_refusal(named, _popups.label_of("  ", 2), true, false),
		"item 2 in the popup of /root/Menu is a separator; Godot ignores a press on it",
		"a separator without text"
	)
	assert_eq(_popups.item_refusal(named, "'Open'", false, false), "", "a plain item")


func test_a_search_filters_its_popup_and_every_submenu_below_it_even_with_the_bar_hidden() -> void:
	var parent := PopupMenu.new()
	var sub := PopupMenu.new()
	var leaf := PopupMenu.new()
	parent.add_submenu_node_item("Sub", sub)
	sub.add_submenu_node_item("Leaf", leaf)
	assert_eq(_popups.filtering_search(leaf), [], "no search")
	var bar: LineEdit = _popups._internal(parent, "LineEdit")
	bar.text = "an"
	assert_true(not bar.visible, "the bar is hidden, search_bar_enabled off")
	assert_eq(_popups.filtering_search(leaf), [parent, "an"], "the parent's search, two levels up")
	assert_eq(_popups.filtering_search(parent), [parent, "an"], "its own search")
	(_popups._internal(sub, "LineEdit") as LineEdit).text = "x"
	assert_eq(_popups.filtering_search(leaf), [sub, "x"], "the nearest search")
	parent.free()


func test_a_refusal_after_the_popup_opened_says_it_is_open() -> void:
	assert_eq(
		_popups.with_opened("could not find 'Z' in /root/Menu within 3 motions", true),
		"could not find 'Z' in /root/Menu within 3 motions; the popup is open now",
		"opened"
	)
	assert_eq(
		_popups.with_opened("/root/Menu is not open; open it first", false),
		"/root/Menu is not open; open it first",
		"not opened"
	)


func test_holds_submenu_reads_the_aimed_items_submenu() -> void:
	assert_true(
		_popups.holds_submenu({"item": {"index": 1, "submenu": "/root/M/Sub"}}), "a submenu"
	)
	assert_true(not _popups.holds_submenu({"item": {"index": 1}}), "an item without one")
	assert_true(not _popups.holds_submenu({"kind": "control"}), "no item")
	assert_true(not _popups.holds_submenu(null), "no aim")


func test_items_area_is_the_part_the_scroll_container_shows_on_screen() -> void:
	var root := Rect2(0, 0, 640, 360)
	var at := Transform2D(0.0, Vector2(100, 50))
	var whole: Rect2 = _popups.items_area(
		Vector2(120, 200), Vector2.ZERO, Vector2(120, 200), at, root
	)
	assert_eq(whole, Rect2(0, 0, 120, 200), "all shown")
	var scrolled: Rect2 = _popups.items_area(
		Vector2(120, 600), Vector2(0, -150), Vector2(120, 200), at, root
	)
	assert_eq(scrolled, Rect2(0, 150, 120, 160), "scrolled, cut at the viewport's bottom")
	var gone: Rect2 = _popups.items_area(
		Vector2(120, 200), Vector2.ZERO, Vector2(120, 200), Transform2D(0.0, Vector2(700, 0)), root
	)
	assert_true(not gone.has_area(), "off-screen")


func test_probe_budget_is_twice_the_bits_plus_four() -> void:
	assert_eq(_popups.probe_budget(1), 4, "one item")
	assert_eq(_popups.probe_budget(2), 6, "two items")
	assert_eq(_popups.probe_budget(30), 14, "thirty items")
	assert_eq(_popups.probe_budget(32), 14, "thirty-two items")


func test_the_probe_finds_the_last_of_thirty_items_within_its_budget() -> void:
	var heights: Array[float] = []
	for _index in 30:
		heights.append(24.0)
	for target: int in [29, 0, 14]:
		var run: Dictionary = _probe(heights, [], target, _total(heights))
		assert_true(run["found"], "item %d found: %s" % [target, run["readings"]])


func test_the_probe_steps_over_minus_one_readings() -> void:
	# Items 4 and 5 are a separator and a disabled item, where item 6's first estimate lands, since
	# the four above are taller than the rest: get_focused_item reads -1 over both.
	var heights: Array[float] = [40.0, 40.0, 40.0, 40.0, 24.0, 24.0, 24.0, 24.0, 24.0, 24.0]
	var run: Dictionary = _probe(heights, [4, 5], 6, _total(heights))
	assert_true(run["found"], "found past the separator: %s" % [run["readings"]])
	assert_eq(run["readings"][0], -1, "the first reading is -1: %s" % [run["readings"]])
	var up: Dictionary = _probe(heights, [4, 5], 2, _total(heights))
	assert_true(up["found"], "found above the separator: %s" % [up["readings"]])


func test_the_probe_runs_out_below_the_visible_items_and_says_so() -> void:
	var heights: Array[float] = []
	for _index in 30:
		heights.append(24.0)
	var run: Dictionary = _probe(heights, [], 25, 240.0)
	assert_true(not run["found"], "item 25 lies below the 240 px shown: %s" % [run["readings"]])
	var readings: Array[int] = []
	readings.assign(run["readings"])
	assert_eq(
		_popups.probe_failure("'Z'", "/root/Menu", readings, 25).get_slice(";", 0),
		"could not find 'Z' in /root/Menu within %d motions" % readings.size(),
		"the failure names the item and the motions"
	)
	assert_true(
		_popups.probe_failure("'Z'", "/root/Menu", readings, 25).ends_with(_popups.PROBE_BELOW),
		"below"
	)
	var above: Array[int] = [7, 6, -1]
	assert_true(
		_popups.probe_failure("'A'", "/root/Menu", above, 2).ends_with(_popups.PROBE_ABOVE), "above"
	)
	var none: Array[int] = [-1, -1]
	assert_eq(
		_popups.probe_failure("item 1", "/root/Menu", none, 1),
		"could not find item 1 in /root/Menu within 2 motions; get_focused_item read [-1, -1]",
		"no hint without an item read"
	)


## Runs the probe over a fake popup whose items have these heights, stacked from y 0, reading -1
## over the indices in unknown and past the last item, with shown pixels of it on screen; returns
## {found, readings}.
func _probe(heights: Array[float], unknown: Array, target: int, shown: float) -> Dictionary:
	var area := Rect2(0.0, 0.0, 100.0, shown)
	var state: Dictionary = _popups.first_probe(area, _total(heights), heights.size(), target)
	var readings: Array = []
	while readings.size() < _popups.probe_budget(heights.size()) and not state.has("stuck"):
		var reading: int = _read(heights, unknown, float(state["y"]))
		readings.append(reading)
		state = _popups.next_probe(state, reading, target)
		if state.has("done"):
			return {"found": true, "readings": readings}
	return {"found": false, "readings": readings}


## The item get_focused_item reads at y: the one whose band holds it, -1 for an unknown one.
func _read(heights: Array[float], unknown: Array, y: float) -> int:
	var bottom: float = 0.0
	for index in heights.size():
		bottom += heights[index]
		if y < bottom:
			return -1 if unknown.has(index) else index
	return -1


func _total(heights: Array[float]) -> float:
	var total: float = 0.0
	for height: float in heights:
		total += height
	return total
