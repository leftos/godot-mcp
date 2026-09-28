extends "res://gd_test.gd"
## SceneSplice.splice (headless/scene_splice.gd) on scene texts written inline: the original as a
## hand-written file has it, the saved one as Godot 4.7 writes it (no load_steps, unique_id= on
## each node, renumbered ids, properties reordered); the output is checked byte for byte.

const SCENE_SPLICE_SCRIPT := "../../headless/scene_splice.gd"

const ORIGINAL_HEADER := '[gd_scene load_steps=3 format=3 uid="uid://bplevel00000a"]'
const ENEMY_EXT := '[ext_resource type="PackedScene" path="res://enemy.tscn" id="1"]'
const HOLDER_EXT := '[ext_resource type="Script" path="res://holder.gd" id="2"]'
const SAVED_HEADER := '[gd_scene format=3 uid="uid://bplevel00000a"]'
const SAVED_ENEMY_EXT := '[ext_resource type="PackedScene" path="res://enemy.tscn" id="1_abcde"]'
const SAVED_HOLDER_EXT := '[ext_resource type="Script" path="res://holder.gd" id="2_fghij"]'
const SAVED_BOX := '[node name="Box" type="Node2D" parent="." unique_id=33]'
const MARKER := '[node name="Marker" type="Sprite2D" parent="Box" unique_id=55]'
const BOX_SCRIPT_EXT := '[ext_resource type="Script" path="res://box.gd" id="3_klmno"]'
const CONNECTION := '[connection signal="ready" from="Box" to="." method="_on_box_ready"]'

var _splice: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_SPLICE_SCRIPT).simplify_path()
)


func test_an_unchanged_scene_splices_to_the_original() -> void:
	var spliced: Dictionary = _splice.splice(_original(), _saved([], _saved_box([])))

	assert_eq(spliced, {"text": _original()}, "a save that only changed the form keeps the file")


func test_an_added_node_adds_only_its_section() -> void:
	var box: Array = _saved_box([]) + ["", MARKER, "position = Vector2(3, 4)"]

	var spliced: Dictionary = _splice.splice(_original(), _saved([], box))

	var expected: String = _original().replace(
		'[node name="Holder"', MARKER + "\nposition = Vector2(3, 4)\n\n" + '[node name="Holder"'
	)
	assert_eq(spliced, {"text": expected}, "the new node is inserted after its parent, as saved")


func test_a_changed_property_replaces_only_that_node() -> void:
	var box: Array = [SAVED_BOX, "position = Vector2(5, 6)", "visible = false"]

	var spliced: Dictionary = _splice.splice(_original(), _saved([], box))

	var expected: String = _original().replace(
		'[node name="Box" type="Node2D" parent="."]\nvisible = false\nposition = Vector2(1, 2)\n',
		SAVED_BOX + "\nposition = Vector2(5, 6)\nvisible = false\n"
	)
	assert_eq(spliced, {"text": expected}, "only the changed node takes the saved text")


func test_a_deleted_node_drops_only_its_section() -> void:
	var spliced: Dictionary = _splice.splice(_original(), _saved([], []))

	var expected: String = _original().replace(
		'[node name="Box" type="Node2D" parent="."]\nvisible = false\nposition = Vector2(1, 2)\n\n',
		""
	)
	assert_eq(spliced, {"text": expected}, "the deleted node's section and its blank line go")


func test_a_new_ext_resource_is_added_after_the_last() -> void:
	var box: Array = _saved_box(['script = ExtResource("3_klmno")'])

	var spliced: Dictionary = _splice.splice(_original(), _saved([BOX_SCRIPT_EXT], box))

	var expected: String = (
		_original()
		. replace("load_steps=3", "load_steps=4")
		. replace(HOLDER_EXT + "\n", HOLDER_EXT + "\n" + BOX_SCRIPT_EXT + "\n")
		. replace(
			'[node name="Box" type="Node2D" parent="."]\nvisible = false\nposition = Vector2(1, 2)\n',
			(
				SAVED_BOX
				+ '\nposition = Vector2(1, 2)\nvisible = false\nscript = ExtResource("3_klmno")\n'
			)
		)
	)
	assert_eq(spliced, {"text": expected}, "the script's ext_resource follows the last one")


func test_renumbered_ids_map_back_to_the_originals() -> void:
	var saved: String = _saved([], _saved_box([])).replace(
		'unique_id=22 instance=ExtResource("1_abcde")]\n',
		'unique_id=22 instance=ExtResource("1_abcde")]\nposition = Vector2(7, 8)\n'
	)

	var spliced: Dictionary = _splice.splice(_original(), saved)

	var expected: String = _original().replace(
		'[node name="Boss" parent="." instance=ExtResource("1")]\n',
		(
			'[node name="Boss" parent="." unique_id=22 instance=ExtResource("1")]\n'
			+ "position = Vector2(7, 8)\n"
		)
	)
	assert_eq(spliced, {"text": expected}, "the changed node refers to the original's ext id")


