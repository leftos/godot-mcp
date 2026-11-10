extends "res://gd_test.gd"
## Headless edits among a TabContainer's pages, through SceneOps.edit_scene on scenes written
## under user://: an insert, a move or a delete keeps every line it did not change, and the tab_N/*
## lines of the container follow the pages (4.7.2 tab_container.cpp L560-565, L622-637); a page
## duplicated keeps its title, disabled state and current tab.

const SCENE_OPS_SCRIPT := "../../headless/scene_ops.gd"
const RunDir := preload("res://run_dir.gd")
const TABS_UID := "uid://b507tabs00001"
## Each page of the fixture: name, unique_id, whether it stores visible = false, its custom title.
const PAGES := [
	["Sound", 1003, false, ""],
	["Gameplay", 1004, true, "Net"],
	["Server", 1005, true, ""],
	["Controls", 1006, true, ""],
]
## The fixture's Tabs lines after its layout_mode: Gameplay's title, Controls disabled.
const TABS_LINES := ["current_tab = 0", 'tab_1/title = "Net"', "tab_3/disabled = true"]
## PAGES as the editor saves them with Server, page 2, the current one: every other page hidden.
const SERVER_CURRENT_PAGES := [
	["Sound", 1003, true, ""],
	["Gameplay", 1004, true, "Net"],
	["Server", 1005, false, ""],
	["Controls", 1006, true, ""],
]
## TABS_LINES with Server, page 2, the current one.
const SERVER_CURRENT_LINES := ["current_tab = 2", 'tab_1/title = "Net"', "tab_3/disabled = true"]

var tabs_scene: String = RunDir.path("tabs_edit.tscn")
var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_OPS_SCRIPT).simplify_path()
)


func test_an_insert_after_a_page_keeps_every_other_line() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))
	var params: Dictionary = _add_page({"after": "Gameplay"})

	var added: Dictionary = _ops.edit_scene("add_node", params)

	assert_eq(_warning_of(added), "", "no warning: %s" % added)
	var pages: Array = [PAGES[0], PAGES[1], ["New"], PAGES[2], PAGES[3]]
	var tab_lines: Array = ["current_tab = 0", 'tab_1/title = "Net"', "tab_4/disabled = true"]
	var expected: String = _tabs_text(pages, tab_lines)
	assert_eq(_saved(tabs_scene), expected, "only the insert, the later indices and tab_N change")
	_remove_scene(tabs_scene)


func test_an_insert_before_the_first_page_shifts_title_icon_disabled() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))
	var params: Dictionary = _add_page({"index": 0})

	var added: Dictionary = _ops.edit_scene("add_node", params)

	assert_eq(_warning_of(added), "", "no warning: %s" % added)
	var pages: Array = [["New"], PAGES[0], PAGES[1], PAGES[2], PAGES[3]]
	# The current tab follows its page, Sound, now at index 1 (TabBar::move_tab).
	var tab_lines: Array = ["current_tab = 1", 'tab_2/title = "Net"', "tab_4/disabled = true"]
	var expected: String = _tabs_text(pages, tab_lines)
	assert_eq(_saved(tabs_scene), expected, "the title and disabled follow their pages")
	_remove_scene(tabs_scene)


func test_an_insert_at_the_end_changes_only_the_new_section() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))
	var params: Dictionary = _add_page(null)

	var added: Dictionary = _ops.edit_scene("add_node", params)

	assert_eq(_warning_of(added), "", "no warning: %s" % added)
	var pages: Array = PAGES + [["New"]]
	var expected: String = _tabs_text(pages, TABS_LINES)
	assert_eq(_saved(tabs_scene), expected, "only the new section")
	_remove_scene(tabs_scene)


