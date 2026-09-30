# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## The state reader (bridge/godot_mcp_state.gd) over a hand-built tree: /root/Game with marked
## nodes, and a stand-in bridge beside it holding the inspector, the JSON module and an
## unregistered logger. The runner's tests run before the root enters the tree, so the group and
## the paths are stood in for: marked walks the tree in order for nodes in the mcp_state group
## (is_in_group works outside a tree), and path_of names a node by its path from the stand-in root.
## A C# node is stood in for by a node with the csharp meta, and the C# helper by a Dotnet child
## whose load and callable are fakes.

const NO_METHOD := "in the mcp_state group but has no _mcp_state method"
const COROUTINE := "_mcp_state returned a coroutine, which is not awaited"
const CSHARP_NAME := "defines _McpState, the C# name; a GDScript node defines _mcp_state"
const STATE_SOURCE := (
	"extends Node\n\nvar state: Variant = {}\n\n\n"
	+ "func _mcp_state() -> Variant:\n\treturn state\n"
)
const RAISING_SOURCE := (
	"extends Node\n\nvar logger: Logger\n\n\nfunc _mcp_state() -> Dictionary:\n"
	+ "\tvar none: Array[ScriptBacktrace] = []\n"
	+ "\tlogger._log_error('f', 'res://t.gd', 1, 'the state broke', '', false, 0, none)\n"
	+ "\treturn {}\n"
)
const COROUTINE_SOURCE := (
	"extends Node\n\nsignal never\n\n\n"
	+ "func _mcp_state() -> Dictionary:\n\tawait never\n\treturn {}\n"
)
const SIZE_LIMIT := "<size limit>"
const LOST := "_mcp_state raised an error the log no longer holds"
const FREEING_SOURCE := (
	"extends Node\n\nvar victims: Array = []\n\n\n"
	+ "func _mcp_state() -> Dictionary:\n\tfor victim: Node in victims:\n\t\tvictim.free()\n"
	+ "\treturn {}\n"
)
const WARNING_SOURCE := (
	"extends Node\n\nvar logger: Logger\n\n\nfunc _mcp_state() -> Dictionary:\n"
	+ "\tvar none: Array[ScriptBacktrace] = []\n"
	+ "\tlogger._log_error('f', 'res://t.gd', 1, 'a warning', '', false, 1, none)\n"
	+ '\treturn {"ok": true}\n'
)
const CSHARP_NAME_SOURCE := "extends Node\n\n\nfunc _McpState() -> Dictionary:\n\treturn {}\n"
const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _logger: Logger\nvar _inspect: Node\n"
	+ "var _dotnet: Node\nvar top: Node\n\n\nfunc _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)
const EXTENSION := "C:/cache/dotnet/0123abcd/godot_mcp_dotnet.gdextension"
const BOTH := "reads _McpState; its _mcp_state is not read"
const CANNOT_RUN := (
	"Node is a C# script; its _McpState is read by the C# helper, " + "which cannot run: "
)
const NO_BUILD := (
	"Node is a C# script; its _McpState is read by the C# helper, "
	+ "which this project has no build of"
)

var _state_script: GDScript = load_bridge_script("godot_mcp_state.gd")
var _dotnet_script: GDScript = load_bridge_script("godot_mcp_dotnet.gd")


func test_a_member_with_the_method_reads_its_state_and_one_without_says_so() -> void:
	var rig: Dictionary = _rig()
	var hud: Node = _add(rig["game"], "Hud", STATE_SOURCE, true)
	hud.state = {"hp": 3, "pos": Vector2(1, 2), "tags": ["a"], "who": null}
	_add(rig["game"], "Plain", "", true)
	_add(rig["game"], "Unmarked", STATE_SOURCE, false)
	var result: Dictionary = rig["state"].handle({}).get("result", {})
	assert_eq(
		result.get("nodes"),
		[
			{
				"path": "/root/Game/Hud",
				"class": "Node",
				"state": {"hp": 3, "pos": {"x": 1.0, "y": 2.0}, "tags": ["a"], "who": null},
			},
			{"path": "/root/Game/Plain", "class": "Node", "error": NO_METHOD},
		],
		"the marked nodes in tree order, the unmarked one left out"
	)
	assert_eq(result.get("total"), 2, "total counts the marked nodes")
	assert_eq(result.get("frame"), Engine.get_process_frames(), "the frame of the read")
	assert_true(not result.has("omitted"), "nothing is omitted")
	_free(rig)


