extends "res://gd_test.gd"
## The tab scroll refusal (bridge/godot_mcp_item_targets.gd): which side of a bar's drawn range a
## scrolled-out tab lies on and how many wheel notches reach it, the two refusals' texts, and that
## a tab within the drawn range keeps the not-drawn refusal. A scrolled-out tab's strip point and
## arrow geometry need a Control in a scene tree, so ItemTargetTests covers those in the real
## engine.

var _items: GDScript = load_bridge_script("godot_mcp_item_targets.gd")
var _targets_script: GDScript = load_bridge_script("godot_mcp_targets.gd")


func test_scroll_side_and_notches_say_which_way_and_how_far_a_scrolled_out_tab_lies() -> void:
	assert_eq(_items.scroll_side(7, 0, 2), "after", "past the drawn tabs")
	assert_eq(_items.scroll_side(0, 3, 5), "before", "before the offset")
	assert_eq(_items.scroll_side(2, 0, 2), "", "within the drawn range")
	assert_eq(_items.scroll_notches(7, 0, 2, "after"), 5, "after")
	assert_eq(_items.scroll_notches(0, 3, 5, "before"), 3, "before")


func test_a_scrolled_out_tab_names_the_scroll_that_reaches_it() -> void:
	assert_eq(
		_items.tab_scrolled_out_text(
			["Tab 7", "/root/Narrow"], [7, "after", 0, 2, 5], Vector2(95, 40), _targets_script
		),
		(
			"item 'Tab 7' of /root/Narrow is scrolled out of the tab bar: tab 7 lies after the "
			+ "drawn tabs (0 to 2); scroll over the tab strip, scroll {target: {x: 95, y: 40}, "
			+ 'direction: "down", notches: 5}, then try again (a notch moves the tabs by one; '
			+ "repeat while the tab is not drawn)"
		),
		"after"
	)
	assert_eq(
		_items.tab_scrolled_out_text(
			["Tab 0", "/root/Narrow"], [0, "before", 3, 5, 3], Vector2(95, 40), _targets_script
		),
		(
			"item 'Tab 0' of /root/Narrow is scrolled out of the tab bar: tab 0 lies before the "
			+ "drawn tabs (3 to 5); scroll over the tab strip, scroll {target: {x: 95, y: 40}, "
			+ 'direction: "up", notches: 3}, then try again (a notch moves the tabs by one; '
			+ "repeat while the tab is not drawn)"
		),
		"before"
	)


func test_a_bar_that_takes_no_wheel_names_its_arrow() -> void:
	assert_eq(
		_items.tab_scroll_off_text(
			["Tab 7", "/root/Locked"],
			[7, "after", 0, 2, 5],
			["increment", Vector2(142, 40)],
			_targets_script
		),
		(
			"item 'Tab 7' of /root/Locked is scrolled out of the tab bar: tab 7 lies after the "
			+ "drawn tabs (0 to 2), and the bar takes no wheel (scrolling_enabled is off); "
			+ "click its increment arrow at (142, 40) 5 times, then try again"
		),
		"after"
	)


func test_a_tab_not_scrolled_out_keeps_the_not_drawn_refusal() -> void:
	var bar := TabBar.new()
	bar.size = Vector2(150, 40)
	var tree := Engine.get_main_loop() as SceneTree
	tree.root.add_child(bar)
	for index in 8:
		bar.add_tab("Tab %d" % index)
	var buttons: bool = bar.get_offset_buttons_visible()
	var drawn: String = _items.tab_scroll_refusal(
		_targets_script, bar, 0, ["Tab 0", "/root/Narrow"]
	)
	bar.free()
	var list := ItemList.new()
	var listed: String = _items.tab_scroll_refusal(
		_targets_script, list, 0, ["Tab 0", "/root/Narrow"]
	)
	list.free()
	assert_true(buttons, "the offset buttons show")
	assert_eq(drawn, "", "tab 0 lies within the drawn range")
	assert_eq(listed, "", "an ItemList draws no tab")
	assert_eq(
		_items.TAB_NOT_DRAWN % ["Tab 0", "/root/Narrow"],
		(
			"item 'Tab 0' of /root/Narrow has no drawn rect; it may be outside the tab bar's "
			+ "drawn range (scroll the tabs) or not laid out yet"
		),
		"the refusal a tab that is not scrolled out keeps"
	)
