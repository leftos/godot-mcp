# gdlint: disable=max-public-methods
# Each public method is a test the runner finds by its test_ prefix, one per case, so the count
# grows with the cases. gdlint reports the class at line 1, so the line above must be the first.
extends "res://gd_test.gd"
## SceneNodes.resolve_position (headless/scene_nodes.gd): the index a position ({index}, {before}
## or {after}) gives a node among a parent's children, for a new node and for one already there.
## The tree is Root, holding Holder, holding A, B and C. And SceneNodes.apply_duplicate_node's
## copies saving with unique_ids of their own, on Combat (_combat); and move_node, through
## SceneOps.edit_scene on scenes written under user://, keeping the lines of the nodes it carries.

const SCENE_NODES_SCRIPT := "../../headless/scene_nodes.gd"
const SCENE_OPS_SCRIPT := "../../headless/scene_ops.gd"
const MOVED_SCENE := "user://move_keeps_layout.tscn"
const PATHS_SCENE := "user://move_rewrites_paths.tscn"
const PATHS_SCRIPT := "user://move_paths_holder.gd"
const PATHS_SCRIPT_TEXT := (
	"extends Node\n\n@export var target: NodePath\n" + "@export var targets: Array[NodePath] = []\n"
)
## Root holding Dest, Outside and Sub, Sub holding Inner: Outside points into Sub with its target
## and with its targets Array, Inner out of Sub to Outside, with a subname.
const PATHS_SCENE_TEXT := (
	'[gd_scene load_steps=2 format=3]\n\n[ext_resource type="Script" path="%s" id="1"]\n\n'
	+ '[node name="Root" type="Node"]\n\n[node name="Dest" type="Node" parent="."]\n\n'
	+ '[node name="Outside" type="Node" parent="."]\nscript = ExtResource("1")\n'
	+ 'target = NodePath("../Sub/Inner")\ntargets = Array[NodePath]([NodePath("../Sub")])\n\n'
	+ '[node name="Sub" type="Node" parent="."]\n\n'
	+ '[node name="Inner" type="Node" parent="Sub"]\nscript = ExtResource("1")\n'
	+ 'target = NodePath("../../Outside:process_mode")\n'
)

## The scene each edge case of a move writes and removes.
const EDGE_SCENE := "user://move_edge.tscn"
const PART_SCENE := "user://move_part.tscn"
const PART_TEXT := (
	'[gd_scene format=3]\n\n[node name="Part" type="Node2D"]\n\n'
	+ '[node name="Holder" type="RemoteTransform2D" parent="."]\n'
)
## Root holding Target, Box and an instance of PART_SCENE with editable children, whose Holder
## points at Target.
const INSTANCE_TEXT := (
	'[gd_scene load_steps=2 format=3]\n\n[ext_resource type="PackedScene" path="%s" id="1"]\n\n'
	+ '[node name="Root" type="Node2D"]\n\n[node name="Target" type="Node2D" parent="."]\n\n'
	+ '[node name="Box" type="Node2D" parent="."]\n\n'
	+ '[node name="Part" parent="." instance=ExtResource("1")]\n\n'
	+ '[node name="Holder" parent="Part"]\nremote_path = NodePath("../../Target")\n\n'
	+ '[editable path="Part"]\n'
)
## Rig holding Hand and From, From holding Arm, whose lines are not in the pack's order, and a
## connection from Arm.
const RIG_TEXT := (
	'[gd_scene format=3 uid="uid://b174rig00000a"]\n\n[node name="Rig" type="Node2D"]\n\n'
	+ '[node name="Hand" type="Node2D" parent="."]\n\n[node name="From" type="Node2D" parent="."]\n\n'
	+ '[node name="Arm" type="RemoteTransform2D" parent="From"]\nupdate_rotation = false\n'
	+ 'remote_path = NodePath("../../Hand")\nposition = Vector2(5, 0)\n\n'
	+ '[connection signal="ready" from="From/Arm" to="Hand" method="hide"]\n'
)
## A full-rect Root holding Box, a VBoxContainer holding the Label Name, then Panel, a Control.
const LABEL_HEAD := (
	'[gd_scene format=3 uid="uid://b174label000a"]\n\n[node name="Root" type="Control"]\n'
	+ "layout_mode = 3\nanchors_preset = 15\nanchor_right = 1.0\nanchor_bottom = 1.0\n"
	+ "grow_horizontal = 2\ngrow_vertical = 2\n\n"
	+ '[node name="Box" type="VBoxContainer" parent="."]\nlayout_mode = 0\n'
	+ "offset_right = 40.0\noffset_bottom = 40.0\n\n"
)
const LABEL_NAME := '[node name="Name" type="Label" parent="Box"]\nlayout_mode = 2\ntext = "a"\n'
const LABEL_PANEL := '[node name="Panel" type="Control" parent="."]\nanchors_preset = 0\n'
## A Node2D root's section, with the blank line after it.
const ROOT_2D := '[node name="Root" type="Node2D"]\n\n'

