extends "res://gd_test.gd"
## describe_class's reader (bridge/godot_mcp_class_info.gd): the edit distance, the close names an
## unknown class is answered with, engine classes with and without their inherited members, the
## project's script classes (JsonTestResource and JsonTestDerivedResource, in this project's
## global class list since run.ps1 gdtest imports it first), and the method pages.

var _info: GDScript = load_bridge_script("godot_mcp_class_info.gd")


func test_levenshtein_counts_single_character_edits() -> void:
	assert_eq(_info.levenshtein("node", "node"), 0, "identical strings")
	assert_eq(_info.levenshtein("node", "nodes"), 1, "one insertion")
	assert_eq(_info.levenshtein("nodes", "node"), 1, "one deletion")
	assert_eq(_info.levenshtein("node", "mode"), 1, "one substitution")
	assert_eq(_info.levenshtein("", ""), 0, "two empty strings")
	assert_eq(_info.levenshtein("", "abc"), 3, "an empty string against three letters")
	assert_eq(_info.levenshtein("abc", ""), 3, "three letters against an empty string")
	assert_eq(_info.levenshtein("kitten", "sitting"), 3, "two substitutions and an insertion")


func test_a_wrong_case_suggests_the_class_first() -> void:
	var described: Dictionary = _info.describe({"className": "Sprite2d"})

	assert_true(not described.has("result"), "Sprite2d is no class")
	var suggestions: Array = described.get("suggestions", [])
	assert_true(suggestions.size() <= 5, "at most five")
	assert_eq(suggestions[0] if not suggestions.is_empty() else "", "Sprite2D", "the first")
	assert_true(str(described.get("error", "")).contains("Sprite2D"), "the error names it")


func test_one_edit_away_suggests_the_class() -> void:
	var described: Dictionary = _info.describe({"className": "Nod3D"})

	var suggestions: Array = described.get("suggestions", [])
	assert_eq(suggestions[0] if not suggestions.is_empty() else "", "Node3D", "the first")
	assert_true(suggestions.size() <= 5, "at most five")


func test_a_script_class_is_described_from_its_script() -> void:
	var described: Dictionary = _info.describe({"className": "JsonTestResource"}).get("result", {})

	assert_eq(described.get("isScript"), true, "a script class")
	assert_eq(described.get("scriptPath"), "res://json_test_resource.gd", "its script")
	assert_eq(described.get("language"), "GDScript", "its language")
	assert_eq(described.get("inherits"), "Resource", "its base")
	assert_eq(described.get("inheritsChain"), ["Resource", "RefCounted", "Object"], "its bases")
	assert_eq(
		_named(described.get("properties", []), "amount"),
		{"name": "amount", "type": "int", "default": 0},
		"its exported variable"
	)
	assert_eq(_named(described.get("properties", []), "resource_name"), {}, "no engine member")


func test_inherited_appends_a_script_class_bases_members() -> void:
	var own: Dictionary = _info.describe({"className": "JsonTestDerivedResource"}).get("result", {})
	var all: Dictionary = (
		_info
		. describe({"className": "JsonTestDerivedResource", "inherited": true})
		. get("result", {})
	)

	assert_eq(
		own.get("inheritsChain"), ["JsonTestResource", "Resource", "RefCounted", "Object"], ""
	)
	assert_eq(_named(all.get("properties", []), "amount").get("type"), "int", "the base script's")
	assert_eq(_named(all.get("properties", []), "label").get("type"), "String", "its own")
	assert_eq(_named(own.get("properties", []), "resource_name"), {}, "no engine member own")
	assert_eq(
		_named(all.get("properties", []), "resource_name").get("type"), "String", "Resource's"
	)


func test_an_engine_class_lists_inherited_members_only_when_asked() -> void:
	var own: Dictionary = _info.describe({"className": "Node2D"}).get("result", {})
	var all: Dictionary = _info.describe({"className": "Node2D", "inherited": true}).get(
		"result", {}
	)

	assert_eq(own.get("isScript"), false, "an engine class")
	assert_eq(own.get("inherits"), "CanvasItem", "its parent")
	assert_eq(own.get("inheritsChain"), ["CanvasItem", "Node", "Object"], "its bases")
	assert_eq(own.get("canInstantiate"), true, "instantiable")
	assert_eq(
		_named(own.get("properties", []), "position"),
		{"name": "position", "type": "Vector2", "default": {"x": 0.0, "y": 0.0}},
		"its own property with its default"
	)
	# global_position is PROPERTY_USAGE_NONE (4.7.2 scene/2d/node_2d.cpp L511), listed all the same.
	assert_eq(
		_named(own.get("properties", []), "global_position"),
		{"name": "global_position", "type": "Vector2"},
		"a property the editor neither shows nor stores, without the default ClassDB has none of"
	)
	assert_eq(_named(own.get("properties", []), "process_mode"), {}, "Node's left out")
	assert_eq(_named(all.get("properties", []), "process_mode").get("type"), "int", "Node's")


func test_a_property_of_usage_none_is_listed() -> void:
	var node: Dictionary = _info.describe({"className": "Node"}).get("result", {})

	# Node's name is PROPERTY_USAGE_NONE (4.7.2 scene/main/node.cpp L4039).
	assert_eq(
		_named(node.get("properties", []), "name"),
		{"name": "name", "type": "StringName"},
		"Node's own name, without the default ClassDB has none of"
	)


func test_methods_are_sorted_by_name_and_paged() -> void:
	var whole: Dictionary = _info.describe({"className": "Node", "offset": 0, "limit": 500}).get(
		"result", {}
	)
	var page: Dictionary = _info.describe({"className": "Node", "offset": 2, "limit": 3}).get(
		"result", {}
	)
	var past: Dictionary = _info.describe({"className": "Node", "offset": 1000, "limit": 3}).get(
		"result", {}
	)

	var names: Array = whole.get("methods", []).map(func(method: Dictionary): return method["name"])
	var sorted: Array = names.duplicate()
	sorted.sort()
	assert_eq(names, sorted, "sorted by name")
	assert_eq(whole.get("methodCount"), names.size(), "every method on one page")
	assert_true(names.size() > 5, "Node has methods")
	assert_eq(page.get("methods"), whole.get("methods", []).slice(2, 5), "the page from 2")
	assert_eq(page.get("methodCount"), names.size(), "the total on a page")
	assert_eq(page.get("offset"), 2, "the offset")
	assert_eq(page.get("limit"), 3, "the limit")
	assert_eq(past.get("methods"), [], "a page past the end")


## The entry of listed named title, or {}.
static func _named(listed: Array, title: String) -> Dictionary:
	for entry: Dictionary in listed:
		if entry["name"] == title:
			return entry
	return {}
