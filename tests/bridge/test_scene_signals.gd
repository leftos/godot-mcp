# gdlint: disable=private-method-call
# _holder is the private step the inherited-connection walk starts from, pinned here alone.
extends "res://gd_test.gd"
## The headless signal ops' pure helpers (headless/scene_signals.gd): reading a connection's
## inherited flag, a connection target's path from the scene's root, and the nearest node holding
## both ends of a connection.

const SCENE_SIGNALS_SCRIPT := "../../headless/scene_signals.gd"

var _signals: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_SIGNALS_SCRIPT).simplify_path()
)


func test_is_inherited_reads_flag_32() -> void:
	assert_true(_signals.is_inherited(34), "PERSIST | INHERITED, as an instanced scene's reads")
	assert_true(_signals.is_inherited(32), "INHERITED alone")
	assert_true(not _signals.is_inherited(CONNECT_PERSIST), "a scene's own connection")
	assert_true(not _signals.is_inherited(0), "no flags")


func test_target_path_is_relative_to_the_root() -> void:
	var root := Node.new()
	var child := Node.new()
	child.name = "Child"
	var grandchild := Node.new()
	grandchild.name = "Grand"
	root.add_child(child)
	child.add_child(grandchild)
	var outside := Node.new()
	assert_eq(_signals.target_path(root, root), ".", "the root itself")
	assert_eq(_signals.target_path(root, grandchild), "Child/Grand", "a node under it")
	assert_eq(_signals.target_path(root, outside), null, "a node outside the scene")
	assert_eq(_signals.target_path(root, Resource.new()), null, "an object that is not a node")
	outside.free()
	root.free()


func test_holder_is_the_nearest_node_holding_both_ends() -> void:
	var root := Node.new()
	var boss := Node.new()
	boss.name = "Boss"
	var sprite := Node.new()
	sprite.name = "Sprite"
	var btn := Node.new()
	btn.name = "Btn"
	root.add_child(boss)
	boss.add_child(sprite)
	root.add_child(btn)
	assert_eq(_signals._holder(sprite, boss), boss, "a child connected to its parent")
	assert_eq(_signals._holder(boss, sprite), boss, "a parent connected to its child")
	assert_eq(_signals._holder(sprite, btn), root, "two branches meet at the root")
	assert_eq(_signals._holder(sprite, sprite), sprite, "a node connected to itself")
	var outside := Node.new()
	var none: Node = _signals._holder(sprite, outside)
	assert_true(none == null, "no node holds a node outside the tree")
	outside.free()
	root.free()
