# gdlint: disable=private-method-call
# _holds is a private helper of scene_props.gd, tested directly.
extends "res://gd_test.gd"
## The headless property edits' pure helpers (headless/scene_props.gd): the names a scene file
## stores for a node, merged across an instanced scene and an inherited base, which instance
## path holds a node path, and the node a script path makes. The scenes and scripts are written
## under user:// for the test.

const SCENE_PROPS_SCRIPT := "../../headless/scene_props.gd"
const ENEMY := "user://scene_props_enemy.tscn"
const LEVEL := "user://scene_props_level.tscn"
const ELITE := "user://scene_props_elite.tscn"
const NODE_SCRIPT := "user://scene_props_node.gd"
const RESOURCE_SCRIPT := "user://scene_props_resource.gd"
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


func test_new_node_makes_a_node_of_a_script_path() -> void:
	_write(NODE_SCRIPT, 'extends Node2D\n\nvar tag := "probe"\n')
	var made: Dictionary = _props._new_node(NODE_SCRIPT, {"scene": LEVEL})
	assert_eq(made.get("type", ""), "Node2D", "a script extending Node2D makes one")
	assert_eq(made.get("script", ""), NODE_SCRIPT, "the result names the script")
	var node: Node = made.get("node")
	assert_true(node is Node2D, "the node is of the script's base class")
	assert_eq(node.get_script().resource_path, NODE_SCRIPT, "the script is attached")
	node.free()


func test_new_node_refuses_a_script_that_cannot_make_a_node() -> void:
	_write(RESOURCE_SCRIPT, "extends Resource\n")
	var made: Dictionary = _props._new_node(RESOURCE_SCRIPT, {"scene": LEVEL})
	assert_eq(
		made.get("error", ""),
		(
			(
				"nodeType '%s' is a script that cannot make a node: it must compile and extend a Node "
				+ "class that can be instanced."
			)
			% RESOURCE_SCRIPT
		),
		"a script whose base class is not a Node"
	)


static func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()