var _nodes: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_NODES_SCRIPT).simplify_path()
)
var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_OPS_SCRIPT).simplify_path()
)


func test_an_index_places_a_node() -> void:
	var tree: Node = _tree()
	var holder: Node = tree.get_node("Holder")
	var fresh := Node.new()

	assert_eq(_resolve(holder, fresh, {"index": 0}), {"index": 0}, "index 0 is first")
	assert_eq(_resolve(holder, holder.get_node("A"), {"index": 1}), {"index": 1}, "a middle index")
	assert_eq(_resolve(holder, fresh, {"index": 1.0}), {"index": 1}, "a JSON number is an integer")
	assert_eq(_resolve(holder, fresh, {"index": -1}), {"index": 3}, "-1 is last for a new node")
	assert_eq(
		_resolve(holder, holder.get_node("A"), {"index": -1}),
		{"index": 2},
		"-1 is last for a child already there"
	)
	fresh.free()
	tree.free()


func test_an_index_out_of_range_is_refused() -> void:
	var tree: Node = _tree()
	var holder: Node = tree.get_node("Holder")
	var fresh := Node.new()

	assert_eq(
		_resolve(holder, fresh, {"index": 4}),
		{
			"error":
			(
				"position.index 4 is out of range: Holder has 4 children once the node is placed, "
				+ "so index takes -4 to 3."
			)
		},
		"past the end for a new node"
	)
	assert_eq(
		_resolve(holder, holder.get_node("C"), {"index": -4}),
		{
			"error":
			(
				"position.index -4 is out of range: Holder has 3 children once the node is placed, "
				+ "so index takes -3 to 2."
			)
		},
		"before the start for a child already there"
	)
	fresh.free()
	tree.free()


func test_before_and_after_a_sibling() -> void:
	var tree: Node = _tree()
	var holder: Node = tree.get_node("Holder")
	var fresh := Node.new()

	assert_eq(_resolve(holder, fresh, {"before": "A"}), {"index": 0}, "before the first")
	assert_eq(_resolve(holder, fresh, {"after": "C"}), {"index": 3}, "after the last")
	assert_eq(
		_resolve(holder, holder.get_node("A"), {"before": "C"}),
		{"index": 1},
		"before a later sibling counts once the node is taken out"
	)
	assert_eq(
		_resolve(holder, holder.get_node("C"), {"after": "A"}),
		{"index": 1},
		"after an earlier sibling"
	)
	assert_eq(
		_resolve(holder, holder.get_node("A"), {"after": "C"}), {"index": 2}, "after the last"
	)
	fresh.free()
	tree.free()


func test_a_missing_sibling_or_the_node_itself_is_refused() -> void:
	var tree: Node = _tree()
	var holder: Node = tree.get_node("Holder")

	assert_eq(
		_resolve(holder, holder.get_node("A"), {"before": "Nope"}),
		{"error": "position.before names no child Nope of Holder."},
		"a name no child has"
	)
	assert_eq(
		_resolve(tree, holder, {"after": "Holder/A"}),
		{"error": "position.after names no child Holder/A of the scene root."},
		"a path to a node that is not a child"
	)
	assert_eq(
		_resolve(holder, holder.get_node("B"), {"after": "B"}),
		{"error": "position.after names the node being moved."},
		"the node itself"
	)
	tree.free()


func test_a_position_takes_exactly_one_key() -> void:
	var tree: Node = _tree()
	var holder: Node = tree.get_node("Holder")
	var child: Node = holder.get_node("A")

	assert_eq(
		_resolve(holder, child, {}),
		{"error": "position takes exactly one of index, before or after; got none."},
		"zero keys"
	)
	assert_eq(
		_resolve(holder, child, {"index": 0, "before": "B"}),
		{"error": "position takes exactly one of index, before or after; got index, before."},
		"two keys"
	)
	assert_eq(
		_resolve(holder, child, {"index": 0, "before": null}),
		{"index": 0},
		"a key without a value is not given"
	)
	tree.free()


