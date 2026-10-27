extends "res://gd_test.gd"
## A property or state condition's equals (bridge/godot_mcp_conditions.gd): null meets a present
## null and never a missing key, and a string compared by match prefix or contains, as stored. The
## nodes are in no tree, as test_conditions.gd's state rig holds them.

## A marked node's script whose _mcp_state returns its state variable.
const STATE_SOURCE := (
	"extends Node\n\nvar state: Variant = {}\n\n\n"
	+ "func _mcp_state() -> Variant:\n\treturn state\n"
)
## The rig's stand-in bridge: the modules the state reader uses, and a node lookup by name under
## top.
const STATE_BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _logger: Logger\nvar _inspect: Node\n"
	+ "var _state: Node\nvar top: Node\n\n\nfunc _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)

var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")


func test_a_null_equals_meets_a_present_null_and_never_a_missing_key() -> void:
	var rig: Dictionary = _state_rig()
	var board: Node = _add_state_node(rig["game"], "Board", true)
	board.state = {"owner": null, "turn": 2}
	var present: Dictionary = {"node": "Board", "state": "owner", "equals": null}
	var missing: Dictionary = {"node": "Board", "state": "winner", "equals": null}
	var not_null: Dictionary = {"node": "Board", "state": "turn", "equals": null}
	assert_eq(rig["conditions"].make_probe("state", present, {}).call(), [true, null], "present")
	assert_eq(rig["conditions"].make_probe("state", missing, {}).call(), [false, null], "missing")
	assert_eq(rig["conditions"].make_probe("state", not_null, {}).call(), [false, 2], "a number")
	_free_state_rig(rig)


func test_a_null_equals_meets_a_property_that_is_null() -> void:
	var rig: Dictionary = _state_rig()
	var board: Node = _add_state_node(rig["game"], "Board", true)
	board.state = null
	var params: Dictionary = {"node": "Board", "property": "state", "equals": null}
	var probe: Callable = rig["conditions"].make_probe("property", params, {})
	assert_eq(probe.call(), [true, null], "met while state is null")
	board.state = 3
	assert_eq(probe.call(), [false, 3], "not met once it holds a value")
	_free_state_rig(rig)


func test_a_string_equals_matches_by_prefix_or_contains_as_stored() -> void:
	var rig: Dictionary = _state_rig()
	var board: Node = _add_state_node(rig["game"], "Board", true)
	board.state = {"log": "Deep Mine\ncleared", "padded": "  Deep Mine", "count": 7}
	var cases: Array = [
		["log", "Deep Mine", "prefix", true, "the first line's start"],
		["log", "cleared", "contains", true, "a part of the second line"],
		["log", "cleared", "prefix", false, "not the start"],
		["log", "deep mine", "prefix", false, "case counts"],
		["log", "MINE", "contains", false, "case counts with contains"],
		["padded", "Deep", "prefix", false, "leading spaces are kept"],
		["padded", " Deep", "prefix", false, "and are not trimmed off either side"],
		["padded", "  Deep", "prefix", true, "the spaces as stored"],
		["count", "7", "prefix", false, "a number never meets a string"],
		["count", "7", "contains", false, "nor with contains"],
	]
	for case: Array in cases:
		var params: Dictionary = {
			"node": "Board", "state": case[0], "equals": case[1], "match": case[2]
		}
		var answer: Array = rig["conditions"].make_probe("state", params, {}).call()
		assert_eq(answer[0], case[3], case[4])
	var on_property: Dictionary = {
		"node": "Board", "property": "name", "equals": "Bo", "match": "prefix"
	}
	assert_eq(
		rig["conditions"].make_probe("property", on_property, {}).call(),
		[true, "Board"],
		"a property's string by its start"
	)
	_free_state_rig(rig)


## {top, game, bridge, conditions}: a stand-in root named root holding Game, and conditions whose
## stand-in bridge (STATE_BRIDGE_SOURCE) finds nodes under it by name and holds a state reader,
## the JSON module, an unregistered logger and the inspector.
func _state_rig() -> Dictionary:
	var top := Node.new()
	top.name = "root"
	var game := Node.new()
	game.name = "Game"
	top.add_child(game)
	var bridge: Node = _compile(STATE_BRIDGE_SOURCE)
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = load_bridge_script("godot_mcp_logger.gd").new()
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	var state: Node = load_bridge_script("godot_mcp_state.gd").new()
	state.bridge = bridge
	state.path_of = func(node: Node) -> String: return "/root/%s" % top.get_path_to(node)
	bridge._state = state
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	return {"top": top, "game": game, "bridge": bridge, "conditions": conditions}


func _free_state_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"]._state.free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()


## A node named node_name under parent running STATE_SOURCE, in the mcp_state group when marked.
func _add_state_node(parent: Node, node_name: String, marked: bool) -> Node:
	var node: Node = _compile(STATE_SOURCE)
	node.name = node_name
	if marked:
		node.add_to_group("mcp_state")
	parent.add_child(node)
	return node


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
