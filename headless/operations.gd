extends SceneTree
## godot-mcp's headless operations, which the server runs as
## godot --headless --path <project> --script <this file> -- <request file>
##
## The request file is JSON, {op, params, result}: result is the path of the JSON file this
## script writes, {ok, result?, error?, engineErrors}, where engineErrors is every error and
## warning the engine logged from this script's _init on, as {type, message, file, line}.
## _initialize frees the project's
## autoloads before they enter the tree (their _init has run; _enter_tree and _ready never do),
## runs the one operation and quits; _process returning true ends the main loop should a script
## error stop _initialize before its quit.
##
## validate loads each target with the cache ignored, never instantiating a scene or resource,
## and checks each C# script a scene or resource uses with can_instantiate; an error is grouped
## under the res:// file it names, else under the file being checked. get_scene_file_tree reads a
## scene's SceneState, expanding instanced scenes and an inherited scene's base in place, without
## instantiating anything.

const RES_PREFIX := "res://"
## How deep instanced scenes are expanded; Godot refuses a scene that instances itself.
const MAX_INSTANCE_DEPTH := 64


## Keeps every error and warning the engine logs, with its file and line, in order.
class ErrorLog:
	extends Logger

	var _mutex := Mutex.new()
	var _entries: Array = []

	func _log_error(
		_function: String,
		file: String,
		line: int,
		code: String,
		rationale: String,
		_editor_notify: bool,
		error_type: int,
		_script_backtraces: Array[ScriptBacktrace]
	) -> void:
		var entry: Dictionary = {
			"type": "warning" if error_type == ERROR_TYPE_WARNING else "error",
			"message": rationale if not rationale.is_empty() else code,
			"file": file,
			"line": line,
		}
		_mutex.lock()
		_entries.append(entry)
		_mutex.unlock()

	## How many entries have been logged so far.
	func count() -> int:
		_mutex.lock()
		var logged: int = _entries.size()
		_mutex.unlock()
		return logged

	## The entries logged from index start on, oldest first.
	func since(start: int) -> Array:
		_mutex.lock()
		var logged: Array = _entries.slice(start)
		_mutex.unlock()
		return logged


var _log := ErrorLog.new()


## The logger goes in first: this script's _init runs before Godot creates the project's
## autoloads (4.7.2 main.cpp L4368-4389, then L4497-4564), so their _init errors are kept.
func _init() -> void:
	OS.add_logger(_log)


func _initialize() -> void:
	for autoload in root.get_children():
		autoload.free()
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() != 1:
		printerr("godot-mcp headless: expected one argument, the request file; got %s" % [args])
		quit(2)
		return
	var request: Dictionary = parse_request(FileAccess.get_file_as_string(args[0]))
	if request.has("error"):
		printerr("godot-mcp headless: %s: %s" % [args[0], request["error"]])
		quit(2)
		return
	var reply: Dictionary = _dispatch(request["op"], request["params"])
	reply["engineErrors"] = _log.since(0)
	_write_reply(request["result"], reply)
	quit()


func _process(_delta: float) -> bool:
	return true


## The request file's {op, params, result}, or {error} saying what is wrong with it.
static func parse_request(text: String) -> Dictionary:
	var json := JSON.new()
	if json.parse(text) != OK:
		return {"error": "the request is not JSON: %s" % json.get_error_message()}
	var data: Variant = json.data
	if not data is Dictionary:
		return {"error": "the request is not a JSON object"}
	for key: String in ["op", "result"]:
		if not data.get(key) is String or (data[key] as String).is_empty():
			return {"error": "the request has no %s" % key}
	var params: Variant = data.get("params", {})
	if not params is Dictionary:
		return {"error": "the request's params is not an object"}
	return {"op": data["op"], "params": params, "result": data["result"]}


