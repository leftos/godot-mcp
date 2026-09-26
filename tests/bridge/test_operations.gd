extends "res://gd_test.gd"
## The headless operations' pure helpers (headless/operations.gd): the request file's parsing, the
## grouping of logged errors by the file they name, the C# scripts among a scene's dependencies,
## and the node tree get_scene_file_tree builds from scene states.

const HEADLESS_SCRIPT := "../../headless/operations.gd"

var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(HEADLESS_SCRIPT).simplify_path()
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


func _entry(type: String, message: String, file: String, line: int) -> Dictionary:
	return {"type": type, "message": message, "file": file, "line": line}


func _facts(node_name: String, type: String, extra: Dictionary) -> Dictionary:
	var facts: Dictionary = {"name": node_name, "type": type, "groups": []}
	facts.merge(extra, true)
	return facts
