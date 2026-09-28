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
##
## A reply saying the helper's call is pending (its Task has not finished) is polled once a frame,
## never waited on in place: the Task's continuation runs on a later frame of this same thread.
## Past the request's timeoutMs (its backstopMs when the server sends one), or when the server
## cancels the request, the call is forgotten, its Task left running in the game.

## The SceneTree meta the helper stores its callable under (Helper.MetaName in the helper).
const META_NAME := "godot_mcp_dotnet"
## Where the shim and the loader report why the helper did not load.
const ERROR_VARIABLE := "GODOT_MCP_DOTNET_ERROR"
const NO_EXTENSION := "dotnet needs 'extension', the path of godot_mcp_dotnet.gdextension."
const NO_REQUEST := "dotnet needs 'request', the helper request as a JSON string."
const NOT_LOADED := "Godot could not load the C# helper extension at %s (load status %d)."
const HELPER_FAILED := "The C# helper failed to load: %s"
const NO_CALLABLE := "The C# helper extension loaded but installed no 'godot_mcp_dotnet' callable."
const TIMED_OUT := "the call did not complete within %d ms; its Task is still running in the game"
## How long a pending call is polled when the params carry no timeoutMs.
const DEFAULT_TIMEOUT_MS := 10000
## How every helper reply for a call whose Task has not finished begins, its id following.
const PENDING_PREFIX := '{"ok":true,"pending":"'

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## Loads an extension by absolute path, func(path: String) -> GDExtensionManager.LoadStatus;
## tests replace it.
var load_extension: Callable = GDExtensionManager.load_extension
## The object holding the helper's callable as meta; the SceneTree when null. Tests replace it.
var meta_host: Object
## Waits for the next frame between polls of a pending call, func() -> void; tests replace it.
var wait_frame: Callable = func() -> void: await (Engine.get_main_loop() as SceneTree).process_frame
## The clock a pending call's deadline is kept by, in milliseconds, func() -> int; tests replace it.
var clock: Callable = Time.get_ticks_msec
## The extension path that loaded; empty until one has.
var _loaded_path: String = ""
## Why the load failed, answered on every later call; empty unless it failed.
var _failure: String = ""


## Runs a dotnet request, {extension, request, timeoutMs?, backstopMs?}; answers {result: {reply,
## loadedNow}} or {error}. A coroutine: a pending reply is polled across frames.
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
	return await _call(helper as Callable, params, loaded_now)


## Calls the helper with the request and, while it answers that the call is pending, polls it once
## a frame until it answers otherwise, or its deadline passes (backstopMs since the call when the
## server sends one, else timeoutMs), or the server cancels the request (params._cancelled), when
## it forgets the call and answers why, naming timeoutMs.
func _call(helper: Callable, params: Dictionary, loaded_now: bool) -> Dictionary:
	var timeout_ms: int = int(params.get("timeoutMs", DEFAULT_TIMEOUT_MS))
	var deadline: int = clock.call() + int(params.get("backstopMs", timeout_ms))
	var reply: String = str(helper.call(params["request"]))
	while reply.begins_with(PENDING_PREFIX):
		var id: String = str((JSON.parse_string(reply) as Dictionary)["pending"])
		if clock.call() >= deadline or params.get("_cancelled", false):
			helper.call(JSON.stringify({"op": "forget", "id": id}))
			return {"error": TIMED_OUT % timeout_ms}
		await wait_frame.call()
		reply = str(helper.call(JSON.stringify({"op": "poll", "id": id})))
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
