extends Node
## The godot-mcp bridge's C# helper module, a child of the bridge: the dotnet command. It loads the
## helper extension (godot_mcp_dotnet.gdextension, at the path the server sends) once per process,
## then hands each helper request, a JSON string, to the callable the helper stores as the
## SceneTree meta godot_mcp_dotnet, and answers the callable's reply string untouched: GDScript's
## JSON parser would read every number as a float.
##
## A failed load is remembered for the life of the process and its error answered on every later
## call without loading again, since a second load returns LOAD_STATUS_ALREADY_LOADED with the
## callable still absent. The first path that loads is the one used for the life of the process.

## The SceneTree meta the helper stores its callable under (Helper.MetaName in the helper).
const META_NAME := "godot_mcp_dotnet"
## Where the shim and the loader report why the helper did not load.
const ERROR_VARIABLE := "GODOT_MCP_DOTNET_ERROR"
const NO_EXTENSION := "dotnet needs 'extension', the path of godot_mcp_dotnet.gdextension."
const NO_REQUEST := "dotnet needs 'request', the helper request as a JSON string."
const NOT_LOADED := "Godot could not load the C# helper extension at %s (load status %d)."
const HELPER_FAILED := "The C# helper failed to load: %s"
const NO_CALLABLE := "The C# helper extension loaded but installed no 'godot_mcp_dotnet' callable."

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## Loads an extension by absolute path, func(path: String) -> GDExtensionManager.LoadStatus;
## tests replace it.
var load_extension: Callable = GDExtensionManager.load_extension
## The object holding the helper's callable as meta; the SceneTree when null. Tests replace it.
var meta_host: Object
## The extension path that loaded; empty until one has.
var _loaded_path: String = ""
## Why the load failed, answered on every later call; empty unless it failed.
var _failure: String = ""


## Runs a dotnet request, {extension, request}; answers {result: {reply, loadedNow}} or {error}.
func handle(params: Dictionary) -> Dictionary:
	var refusal: String = _refusal(params)
	if refusal.is_empty():
		refusal = _failure
	if not refusal.is_empty():
		return {"error": refusal}
	var loaded_now: bool = _loaded_path.is_empty()
	if loaded_now:
		_failure = _load(params["extension"])
		if not _failure.is_empty():
			return {"error": _failure}
		_loaded_path = params["extension"]
	var helper: Variant = _helper()
	if not helper is Callable:
		return {"error": NO_CALLABLE}
	var reply: String = str((helper as Callable).call(params["request"]))
	return {"result": {"reply": reply, "loadedNow": loaded_now}}


## Why params cannot be run: no non-empty string extension, or no string request; empty when
## they can.
func _refusal(params: Dictionary) -> String:
	var extension: Variant = params.get("extension")
	if not extension is String or (extension as String).is_empty():
		return NO_EXTENSION
	if not params.get("request") is String:
		return NO_REQUEST
	return ""


## Loads the extension at path and checks the helper installed its callable; answers why it did
## not, or empty when it did.
func _load(path: String) -> String:
	var status: int = load_extension.call(path)
	if (
		status != GDExtensionManager.LOAD_STATUS_OK
		and status != GDExtensionManager.LOAD_STATUS_ALREADY_LOADED
	):
		return NOT_LOADED % [path, status]
	var reported: String = OS.get_environment(ERROR_VARIABLE)
	if not reported.is_empty():
		return HELPER_FAILED % reported
	if not _helper() is Callable:
		return NO_CALLABLE
	return ""


## The helper's callable from the meta host, or null when it has none.
func _helper() -> Variant:
	var host: Object = meta_host if meta_host != null else Engine.get_main_loop()
	return host.get_meta(META_NAME) if host.has_meta(META_NAME) else null