func test_a_tab_index_set_in_add_node_properties_cannot_desync_the_titles() -> void:
	var text: String = _tabs_text(PAGES, TABS_LINES)
	_write(tabs_scene, text)
	var params: Dictionary = _add_page({"after": "Gameplay"})
	params["properties"] = {"metadata/_tab_index": 0}
	var refusal: String = (
		"metadata/_tab_index is set by Godot from the page's position in its TabContainer; "
		+ "move the node instead."
	)

	var added: Dictionary = _ops.edit_scene("add_node", params)
	var update: Dictionary = {
		"nodePath": "Tabs/Server", "property": "metadata/_tab_index", "value": 0
	}
	var updated: Dictionary = _ops.edit_scene(
		"set_node_properties", {"scene": tabs_scene, "updates": [update]}
	)

	assert_eq(added, {"ok": false, "error": refusal}, "add_node refuses the key")
	assert_eq(updated, {"ok": false, "error": refusal}, "set_node_properties refuses the key")
	assert_eq(_saved(tabs_scene), text, "nothing is saved")
	_remove_scene(tabs_scene)


func test_a_move_among_pages_shifts_tab_properties() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))
	var params: Dictionary = {
		"scene": tabs_scene, "nodePath": "Tabs/Controls", "position": {"index": 0}
	}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_eq(_warning_of(moved), "", "no warning: %s" % moved)
	var pages: Array = [PAGES[3], PAGES[0], PAGES[1], PAGES[2]]
	var tab_lines: Array = ["current_tab = 1", "tab_0/disabled = true", 'tab_2/title = "Net"']
	var expected: String = _tabs_text(pages, tab_lines)
	assert_eq(_saved(tabs_scene), expected, "disabled and the title follow their pages")
	_remove_scene(tabs_scene)


func test_deleting_a_page_shifts_tab_properties() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))
	var params: Dictionary = {"scene": tabs_scene, "nodePaths": ["Tabs/Sound"]}

	var deleted: Dictionary = _ops.edit_scene("delete_nodes", params)

	assert_eq(_warning_of(deleted), "", "no warning: %s" % deleted)
	var pages: Array = [PAGES[1], PAGES[2], PAGES[3]]
	var tab_lines: Array = ["current_tab = 0", 'tab_0/title = "Net"', "tab_2/disabled = true"]
	var expected: String = _tabs_text(pages, tab_lines)
	assert_eq(_saved(tabs_scene), expected, "the title and disabled follow their pages")
	_remove_scene(tabs_scene)


func test_duplicating_a_page_keeps_its_custom_title() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))

	var copied: Dictionary = _ops.edit_scene(
		"duplicate_node", {"scene": tabs_scene, "nodePath": "Tabs/Gameplay"}
	)

	assert_eq(_warning_of(copied), "", "no warning: %s" % copied)
	var pages: Array = [PAGES[0], PAGES[1], ["Gameplay2"], PAGES[2], PAGES[3]]
	var tab_lines: Array = ["current_tab = 0", 'tab_1/title = "Net"', "tab_4/disabled = true"]
	assert_eq(_saved(tabs_scene), _tabs_text(pages, tab_lines), "Gameplay keeps the title Net")
	_remove_scene(tabs_scene)


func test_duplicating_a_page_keeps_its_disabled_state() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))

	var copied: Dictionary = _ops.edit_scene(
		"duplicate_node", {"scene": tabs_scene, "nodePath": "Tabs/Controls"}
	)

	assert_eq(_warning_of(copied), "", "no warning: %s" % copied)
	var pages: Array = PAGES + [["Controls2"]]
	assert_eq(_saved(tabs_scene), _tabs_text(pages, TABS_LINES), "Controls stays disabled")
	_remove_scene(tabs_scene)


func test_duplicating_the_current_page_keeps_it_current() -> void:
	_write(tabs_scene, _tabs_text(PAGES, TABS_LINES))

	var copied: Dictionary = _ops.edit_scene(
		"duplicate_node", {"scene": tabs_scene, "nodePath": "Tabs/Sound"}
	)

	assert_eq(_warning_of(copied), "", "no warning: %s" % copied)
	var pages: Array = [PAGES[0], ["Sound2"], PAGES[1], PAGES[2], PAGES[3]]
	var tab_lines: Array = ["current_tab = 0", 'tab_2/title = "Net"', "tab_4/disabled = true"]
	assert_eq(_saved(tabs_scene), _tabs_text(pages, tab_lines), "Sound stays the current page")
	_remove_scene(tabs_scene)