func test_a_sub_resource_is_matched_by_content() -> void:
	var original: String = _text(
		[
			"[gd_scene load_steps=2 format=3]",
			"",
			'[sub_resource type="RectangleShape2D" id="1"]',
			"size = Vector2(4, 4)",
			"",
			'[node name="Body" type="StaticBody2D"]',
			"",
			'[node name="Shape" type="CollisionShape2D" parent="."]',
			'shape = SubResource("1")',
			"position = Vector2(1, 1)",
		]
	)
	var saved: String = _text(
		[
			"[gd_scene format=3]",
			"",
			'[sub_resource type="RectangleShape2D" id="RectangleShape2D_ab12c"]',
			"size = Vector2(4, 4)",
			"",
			'[node name="Body" type="StaticBody2D" unique_id=1]',
			"",
			'[node name="Shape" type="CollisionShape2D" parent="." unique_id=2]',
			"position = Vector2(2, 2)",
			'shape = SubResource("RectangleShape2D_ab12c")',
		]
	)

	var spliced: Dictionary = _splice.splice(original, saved)

	var expected: String = original.replace(
		(
			'[node name="Shape" type="CollisionShape2D" parent="."]\nshape = SubResource("1")\n'
			+ "position = Vector2(1, 1)\n"
		),
		(
			'[node name="Shape" type="CollisionShape2D" parent="." unique_id=2]\n'
			+ 'position = Vector2(2, 2)\nshape = SubResource("1")\n'
		)
	)
	assert_eq(spliced, {"text": expected}, "the sub_resource is kept and referred to by its id")


func test_an_added_connection_adds_only_its_section() -> void:
	var saved: String = _saved([], _saved_box([])) + "\n" + CONNECTION + "\n"

	var spliced: Dictionary = _splice.splice(_original(), saved)

	assert_eq(spliced, {"text": _original() + "\n" + CONNECTION + "\n"}, "the connection is added")


func test_a_header_without_load_steps_stays_without() -> void:
	var original: String = _original().replace("load_steps=3 ", "")
	var box: Array = _saved_box(['script = ExtResource("3_klmno")'])

	var spliced: Dictionary = _splice.splice(original, _saved([BOX_SCRIPT_EXT], box))

	assert_true(spliced.has("text"), "the edit splices")
	var header: String = str(spliced.get("text", "")).get_slice("\n", 0)
	assert_eq(header, '[gd_scene format=3 uid="uid://bplevel00000a"]', "no load_steps is added")


func test_a_duplicate_key_falls_back() -> void:
	var original: String = _original() + '\n[node name="Box" type="Node2D" parent="."]\n'

	var spliced: Dictionary = _splice.splice(original, _saved([], _saved_box([])))

	assert_eq(
		spliced,
		{"fallback": "two sections have the key node parent=. name=Box"},
		"two nodes with one parent and name cannot be told apart"
	)


func test_an_unknown_id_falls_back() -> void:
	var original: String = _original().replace('ExtResource("2")', 'ExtResource("9")')

	var spliced: Dictionary = _splice.splice(original, _saved([], _saved_box([])))

	assert_eq(
		spliced,
		{"fallback": 'ExtResource("9") names no ext_resource'},
		"a reference to no section cannot be compared"
	)


func test_a_colliding_new_id_falls_back() -> void:
	var new_ext: String = '[ext_resource type="Script" path="res://box.gd" id="1"]'
	var box: Array = _saved_box(['script = ExtResource("1")'])

	var spliced: Dictionary = _splice.splice(_original(), _saved([new_ext], box))

	assert_eq(
		spliced,
		{"fallback": 'the new ext_resource\'s id "1" is already used in the file'},
		"a new resource cannot take an id the file already gives another"
	)


func test_the_self_check_falls_back_when_an_edit_would_be_lost() -> void:
	var changed: Array = [SAVED_BOX, "position = Vector2(5, 6)", "visible = false"]

	var lost: String = _splice.self_check(_original(), _saved([], changed))
	var kept: String = _splice.self_check(_original(), _saved([], _saved_box([])))

	assert_eq(
		lost,
		"the spliced text changed the section node parent=. name=Box",
		"a text that does not hold the saved change fails the check"
	)
	assert_eq(kept, "", "a text that reads as the save passes")