func test_a_copy_gets_fresh_unique_ids() -> void:
	var root: Node = _combat()
	var before: PackedInt32Array = _saved_ids(root)
	var context: Dictionary = {"scene": "res://combat.tscn"}
	var under_layer: Dictionary = {"nodePath": "Ties", "newName": "Lines", "parent": "Layer"}

	assert_eq(
		_nodes.apply_duplicate_node(root, under_layer, context),
		{"result": {"originalPath": "Ties", "newPath": "Layer/Lines"}},
		"a copy saved before its original"
	)
	assert_eq(
		_nodes.apply_duplicate_node(root, {"nodePath": "Ties"}, context),
		{"result": {"originalPath": "Ties", "newPath": "Ties2"}},
		"a copy saved after its original"
	)
	# Saved order: Combat, Layer, Layer/Lines, Layer/Lines/Tie, Ties, Ties/Tie, Ties2, Ties2/Tie, Hud.
	var after: PackedInt32Array = _saved_ids(root)
	assert_eq(
		[after[0], after[1], after[4], after[5], after[8]],
		Array(before),
		"every node already there keeps its id"
	)
	var distinct: Dictionary = {}
	for id: int in after:
		distinct[id] = true
	assert_eq(distinct.size(), 9, "no two nodes, the copies' children among them, share an id")
	assert_true(not distinct.has(0), "every node is saved with an id")
	root.free()


func test_a_move_keeps_the_lines_of_the_nodes_it_carries() -> void:
	var original: String = _moved_scene_text('parent="."', 'parent="Sub"')
	_write(MOVED_SCENE, original)
	var params: Dictionary = {
		"scene": MOVED_SCENE, "nodePath": "Sub", "parent": "Dest", "keepGlobalTransform": true
	}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_eq(moved.get("ok"), true, "the move is saved: %s" % moved)
	assert_true(not moved.get("result", {}).has("warning"), "the save keeps the layout")
	assert_eq(
		FileAccess.get_file_as_string(MOVED_SCENE),
		_moved_scene_text('parent="Dest"', 'parent="Dest/Sub"'),
		"only the parent= of Sub and of each node under it changes"
	)
	DirAccess.remove_absolute(MOVED_SCENE)
	DirAccess.remove_absolute(MOVED_SCENE + ".uidren")


func test_a_move_rewrites_the_node_paths_into_and_out_of_what_it_carries() -> void:
	_write(PATHS_SCRIPT, PATHS_SCRIPT_TEXT)
	_write(PATHS_SCENE, PATHS_SCENE_TEXT % PATHS_SCRIPT)
	var params: Dictionary = {"scene": PATHS_SCENE, "nodePath": "Sub", "parent": "Dest"}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_eq(moved.get("ok"), true, "the move is saved: %s" % moved)
	assert_eq(
		moved.get("result", {}).get("rewrittenPaths"),
		[
			{
				"node": "Outside",
				"property": "target",
				"from": "../Sub/Inner",
				"to": "../Dest/Sub/Inner",
			},
			{"node": "Outside", "property": "targets", "from": "../Sub", "to": "../Dest/Sub"},
			{
				"node": "Dest/Sub/Inner",
				"property": "target",
				"from": "../../Outside:process_mode",
				"to": "../../../Outside:process_mode",
			},
		],
		"each rewritten NodePath is listed, subnames kept"
	)
	var scene := (
		ResourceLoader.load(PATHS_SCENE, "", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
	)
	var root: Node = scene.instantiate()
	var outside: Node = root.get_node("Outside")
	var inner: Node = root.get_node("Dest/Sub/Inner")
	assert_eq(inner.get_node(inner.get("target")), outside, "a path out of the moved subtree")
	assert_eq(outside.get_node(outside.get("target")), inner, "a path into the moved subtree")
	assert_eq(
		outside.get_node(outside.get("targets")[0]),
		root.get_node("Dest/Sub"),
		"an Array's path into the moved subtree"
	)
	root.free()
	for path: String in [PATHS_SCENE, PATHS_SCENE + ".uidren", PATHS_SCRIPT]:
		DirAccess.remove_absolute(path)


func test_a_move_with_no_node_paths_rewrites_none() -> void:
	_write(MOVED_SCENE, _moved_scene_text('parent="."', 'parent="Sub"'))
	var params: Dictionary = {"scene": MOVED_SCENE, "nodePath": "Sub", "parent": "Dest"}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_eq(moved.get("result", {}).get("rewrittenPaths"), [], "no NodePath to rewrite")
	DirAccess.remove_absolute(MOVED_SCENE)
	DirAccess.remove_absolute(MOVED_SCENE + ".uidren")


func test_a_node_added_where_a_moved_one_was_is_new() -> void:
	var original: String = _text(
		[
			'[gd_scene format=3 uid="uid://b174collide0a"]',
			"",
			'[node name="Root" type="Node2D"]',
			"",
			'[node name="Q" type="Node2D" parent="."]',
			"",
			'[node name="A" type="Node2D" parent="."]',
			"z_index = 3",
			"position = Vector2(1, 2)",
		]
	)
	_write(EDGE_SCENE, original)
	var move: Dictionary = {"op": "move_node", "params": {"nodePath": "A", "parent": "Q"}}
	var add: Dictionary = {"op": "add_node", "params": {"nodeType": "Node2D", "nodeName": "A"}}

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": [move, add]})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(not batch.get("result", {}).has("warning"), "the save keeps the layout")
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	var kept: String = original.replace('parent="."]\nz_index', 'parent="Q"]\nz_index')
	assert_true(saved.begins_with(kept), "the moved A keeps its lines: %s" % saved)
	var fresh := RegEx.create_from_string(
		'^\\n\\[node name="A" type="Node2D" parent="\\." unique_id=-?\\d+\\]\\n$'
	)
	assert_true(fresh.search(saved.substr(kept.length())) != null, "the new A as Godot writes it")
	_remove_scene(EDGE_SCENE)


func test_a_node_moved_onto_a_deleted_nodes_path_keeps_its_lines_and_drops_the_deleted() -> void:
	var head: String = _text(
		[
			'[gd_scene format=3 uid="uid://b174displac0"]',
			"",
			'[node name="Root" type="Node2D"]',
			"",
		]
	)
	var a_lines: String = "z_index = 3\nposition = Vector2(1, 2)\n"
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\n'
		+ a_lines
		+ "\n"
		+ b_section
		+ '\n[node name="A" type="Node2D" parent="B"]\nvisible = false\n'
		+ '\n[connection signal="ready" from="B/A" to="." method="hide"]\n'
	)
	_write(EDGE_SCENE, original)
	var delete: Dictionary = {"op": "delete_nodes", "params": {"nodePaths": ["B/A"]}}
	var move: Dictionary = {"op": "move_node", "params": {"nodePath": "A", "parent": "B"}}

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": [delete, move]})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE),
		head + b_section + '\n[node name="A" type="Node2D" parent="B"]\n' + a_lines,
		"the carried A keeps its lines; the deleted B/A and its connection are gone"
	)
	_remove_scene(EDGE_SCENE)


