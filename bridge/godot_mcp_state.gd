extends Node
## The godot-mcp bridge's state reader, a child of the bridge: the state command, get_game_state's
## read. It takes the nodes in the mcp_state group in tree order (SceneTree.get_nodes_in_group),
## leaves out the bridge's own nodes and, when params.node names a node, every node not at or
## under it, reads the first params.maxNodes of the rest and lists the others. A GDScript node is
## read by calling its _mcp_state(); the value is written to params.maxDepth levels of Dictionaries
## and Arrays, a deeper one as "<depth limit: Dictionary>" or "<depth limit: Array>" (so a cyclic
## value ends there too), and every other value through the JSON module's to_json.
##
## A C# node, when params.extension names the C# helper, is left to the helper: its entry is
## {path, class, id}, and one call of the helper's state op through the Dotnet child reads every
## such node by instance id; its reply string goes into the result as csharp, untouched, for the
## server to fill those entries from (GDScript's JSON parser would read every number as a float).
## A C# node the helper finds no _McpState on is read through a Godot-visible _mcp_state here. One
## handler with no await reads every node, so every node is read in the same frame.

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
## A C# node with no Godot-visible _mcp_state in a request with no helper extension; %s its type.
const CSHARP_NO_BUILD := (
	"%s is a C# script; its _McpState is read by the C# helper, "
	+ "which this project has no build of"
)
## A C# node with no Godot-visible _mcp_state in a request whose csharpError says why the server
## sent no helper extension; %s its type, then that error.
const CSHARP_CANNOT_RUN := (
	"%s is a C# script; its _McpState is read by the C# helper, " + "which cannot run: %s"
)
## A C# node the helper read through _McpState that has a Godot-visible _mcp_state too.
const BOTH_NAMES := "reads _McpState; its _mcp_state is not read"
const HELPER_REFUSED := "the C# helper refused the state read: %s"

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## The marked nodes in tree order, func() -> Array; the SceneTree's mcp_state group. Tests replace
## it, since their nodes are in no tree.
var marked: Callable = func() -> Array:
	return (Engine.get_main_loop() as SceneTree).get_nodes_in_group(GROUP)
## A node's path, func(node: Node) -> String; tests replace it, since their nodes are in no tree.
var path_of: Callable = func(node: Node) -> String: return str(node.get_path())
## Whether a node's script is a C# script, func(node: Node) -> bool; tests replace it, since a C#
## script needs a built assembly.
var is_csharp: Callable = func(node: Node) -> bool: return _is_csharp(node)
## How many more values the node being written may take (MAX_VALUES at each node's start).
var _values_left: int = 0


## Reads the marked nodes, params {node, maxNodes, maxDepth, extension?, csharpError?} (csharpError:
## why a C# project sent no extension, each such C# node's error); answers {result: {frame,
## nodes, total, omitted?, csharp?}} or {error} when params.node names no node or a node of the
## bridge's. nodes holds {path, class, state}, {path, class, error} or, for a C# node the helper
## read, {path, class, id} (with warning when it has _mcp_state too) for each node read; total
## counts every marked node kept, and omitted {count, paths} the ones past maxNodes, the first 20
## paths; csharp is the helper's reply string. A node an earlier node's _mcp_state freed is skipped.
func handle(params: Dictionary) -> Dictionary:
	var found: Variant = _members(str(params.get("node", "")))
	if found is String:
		return {"error": found}
	var members: Array = found
	var max_nodes: int = int(params.get("maxNodes", DEFAULT_MAX_NODES))
	var reading: Dictionary = {
		"maxDepth": int(params.get("maxDepth", DEFAULT_MAX_DEPTH)),
		"extension": str(params.get("extension", "")),
		"csharpError": str(params.get("csharpError", "")),
		"csharp": {},
	}
	var nodes: Array = []
	for node: Variant in members.slice(0, max_nodes):
		if is_instance_valid(node):
			nodes.append(_entry(node, reading, nodes.size()))
	var result: Dictionary = {
		"frame": Engine.get_process_frames(), "nodes": nodes, "total": members.size()
	}
	if not (reading["csharp"] as Dictionary).is_empty():
		_read_csharp(reading, nodes, result)
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


## A node's entry, read now; or, for a C# node when reading carries the helper's extension, its
## {path, class, id}, the id kept in reading.csharp with index, the entry's place in nodes. A C#
## node read now without the helper takes reading.csharpError, when given, into its error.
func _entry(node: Node, reading: Dictionary, index: int) -> Dictionary:
	if not is_csharp.call(node):
		return _read(node, reading["maxDepth"], "")
	if (reading["extension"] as String).is_empty():
		return _read(node, reading["maxDepth"], _cannot_run(node, reading["csharpError"]))
	var id: String = str(node.get_instance_id())
	reading["csharp"][id] = index
	return {"path": path_of.call(node), "class": node.get_class(), "id": id}


## One node's entry: {path, class, state}, or {path, class, error} when it has no state method or
## the call logged an error or returned a coroutine, which is never awaited. csharp_error is a C#
## node's error when it has no _mcp_state; empty, the no-build error naming its type.
func _read(node: Node, max_depth: int, csharp_error: String) -> Dictionary:
	var entry: Dictionary = {"path": path_of.call(node), "class": node.get_class()}
	var refusal: String = _refusal(node, csharp_error)
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


