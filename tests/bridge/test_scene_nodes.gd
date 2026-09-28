extends "res://gd_test.gd"
## SceneNodes.resolve_position (headless/scene_nodes.gd): the index a position ({index}, {before}
## or {after}) gives a node among a parent's children, for a new node and for one already there.
## The tree is Root, holding Holder, holding A, B and C.

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


func _resolve(parent: Node, node: Node, position: Dictionary) -> Dictionary:
	return _nodes.resolve_position(parent, node, position)


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
