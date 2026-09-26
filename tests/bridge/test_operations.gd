extends "res://gd_test.gd"
## The headless operations' pure helpers (headless/operations.gd): the request file's parsing, the
## grouping of logged errors by the file they name, the C# scripts among a scene's dependencies,
## and the node tree get_scene_file_tree builds from scene states.

const HEADLESS_SCRIPT := "../../headless/operations.gd"
const SCENE_EDIT_SCRIPT := "../../headless/scene_edit.gd"
const SCENE_NODES_SCRIPT := "../../headless/scene_nodes.gd"

var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(HEADLESS_SCRIPT).simplify_path()
)
var _edit: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_EDIT_SCRIPT).simplify_path()
)
var _nodes: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_NODES_SCRIPT).simplify_path()
)


func test_request_gives_op_params_and_result() -> void:
	var text := '{"op": "validate", "params": {"targets": ["res://a.gd"]}, "result": "C:/r.json"}'
	var expected: Dictionary = {
		"op": "validate", "params": {"targets": ["res://a.gd"]}, "result": "C:/r.json"
	}
	assert_eq(_ops.parse_request(text), expected, "a full request")
	var bare: Dictionary = _ops.parse_request('{"op": "validate", "result": "r.json"}')
	assert_eq(bare["params"], {}, "params default to an empty object")


func test_request_refusals_say_what_is_wrong() -> void:
	assert_true(
		(_ops.parse_request("{op")["error"] as String).begins_with("the request is not JSON"),
		"text that is not JSON"
	)
	assert_eq(_ops.parse_request("[1]"), {"error": "the request is not a JSON object"}, "an array")
	assert_eq(
		_ops.parse_request('{"result": "r.json"}'), {"error": "the request has no op"}, "no op"
	)
	assert_eq(
		_ops.parse_request('{"op": "validate", "result": ""}'),
		{"error": "the request has no result"},
		"an empty result path"
	)
	assert_eq(
		_ops.parse_request('{"op": "validate", "result": "r.json", "params": 3}'),
		{"error": "the request's params is not an object"},
		"params that are not an object"
	)


func test_errors_group_under_the_res_file_they_name() -> void:
	var groups: Dictionary = {}
	var entries: Array = [
		_entry("error", "Parse Error: x", "res://lib.gd", 4),
		_entry("error", "Cannot instantiate C# script", "modules/mono/csharp_script.cpp", 2605),
	]
	_ops.group_errors(entries, "res://main.tscn", groups)
	assert_eq(groups.keys(), ["res://lib.gd", "res://main.tscn"], "one group per file")
	assert_eq(
		groups["res://main.tscn"],
		[
			{
				"message": "Cannot instantiate C# script",
				"file": "modules/mono/csharp_script.cpp",
				"line": 2605
			}
		],
		"an engine file's error lands under the file being checked, its site kept"
	)


func test_warnings_are_left_out_and_repeats_listed_once() -> void:
	var groups: Dictionary = {}
	var error: Dictionary = _entry("error", "Parse Error: x", "res://a.gd", 2)
	_ops.group_errors([error, _entry("warning", "unused", "res://a.gd", 3)], "res://a.gd", groups)
	_ops.group_errors([error], "res://b.tscn", groups)
	var listed: Array = [{"message": "Parse Error: x", "file": "res://a.gd", "line": 2}]
	assert_eq(groups, {"res://a.gd": listed}, "one error, listed once")


func test_results_are_ordered_by_path() -> void:
	var groups: Dictionary = {"res://b.gd": [1], "res://a.gd": [2]}
	var expected: Array = [
		{"path": "res://a.gd", "errors": [2]}, {"path": "res://b.gd", "errors": [1]}
	]
	assert_eq(_ops.results_of(groups), expected, "results by path")


func test_csharp_dependencies_read_every_entry_form() -> void:
	var dependencies: PackedStringArray = [
		"res://Player.cs::Script",
		"uid://b1::Script::res://Enemy.cs",
		"uid://b2::::res://Boss.cs",
		"res://art/icon.png::Texture2D",
		"res://Player.cs",
	]
	var expected: PackedStringArray = ["res://Player.cs", "res://Enemy.cs", "res://Boss.cs"]
	assert_eq(_ops.csharp_dependencies(dependencies), expected, "each C# script once")


