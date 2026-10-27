# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## A wait's condition on a node saved with a unique name (bridge/godot_mcp_conditions.gd), on a
## headless tree whose root the conditions are pointed at: each check walks the scenes once, the
## inspector counting its walks.
##
## The tests run before the root enters a tree, where get_path fails (DEVELOPMENT.md's footguns),
## so no test here covers an ambiguous name, whose refusal names the holders' paths: InspectionTests
## and TimeTests pin that answer instead.

## A node named Only with the property the rig's checks read.
const UNIQUE_ONLY_SOURCE := "extends Node\n\nvar health: int = 3\n"
## The rig's stand-in bridge: the modules a property read uses; a path or a bare name finds no
## node, since a wait's %name reaches the tree through the inspector's unique_lookup.
const UNIQUE_BRIDGE_SOURCE := (
	"extends Node\n\nvar _inspect: Node\nvar _json: GDScript\nvar _time: Node\n\n\n"
	+ "func _find_node(_element: String) -> Node:\n\treturn null\n"
)
## The inspector, counting its tree walks: a subclass adding one to walks per unique_matches.
const COUNTING_INSPECT_SOURCE := (
	'extends "%s"\n\nvar walks: int = 0\n\n\n'
	+ "func unique_matches(root: Node, segment: String) -> Array:\n\twalks += 1\n"
	+ "\treturn super(root, segment)\n"
)
## The conditions, their tree root the rig's headless root rather than get_tree().root.
const ROOTED_CONDITIONS_SOURCE := (
	'extends "%s"\n\nvar top: Node\n\n\n' + "func _tree_root() -> Node:\n\treturn top\n"
)

var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")
var _inspect_script: GDScript = load_bridge_script("godot_mcp_inspect.gd")


func test_a_missing_unique_name_is_not_met_in_one_walk() -> void:
	var rig: Dictionary = _unique_rig()
	var probe: Callable = rig["conditions"].make_probe("exists", {"node": "%Nowhere"}, {})

	assert_eq(probe.call(), [false, false], "a unique name no scene holds is not met")
	assert_eq(rig["inspect"].walks, 1, "one walk for a missing unique name, not two")
	_free_rig(rig)


func test_a_settled_check_of_a_found_unique_name_walks_once() -> void:
	var rig: Dictionary = _unique_rig()
	var probe: Callable = rig["conditions"].make_probe(
		"settled", {"node": "%Only", "property": "health"}, {}
	)
	var answer: Array = probe.call()

	assert_eq(answer.size(), 2, "a found node's value, not a failure: %s" % [answer])
	assert_eq(answer[1], {"frames": 0, "value": 3}, "the first check's count and the property")
	assert_eq(rig["inspect"].walks, 1, "one walk for a found unique name, not two")
	_free_rig(rig)


func test_a_found_unique_name_answers_its_property_in_one_walk() -> void:
	var rig: Dictionary = _unique_rig()

	var answer: Array = rig["conditions"].check_property("%Only", "health", 3, "exact")

	assert_eq(answer, [true, 3], "the property's value")
	assert_eq(rig["inspect"].walks, 1, "one walk for a found unique name")
	_free_rig(rig)


func test_a_path_under_a_unique_name_finds_its_child_in_one_walk() -> void:
	var rig: Dictionary = _unique_rig()

	var answer: Array = rig["conditions"].check_property("%Only/Child", "health", 3, "exact")

	assert_eq(answer, [true, 3], "the child's property value")
	assert_eq(rig["inspect"].walks, 1, "one walk for a unique name with a path under it")
	_free_rig(rig)


## {top, bridge, conditions, inspect}: a headless root holding a scene owner whose node Only is
## unique in it and holds a Child, the inspector counting its walks, and conditions whose tree root
## is that root.
func _unique_rig() -> Dictionary:
	var top := Node.new()
	top.name = "root"
	var scene := Node.new()
	scene.name = "Owner"
	top.add_child(scene)
	var only: Node = _compile(UNIQUE_ONLY_SOURCE)
	only.name = "Only"
	scene.add_child(only)
	only.owner = scene
	only.unique_name_in_owner = true
	var child: Node = _compile(UNIQUE_ONLY_SOURCE)
	child.name = "Child"
	only.add_child(child)
	var inspect: Node = _compile(COUNTING_INSPECT_SOURCE % _inspect_script.resource_path)
	var bridge: Node = _compile(UNIQUE_BRIDGE_SOURCE)
	bridge._inspect = inspect
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._time = load_bridge_script("godot_mcp_time.gd").new()
	var conditions: Node = _compile(ROOTED_CONDITIONS_SOURCE % _conditions_script.resource_path)
	conditions.top = top
	conditions.bridge = bridge
	return {"top": top, "bridge": bridge, "conditions": conditions, "inspect": inspect}


func _free_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"]._time.free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