func test_an_unrelated_edit_keeps_a_stored_current_tab() -> void:
	var text: String = _tabs_text(SERVER_CURRENT_PAGES, SERVER_CURRENT_LINES)
	_write(tabs_scene, text)

	var edited: Dictionary = _ops.edit_scene("set_node_properties", _side_text_update("b"))

	assert_eq(_warning_of(edited), "", "no warning: %s" % edited)
	var saved: String = _saved(tabs_scene)
	assert_eq(_visible_lines(saved), _visible_lines(text), "every page keeps its visibility")
	var expected: String = _with_side_edited(text)
	assert_eq(saved, expected, "current_tab = 2 is kept, only Side's text changes")
	_remove_scene(tabs_scene)


func test_an_out_of_range_current_tab_is_left_to_the_engine() -> void:
	var tab_lines: Array = ["current_tab = 9", 'tab_1/title = "Net"', "tab_3/disabled = true"]
	var text: String = _tabs_text(PAGES, tab_lines)
	_write(tabs_scene, text)

	var edited: Dictionary = _ops.edit_scene("set_node_properties", _side_text_update("b"))

	assert_eq(_warning_of(edited), "", "no warning: %s" % edited)
	var expected: String = _with_side_edited(text.replace("current_tab = 9", "current_tab = 0"))
	assert_eq(_saved(tabs_scene), expected, "the save holds the tab bar's current page, 0")
	_remove_scene(tabs_scene)


func test_a_batch_edit_keeps_a_stored_current_tab() -> void:
	var text: String = _tabs_text(SERVER_CURRENT_PAGES, SERVER_CURRENT_LINES)
	_write(tabs_scene, text)
	var step: Dictionary = {"op": "set_node_properties", "params": _side_text_update("b")}

	var batch: Dictionary = _ops.batch_scene({"scene": tabs_scene, "steps": [step]})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	var expected: String = _with_side_edited(text)
	assert_eq(_saved(tabs_scene), expected, "current_tab = 2 is kept, only Side's text changes")
	_remove_scene(tabs_scene)


func test_a_non_tab_parent_is_unaffected() -> void:
	var head: Array = [
		'[gd_scene format=3 uid="%s"]' % TABS_UID,
		"",
		'[node name="Root" type="Control" unique_id=1001]',
		"",
		'[node name="Box" type="VBoxContainer" parent="." unique_id=1002]',
		"layout_mode = 0",
		"",
		'[node name="A" type="Label" parent="Box" unique_id=1003]',
		"layout_mode = 2",
		'text = "a"',
		"",
	]
	var tail: Array = [
		'[node name="B" type="Label" parent="Box" unique_id=1004]',
		"layout_mode = 2",
		"size_flags_vertical = 4",
		'text = "b"',
	]
	_write(tabs_scene, _text(head + tail))
	var params: Dictionary = {
		"scene": tabs_scene,
		"parent": "Box",
		"nodeType": "Label",
		"nodeName": "New",
		"position": {"after": "A"},
	}

	var added: Dictionary = _ops.edit_scene("add_node", params)

	assert_eq(_warning_of(added), "", "no warning: %s" % added)
	var inserted: Array = [
		'[node name="New" type="Label" parent="Box" unique_id=0]', "layout_mode = 2", ""
	]
	assert_eq(_saved(tabs_scene), _text(head + inserted + tail), "only the new section")
	_remove_scene(tabs_scene)


