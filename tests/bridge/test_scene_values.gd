extends "res://gd_test.gd"
## The headless scene values (headless/scene_values.gd): a Node-typed export read from a path from
## the scene's root, a missing or mistyped node refused, other values converted as the JSON module
## converts them, and the JSON module's node_root left unset after a read.

const SCENE_VALUES_SCRIPT := "../../headless/scene_values.gd"
const NODE_INFO: Dictionary = {
	"type": TYPE_OBJECT, "hint": PROPERTY_HINT_NODE_TYPE, "hint_string": "Node2D"
}
const NOT_A_PATH := (
	"a node-typed export takes a node path from the scene root, "
	+ 'like "Card" or "Board/Card"; got '
)

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


func test_node_export_refuses_a_missing_or_mistyped_node_saying_why() -> void:
	var root: Node2D = _scene()
	var missing: Array = [false, null, 'no node at "Nope" from the scene root']
	assert_eq(_values.from_json("Nope", NODE_INFO, root), missing, "a missing node")
	var mistyped: Array = [false, null, '"Plain" is a Node; the export takes Node2D']
	assert_eq(_values.from_json("Plain", NODE_INFO, root), mistyped, "a node of another class")
	var absolute: Array = [false, null, NOT_A_PATH + '"/root/Box"']
	assert_eq(_values.from_json("/root/Box", NODE_INFO, root), absolute, "an absolute path")
	var number: Array = [false, null, NOT_A_PATH + "3"]
	assert_eq(_values.from_json(3, NODE_INFO, root), number, "a value that is not a path")
	root.free()


func test_node_export_refusal_names_each_hinted_class_and_the_script_class() -> void:
	var root: Node2D = _scene()
	var listed: Dictionary = NODE_INFO.merged({"hint_string": "Node2D, Control"}, true)
	var either: Array = [false, null, '"Plain" is a Node; the export takes Node2D or Control']
	assert_eq(_values.from_json("Plain", listed, root), either, "a list of classes")
	var plain: Node = root.get_node("Plain")
	plain.set_script(_class_script("SceneValuesOtherView", "Node"))
	var named: Array = [
		false, null, '"Plain" is a SceneValuesOtherView (Node); the export takes Node2D'
	]
	assert_eq(_values.from_json("Plain", NODE_INFO, root), named, "a script with a class_name")
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


## A script made in memory whose class_name is title, extending the native class base.
func _class_script(title: String, base: String) -> GDScript:
	var script := GDScript.new()
	script.source_code = "class_name %s\nextends %s\n" % [title, base]
	script.reload()
	return script


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
