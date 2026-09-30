extends "res://gd_test.gd"
## Unique names (%Name) in the inspector's finder and not-found refusal
## (bridge/godot_mcp_inspect.gd), on a small tree under this run's root:
##
##   Main (no owner): Solo (with a child Leaf) and Shared, unique in Main; Sub, a sub-scene root
##     owned by Main, holding Inner, unique in Sub
##   Hud (no owner): Shared, unique in Hud
##
## The tests run before the root enters its tree, where get_path fails, so the texts that name a
## node's path (the ambiguity and a later unique name that misses) are pinned by the C#
## integration tests instead.

const SCENE_HINT := "get_scene_tree lists the nodes' paths"

var _inspect_script: GDScript = load_bridge_script("godot_mcp_inspect.gd")


func test_a_unique_name_one_scene_holds_is_found() -> void:
	var inspect: Node = _inspect_script.new()
	var built: Array[Node] = _build()
	var main: Node = built[0]
	assert_eq(inspect.find_unique(_root(), "%Solo"), main.get_node("Solo"), "Main's Solo")
	assert_eq(inspect.find_unique(_root(), "%Inner"), main.get_node("Sub/Inner"), "Sub's Inner")
	assert_eq(
		inspect.find_unique(_root(), "%Solo/Leaf"),
		main.get_node("Solo/Leaf"),
		"the rest of the path is read from Solo"
	)
	assert_true(inspect.find_unique(_root(), "%Solo/Nope") == null, "a missing rest")
	_free(built, inspect)


func test_a_sub_scene_root_reaching_its_parents_name_counts_once() -> void:
	var inspect: Node = _inspect_script.new()
	var built: Array[Node] = _build()
	var main: Node = built[0]
	var matches: Array = inspect.unique_matches(_root(), "%Solo")
	assert_eq(matches.size(), 1, "Main and Sub reach the same Solo")
	assert_eq(matches[0][0], main, "listed with the first owner that reached it")
	assert_eq(matches[0][1], main.get_node("Solo"), "Main's Solo")
	_free(built, inspect)


func test_a_unique_name_two_scenes_hold_finds_nothing() -> void:
	var inspect: Node = _inspect_script.new()
	var built: Array[Node] = _build()
	assert_true(inspect.find_unique(_root(), "%Shared") == null, "no node for an ambiguous name")
	var matches: Array = inspect.unique_matches(_root(), "%Shared")
	assert_eq(matches.size(), 2, "both scenes hold it")
	assert_eq([matches[0][0], matches[1][0]], [built[0], built[1]], "Main then Hud, breadth first")
	_free(built, inspect)


func test_a_unique_name_no_scene_holds_says_so() -> void:
	var inspect: Node = _inspect_script.new()
	var built: Array[Node] = _build()
	assert_true(inspect.find_unique(_root(), "%Missing") == null, "no node")
	assert_eq(
		inspect.not_found_under(_root(), "%Missing/Label", SCENE_HINT),
		(
			"No node '%Missing/Label' in the running game: no scene under /root has a node with the"
			+ " unique name '%Missing' (a unique name is one saved with unique_name_in_owner)."
		),
		"the whole value, then the unique name"
	)
	_free(built, inspect)


static func _root() -> Window:
	return (Engine.get_main_loop() as SceneTree).root


## [Main, Hud], added under the root.
static func _build() -> Array[Node]:
	var main := _owner("Main")
	var solo: Node = _unique(main, main, "Solo")
	var leaf := Node.new()
	leaf.name = "Leaf"
	solo.add_child(leaf)
	leaf.owner = main
	_unique(main, main, "Shared")
	var sub := Node.new()
	sub.name = "Sub"
	sub.scene_file_path = "res://sub.tscn"
	main.add_child(sub)
	sub.owner = main
	_unique(sub, sub, "Inner")
	var hud := _owner("Hud")
	_unique(hud, hud, "Shared")
	return [main, hud]


static func _owner(node_name: String) -> Node:
	var node := Node.new()
	node.name = node_name
	_root().add_child(node)
	return node


static func _unique(parent: Node, scene_owner: Node, node_name: String) -> Node:
	var node := Node.new()
	node.name = node_name
	parent.add_child(node)
	node.owner = scene_owner
	node.unique_name_in_owner = true
	return node


static func _free(built: Array[Node], inspect: Node) -> void:
	for node: Node in built:
		_root().remove_child(node)
		node.free()
	inspect.free()
