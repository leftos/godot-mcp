extends "res://gd_test.gd"
## The item targets' pure helpers (bridge/godot_mcp_item_targets.gd): the shape refusals, the
## flat lists' pick, the listing and path texts, the ambiguity refusals, the visible rect and the
## scrolled-out decision, the right-to-left mirror, the Tree cell's aim point and drawn text, and a
## Tree's paths and collapsed ancestors on a Tree never added to the scene tree; item geometry
## needs laid-out Controls and is covered by ItemTargetTests.

var _items: GDScript = load_bridge_script("godot_mcp_item_targets.gd")


func test_shape_refusal_takes_the_list_classes_and_their_descendants() -> void:
	for list_class: String in ["ItemList", "TabBar", "TabContainer"]:
		assert_eq(_items.shape_refusal("/root/L", list_class, {"index": 0}), "", list_class)
	assert_eq(_items.shape_refusal("/root/L", "Tree", {"text": "A"}), "", "a Tree")
	assert_eq(_items.item_kind("Tree"), "tree", "a Tree's kind")
	assert_eq(_items.item_kind("TabContainer"), "flat", "a TabContainer's kind")
	assert_eq(
		_items.shape_refusal("/root/Go", "Button", {"text": "A"}),
		(
			"/root/Go is a Button; item targets take an ItemList, TabBar, TabContainer, Tree, "
			+ "OptionButton, MenuButton or PopupMenu"
		),
		"a Button"
	)


func test_shape_refusal_keeps_path_and_column_for_a_tree_and_index_for_a_flat_list() -> void:
	assert_eq(
		_items.shape_refusal("/root/Rows", "ItemList", {"path": ["A"]}),
		"/root/Rows is a ItemList; item.path is for a Tree only",
		"path on an ItemList"
	)
	assert_eq(
		_items.shape_refusal("/root/Tabs", "TabBar", {"text": "A", "column": 1}),
		"/root/Tabs is a TabBar; item.column is for a Tree only",
		"column on a TabBar"
	)
	assert_eq(
		_items.shape_refusal("/root/Inv", "Tree", {"index": 2}),
		"/root/Inv is a Tree; a Tree item takes text or path, not index",
		"index on a Tree"
	)
	assert_eq(_items.shape_refusal("/root/Inv", "Tree", {"path": ["A"], "column": 1}), "", "a Tree")


func test_shape_refusal_refuses_an_item_it_cannot_read() -> void:
	var prefix := "item must be an object with one of text, index or a non-empty path; got "
	assert_eq(_items.shape_refusal("/root/L", "ItemList", "A"), prefix + '"A"', "not an object")
	assert_eq(_items.shape_refusal("/root/L", "ItemList", {}), prefix + "{}", "no key")
	assert_true(_items.malformed({"text": "A", "index": 0}), "two keys")
	assert_true(_items.malformed({"path": []}), "an empty path")
	assert_true(_items.malformed({"path": "A"}), "a path that is not an array")
	assert_true(not _items.malformed({"path": ["A"]}), "a path")


func test_flat_pick_finds_an_item_by_index_or_by_its_trimmed_text() -> void:
	var texts := PackedStringArray(["Alex", " Sam ", "Kit"])
	var hidden: Array[bool] = [false, false, false]
	assert_eq(_items.flat_pick("/root/L", texts, hidden, {"index": 2.0}), 2, "a JSON index")
	assert_eq(_items.flat_pick("/root/L", texts, hidden, {"text": "Sam"}), 1, "trimmed")
	assert_eq(
		_items.flat_pick("/root/L", texts, hidden, {"text": "sam"}),
		"/root/L has no item 'sam'; items: 'Alex', ' Sam ', 'Kit'",
		"case kept"
	)
	assert_eq(
		_items.flat_pick("/root/L", texts, hidden, {"index": 3}),
		"/root/L has no item 3; it has 3",
		"out of range"
	)


