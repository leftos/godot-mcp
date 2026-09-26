# gdlint: disable=private-method-call
# _holds is a private helper of scene_props.gd, tested directly.
extends "res://gd_test.gd"
## The headless property edits' pure helpers (headless/scene_props.gd): the names a scene file
## stores for a node, merged across an instanced scene and an inherited base, and which instance
## path holds a node path. The scenes are written under user:// for the test.

const SCENE_PROPS_SCRIPT := "../../headless/scene_props.gd"
const ENEMY := "user://scene_props_enemy.tscn"
const LEVEL := "user://scene_props_level.tscn"
const ELITE := "user://scene_props_elite.tscn"
const ENEMY_TEXT := (
	'[gd_scene format=3]\n\n[node name="Enemy" type="Node2D"]\n\n'
	+ '[node name="Sprite" type="Sprite2D" parent="."]\nposition = Vector2(1, 2)\n'
)
const INSTANCING := (
	"[gd_scene load_steps=2 format=3]\n\n"
	+ '[ext_resource type="PackedScene" path="%s" id="1"]\n\n'
)
const LEVEL_TEXT := (
	INSTANCING
	+ '[node name="Level" type="Node2D"]\n\n[node name="Boss" parent="." instance=ExtResource("1")]\n'
	+ "visible = false\n"
)
const ELITE_TEXT := (
	INSTANCING
	+ '[node name="Elite" instance=ExtResource("1")]\n\n[node name="Sprite" parent="."]\n'
	+ "visible = false\n"
)

var _props: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_PROPS_SCRIPT).simplify_path()
)


func test_stored_names_merge_an_instance_and_a_base() -> void:
	_write(ENEMY, ENEMY_TEXT)
	_write(LEVEL, LEVEL_TEXT % ENEMY)
	_write(ELITE, ELITE_TEXT % ENEMY)
	assert_eq(_props.stored_names(LEVEL, "Boss/Sprite"), ["position"], "a node inside an instance")
	assert_eq(_props.stored_names(LEVEL, "Boss"), ["visible"], "an instance root's own override")
	assert_eq(
		_props.stored_names(ELITE, "Sprite"),
		["position", "visible"],
		"a base's values, then its own"
	)
	assert_eq(_props.stored_names(ELITE, "Nope"), [], "a node the file does not list")


func test_holds_a_path_at_or_under_an_instance() -> void:
	assert_true(_props._holds(".", "Boss/Sprite"), "the root holds every path")
	assert_true(_props._holds("Boss", "Boss"), "the instance itself")
	assert_true(_props._holds("Boss", "Boss/Sprite"), "a node under it")
	assert_true(not _props._holds("Boss", "Bossy/Sprite"), "a sibling sharing a prefix")
	assert_true(not _props._holds("Boss/Sprite", "Boss"), "an ancestor")


static func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()