func test_a_node_added_where_a_deleted_one_was_then_moved_is_new() -> void:
	var original: String = _text(
		[
			'[gd_scene format=3 uid="uid://b174readded0"]',
			"",
			'[node name="Root" type="Node2D"]',
			"",
			'[node name="A" type="Node2D" parent="."]',
			"z_index = 3",
			"position = Vector2(1, 2)",
			"",
			'[node name="B" type="Node2D" parent="."]',
		]
	)
	_write(EDGE_SCENE, original)
	var steps: Array = [
		{"op": "delete_nodes", "params": {"nodePaths": ["A"]}},
		{"op": "add_node", "params": {"nodeType": "Node2D", "nodeName": "A"}},
		{"op": "move_node", "params": {"nodePath": "A", "parent": "B"}},
	]

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": steps})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	var fresh := RegEx.create_from_string(
		'\\n\\[node name="A" type="Node2D" parent="B" unique_id=-?\\d+\\]\\n$'
	)
	assert_true(fresh.search(saved) != null, "the new A as Godot writes it: %s" % saved)
	assert_true(not saved.contains("z_index"), "the deleted A's lines are gone: %s" % saved)
	_remove_scene(EDGE_SCENE)


func test_a_copy_moved_elsewhere_is_new_and_its_original_keeps_its_lines() -> void:
	var a_section: String = (
		'[node name="A" type="Node2D" parent="."]\nz_index = 3\nposition = Vector2(1, 2)\n\n'
		+ '[node name="K" type="Node2D" parent="A"]\nz_index = 1\nposition = Vector2(3, 4)\n'
	)
	var head: String = '[gd_scene format=3 uid="uid://b174copied00"]\n\n' + ROOT_2D
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	_write(EDGE_SCENE, head + a_section + "\n" + b_section)
	var steps: Array = [
		{"op": "duplicate_node", "params": {"nodePath": "A", "newName": "A2"}},
		{"op": "move_node", "params": {"nodePath": "A2", "parent": "B"}},
	]

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": steps})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	assert_true(saved.begins_with(head + a_section), "A and K keep their lines: %s" % saved)
	var copies: Array = [
		'name="A2" type="Node2D" parent="B"', 'name="K" type="Node2D" parent="B/A2"'
	]
	for tag: String in copies:
		var fresh := RegEx.create_from_string("\\n\\[node %s unique_id=-?\\d+\\]\\n" % tag)
		assert_true(fresh.search(saved) != null, "the copy's %s is new: %s" % [tag, saved])
	_remove_scene(EDGE_SCENE)