func test_flat_pick_returns_several_shown_matches_and_skips_hidden_ones() -> void:
	var texts := PackedStringArray(["Deal", "Pass", "Deal", "Deal"])
	var hidden: Array[bool] = [false, true, false, true]
	var several: Variant = _items.flat_pick("/root/T", texts, hidden, {"text": "Deal"})
	assert_eq(several, [0, 2], "the shown matches")
	var one_hidden: Array[bool] = [true, true, false, true]
	assert_eq(_items.flat_pick("/root/T", texts, one_hidden, {"text": "Deal"}), 2, "one shown")
	assert_eq(
		_items.flat_pick("/root/T", texts, hidden, {"text": "Pass"}),
		"item 'Pass' of /root/T is hidden",
		"only a hidden match"
	)
	assert_eq(
		_items.flat_pick("/root/T", texts, hidden, {"index": 1}),
		"item 'Pass' of /root/T is hidden",
		"a hidden index"
	)
	assert_eq(
		_items.flat_pick("/root/T", texts, hidden, {"text": "Fold"}),
		"/root/T has no item 'Fold'; items: 'Deal', 'Deal'",
		"the shown texts listed"
	)


func test_listed_texts_lists_at_most_ten() -> void:
	assert_eq(_items.listed_texts(PackedStringArray()), "none", "none")
	var many := PackedStringArray()
	for index in 12:
		many.append(str(index))
	assert_eq(
		_items.listed_texts(many),
		"'0', '1', '2', '3', '4', '5', '6', '7', '8', '9', …",
		"ten and more"
	)


func test_listed_matches_marks_the_matches_left_out() -> void:
	var listed := PackedStringArray(["index 0 at 1,2,3,4", "index 2 at 1,6,3,4"])
	assert_eq(_items.listed_matches(2, listed), "index 0 at 1,2,3,4, index 2 at 1,6,3,4", "all")
	assert_eq(
		_items.listed_matches(12, listed), "index 0 at 1,2,3,4, index 2 at 1,6,3,4, …", "more"
	)


func test_a_tree_ambiguity_narrows_by_path_unless_the_matches_share_one() -> void:
	var paths := PackedStringArray(['["A", "S"]', '["B", "S"]'])
	var listed := PackedStringArray(['path ["A", "S"] at 1,2,3,4', 'path ["B", "S"] at 1,6,3,4'])
	assert_eq(
		_items.tree_ambiguity_refusal("/root/Inv", "S", 2, listed, paths),
		(
			'/root/Inv has 2 items reading \'S\': path ["A", "S"] at 1,2,3,4, path ["B", "S"] '
			+ "at 1,6,3,4; narrow with item.path"
		),
		"two paths"
	)
	var same := PackedStringArray(['["A", "S"]', '["A", "S"]'])
	assert_eq(
		_items.tree_ambiguity_refusal("/root/Inv", "S", 2, listed, same),
		(
			'/root/Inv has 2 items reading \'S\': path ["A", "S"] at 1,2,3,4, path ["B", "S"] '
			+ 'at 1,6,3,4; they share the path ["A", "S"]; aim with {x, y} inside one of the '
			+ "rects listed"
		),
		"one path"
	)
	assert_true(
		_items.tree_ambiguity_refusal("/root/Inv", "S", 12, listed, same).ends_with("item.path"),
		"more matches than listed may not share it"
	)


func test_path_text_quotes_each_step() -> void:
	assert_eq(
		_items.path_text(PackedStringArray(["Weapons", "Sword"])), '["Weapons", "Sword"]', "two"
	)
	assert_eq(_items.path_text(PackedStringArray()), "[]", "none")


func test_visible_rect_cuts_the_titles_and_each_scroll_bar_on_its_side() -> void:
	var size := Vector2(200, 150)
	var none: Array[Rect2] = []
	assert_eq(_items.visible_rect(size, 0.0, none), Rect2(0, 0, 200, 150), "whole")
	var right_and_bottom: Array[Rect2] = [Rect2(190, 0, 10, 140), Rect2(0, 140, 190, 10)]
	assert_eq(
		_items.visible_rect(size, 30.0, right_and_bottom), Rect2(0, 30, 190, 110), "titles and bars"
	)
	var left: Array[Rect2] = [Rect2(0, 0, 10, 150)]
	assert_eq(_items.visible_rect(size, 0.0, left), Rect2(10, 0, 190, 150), "a bar on the left")