func test_node_paths_are_relative_to_the_scene_root() -> void:
	assert_eq(_ops.normalise_node_path(""), ".", "empty is the root")
	assert_eq(_ops.normalise_node_path("./A/B/"), "A/B", "a SceneState path")
	assert_eq(_ops.join_node_path(".", "."), ".", "the scene's own root")
	assert_eq(_ops.join_node_path(".", "./A"), "A", "a child of the scene's root")
	assert_eq(_ops.join_node_path("A", "."), "A", "an instance's root is the instancing node")
	assert_eq(_ops.join_node_path("A", "./B/C"), "A/B/C", "a node inside an instance")


func test_an_instancing_node_lays_its_values_over_the_instance() -> void:
	var tree: Dictionary = {"nodes": {}, "children": {}}
	_ops.merge_node(tree, ".", _facts("Main", "Node2D", {}))
	var instanced := {"script": "res://enemy.gd", "groups": ["enemies"]}
	_ops.merge_node(tree, "Foe", _facts("Enemy", "CharacterBody2D", instanced))
	_ops.merge_node(tree, "Foe/Sprite", _facts("Sprite", "Sprite2D", {}))
	var instancing := {"instance": "res://enemy.tscn", "groups": ["boss"]}
	_ops.merge_node(tree, "Foe", _facts("Foe", "", instancing))
	var listed: Array = []
	_ops.list_nodes(tree, ".", -1, listed)
	var foe: Dictionary = {
		"path": "Foe",
		"name": "Foe",
		"type": "CharacterBody2D",
		"script": "res://enemy.gd",
		"instance": "res://enemy.tscn",
		"groups": ["enemies", "boss"],
		"childCount": 1,
	}
	assert_eq(listed.size(), 3, "every node")
	assert_eq(listed[1], foe, "the instance's type and script, the instancing node's name")
	assert_eq(listed[2]["path"], "Foe/Sprite", "the instance's own child under it")
	var shallow: Array = []
	_ops.list_nodes(tree, "Foe", 0, shallow)
	assert_eq(shallow.size(), 1, "maxDepth 0 lists the root alone")


func test_a_new_scene_root_is_named_after_its_file() -> void:
	assert_eq(_edit.root_name_for("res://player_ship.tscn"), "PlayerShip", "snake_case")
	assert_eq(_edit.root_name_for("res://rooms/boss-room.tscn"), "BossRoom", "kebab-case")
	assert_eq(_edit.root_name_for("res://Level1.tscn"), "Level1", "already PascalCase")
	assert_eq(_edit.root_name_for("res://hud_HUDMenu.scn"), "HudHUDMenu", "inner capitals kept")
	assert_eq(_edit.root_name_for("res://__.tscn"), "Root", "no word at all")


func test_ext_resources_get_their_uids_back() -> void:
	var text := (
		"\n"
		. join(
			[
				"[gd_scene format=3]",
				"",
				'[ext_resource type="Script" path="res://a.gd" id="1_a"]',
				'[ext_resource type="PackedScene" uid="uid://kept" path="res://b.tscn" id="2_b"]',
				'[ext_resource type="Texture2D" path="res://c.png" id="3_c"]',
				"",
				'[node name="A" type="Node2D"]',
				'script_path = " path="res://a.gd"',
			]
		)
	)
	var uids: Dictionary = {
		"res://a.gd": "uid://aaa", "res://b.tscn": "uid://other", "res://z.gd": "uid://zzz"
	}
	var lines: PackedStringArray = _edit.with_ext_uids(text, uids).split("\n")
	assert_eq(lines.size(), 8, "no line added or lost")
	assert_eq(
		lines[2],
		'[ext_resource type="Script" uid="uid://aaa" path="res://a.gd" id="1_a"]',
		"a uid added before the path"
	)
	assert_eq(
		lines[3],
		'[ext_resource type="PackedScene" uid="uid://kept" path="res://b.tscn" id="2_b"]',
		"a uid already there is kept"
	)
	assert_eq(
		lines[4], '[ext_resource type="Texture2D" path="res://c.png" id="3_c"]', "no uid known"
	)
	assert_eq(lines[7], 'script_path = " path="res://a.gd"', "a line that is not a tag")
	assert_eq(_edit.ext_resource_path('[ext_resource type="Script" id="1"]'), "", "no path")
	var header := '[gd_scene load_steps=2 format=3 uid="uid://abc"]'
	assert_eq(_edit.quoted_value(header, "uid"), "uid://abc", "a header's uid")
	assert_eq(_edit.quoted_value('uid="uid://imp"', "uid"), "uid://imp", "an .import line")
	assert_eq(_edit.quoted_value('[x myuid="uid://no"]', "uid"), "", "only the whole key")