func test_a_copy_named_as_a_deleted_node_then_moved_is_new() -> void:
	var a_section: String = (
		'[node name="A" type="Node2D" parent="."]\nz_index = 3\nposition = Vector2(1, 2)\n\n'
		+ '[node name="K" type="Node2D" parent="A"]\nz_index = 1\nposition = Vector2(3, 4)\n'
	)
	var head: String = '[gd_scene format=3 uid="uid://b174copyx000"]\n\n' + ROOT_2D
	var x_section: String = (
		'[node name="X" type="Node2D" parent="."]\nvisible = false\n\n'
		+ '[node name="K" type="Node2D" parent="X"]\nz_index = 5\n'
	)
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	_write(EDGE_SCENE, head + a_section + "\n" + x_section + "\n" + b_section)
	var steps: Array = [
		{"op": "delete_nodes", "params": {"nodePaths": ["X"]}},
		{"op": "duplicate_node", "params": {"nodePath": "A", "newName": "X"}},
		{"op": "move_node", "params": {"nodePath": "X", "parent": "B"}},
	]

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": steps})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	assert_true(saved.begins_with(head + a_section), "A and K keep their lines: %s" % saved)
	assert_true(not saved.contains("visible = false"), "the deleted X is gone: %s" % saved)
	assert_true(not saved.contains("z_index = 5"), "the deleted X/K is gone: %s" % saved)
	var copies: Array = ['name="X" type="Node2D" parent="B"', 'name="K" type="Node2D" parent="B/X"']
	for tag: String in copies:
		var fresh := RegEx.create_from_string("\\n\\[node %s unique_id=-?\\d+\\]\\n" % tag)
		assert_true(fresh.search(saved) != null, "the copy's %s is new: %s" % [tag, saved])
	_remove_scene(EDGE_SCENE)


func test_a_node_moved_back_to_its_own_path_under_a_carried_node_keeps_its_lines() -> void:
	var head: String = '[gd_scene format=3 uid="uid://b174backhm00"]\n\n' + ROOT_2D
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	var c_section: String = '[node name="C" type="Node2D" parent="."]\n'
	var x_lines: String = "z_index = 1\nposition = Vector2(3, 4)\n"
	var x_section: String = '[node name="x" type="Node2D" parent="A"]\n' + x_lines
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\nz_index = 3\n\n'
		+ x_section
		+ "\n"
		+ b_section
		+ "\n"
		+ c_section
		+ '\n[node name="A" type="Node2D" parent="C"]\nvisible = false\n'
	)
	_write(EDGE_SCENE, original)
	var steps: Array = [
		{"op": "move_node", "params": {"nodePath": "A", "parent": "B"}},
		{"op": "move_node", "params": {"nodePath": "C/A", "parent": "."}},
		{"op": "move_node", "params": {"nodePath": "B/A/x", "parent": "A"}},
	]

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": steps})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE),
		(
			head
			+ b_section
			+ '\n[node name="A" type="Node2D" parent="B"]\nz_index = 3\n\n'
			+ c_section
			+ '\n[node name="A" type="Node2D" parent="."]\nvisible = false\n\n'
			+ x_section
		),
		"x, back at A/x under the A that took that path, keeps its source lines"
	)
	_remove_scene(EDGE_SCENE)


func test_connections_of_a_displaced_node_drop_without_sharing_a_key() -> void:
	var head: String = '[gd_scene format=3 uid="uid://b174connkey0"]\n\n' + ROOT_2D
	var a_lines: String = "z_index = 3\nposition = Vector2(1, 2)\n"
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\n'
		+ a_lines
		+ "\n"
		+ b_section
		+ '\n[node name="A" type="Node2D" parent="B"]\nvisible = false\n'
		+ '\n[connection signal="ready" from="A" to="B/A" method="hide"]\n'
		+ '\n[connection signal="ready" from="B/A" to="B/A" method="hide"]\n'
		+ '\n[connection signal="ready" from="A" to="B" method="hide"]\n'
	)
	_write(EDGE_SCENE, original)

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": _displacing_steps()})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE),
		(
			head
			+ b_section
			+ '\n[node name="A" type="Node2D" parent="B"]\n'
			+ a_lines
			+ '\n[connection signal="ready" from="B/A" to="B" method="hide"]\n'
		),
		"both connections to the deleted B/A are gone; the carried A's own stays"
	)
	_remove_scene(EDGE_SCENE)


