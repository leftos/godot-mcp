extends Node
## The godot-mcp bridge's state reader, a child of the bridge: the state command, get_game_state's
## read. It takes the nodes in the mcp_state group in tree order (SceneTree.get_nodes_in_group),
## leaves out the bridge's own nodes and, when params.node names a node, every node not at or
## under it, reads the first params.maxNodes of the rest and lists the others. A node is read by
## calling its _mcp_state(); the value is written to params.maxDepth levels of Dictionaries and
## Arrays, a deeper one as "<depth limit: Dictionary>" or "<depth limit: Array>" (so a cyclic
## value ends there too), and every other value through the JSON module's to_json. One handler
## with no await reads every node, so every node is read in the same frame.

const GROUP := "mcp_state"
const METHOD := "_mcp_state"
## The C# name of the state method, which a GDScript node defining it has confused.
const CSHARP_METHOD := "_McpState"
const DEFAULT_MAX_NODES := 50
const DEFAULT_MAX_DEPTH := 4
## The most paths omitted lists; its count is whole.
const MAX_OMITTED_PATHS := 20
const NO_METHOD := "in the mcp_state group but has no _mcp_state method"
## The most values written for one node, containers and leaves counted; past it every further value
## is SIZE_LIMIT, so shared references that fan out stop quickly.
const MAX_VALUES := 5000
const SIZE_LIMIT := "<size limit>"
const RAISED := "_mcp_state raised: %s"
## An error the logger took across the call but dropped, being at its cap.
const LOST := "_mcp_state raised an error the log no longer holds"
const COROUTINE := "_mcp_state returned a coroutine, which is not awaited"
const CSHARP_NAME := "defines _McpState, the C# name; a GDScript node defines _mcp_state"
const CSHARP_NODE := (
	"a C# node's _McpState is read by the C# helper, " + "which get_game_state does not call yet"
)

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## The marked nodes in tree order, func() -> Array; the SceneTree's mcp_state group. Tests replace
## it, since their nodes are in no tree.
var marked: Callable = func() -> Array:
	return (Engine.get_main_loop() as SceneTree).get_nodes_in_group(GROUP)
## A node's path, func(node: Node) -> String; tests replace it, since their nodes are in no tree.
var path_of: Callable = func(node: Node) -> String: return str(node.get_path())
## How many more values the node being written may take (MAX_VALUES at each node's start).
var _values_left: int = 0


## Reads the marked nodes, params {node, maxNodes, maxDepth}; answers {result: {frame, nodes,
## total, omitted?}} or {error} when params.node names no node or a node of the bridge's.
## nodes holds {path, class, state} or {path, class, error} for each node read; total counts every
## marked node kept, and omitted {count, paths} the ones past maxNodes, the first 20 paths. A node
## an earlier node's _mcp_state freed is skipped.
func handle(params: Dictionary) -> Dictionary:
	var found: Variant = _members(str(params.get("node", "")))
	if found is String:
		return {"error": found}
	var members: Array = found
	var max_nodes: int = int(params.get("maxNodes", DEFAULT_MAX_NODES))
	var max_depth: int = int(params.get("maxDepth", DEFAULT_MAX_DEPTH))
	var nodes: Array = []
	for node: Variant in members.slice(0, max_nodes):
		if is_instance_valid(node):
			nodes.append(_read(node, max_depth))
	var result: Dictionary = {
		"frame": Engine.get_process_frames(), "nodes": nodes, "total": members.size()
	}
	if members.size() > max_nodes:
		result["omitted"] = _omitted(members.slice(max_nodes))
	return {"result": result}


## The marked nodes outside the bridge, those at or under the node node_name names when it is not
## empty; or a String saying why that node cannot be read from, as the inspector refuses it.
func _members(node_name: String) -> Variant:
	if node_name.is_empty():
		return _kept(null)
	var found: Variant = bridge._inspect._resolve(node_name)
	return found if found is String else _kept(found)