## Why the node is not read, or "" when it has _mcp_state: a C# node without one says csharp_error
## (or, empty, that the project has no helper build), and a GDScript node with the C# name defines
## the wrong one.
func _refusal(node: Node, csharp_error: String) -> String:
	if node.has_method(METHOD):
		return ""
	if is_csharp.call(node):
		return csharp_error if not csharp_error.is_empty() else CSHARP_NO_BUILD % _type_name(node)
	if node.has_method(CSHARP_METHOD):
		return CSHARP_NAME
	return NO_METHOD


## A C# node's error when the server sent why the helper cannot run (csharp_error), naming its
## type; "" when it sent none, so the no-build error stands.
static func _cannot_run(node: Node, csharp_error: String) -> String:
	return "" if csharp_error.is_empty() else CSHARP_CANNOT_RUN % [_type_name(node), csharp_error]


## Reads the C# nodes collected in reading.csharp with one call of the helper's state op, in this
## frame, and puts its reply string into result.csharp. A node the helper finds no _McpState on is
## read here through _mcp_state when Godot sees one, and one it read that has _mcp_state too is
## warned. When the helper cannot load or refuses, each node is read as with no extension, the
## helper's error in place of the no-build one, and result has no csharp.
func _read_csharp(reading: Dictionary, nodes: Array, result: Dictionary) -> void:
	var at: Dictionary = reading["csharp"]
	var request: String = JSON.stringify(
		{"op": "state", "ids": at.keys(), "maxDepth": reading["maxDepth"]}
	)
	var called: Dictionary = bridge._dotnet.call_now(reading["extension"], request)
	var reply: String = str((called.get("result", {}) as Dictionary).get("reply", ""))
	var parsed: Variant = JSON.parse_string(reply) if not reply.is_empty() else null
	var failure: String = _failure_of(called, parsed)
	if not failure.is_empty():
		for id: String in at:
			_refill(nodes, at[id], id, reading["maxDepth"], failure)
		return
	var answers: Dictionary = _answers(parsed)
	for id: String in at:
		_settle(nodes, at[id], id, answers.get(id, {}), reading["maxDepth"])
	result["csharp"] = reply


## Why the helper's state read left nothing to merge: the Dotnet child's error (a load failure or
## an absent callable), or the helper's refusal; "" when it answered.
static func _failure_of(called: Dictionary, parsed: Variant) -> String:
	if called.has("error"):
		return str(called["error"])
	if not parsed is Dictionary:
		return HELPER_REFUSED % "its reply is not a JSON object"
	if not (parsed as Dictionary).get("ok", false):
		return HELPER_REFUSED % str((parsed as Dictionary).get("error", "no message"))
	return ""


## The helper's entries by id: {id: entry}.
static func _answers(parsed: Dictionary) -> Dictionary:
	var by_id: Dictionary = {}
	var result: Variant = parsed.get("result")
	var answers: Variant = (result as Dictionary).get("nodes", []) if result is Dictionary else []
	if not answers is Array:
		return by_id
	for answer: Variant in answers:
		if answer is Dictionary:
			by_id[str((answer as Dictionary).get("id", ""))] = answer
	return by_id


## A C# node's entry once the helper answered with answer (empty when it has none for id), at index
## in nodes: read here through _mcp_state when the helper found no _McpState and Godot sees
## _mcp_state, warned when the helper read its state and it has _mcp_state too; otherwise left for
## the server to fill.
func _settle(nodes: Array, index: int, id: String, answer: Dictionary, max_depth: int) -> void:
	var node: Object = instance_from_id(int(id))
	if not is_instance_valid(node) or not node.has_method(METHOD):
		return
	if answer.get("missing", false):
		nodes[index] = _read(node as Node, max_depth, "")
	elif answer.has("state"):
		nodes[index]["warning"] = BOTH_NAMES


## A C# node's entry, at index in nodes, when the helper could not answer: read through _mcp_state
## when Godot sees one, else CSHARP_CANNOT_RUN naming its type and failure as its error (failure
## alone for a node freed since, which has no type to name).
func _refill(nodes: Array, index: int, id: String, max_depth: int, failure: String) -> void:
	var node: Object = instance_from_id(int(id))
	if is_instance_valid(node):
		nodes[index] = _read(node as Node, max_depth, _cannot_run(node as Node, failure))
		return
	var entry: Dictionary = nodes[index]
	entry.erase("id")
	entry["error"] = failure


## Whether the node's script is a C# script; the class is named as a String, so a build without
## C# support, which has no CSharpScript, reads false.
static func _is_csharp(node: Node) -> bool:
	var node_script := node.get_script() as Script
	return node_script != null and node_script.is_class("CSharpScript")


## The type a node's script declares as its file names it (a C# script's file is named for its
## class), or the node's class when the script has no file.
static func _type_name(node: Node) -> String:
	var node_script := node.get_script() as Script
	var file: String = node_script.resource_path.get_file().get_basename() if node_script else ""
	return file if not file.is_empty() else node.get_class()


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
