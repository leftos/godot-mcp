extends "res://gd_test.gd"
## The headless scene values (headless/scene_values.gd): a Node-typed export read from a path from
## the scene's root, a missing or mistyped node refused, other values converted as the JSON module
## converts them, and the JSON module's node_root left unset after a read.

const SCENE_VALUES_SCRIPT := "../../headless/scene_values.gd"
const NODE_INFO: Dictionary = {
	"type": TYPE_OBJECT, "hint": PROPERTY_HINT_NODE_TYPE, "hint_string": "Node2D"
}
const REFUSED: Array = [false, null]

var _values: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_VALUES_SCRIPT).simplify_path()
)


func test_node_export_reads_a_path_from_the_root() -> void:
	var root: Node2D = _scene()
	var box: Node = root.get_node("Box")
	assert_eq(_values.from_json("Box", NODE_INFO, root), [true, box], "a child by its path")
	assert_eq(_values.from_json(".", NODE_INFO, root), [true, root], "the root itself")
	assert_eq(_values.from_json(null, NODE_INFO, root), [true, null], "null clears it")
	var any_node: Dictionary = {"type": TYPE_OBJECT, "hint": PROPERTY_HINT_NODE_TYPE}
	var plain: Node = root.get_node("Plain")
	assert_eq(_values.from_json("Plain", any_node, root), [true, plain], "no hinted class")
	root.free()


func test_node_export_refuses_a_missing_or_mistyped_node() -> void:
	var root: Node2D = _scene()
	assert_eq(_values.from_json("Nope", NODE_INFO, root), REFUSED, "a missing node")
	assert_eq(_values.from_json("Plain", NODE_INFO, root), REFUSED, "a node of another class")
	assert_eq(_values.from_json("/root/Box", NODE_INFO, root), REFUSED, "an absolute path")
	assert_eq(_values.from_json(3, NODE_INFO, root), REFUSED, "a value that is not a path")
	root.free()


func test_other_values_convert_as_the_json_module_does() -> void:
	var root: Node2D = _scene()
	var vector: Array = _values.from_json({"x": 1, "y": 2}, {"type": TYPE_VECTOR2}, root)
	assert_eq(vector, [true, Vector2(1, 2)], "a vector")
	var node_path: Array = _values.from_json("Box", {"type": TYPE_NODE_PATH}, root)
	assert_eq(node_path, [true, NodePath("Box")], "a NodePath property stays a path")
	root.free()


func test_to_json_reads_nodes_from_the_root_and_resets_node_root() -> void:
	var root: Node2D = _scene()
	assert_eq(_values.to_json(root.get_node("Box"), root), "Box", "a node as its path")
	assert_eq(_values.to_json(root, root), ".", "the root")
	assert_eq(_values.to_json({"at": root.get_node("Box")}, root), {"at": "Box"}, "a node inside")
	assert_eq(_values.Json.node_root, null, "node_root is unset after the read")
	var sprite := Sprite2D.new()
	assert_eq(_values.to_json(sprite.texture, root), null, "an empty object property")
	sprite.free()
	root.free()


## A root Level with a Node2D Box and a plain Node Plain, in no tree.
func _scene() -> Node2D:
	var root := Node2D.new()
	root.name = "Level"
	var box := Node2D.new()
	box.name = "Box"
	root.add_child(box)
	var plain := Node.new()
	plain.name = "Plain"
	root.add_child(plain)
	return root