func test_a_subtree_filter_keeps_the_node_and_the_marked_nodes_under_it() -> void:
	var rig: Dictionary = _rig()
	_add(rig["game"], "Hud", STATE_SOURCE, true)
	var board: Node = _add(rig["game"], "Board", STATE_SOURCE, true)
	_add(board, "Card", STATE_SOURCE, true)
	var result: Dictionary = rig["state"].handle({"node": "Board"}).get("result", {})
	assert_eq(
		_paths(result), ["/root/Game/Board", "/root/Game/Board/Card"], "Board and its Card only"
	)
	assert_eq(result.get("total"), 2, "total counts the kept nodes")
	assert_eq(
		rig["state"].handle({"node": "Nowhere"}),
		{
			"error":
			(
				"No node named 'Nowhere' anywhere under /root in the running game; "
				+ "get_scene_tree lists the nodes' paths."
			)
		},
		"a node that is not there is refused as the inspector refuses it"
	)
	_free(rig)


func test_the_bridges_own_nodes_are_never_read() -> void:
	var rig: Dictionary = _rig()
	var bridge: Node = rig["bridge"]
	bridge.add_to_group("mcp_state")
	_add(bridge, "Inner", STATE_SOURCE, true)
	_add(rig["game"], "Hud", STATE_SOURCE, true)
	var result: Dictionary = rig["state"].handle({}).get("result", {})
	assert_eq(_paths(result), ["/root/Game/Hud"], "the bridge and its child are left out")
	assert_eq(result.get("total"), 1, "and not counted")
	_free(rig)


func test_a_raising_method_gives_the_feeds_error_and_the_read_goes_on() -> void:
	var rig: Dictionary = _rig()
	var broken: Node = _add(rig["game"], "Broken", RAISING_SOURCE, true)
	broken.logger = rig["bridge"]._logger
	var after: Node = _add(rig["game"], "After", STATE_SOURCE, true)
	after.state = {"ok": true}
	var nodes: Array = rig["state"].handle({}).get("result", {}).get("nodes", [])
	assert_eq(
		nodes,
		[
			{
				"path": "/root/Game/Broken",
				"class": "Node",
				"error": "_mcp_state raised: the state broke"
			},
			{"path": "/root/Game/After", "class": "Node", "state": {"ok": true}},
		],
		"the error's message, then the next node read"
	)
	_free(rig)


func test_a_coroutine_is_an_error_and_is_not_awaited() -> void:
	var rig: Dictionary = _rig()
	_add(rig["game"], "Waiting", COROUTINE_SOURCE, true)
	var nodes: Array = rig["state"].handle({}).get("result", {}).get("nodes", [])
	assert_eq(
		nodes,
		[{"path": "/root/Game/Waiting", "class": "Node", "error": COROUTINE}],
		"the coroutine's error"
	)
	_free(rig)


func test_a_gdscript_node_with_only_the_csharp_name_is_told_the_gdscript_one() -> void:
	var rig: Dictionary = _rig()
	_add(rig["game"], "Confused", CSHARP_NAME_SOURCE, true)
	var nodes: Array = rig["state"].handle({}).get("result", {}).get("nodes", [])
	assert_eq(
		nodes,
		[{"path": "/root/Game/Confused", "class": "Node", "error": CSHARP_NAME}],
		"the C# name's error"
	)
	_free(rig)


func test_nesting_past_max_depth_writes_the_marker() -> void:
	var rig: Dictionary = _rig()
	var deep: Node = _add(rig["game"], "Deep", STATE_SOURCE, true)
	deep.state = {"a": {"b": {"c": 1}}, "list": [[1], 2]}
	assert_eq(
		_first_state(rig, 2),
		{"a": {"b": "<depth limit: Dictionary>"}, "list": ["<depth limit: Array>", 2]},
		"two levels written, the third cut"
	)
	assert_eq(
		_first_state(rig, 1),
		{"a": "<depth limit: Dictionary>", "list": "<depth limit: Array>"},
		"one level written"
	)
	assert_eq(
		_first_state(rig, 3), {"a": {"b": {"c": 1}}, "list": [[1], 2]}, "three levels, all written"
	)
	deep.state = [{"x": 1}]
	assert_eq(_first_state(rig, 1), ["<depth limit: Dictionary>"], "an Array returned is level 1")
	_free(rig)