## Adds the errors (not warnings) among entries to groups, a Dictionary of path to its errors,
## each as {message, file, line}: under the res:// file an error names, else under checked_path.
## An error already listed under its path is not listed again.
static func group_errors(entries: Array, checked_path: String, groups: Dictionary) -> void:
	for entry: Dictionary in entries:
		if entry["type"] != "error":
			continue
		var file: String = entry["file"]
		var key: String = file if file.begins_with(RES_PREFIX) else checked_path
		var error: Dictionary = {"message": entry["message"], "file": file, "line": entry["line"]}
		var listed: Array = groups.get_or_add(key, [])
		if not listed.has(error):
			listed.append(error)


## groups as validate's results, [{path, errors}], by path.
static func results_of(groups: Dictionary) -> Array:
	var paths: Array = groups.keys()
	paths.sort()
	var results: Array = []
	for path: String in paths:
		results.append({"path": path, "errors": groups[path]})
	return results


## The res:// paths of the C# scripts among ResourceLoader.get_dependencies entries, which read
## "<path>[::<type>]" or, for a dependency saved with its UID, "<uid>::<type>::<path>".
static func csharp_dependencies(dependencies: PackedStringArray) -> PackedStringArray:
	var found: PackedStringArray = []
	for dependency in dependencies:
		for part in dependency.split("::"):
			if part.begins_with(RES_PREFIX) and part.ends_with(".cs") and not found.has(part):
				found.append(part)
	return found


## A node path relative to a scene's root, without "." or empty segments; the root is ".".
static func normalise_node_path(path: String) -> String:
	var names: PackedStringArray = []
	for part in path.split("/"):
		if not part.is_empty() and part != ".":
			names.append(part)
	return "." if names.is_empty() else "/".join(names)


## The path of relative (a scene's own node path) inside an instance placed at prefix.
static func join_node_path(prefix: String, relative: String) -> String:
	var inner: String = normalise_node_path(relative)
	if inner == ".":
		return prefix
	return inner if prefix == "." else prefix + "/" + inner


func _dispatch(op: String, params: Dictionary) -> Dictionary:
	match op:
		"validate":
			return {"ok": true, "result": _validate(params.get("targets", []))}
		"get_scene_file_tree":
			return _scene_file_tree(params)
	return {"ok": false, "error": "unknown operation '%s'" % op}


func _write_reply(path: String, reply: Dictionary) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		var reason: String = error_string(FileAccess.get_open_error())
		printerr("godot-mcp headless: cannot write the result %s: %s" % [path, reason])
		return
	# Keys stay in the order they were set, so a node reads path, name, type... as documented.
	file.store_string(JSON.stringify(reply, "", false))
	file.close()


## Errors logged before the first target (an autoload's _init, the project's settings) are listed
## under the res:// file they name, else in the result's engineErrors.
func _validate(targets: Array) -> Dictionary:
	var groups: Dictionary = {}
	group_errors(_log.since(0), "", groups)
	var unattributed: Array = groups.get("", [])
	groups.erase("")
	for target: String in targets:
		_check_file(target, groups)
	return {"checked": targets.size(), "results": results_of(groups), "engineErrors": unattributed}


func _check_file(path: String, groups: Dictionary) -> void:
	var start: int = _log.count()
	var resource: Resource = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE)
	var entries: Array = _log.since(start)
	if resource == null and entries.is_empty():
		entries = [{"type": "error", "message": "Godot could not load it", "file": path, "line": 0}]
	group_errors(entries, path, groups)
	if path.ends_with(".gd"):
		return
	for script_path in csharp_dependencies(ResourceLoader.get_dependencies(path)):
		start = _log.count()
		var script := load(script_path) as Script
		if script != null:
			script.can_instantiate()
		group_errors(_log.since(start), script_path, groups)