func test_a_file_s_own_uid_is_read_from_it_or_its_sidecar() -> void:
	_write("user://uid_probe.gd.uid", "uid://cscript\n")
	_write(
		"user://uid_probe.png.import",
		'[remap]\n\nimporter="texture"\nuid="uid://cimport"\npath="res://.godot/p.ctex"\n'
	)
	_write("user://uid_probe.tscn", '[gd_scene format=3 uid="uid://cscene"]\n\n[node name="A"]\n')
	assert_eq(_edit._uid_text_of("user://uid_probe.gd"), "uid://cscript", "a script's .uid file")
	assert_eq(_edit._uid_text_of("user://uid_probe.png"), "uid://cimport", "an .import uid line")
	assert_eq(_edit._uid_text_of("user://uid_probe.tscn"), "uid://cscene", "a text scene's header")
	assert_eq(_edit._uid_text_of("user://uid_missing.gd"), "", "no file and no sidecar")
	assert_eq(_edit._sidecar_uid("user://uid_missing.gd.uid"), "", "a missing sidecar")


func test_the_source_s_ext_resource_uids_are_read_by_path() -> void:
	var text := (
		"\n"
		. join(
			[
				'[gd_scene format=3 uid="uid://self"]',
				'[ext_resource type="Script" uid="uid://sgd" path="res://s.gd" id="1"]',
				'[ext_resource type="Texture2D" path="res://t.png" id="2"]',
			]
		)
	)
	assert_eq(_edit.ext_uids_in(text), {"res://s.gd": "uid://sgd"}, "only tags carrying a uid")


func test_a_copy_is_named_as_the_editor_names_a_duplicate() -> void:
	var taken := PackedStringArray(["Sprite", "Sprite2", "Box09", "Tail", "7"])
	assert_eq(_nodes.copy_name("Sprite", taken), "Sprite3", "Sprite2 is taken too")
	assert_eq(_nodes.copy_name("Sprite2", taken), "Sprite3", "the trailing number counts up")
	assert_eq(_nodes.copy_name("Tail", taken), "Tail2", "no number: 2 is added")
	assert_eq(_nodes.copy_name("Box09", taken), "Box10", "leading zeros carry as in the engine")
	assert_eq(_nodes.copy_name("7", taken), "8", "a name of digits alone")
	assert_eq(_nodes.copy_name("Head", taken), "Head", "a free name is kept")


func test_only_a_texture_2d_texture_property_takes_a_texture() -> void:
	var texture := {
		"name": "texture",
		"type": TYPE_OBJECT,
		"hint": PROPERTY_HINT_RESOURCE_TYPE,
		"hint_string": "Texture2D"
	}
	assert_true(_nodes.takes_texture_2d(texture), "Sprite2D's texture")
	texture["hint_string"] = "Texture"
	assert_true(_nodes.takes_texture_2d(texture), "a parent class of Texture2D")
	texture["hint_string"] = "Texture3D"
	assert_true(not _nodes.takes_texture_2d(texture), "another texture type")
	texture["hint_string"] = "Texture2D"
	texture["name"] = "normal_map"
	assert_true(not _nodes.takes_texture_2d(texture), "another property")
	var found: Array = []
	for type: String in [
		"Sprite2D", "Sprite3D", "TextureRect", "NinePatchRect", "Polygon2D", "Node2D"
	]:
		var node: Node = ClassDB.instantiate(type)
		if node.get_property_list().any(
			func(entry: Dictionary) -> bool: return _nodes.takes_texture_2d(entry)
		):
			found.append(type)
		node.free()
	assert_eq(
		found, ["Sprite2D", "Sprite3D", "TextureRect", "NinePatchRect", "Polygon2D"], "the classes"
	)


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()


func _entry(type: String, message: String, file: String, line: int) -> Dictionary:
	return {"type": type, "message": message, "file": file, "line": line}


func _facts(node_name: String, type: String, extra: Dictionary) -> Dictionary:
	var facts: Dictionary = {"name": node_name, "type": type, "groups": []}
	facts.merge(extra, true)
	return facts
