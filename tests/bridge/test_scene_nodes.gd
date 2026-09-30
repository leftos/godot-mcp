extends "res://gd_test.gd"
## SceneNodes.resolve_position (headless/scene_nodes.gd): the index a position ({index}, {before}
## or {after}) gives a node among a parent's children, for a new node and for one already there.
## The tree is Root, holding Holder, holding A, B and C. And SceneNodes.apply_duplicate_node's
## copies saving with unique_ids of their own, on Combat (_combat).

const SCENE_NODES_SCRIPT := "../../headless/scene_nodes.gd"

var _nodes: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_NODES_SCRIPT).simplify_path()
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