func test_a_cyclic_dictionary_ends_at_the_cut() -> void:
	var rig: Dictionary = _rig()
	var looped: Node = _add(rig["game"], "Looped", STATE_SOURCE, true)
	var cycle: Dictionary = {"n": 1}
	cycle["self"] = cycle
	looped.state = cycle
	var written: Variant = _first_state(rig, 3)
	cycle.erase("self")
	assert_eq(
		written,
		{"n": 1, "self": {"n": 1, "self": {"n": 1, "self": "<depth limit: Dictionary>"}}},
		"three levels, then the marker"
	)
	_free(rig)


func test_shared_references_stop_quickly_at_the_size_limit() -> void:
	var rig: Dictionary = _rig()
	var grid: Node = _add(rig["game"], "Grid", STATE_SOURCE, true)
	var cells: Array = []
	for index in 64:
		cells.append({"id": index, "n": []})
	for index in 64:
		for step in [1, 2, 3, 4]:
			cells[index]["n"].append(cells[(index + step) % 64])
	grid.state = {"cells": cells}
	var started: int = Time.get_ticks_msec()
	var written: Variant = _first_state(rig, 8)
	var elapsed: int = Time.get_ticks_msec() - started
	for cell: Dictionary in cells:
		cell["n"].clear()
	assert_eq(written["cells"][0]["id"], 0, "the first cell is written")
	assert_eq(written["cells"][63], SIZE_LIMIT, "the last cell is past the limit")
	assert_true(elapsed < 5000, "the write stops quickly: %d ms" % elapsed)
	_free(rig)


func test_a_self_appending_array_stops_at_the_size_limit() -> void:
	var rig: Dictionary = _rig()
	var looped: Node = _add(rig["game"], "Looped", STATE_SOURCE, true)
	var items: Array = []
	for _index in 3:
		items.append(items)
	looped.state = items
	var written: Variant = _first_state(rig, 8)
	items.clear()
	assert_eq(typeof(written[0]), TYPE_ARRAY, "the first branch is written")
	assert_eq(written[2], SIZE_LIMIT, "the third branch is past the limit")
	_free(rig)


func test_a_node_an_earlier_read_freed_is_skipped() -> void:
	var rig: Dictionary = _rig()
	var freeing: Node = _add(rig["game"], "Freeing", FREEING_SOURCE, true)
	var victim: Node = _add(rig["game"], "Victim", STATE_SOURCE, true)
	_add(rig["game"], "Later", STATE_SOURCE, true)
	var gone: Node = _add(rig["game"], "Gone", STATE_SOURCE, true)
	freeing.victims = [victim, gone]
	var result: Dictionary = rig["state"].handle({"maxNodes": 3}).get("result", {})
	assert_eq(_paths(result), ["/root/Game/Freeing", "/root/Game/Later"], "the freed node skipped")
	assert_eq(result.get("total"), 4, "total counts the marked nodes found")
	assert_eq(result.get("omitted"), {"count": 1, "paths": []}, "a freed omitted node has no path")
	_free(rig)


func test_an_error_the_full_log_dropped_still_counts_as_raised() -> void:
	var rig: Dictionary = _rig()
	var logger: Logger = rig["bridge"]._logger
	var warns: Node = _add(rig["game"], "Warns", WARNING_SOURCE, true)
	warns.logger = logger
	var broken: Node = _add(rig["game"], "Broken", RAISING_SOURCE, true)
	broken.logger = logger
	var none: Array[ScriptBacktrace] = []
	for _index in 199:
		logger._log_error("f", "res://t.gd", 1, "filler", "", false, 1, none)
	var nodes: Array = rig["state"].handle({}).get("result", {}).get("nodes", [])
	assert_eq(nodes[0].get("state"), {"ok": true}, "a warning the log holds is no error")
	assert_eq(nodes[1].get("error"), LOST, "the error the full log dropped")
	_free(rig)


func test_values_of_every_kind_go_through_to_json() -> void:
	var rig: Dictionary = _rig()
	var hud: Node = _add(rig["game"], "Hud", STATE_SOURCE, true)
	var plain := Object.new()
	var freed := Object.new()
	freed.free()
	var ints: Array[int] = [1, 2]
	hud.state = {
		"node": hud,
		"resource": Resource.new(),
		"object": plain,
		"callable": Callable(hud, "get_class"),
		"ints": ints,
		"packed": PackedInt32Array([3, 4]),
		"name": &"sn",
		"freed": freed,
	}
	var json: GDScript = rig["bridge"]._json
	json.node_root = rig["top"]
	var written: Variant = _first_state(rig, 4)
	json.node_root = null
	plain.free()
	assert_eq(written["node"], "Game/Hud", "a Node as its path")
	assert_eq(written["resource"].get("class"), "Resource", "a Resource by its class")
	assert_eq(written["object"].get("class"), "Object", "a plain Object as {class, string}")
	assert_eq(typeof(written["callable"]), TYPE_STRING, "a Callable as its text")
	assert_eq(written["ints"], [1, 2], "a typed Array as a list")
	assert_eq(written["packed"], [3, 4], "a packed array as a list")
	assert_eq(written["name"], "sn", "a StringName as its text")
	assert_eq(written["freed"], "<freed object>", "a freed Object")
	_free(rig)


