extends "res://gd_test.gd"
## The headless scene edits' C# refusal (headless/scene_edit.gd csharp_refusal): refused only while
## the build failed and the scene uses C#, quoting the configuration and the compiler errors. And
## the node refusal (SceneEdit.node_or_error): a missing path names the deepest node of it that
## exists, the segment it lacks and that node's children.

const SCENE_EDIT_SCRIPT := "../../headless/scene_edit.gd"
const ERRORS := "D:/p/CsProbeNode.cs:10: CS1002 ; expected\n(and 3 more)"

var _edit: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_EDIT_SCRIPT).simplify_path()
)
## The scripts _shadow_script compiled, by the scale their _get reads.
var _shadow_scripts: Dictionary = {}


func test_csharp_refusal_quotes_the_configuration_and_the_errors() -> void:
	var prep := {"build": "failed", "buildConfiguration": "Debug", "buildErrors": ERRORS}
	assert_eq(
		_edit.csharp_refusal("res://main.tscn", true, prep),
		(
			"res://main.tscn uses C# scripts and the project's Debug C# build failed; fix it first:\n"
			+ ERRORS
		),
		"a failed build's refusal quotes its errors"
	)


func test_csharp_refusal_without_errors_points_at_validate() -> void:
	assert_eq(
		_edit.csharp_refusal("res://main.tscn", true, {"build": "failed"}),
		(
			"res://main.tscn uses C# scripts and the project's C# build failed; fix it first"
			+ " (validate lists the errors)."
		),
		"no quoted errors or configuration"
	)


func test_csharp_refusal_is_empty_unless_the_build_failed_and_csharp_is_used() -> void:
	var failed := {"build": "failed", "buildConfiguration": "Debug", "buildErrors": ERRORS}
	assert_eq(_edit.csharp_refusal("res://a.tscn", false, failed), "", "a scene without C#")
	for build in ["built", "up-to-date", "no-csproj", "skipped", ""]:
		var prep := {"build": build, "buildConfiguration": "Debug", "buildErrors": ERRORS}
		assert_eq(_edit.csharp_refusal("res://a.tscn", true, prep), "", "build %s" % build)


func test_pack_native_stores_the_engine_value_a_script_member_hides() -> void:
	var root := Node2D.new()
	var hidden := _shadowed(root, "Hidden", "1.0", Vector2(2, 2))
	_shadowed(root, "Untouched", "1.0", Vector2(1, 1))
	_shadowed(root, "Matching", "Vector2(1, 1)", Vector2(3, 3))
	var packing: Dictionary = _edit.pack_native(root)
	var state: SceneState = (packing["packed"] as PackedScene).get_state()
	assert_eq(
		_stored(state, "Hidden")["scale"], Vector2(2, 2), "a stored pair takes the engine value"
	)
	assert_eq(_stored(state, "Untouched").keys(), ["script"], "a default is dropped")
	assert_eq(_stored(state, "Matching").keys(), ["scale", "script"], "added first")
	assert_eq(
		_stored(state, "Matching")["scale"], Vector2(3, 3), "an added pair holds the engine value"
	)
	assert_eq(
		packing["clashes"],
		PackedStringArray(
			[".scale (a C# field) hides Node2D.scale; the file stores the engine's value"]
		),
		"one clause for the one script and name"
	)
	assert_eq(
		ClassDB.class_get_property(hidden, "scale"), Vector2(2, 2), "the live node is left alone"
	)
	root.free()


func test_a_missing_path_names_the_deepest_node_and_its_children() -> void:
	var root := _plate_tree()
	assert_eq(
		_edit.node_or_error(root, "Plate/Column/Scroll", "res://s.tscn"),
		{
			"error":
			(
				"res://s.tscn has no node Plate/Column/Scroll: Plate has no child 'Column'"
				+ " (children: Rows, Title)."
			)
		},
		"a missing segment names the node it is missing under and that node's children"
	)
	root.free()