func test_identical_sub_resources_splice_without_falling_back() -> void:
	var spliced: Dictionary = _splice.splice(_shapes(), _saved_shapes("Vector2(2, 2)", false))

	var expected: String = _shapes().replace(
		(
			'[node name="B" type="CollisionShape2D" parent="."]\nshape = SubResource("2")\n'
			+ "position = Vector2(1, 1)\n"
		),
		(
			'[node name="B" type="CollisionShape2D" parent="." unique_id=3]\n'
			+ 'position = Vector2(2, 2)\nshape = SubResource("2")\n'
		)
	)
	assert_eq(spliced, {"text": expected}, "both equal shapes keep their ids; only B changes")


func test_an_added_identical_sub_resource_is_added() -> void:
	var spliced: Dictionary = _splice.splice(_shapes(), _saved_shapes("Vector2(1, 1)", true))

	var expected: String = (
		_shapes().replace("load_steps=3", "load_steps=4").replace(
			'[node name="Body"',
			(
				'[sub_resource type="RectangleShape2D" id="RectangleShape2D_ccccc"]\n'
				+ 'size = Vector2(4, 4)\n\n[node name="Body"'
			)
		)
		+ '\n[node name="C" type="CollisionShape2D" parent="." unique_id=4]\n'
		+ 'shape = SubResource("RectangleShape2D_ccccc")\n'
	)
	assert_eq(spliced, {"text": expected}, "the third equal shape and its node are added")


## A body with two collision shapes, A and B, whose rectangles are equal.
func _shapes() -> String:
	return _text(
		[
			"[gd_scene load_steps=3 format=3]",
			"",
			'[sub_resource type="RectangleShape2D" id="1"]',
			"size = Vector2(4, 4)",
			"",
			'[sub_resource type="RectangleShape2D" id="2"]',
			"size = Vector2(4, 4)",
			"",
			'[node name="Body" type="StaticBody2D"]',
			"",
			'[node name="A" type="CollisionShape2D" parent="."]',
			'shape = SubResource("1")',
			"",
			'[node name="B" type="CollisionShape2D" parent="."]',
			'shape = SubResource("2")',
			"position = Vector2(1, 1)",
		]
	)


## _shapes as Godot 4.7 saves it, with B at b_position, and with a third equal shape on a new node C
## when third.
func _saved_shapes(b_position: String, third: bool) -> String:
	var lines: Array = ["[gd_scene format=3]", ""]
	for id: String in ["aaaaa", "bbbbb", "ccccc"] if third else ["aaaaa", "bbbbb"]:
		lines += ['[sub_resource type="RectangleShape2D" id="RectangleShape2D_%s"]' % id]
		lines += ["size = Vector2(4, 4)", ""]
	lines += ['[node name="Body" type="StaticBody2D" unique_id=1]', ""]
	lines += ['[node name="A" type="CollisionShape2D" parent="." unique_id=2]']
	lines += ['shape = SubResource("RectangleShape2D_aaaaa")', ""]
	lines += ['[node name="B" type="CollisionShape2D" parent="." unique_id=3]']
	lines += ["position = " + b_position, 'shape = SubResource("RectangleShape2D_bbbbb")']
	if third:
		lines += ["", '[node name="C" type="CollisionShape2D" parent="." unique_id=4]']
		lines += ['shape = SubResource("RectangleShape2D_ccccc")']
	return _text(lines)


func _original() -> String:
	return _text(
		[
			ORIGINAL_HEADER,
			"",
			ENEMY_EXT,
			HOLDER_EXT,
			"",
			'[node name="Level" type="Node2D"]',
			"",
			'[node name="Boss" parent="." instance=ExtResource("1")]',
			"",
			'[node name="Box" type="Node2D" parent="."]',
			"visible = false",
			"position = Vector2(1, 2)",
			"",
			'[node name="Holder" type="Node2D" parent="."]',
			'script = ExtResource("2")',
		]
	)


## The original as Godot 4.7 saves it, with new_exts after its ext_resources and box (the Box
## section's lines, none to leave it out) between Boss and Holder.
func _saved(new_exts: Array, box: Array) -> String:
	var lines: Array = [SAVED_HEADER, "", SAVED_ENEMY_EXT, SAVED_HOLDER_EXT] + new_exts
	lines += ["", '[node name="Level" type="Node2D" unique_id=11]', ""]
	lines += ['[node name="Boss" parent="." unique_id=22 instance=ExtResource("1_abcde")]', ""]
	if not box.is_empty():
		lines += box + [""]
	lines += ['[node name="Holder" type="Node2D" parent="." unique_id=44]']
	lines += ['script = ExtResource("2_fghij")']
	return _text(lines)


## The Box section as Godot 4.7 saves it, its properties reordered, then extra lines.
func _saved_box(extra: Array) -> Array:
	return [SAVED_BOX, "position = Vector2(1, 2)", "visible = false"] + extra


func _text(lines: Array) -> String:
	return "\n".join(PackedStringArray(lines)) + "\n"