func test_max_nodes_reads_the_first_and_lists_the_rest() -> void:
	var rig: Dictionary = _rig()
	for index in 25:
		_add(rig["game"], "N%d" % index, STATE_SOURCE, true)
	var result: Dictionary = rig["state"].handle({"maxNodes": 3}).get("result", {})
	assert_eq(
		_paths(result), ["/root/Game/N0", "/root/Game/N1", "/root/Game/N2"], "the first three"
	)
	assert_eq(result.get("total"), 25, "total counts every marked node")
	var expected: Array = []
	for index in range(3, 23):
		expected.append("/root/Game/N%d" % index)
	assert_eq(
		result.get("omitted"), {"count": 22, "paths": expected}, "22 left out, the first 20 named"
	)
	_free(rig)


func test_csharp_nodes_go_to_the_helper_in_one_call_and_its_reply_passes_through() -> void:
	var rig: Dictionary = _rig()
	_add(rig["game"], "First", STATE_SOURCE, true)
	var one: String = str(_add_csharp(rig["game"], "CsOne", "").get_instance_id())
	_add(rig["game"], "Middle", STATE_SOURCE, true)
	var two: String = str(_add_csharp(rig["game"], "CsTwo", "").get_instance_id())
	var reply: String = (
		'{"ok":true,"result":{"nodes":[{"id":"%s","state":18446744073709551615},' % one
		+ '{"id":"%s","state":null}]}}' % two
	)
	var calls: Dictionary = _helper(rig, GDExtensionManager.LOAD_STATUS_OK, reply)
	var params: Dictionary = {"extension": EXTENSION, "maxDepth": 3.0}
	var result: Dictionary = rig["state"].handle(params).get("result", {})
	assert_eq(
		result.get("nodes"),
		[
			{"path": "/root/Game/First", "class": "Node", "state": {}},
			{"path": "/root/Game/CsOne", "class": "Node", "id": one},
			{"path": "/root/Game/Middle", "class": "Node", "state": {}},
			{"path": "/root/Game/CsTwo", "class": "Node", "id": two},
		],
		"the GDScript nodes read, the C# ones left to the server by id, in tree order"
	)
	assert_true(result.get("csharp") == reply, "the helper's reply, byte for byte")
	assert_eq(
		calls["requests"],
		['{"ids":["%s","%s"],"maxDepth":3,"op":"state"}' % [one, two]],
		"one state call with every C# node's id and maxDepth as an integer"
	)
	assert_eq(calls["loads"], [1], "the helper loaded once")
	_free(rig)


func test_a_missing_mcpstate_falls_back_to_mcp_state_and_both_names_warn() -> void:
	var rig: Dictionary = _rig()
	var fallback: Node = _add_csharp(rig["game"], "Fallback", STATE_SOURCE)
	fallback.state = {"from": "_mcp_state"}
	var bare: String = str(_add_csharp(rig["game"], "Bare", "").get_instance_id())
	var both: String = str(_add_csharp(rig["game"], "Both", STATE_SOURCE).get_instance_id())
	var reply: String = (
		(
			'{"ok":true,"result":{"nodes":[{"id":"%s","missing":true,"error":"x"},'
			% fallback.get_instance_id()
		)
		+ '{"id":"%s","missing":true,"error":"y"},{"id":"%s","state":1}]}}' % [bare, both]
	)
	_helper(rig, GDExtensionManager.LOAD_STATUS_OK, reply)
	var result: Dictionary = rig["state"].handle({"extension": EXTENSION}).get("result", {})
	assert_eq(
		result.get("nodes"),
		[
			{"path": "/root/Game/Fallback", "class": "Node", "state": {"from": "_mcp_state"}},
			{"path": "/root/Game/Bare", "class": "Node", "id": bare},
			{"path": "/root/Game/Both", "class": "Node", "id": both, "warning": BOTH},
		],
		"read through _mcp_state, left for the helper's error, and warned"
	)
	assert_true(result.get("csharp") == reply, "the reply still passes through")
	_free(rig)