## The fixture's text: Root, then Tabs storing tab_lines after layout_mode, then pages ([name,
## unique_id, hidden, title], or [name] for a new page, _page_lines), each with its
## metadata/_tab_index its place, then Side, a Label storing the size_flags_vertical = 4 Godot's
## pack drops.
func _tabs_text(pages: Array, tab_lines: Array) -> String:
	var lines: Array = [
		'[gd_scene format=3 uid="%s"]' % TABS_UID,
		"",
		'[node name="Root" type="Control" unique_id=1001]',
		"layout_mode = 3",
		"anchors_preset = 15",
		"anchor_right = 1.0",
		"anchor_bottom = 1.0",
		"grow_horizontal = 2",
		"grow_vertical = 2",
		"",
		'[node name="Tabs" type="TabContainer" parent="." unique_id=1002]',
		"layout_mode = 0",
	]
	lines.append_array(tab_lines)
	lines.append("")
	for index in pages.size():
		lines.append_array(_page_lines(pages[index], index))
	(
		lines
		. append_array(
			[
				'[node name="Side" type="Label" parent="." unique_id=1007]',
				"layout_mode = 0",
				"size_flags_vertical = 4",
				'text = "a"',
			]
		)
	)
	return _text(lines)


## The section of page ([name, unique_id, hidden, title], or [name] for a new one, whose
## unique_id _saved masks to 0) at index.
func _page_lines(page: Array, index: int) -> Array:
	if page.size() == 1:
		var head: String = '[node name="%s" type="VBoxContainer" parent="Tabs" unique_id=0]'
		return [head % page[0], "layout_mode = 2", "metadata/_tab_index = %d" % index, ""]
	var lines: Array = [
		'[node name="%s" type="VBoxContainer" parent="Tabs" unique_id=%d]' % [page[0], page[1]]
	]
	if page[2]:
		lines.append("visible = false")
	lines.append("layout_mode = 2")
	if not str(page[3]).is_empty():
		lines.append('metadata/_tab_name = "%s"' % page[3])
	lines.append_array(["metadata/_tab_index = %d" % index, ""])
	return lines


## add_node's params for a VBoxContainer page New under Tabs, at position (none when null).
func _add_page(position: Variant) -> Dictionary:
	var params: Dictionary = {
		"scene": tabs_scene, "parent": "Tabs", "nodeType": "VBoxContainer", "nodeName": "New"
	}
	if position != null:
		params["position"] = position
	return params


## set_node_properties' params setting Side's text to value, an edit away from the TabContainer.
func _side_text_update(value: String) -> Dictionary:
	var update: Dictionary = {"nodePath": "Side", "property": "text", "value": value}
	return {"scene": tabs_scene, "updates": [update]}


## text with Side's text set to "b" as the save writes it: the section the edit changed in Godot's
## pack form, its text before its size_flags_vertical.
func _with_side_edited(text: String) -> String:
	return text.replace(
		'size_flags_vertical = 4\ntext = "a"', 'text = "b"\nsize_flags_vertical = 4'
	)


## Each section's node line and its visible line, in order, for the pages' visibility in text.
func _visible_lines(text: String) -> Array:
	var lines: Array = []
	for line in text.split("\n"):
		if line.begins_with("[node ") or line.begins_with("visible = "):
			lines.append(line)
	return lines


## The warning of an edit's result, "" when it has none; a failed edit's error, so it shows.
func _warning_of(edited: Dictionary) -> String:
	if not edited.get("ok", false):
		return "error: %s" % edited.get("error", "")
	return str((edited["result"] as Dictionary).get("warning", ""))


## The text saved at path with the unique_id of a new node (New, or a copy named with a 2) masked
## to 0, since a new node's id is random.
func _saved(path: String) -> String:
	var pattern: String = '(\\[node name="(?:New|[A-Za-z]+2)"[^\\]]*) unique_id=\\d+\\]'
	var masked := RegEx.create_from_string(pattern)
	return masked.sub(FileAccess.get_file_as_string(path), "$1 unique_id=0]")


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	assert_true(file != null, "%s opens for writing" % path)
	file.store_string(text)
	file.close()


func _remove_scene(path: String) -> void:
	DirAccess.remove_absolute(path)
	DirAccess.remove_absolute(path + ".uidren")


func _text(lines: Array) -> String:
	return "\n".join(PackedStringArray(lines)) + "\n"