func test_a_deleted_nodes_child_drops_where_a_carried_nodes_child_takes_its_path() -> void:
	var head: String = '[gd_scene format=3 uid="uid://b174prefix00"]\n\n' + ROOT_2D
	var a_lines: String = "z_index = 3\nposition = Vector2(1, 2)\n"
	var k_lines: String = "z_index = 1\nposition = Vector2(3, 4)\n"
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\n'
		+ a_lines
		+ '\n[node name="K" type="Node2D" parent="A"]\n'
		+ k_lines
		+ "\n"
		+ b_section
		+ '\n[node name="A" type="Node2D" parent="B"]\nvisible = false\n'
		+ '\n[node name="K" type="Node2D" parent="B/A"]\nvisible = false\n'
	)
	_write(EDGE_SCENE, original)

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": _displacing_steps()})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE),
		(
			head
			+ b_section
			+ '\n[node name="A" type="Node2D" parent="B"]\n'
			+ a_lines
			+ '\n[node name="K" type="Node2D" parent="B/A"]\n'
			+ k_lines
		),
		"the carried K keeps its lines; the deleted B/A/K is gone"
	)
	_remove_scene(EDGE_SCENE)


func test_a_displaced_nodes_connection_drops_beside_the_carried_ones_like_it() -> void:
	var head: String = '[gd_scene format=3 uid="uid://b174connsame"]\n\n' + ROOT_2D
	var a_lines: String = "z_index = 3\nposition = Vector2(1, 2)\n"
	var b_section: String = '[node name="B" type="Node2D" parent="."]\n'
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\n'
		+ a_lines
		+ "\n"
		+ b_section
		+ '\n[node name="A" type="Node2D" parent="B"]\nvisible = false\n'
		+ '\n[connection signal="ready" from="A" to="." method="hide"]\n'
		+ '\n[connection signal="ready" from="B/A" to="." method="hide"]\n'
	)
	_write(EDGE_SCENE, original)

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": _displacing_steps()})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE),
		(
			head
			+ b_section
			+ '\n[node name="A" type="Node2D" parent="B"]\n'
			+ a_lines
			+ '\n[connection signal="ready" from="B/A" to="." method="hide"]\n'
		),
		"the carried A's connection stays; the deleted B/A's is gone"
	)
	_remove_scene(EDGE_SCENE)


func test_a_displaced_editable_instance_drops_beside_the_carried_one() -> void:
	_write(PART_SCENE, PART_TEXT)
	var original: String = (
		'[gd_scene format=3 uid="uid://b174editab00"]\n\n'
		+ ('[ext_resource type="PackedScene" path="%s" id="1"]\n\n' % PART_SCENE)
		+ ROOT_2D
		+ '[node name="A" parent="." instance=ExtResource("1")]\n\n'
		+ '[node name="B" type="Node2D" parent="."]\n\n'
		+ '[node name="A" parent="B" instance=ExtResource("1")]\n\n'
		+ '[editable path="A"]\n\n[editable path="B/A"]\n'
	)
	_write(EDGE_SCENE, original)

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": _displacing_steps()})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	assert_true(
		saved.contains('\n[node name="A" parent="B" instance=ExtResource("1")]\n'),
		"the carried A keeps its tag line: %s" % saved
	)
	assert_eq(saved.count("[editable "), 1, "one editable section: %s" % saved)
	assert_true(saved.ends_with('\n[editable path="B/A"]\n'), "the carried A's: %s" % saved)
	_remove_scene(EDGE_SCENE)
	DirAccess.remove_absolute(PART_SCENE)


func test_a_node_added_where_a_displaced_nodes_child_was_takes_none_of_its_lines() -> void:
	var head: String = '[gd_scene format=3 uid="uid://b174srcpath0"]\n\n' + ROOT_2D
	var a_lines: String = "z_index = 3\nposition = Vector2(1, 2)\n"
	var original: String = (
		head
		+ '[node name="A" type="Node2D" parent="."]\n'
		+ a_lines
		+ '\n[node name="B" type="Node2D" parent="."]\n'
		+ '\n[node name="A" type="Node2D" parent="B"]\n'
		+ '\n[node name="k" type="Node2D" parent="B/A"]\nvisible = true\n'
	)
	_write(EDGE_SCENE, original)
	var add: Dictionary = {
		"op": "add_node", "params": {"nodeType": "Node2D", "nodeName": "k", "parent": "B/A"}
	}

	var batch: Dictionary = _ops.batch_scene(
		{"scene": EDGE_SCENE, "steps": _displacing_steps() + [add]}
	)

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	var fresh := RegEx.create_from_string(
		'\\n\\[node name="k" type="Node2D" parent="B/A" unique_id=-?\\d+\\]\\n$'
	)
	assert_true(fresh.search(saved) != null, "the new k as Godot writes it: %s" % saved)
	assert_true(not saved.contains("visible"), "no line of the deleted B/A/k: %s" % saved)
	_remove_scene(EDGE_SCENE)