## The marked nodes outside the bridge, at or under under when it is not null.
func _kept(under: Node) -> Array:
	var kept: Array = []
	for node: Node in marked.call():
		if node == bridge or bridge.is_ancestor_of(node):
			continue
		if under == null or node == under or under.is_ancestor_of(node):
			kept.append(node)
	return kept


## One node's entry: {path, class, state}, or {path, class, error} when it has no state method or
## the call logged an error or returned a coroutine, which is never awaited.
func _read(node: Node, max_depth: int) -> Dictionary:
	var entry: Dictionary = {"path": path_of.call(node), "class": node.get_class()}
	var refusal: String = _refusal(node)
	if not refusal.is_empty():
		entry["error"] = refusal
		return entry
	var mark: int = bridge._logger.sequence()
	var value: Variant = node.call(METHOD)
	var raised: String = _raised_since(mark)
	if not raised.is_empty():
		entry["error"] = raised
	elif bridge._inspect.is_coroutine(value):
		entry["error"] = COROUTINE
	else:
		_values_left = MAX_VALUES
		entry["state"] = _written(value, 0, max_depth)
	return entry


## The error text for the first error logged at or after sequence number mark, LOST when an entry
## logged since was dropped at the logger's cap and none is held, or "" when there is neither.
func _raised_since(mark: int) -> String:
	var raised: String = bridge._logger.first_error_since(mark)
	if not raised.is_empty():
		return RAISED % raised
	return LOST if bridge._logger.lost_since(mark) else ""


## Why the node is not read, or "" when it has _mcp_state: a C# node without one is the C#
## helper's, and a GDScript node with the C# name defines the wrong one.
func _refusal(node: Node) -> String:
	if node.has_method(METHOD):
		return ""
	if _is_csharp(node):
		return CSHARP_NODE
	if node.has_method(CSHARP_METHOD):
		return CSHARP_NAME
	return NO_METHOD


## Whether the node's script is a C# script; the class is named as a String, so a build without
## C# support, which has no CSharpScript, reads false.
static func _is_csharp(node: Node) -> bool:
	var node_script := node.get_script() as Script
	return node_script != null and node_script.is_class("CSharpScript")


## value as JSON: a Dictionary or Array at depth (the returned value is at 0) below max_depth is
## written element by element, one at max_depth or deeper as its depth-limit marker, and any
## other value through to_json. Each value written takes one of _values_left; once none is left,
## every further value is SIZE_LIMIT.
func _written(value: Variant, depth: int, max_depth: int) -> Variant:
	if _values_left <= 0:
		return SIZE_LIMIT
	_values_left -= 1
	var type: int = typeof(value)
	if type != TYPE_DICTIONARY and type != TYPE_ARRAY:
		return bridge._json.to_json(value)
	if depth >= max_depth:
		return "<depth limit: %s>" % ("Dictionary" if type == TYPE_DICTIONARY else "Array")
	return _written_container(value, depth, max_depth)


## A Dictionary or Array below max_depth written element by element, its elements at depth + 1.
func _written_container(value: Variant, depth: int, max_depth: int) -> Variant:
	if typeof(value) == TYPE_ARRAY:
		var items: Array = []
		for item: Variant in value:
			items.append(_written(item, depth + 1, max_depth))
		return items
	var fields: Dictionary = {}
	for key: Variant in value:
		fields[str(key)] = _written(value[key], depth + 1, max_depth)
	return fields


## {count, paths}: how many nodes were left out and the paths of the first MAX_OMITTED_PATHS still
## valid (one an earlier node's _mcp_state freed has none).
func _omitted(nodes: Array) -> Dictionary:
	var paths: Array = []
	for node: Variant in nodes.slice(0, MAX_OMITTED_PATHS):
		if is_instance_valid(node):
			paths.append(path_of.call(node))
	return {"count": nodes.size(), "paths": paths}