func test_a_missing_child_of_the_root_names_the_root() -> void:
	var root := _plate_tree()
	assert_eq(
		_edit.node_or_error(root, "Nope", "res://s.tscn"),
		{"error": "res://s.tscn has no node Nope: its root has no child 'Nope' (children: Plate)."},
		"a path whose first segment is missing names the root"
	)
	root.free()


func test_a_missing_last_segment_names_its_parent() -> void:
	var root := _plate_tree()
	assert_eq(
		_edit.node_or_error(root, "Plate/Rows/Gone", "res://s.tscn"),
		{
			"error":
			(
				"res://s.tscn has no node Plate/Rows/Gone: Plate/Rows has no child 'Gone'"
				+ " (children: Scroll)."
			)
		},
		"a path that exists but for its last segment names that segment's parent"
	)
	root.free()


func test_a_node_with_no_children_says_none() -> void:
	var root := _plate_tree()
	assert_eq(
		_edit.node_or_error(root, "Plate/Title/X", "res://s.tscn"),
		{
			"error":
			(
				"res://s.tscn has no node Plate/Title/X: Plate/Title has no child 'X'"
				+ " (children: none)."
			)
		},
		"a node without children lists none"
	)
	root.free()


func test_more_than_ten_children_are_cut_at_ten() -> void:
	var root := Node.new()
	root.name = "Root"
	for index in 12:
		_child_named(root, "C%d" % index)
	assert_eq(
		_edit.node_or_error(root, "Nope", "res://s.tscn"),
		{
			"error":
			(
				"res://s.tscn has no node Nope: its root has no child 'Nope' (children:"
				+ " C0, C1, C2, C3, C4, C5, C6, C7, C8, C9 (+2))."
			)
		},
		"only ten children are listed, with the count of the rest"
	)
	root.free()


func test_a_path_that_exists_returns_its_node() -> void:
	var root := _plate_tree()
	assert_eq(
		_edit.node_or_error(root, "Plate/Rows/Scroll", "res://s.tscn"),
		{"node": root.get_node("Plate/Rows/Scroll")},
		"an existing path returns its node"
	)
	assert_eq(
		_edit.node_or_error(root, "./Plate", "res://s.tscn"),
		{"node": root.get_node("Plate")},
		"a path starting at ./ is read from the root"
	)
	root.free()


## A root named Root holding Plate, which holds Rows and Title, Rows holding Scroll.
func _plate_tree() -> Node:
	var root := Node.new()
	root.name = "Root"
	var plate := _child_named(root, "Plate")
	_child_named(plate, "Rows")
	_child_named(plate, "Title")
	_child_named(plate.get_node("Rows"), "Scroll")
	return root


## A plain Node named node_name added under parent.
func _child_named(parent: Node, node_name: String) -> Node:
	var node := Node.new()
	node.name = node_name
	parent.add_child(node)
	return node


## A Node2D named node_name under root, owned by it, with engine scale and a script whose _get
## reads scale as the GDScript expression shown.
func _shadowed(root: Node, node_name: String, shown: String, scale: Vector2) -> Node2D:
	var node := Node2D.new()
	node.name = node_name
	node.scale = scale
	node.set_script(_shadow_script(shown))
	root.add_child(node)
	node.owner = root
	return node


## One script to each shown value, so two nodes that read scale alike share their script.
func _shadow_script(shown: String) -> GDScript:
	if not _shadow_scripts.has(shown):
		var script := GDScript.new()
		script.source_code = (
			"extends Node2D\n\n\nfunc _get(property: StringName) -> Variant:\n"
			+ ('\treturn %s if property == &"scale" else null\n' % shown)
		)
		script.reload()
		_shadow_scripts[shown] = script
	return _shadow_scripts[shown]


## The properties state stores for its node named node_name, in their stored order.
func _stored(state: SceneState, node_name: String) -> Dictionary:
	var stored: Dictionary = {}
	for index in state.get_node_count():
		if state.get_node_name(index) != StringName(node_name):
			continue
		for property in state.get_node_property_count(index):
			var property_name: String = state.get_node_property_name(index, property)
			stored[property_name] = state.get_node_property_value(index, property)
	return stored