## A node the edit made has no source (moved_paths leaves out its ""), so moved onto a deleted
## node's path it matches that node's section, as a node added at that path does.
func test_a_node_added_and_moved_onto_a_deleted_nodes_path_matches_its_section() -> void:
	var original: String = _text(
		[
			'[gd_scene format=3 uid="uid://b174created0"]',
			"",
			'[node name="Root" type="Node2D"]',
			"",
			'[node name="B" type="Node2D" parent="."]',
			"",
			'[node name="A" type="Node2D" parent="B"]',
		]
	)
	_write(EDGE_SCENE, original)
	var steps: Array = [
		{"op": "delete_nodes", "params": {"nodePaths": ["B/A"]}},
		{"op": "add_node", "params": {"nodeType": "Node2D", "nodeName": "A"}},
		{"op": "move_node", "params": {"nodePath": "A", "parent": "B"}},
	]

	var batch: Dictionary = _ops.batch_scene({"scene": EDGE_SCENE, "steps": steps})

	assert_eq(batch.get("result", {}).get("passed"), true, "the batch passes: %s" % batch)
	assert_true(
		not batch.get("result", {}).has("warning"), "the splice keeps the layout: %s" % batch
	)
	assert_eq(
		FileAccess.get_file_as_string(EDGE_SCENE), original, "the new A takes the deleted A's lines"
	)
	_remove_scene(EDGE_SCENE)


func test_a_moved_nodes_internal_child_is_walked() -> void:
	var root := Node2D.new()
	var sub := Node2D.new()
	sub.name = "Sub"
	root.add_child(sub)
	sub.owner = root
	var inner := RemoteTransform2D.new()
	inner.name = "Inner"
	sub.add_child(inner, false, Node.INTERNAL_MODE_BACK)
	inner.remote_path = NodePath("../..")
	var context: Dictionary = {}

	var held: Array = _nodes._held_node_paths(root)
	_nodes._note_sources(sub, root, context)

	var holders: Array = held.map(func(record: Dictionary) -> Node: return record["holder"])
	assert_true(holders.has(inner), "the internal child's NodePath is read before a move")
	assert_eq(
		context.get(_nodes.MOVED_KEY, {}).get(inner), "Sub/Inner", "the internal child is mapped"
	)
	root.free()