func test_scrolled_out_is_a_point_outside_the_visible_rect() -> void:
	var visible := Rect2(0, 30, 190, 110)
	assert_true(not _items.scrolled_out(Vector2(50, 60), visible), "inside")
	assert_true(_items.scrolled_out(Vector2(50, 20), visible), "under the titles")
	assert_true(_items.scrolled_out(Vector2(195, 60), visible), "under the scroll bar")
	assert_true(_items.scrolled_out(Vector2(50, 145), visible), "under the bottom bar")


func test_mirror_axis_is_the_one_get_item_at_position_mirrors_about() -> void:
	assert_eq(_items.mirror_axis(240.0, 4.0, 8.0, 0.0), 240.0, "margins 4 and 4, no scroll")
	assert_eq(_items.mirror_axis(240.0, 4.0, 8.0, 10.0), 220.0, "scrolled 10")


func test_span_centre_skips_the_fold_indent_on_the_reading_side() -> void:
	var rect := Rect2(10, 20, 90, 30)
	assert_eq(_items.span_centre(rect, 48.0, false), Vector2(79, 35), "left to right")
	assert_eq(_items.span_centre(rect, 48.0, true), Vector2(31, 35), "right to left")
	assert_eq(_items.span_centre(rect, 0.0, false), rect.get_center(), "no indent")


func test_drawn_translates_as_the_mode_says() -> void:
	var node := Node.new()
	var messages := Translation.new()
	messages.locale = TranslationServer.get_locale()
	messages.add_message("ITEM_TARGETS_KEY", "Shown")
	TranslationServer.add_translation(messages)
	var texts: Array = [
		_items.drawn(node, Node.AUTO_TRANSLATE_MODE_ALWAYS, "ITEM_TARGETS_KEY"),
		_items.drawn(node, Node.AUTO_TRANSLATE_MODE_DISABLED, "ITEM_TARGETS_KEY"),
	]
	TranslationServer.remove_translation(messages)
	node.free()
	assert_eq(texts, ["Shown", "ITEM_TARGETS_KEY"], "always and never")


func test_a_tree_path_starts_at_the_first_shown_level() -> void:
	var tree := Tree.new()
	var root: TreeItem = tree.create_item()
	root.set_text(0, "Root")
	var weapons: TreeItem = tree.create_item(root)
	weapons.set_text(0, "Weapons")
	var sword: TreeItem = tree.create_item(weapons)
	sword.set_text(0, "Sword")
	var shown: PackedStringArray = _items.shown_path(tree, sword)
	tree.hide_root = true
	var hidden: PackedStringArray = _items.shown_path(tree, sword)
	tree.free()
	assert_eq(shown, PackedStringArray(["Root", "Weapons", "Sword"]), "shown")
	assert_eq(hidden, PackedStringArray(["Weapons", "Sword"]), "hidden")


func test_collapsed_ancestor_is_the_outermost_collapsed() -> void:
	var tree := Tree.new()
	var root: TreeItem = tree.create_item()
	var outer: TreeItem = tree.create_item(root)
	var inner: TreeItem = tree.create_item(outer)
	var leaf: TreeItem = tree.create_item(inner)
	assert_eq(_items.collapsed_ancestor(leaf), null, "none collapsed")
	inner.collapsed = true
	assert_eq(_items.collapsed_ancestor(leaf), inner, "the inner")
	outer.collapsed = true
	assert_eq(_items.collapsed_ancestor(leaf), outer, "the outermost")
	assert_eq(_items.collapsed_ancestor(outer), null, "its own collapse is not an ancestor's")
	tree.free()