func _scene_file_tree(params: Dictionary) -> Dictionary:
	var scene_path: String = params.get("scene", "")
	var scene := ResourceLoader.load(scene_path) as PackedScene
	if scene == null:
		return {
			"ok": false, "error": "%s did not load as a scene; engineErrors say why" % scene_path
		}
	var tree: Dictionary = {"nodes": {}, "children": {}}
	add_state(scene.get_state(), ".", tree, 0)
	var root_path: String = normalise_node_path(str(params.get("root", ".")))
	if not tree["nodes"].has(root_path):
		var hint := "get_scene_file_tree without root lists its nodes"
		return {"ok": false, "error": "%s has no node %s; %s" % [scene_path, root_path, hint]}
	var listed: Array = []
	list_nodes(tree, root_path, int(params.get("maxDepth", -1)), listed)
	return {"ok": true, "result": {"nodes": listed}}


## Adds a scene state's nodes to tree under prefix, each instanced scene's nodes first so that
## the instancing node's own values, and the nodes the state adds inside it, land over them.
static func add_state(state: SceneState, prefix: String, tree: Dictionary, depth: int) -> void:
	for index in state.get_node_count():
		var path: String = join_node_path(prefix, String(state.get_node_path(index)))
		var instance: PackedScene = state.get_node_instance(index)
		if instance != null and depth < MAX_INSTANCE_DEPTH:
			add_state(instance.get_state(), path, tree, depth + 1)
		merge_node(tree, path, _node_facts(state, index))


## Adds the node at path to tree, or lays facts ({name, type, groups, script?, instance?}) over
## the node already there: an empty type keeps the one it has, and groups add up.
static func merge_node(tree: Dictionary, path: String, facts: Dictionary) -> void:
	var nodes: Dictionary = tree["nodes"]
	if not nodes.has(path):
		nodes[path] = {"path": path, "name": "", "type": "", "groups": []}
		tree["children"][path] = []
		var parent: String = _parent_path(path)
		if tree["children"].has(parent):
			tree["children"][parent].append(path)
	var node: Dictionary = nodes[path]
	node["name"] = facts["name"]
	if not (facts["type"] as String).is_empty():
		node["type"] = facts["type"]
	for key: String in ["script", "instance"]:
		if facts.has(key):
			node[key] = facts[key]
	for group: String in facts["groups"]:
		if not node["groups"].has(group):
			node["groups"].append(group)


## Appends the node at path and its descendants to listed, depth first, down to max_depth levels
## below it (all when max_depth is negative), each as {path, name, type, script?, instance?,
## groups?, childCount}.
static func list_nodes(tree: Dictionary, path: String, max_depth: int, listed: Array) -> void:
	var node: Dictionary = tree["nodes"][path]
	var children: Array = tree["children"][path]
	var shown: Dictionary = {"path": path, "name": node["name"], "type": node["type"]}
	for key: String in ["script", "instance"]:
		if not str(node.get(key, "")).is_empty():
			shown[key] = node[key]
	if not node["groups"].is_empty():
		shown["groups"] = node["groups"]
	shown["childCount"] = children.size()
	listed.append(shown)
	if max_depth == 0:
		return
	for child: String in children:
		list_nodes(tree, child, max_depth - 1, listed)


static func _node_facts(state: SceneState, index: int) -> Dictionary:
	var facts: Dictionary = {
		"name": String(state.get_node_name(index)),
		"type": String(state.get_node_type(index)),
		"groups":
		Array(state.get_node_groups(index)).map(func(group: StringName): return String(group)),
	}
	var instance: PackedScene = state.get_node_instance(index)
	if instance != null:
		facts["instance"] = instance.resource_path
	elif state.is_node_instance_placeholder(index):
		facts["instance"] = state.get_node_instance_placeholder(index)
	for property in state.get_node_property_count(index):
		if state.get_node_property_name(index, property) == &"script":
			var script: Variant = state.get_node_property_value(index, property)
			facts["script"] = script.resource_path if script is Script else ""
	return facts


static func _parent_path(path: String) -> String:
	if path == ".":
		return ""
	var slash: int = path.rfind("/")
	return "." if slash < 0 else path.substr(0, slash)