func test_a_move_rewrites_a_path_an_editable_instance_child_holds() -> void:
	_write(PART_SCENE, PART_TEXT)
	_write(EDGE_SCENE, INSTANCE_TEXT % PART_SCENE)
	var params: Dictionary = {"scene": EDGE_SCENE, "nodePath": "Part", "parent": "Box"}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	var rewritten: Dictionary = {
		"node": "Box/Part/Holder",
		"property": "remote_path",
		"from": "../../Target",
		"to": "../../../Target",
	}
	assert_eq(moved.get("result", {}).get("rewrittenPaths"), [rewritten], "the override is listed")
	var scene := (
		ResourceLoader.load(EDGE_SCENE, "", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
	)
	var root: Node = scene.instantiate()
	var holder: Node = root.get_node("Box/Part/Holder")
	assert_eq(holder.get_node(holder.get("remote_path")), root.get_node("Target"), "it resolves")
	root.free()
	_remove_scene(EDGE_SCENE)
	DirAccess.remove_absolute(PART_SCENE)


## Covers both a carried section's changed line in place and its connection keeping its place.
func test_a_move_changes_only_the_tag_the_rewritten_path_and_the_connection() -> void:
	_write(EDGE_SCENE, RIG_TEXT)
	var params: Dictionary = {"scene": EDGE_SCENE, "nodePath": "From/Arm", "parent": "."}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_true(not moved.get("result", {}).has("warning"), "the save keeps the layout: %s" % moved)
	var expected: String = (
		RIG_TEXT
		. replace('parent="From"]', 'parent="."]')
		. replace('NodePath("../../Hand")', 'NodePath("../Hand")')
		. replace('from="From/Arm"', 'from="Arm"')
	)
	assert_eq(FileAccess.get_file_as_string(EDGE_SCENE), expected, "only those three lines change")
	_remove_scene(EDGE_SCENE)


func test_a_control_moved_out_of_a_container_keeps_its_lines() -> void:
	_write(EDGE_SCENE, LABEL_HEAD + LABEL_NAME + "\n" + LABEL_PANEL)
	var params: Dictionary = {"scene": EDGE_SCENE, "nodePath": "Box/Name", "parent": "Panel"}

	var moved: Dictionary = _ops.edit_scene("move_node", params)

	assert_true(not moved.get("result", {}).has("warning"), "the save keeps the layout: %s" % moved)
	var saved: String = FileAccess.get_file_as_string(EDGE_SCENE)
	assert_true(
		saved.begins_with(LABEL_HEAD + LABEL_PANEL + "\n"), "the other nodes stay: %s" % saved
	)
	var name_lines: PackedStringArray = saved.substr((LABEL_HEAD + LABEL_PANEL).length()).split(
		"\n"
	)
	assert_eq(name_lines[1], '[node name="Name" type="Label" parent="Panel"]', "its tag line")
	assert_true(name_lines.has('text = "a"'), "its own line stays: %s" % saved)
	var scene := (
		ResourceLoader.load(EDGE_SCENE, "", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene
	)
	var root: Node = scene.instantiate()
	assert_eq(root.get_node("Panel/Name").get("layout_mode"), 0, "position mode at its new place")
	root.free()
	_remove_scene(EDGE_SCENE)


## A batch's steps that delete B/A, then move A under B onto the deleted node's path.
func _displacing_steps() -> Array:
	return [
		{"op": "delete_nodes", "params": {"nodePaths": ["B/A"]}},
		{"op": "move_node", "params": {"nodePath": "A", "parent": "B"}},
	]


func _remove_scene(path: String) -> void:
	DirAccess.remove_absolute(path)
	DirAccess.remove_absolute(path + ".uidren")


func _text(lines: Array) -> String:
	return "\n".join(PackedStringArray(lines)) + "\n"


func _resolve(parent: Node, node: Node, position: Dictionary) -> Dictionary:
	return _nodes.resolve_position(parent, node, position)


## Combat, holding Layer, Ties with a Tie of its own, and Hud, each owned by Combat and given its
## unique_id by a first save (_saved_ids).
func _combat() -> Node:
	var root := Node2D.new()
	root.name = "Combat"
	for child_name: String in ["Layer", "Ties", "Hud"]:
		var child := Node2D.new()
		child.name = child_name
		root.add_child(child)
		child.owner = root
	var tie := Line2D.new()
	tie.name = "Tie"
	root.get_node("Ties").add_child(tie)
	tie.owner = root
	return root


## The unique_ids a save of the scene at root writes, in the save's order. Packing gives a node
## without one, or with one an earlier node has, a fresh id (4.7.2
## scene/resources/packed_scene.cpp L1099-1123).
func _saved_ids(root: Node) -> PackedInt32Array:
	var packed := PackedScene.new()
	assert_eq(packed.pack(root), OK, "the scene packs")
	return packed.get("_bundled")["node_ids"]


## Root, holding Holder, holding A, B and C.
func _tree() -> Node:
	var root := Node.new()
	root.name = "Root"
	var holder := Node.new()
	holder.name = "Holder"
	root.add_child(holder)
	for child_name: String in ["A", "B", "C"]:
		var child := Node.new()
		child.name = child_name
		holder.add_child(child)
	return root


## Root, holding Dest, then Sub (under sub_parent) holding three Controls whose lines Godot's own
## pack writes otherwise: KidPlain without the anchors_preset = 0 the pack adds, KidMode0 with the
## layout_mode = 0 it drops, KidLabel with the size_flags_vertical = 4 it drops and without the
## layout_mode = 0 it adds (4.7.2 control.cpp L1071-1074, L4911-4912, L4946-4947;
## packed_scene.cpp L980-995). Each loads the same with or without those lines.
func _moved_scene_text(sub_parent: String, kid_parent: String) -> String:
	var lines: PackedStringArray = [
		'[gd_scene format=3 uid="uid://b174movelyt0"]',
		"",
		'[node name="Root" type="Control" unique_id=1001]',
		"",
		'[node name="Dest" type="Control" parent="." unique_id=1010]',
		"anchors_preset = 0",
		"",
		'[node name="Sub" type="Control" %s unique_id=1030]' % sub_parent,
		"anchors_preset = 0",
		"",
		'[node name="KidPlain" type="Control" %s unique_id=1031]' % kid_parent,
		"offset_right = 40.0",
		"offset_bottom = 40.0",
		"",
		'[node name="KidMode0" type="Control" %s unique_id=1032]' % kid_parent,
		"layout_mode = 0",
		"anchors_preset = 0",
		"",
		'[node name="KidLabel" type="Label" %s unique_id=1033]' % kid_parent,
		"size_flags_vertical = 4",
		'text = "a"',
		"",
	]
	return "\n".join(lines)


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	assert_true(file != null, "%s opens for writing" % path)
	file.store_string(text)
	file.close()