func test_without_an_extension_a_csharp_node_reads_mcp_state_or_says_there_is_no_build() -> void:
	var rig: Dictionary = _rig()
	_add_csharp(rig["game"], "Bare", "")
	_add_csharp(rig["game"], "Visible", STATE_SOURCE)
	var result: Dictionary = rig["state"].handle({}).get("result", {})
	assert_eq(
		result.get("nodes"),
		[
			{"path": "/root/Game/Bare", "class": "Node", "error": NO_BUILD},
			{"path": "/root/Game/Visible", "class": "Node", "state": {}},
		],
		"the no-build error, and the Godot-visible _mcp_state read"
	)
	assert_true(not result.has("csharp"), "no helper reply")
	_free(rig)


func test_a_csharp_error_from_the_server_is_each_csharp_nodes_error_without_mcp_state() -> void:
	var rig: Dictionary = _rig()
	_add_csharp(rig["game"], "Bare", "")
	_add_csharp(rig["game"], "Visible", STATE_SOURCE)
	_add(rig["game"], "Gd", "", true)
	var params: Dictionary = {"csharpError": "The C# helper is not built."}
	var result: Dictionary = rig["state"].handle(params).get("result", {})
	var cannot_run: String = CANNOT_RUN + "The C# helper is not built."
	assert_eq(
		result.get("nodes"),
		[
			{"path": "/root/Game/Bare", "class": "Node", "error": cannot_run},
			{"path": "/root/Game/Visible", "class": "Node", "state": {}},
			{"path": "/root/Game/Gd", "class": "Node", "error": NO_METHOD},
		],
		"the server's reason, _mcp_state still read, and a GDScript node untouched"
	)
	assert_true(not result.has("csharp"), "no helper reply")
	_free(rig)


func test_a_node_the_helper_could_not_read_is_not_warned_for_its_mcp_state() -> void:
	var rig: Dictionary = _rig()
	var both: String = str(_add_csharp(rig["game"], "Both", STATE_SOURCE).get_instance_id())
	var reply: String = (
		'{"ok":true,"result":{"nodes":[{"id":"%s","error":"InvalidOperationException: x"}]}}' % both
	)
	_helper(rig, GDExtensionManager.LOAD_STATUS_OK, reply)
	var result: Dictionary = rig["state"].handle({"extension": EXTENSION}).get("result", {})
	assert_eq(
		result.get("nodes"),
		[{"path": "/root/Game/Both", "class": "Node", "id": both}],
		"left for the helper's error, with no warning"
	)
	_free(rig)


func test_a_helper_that_cannot_load_gives_each_csharp_node_its_error() -> void:
	var rig: Dictionary = _rig()
	_add_csharp(rig["game"], "Bare", "")
	_add_csharp(rig["game"], "Visible", STATE_SOURCE)
	var calls: Dictionary = _helper(rig, GDExtensionManager.LOAD_STATUS_FAILED, "{}")
	var failed: String = (
		CANNOT_RUN
		+ "Godot could not load the C# helper extension at %s (load status 1)." % EXTENSION
	)
	var expected: Array = [
		{"path": "/root/Game/Bare", "class": "Node", "error": failed},
		{"path": "/root/Game/Visible", "class": "Node", "state": {}},
	]
	var first: Dictionary = rig["state"].handle({"extension": EXTENSION}).get("result", {})
	var second: Dictionary = rig["state"].handle({"extension": EXTENSION}).get("result", {})
	assert_eq(first.get("nodes"), expected, "the load error, and _mcp_state still read")
	assert_eq(second.get("nodes"), expected, "the remembered error on the next read")
	assert_true(not first.has("csharp"), "no helper reply")
	assert_eq(calls["loads"], [1], "the load is tried once")
	assert_eq(calls["requests"], [], "the helper is never called")
	_free(rig)


func test_a_helper_refusal_is_each_csharp_nodes_error() -> void:
	var rig: Dictionary = _rig()
	_add_csharp(rig["game"], "Bare", "")
	_helper(rig, GDExtensionManager.LOAD_STATUS_OK, '{"ok":false,"error":"Unknown op \'state\'."}')
	var result: Dictionary = rig["state"].handle({"extension": EXTENSION}).get("result", {})
	assert_eq(
		result.get("nodes"),
		[
			{
				"path": "/root/Game/Bare",
				"class": "Node",
				"error": CANNOT_RUN + "the C# helper refused the state read: Unknown op 'state'."
			}
		],
		"the helper's refusal"
	)
	assert_true(not result.has("csharp"), "no helper reply")
	_free(rig)


func test_the_helper_is_not_called_without_a_csharp_node_to_read() -> void:
	var rig: Dictionary = _rig()
	_add(rig["game"], "First", STATE_SOURCE, true)
	_add_csharp(rig["game"], "Past", "")
	var calls: Dictionary = _helper(rig, GDExtensionManager.LOAD_STATUS_OK, "{}")
	var params: Dictionary = {"extension": EXTENSION, "maxNodes": 1}
	var result: Dictionary = rig["state"].handle(params).get("result", {})
	assert_eq(_paths(result), ["/root/Game/First"], "the GDScript node read")
	assert_eq(
		result.get("omitted"), {"count": 1, "paths": ["/root/Game/Past"]}, "the C# one omitted"
	)
	assert_true(not result.has("csharp"), "no helper reply")
	assert_eq(calls["loads"], [0], "the helper is never loaded")
	assert_eq(calls["requests"], [], "or called")
	_free(rig)


## {top, game, bridge, state}: a stand-in root named root holding Game and the stand-in bridge
## GodotMcpBridge, and a state reader whose marked and path_of read that tree and whose is_csharp
## takes a node with the csharp meta for a C# one.
func _rig() -> Dictionary:
	var top := Node.new()
	top.name = "root"
	var game := Node.new()
	game.name = "Game"
	top.add_child(game)
	var bridge: Node = _compile(BRIDGE_SOURCE)
	bridge.name = "GodotMcpBridge"
	top.add_child(bridge)
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = load_bridge_script("godot_mcp_logger.gd").new()
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	var state: Node = _state_script.new()
	state.bridge = bridge
	state.marked = func() -> Array: return _marked(top, [])
	state.path_of = func(node: Node) -> String: return "/root/%s" % top.get_path_to(node)
	state.is_csharp = func(node: Node) -> bool: return node.has_meta("csharp")
	return {"top": top, "game": game, "bridge": bridge, "state": state}


func _free(rig: Dictionary) -> void:
	rig["state"].free()
	rig["bridge"]._inspect.free()
	if rig["bridge"]._dotnet != null:
		rig["bridge"]._dotnet.free()
	rig["top"].free()


## A marked node under parent that the rig's is_csharp takes for a C# node, with a script compiled
## from source when it is not empty.
func _add_csharp(parent: Node, node_name: String, source: String) -> Node:
	var node: Node = _add(parent, node_name, source, true)
	node.set_meta("csharp", true)
	return node


## Gives the rig's bridge a Dotnet child whose load answers status and whose helper answers reply;
## returns {loads: [count], requests: [each request]} as they happen.
func _helper(rig: Dictionary, status: int, reply: String) -> Dictionary:
	var calls: Dictionary = {"loads": [0], "requests": []}
	var dotnet: Node = _dotnet_script.new()
	dotnet.load_extension = func(_path: String) -> int:
		calls["loads"][0] += 1
		return status
	var host := RefCounted.new()
	host.set_meta(
		"godot_mcp_dotnet",
		func(request: String) -> String:
			calls["requests"].append(request)
			return reply
	)
	dotnet.meta_host = host
	rig["bridge"]._dotnet = dotnet
	return calls


## A node named node_name under parent, with a script compiled from source when it is not empty,
## and in the mcp_state group when marked.
func _add(parent: Node, node_name: String, source: String, marked: bool) -> Node:
	var node: Node = Node.new() if source.is_empty() else _compile(source)
	node.name = node_name
	if marked:
		node.add_to_group("mcp_state")
	parent.add_child(node)
	return node


## The nodes in the mcp_state group under node, itself included, depth first as the tree orders
## them.
func _marked(node: Node, into: Array) -> Array:
	if node.is_in_group("mcp_state"):
		into.append(node)
	for child: Node in node.get_children():
		_marked(child, into)
	return into


## The state of the first node read at max_depth.
func _first_state(rig: Dictionary, max_depth: int) -> Variant:
	var nodes: Array = rig["state"].handle({"maxDepth": max_depth}).get("result", {}).get(
		"nodes", []
	)
	return nodes[0].get("state") if not nodes.is_empty() else null


static func _paths(result: Dictionary) -> Array:
	var paths: Array = []
	for entry: Dictionary in result.get("nodes", []):
		paths.append(entry["path"])
	return paths


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
