extends "res://gd_test.gd"
## The C# helper module (bridge/godot_mcp_dotnet.gd) on instances never added to the tree, with a
## fake load_extension and a stand-in meta host, so no built extension is needed: the argument
## checks, a failed load named and remembered, the shim's error variable, an absent callable, one
## load per process, and the helper's reply passed through as it came.

const EXTENSION := "C:/cache/dotnet/0123abcd/godot_mcp_dotnet.gdextension"
const PING := '{"op":"ping"}'
const META_NAME := "godot_mcp_dotnet"
const ERROR_VARIABLE := "GODOT_MCP_DOTNET_ERROR"

var _dotnet_script: GDScript = load_bridge_script("godot_mcp_dotnet.gd")


func test_a_missing_extension_is_refused() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_OK, loads, _host_answering("{}"))
	var refusal: Dictionary = {
		"error": "dotnet needs 'extension', the path of godot_mcp_dotnet.gdextension."
	}
	assert_eq(dotnet.handle({"request": PING}), refusal, "no extension")
	assert_eq(dotnet.handle({"extension": "", "request": PING}), refusal, "an empty extension")
	assert_eq(dotnet.handle({"extension": 3, "request": PING}), refusal, "a number")
	assert_eq(loads, [0], "nothing is loaded")
	dotnet.free()


func test_a_missing_request_is_refused() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_OK, loads, _host_answering("{}"))
	var refusal: Dictionary = {
		"error": "dotnet needs 'request', the helper request as a JSON string."
	}
	assert_eq(dotnet.handle({"extension": EXTENSION}), refusal, "no request")
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": {"op": "ping"}}),
		refusal,
		"a request sent as an object"
	)
	assert_eq(loads, [0], "nothing is loaded")
	dotnet.free()


func test_a_failed_load_names_the_path_and_status() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_FAILED, loads, _host_answering("{}"))
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": PING}),
		{
			"error":
			"Godot could not load the C# helper extension at %s (load status 1)." % EXTENSION
		},
		"the path and LOAD_STATUS_FAILED's number"
	)
	dotnet.free()


func test_a_failed_load_is_remembered_without_loading_again() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(
		GDExtensionManager.LOAD_STATUS_NEEDS_RESTART, loads, _host_answering("{}")
	)
	var first: Dictionary = dotnet.handle({"extension": EXTENSION, "request": PING})
	var second: Dictionary = dotnet.handle({"extension": EXTENSION, "request": PING})
	assert_eq(
		first,
		{
			"error":
			"Godot could not load the C# helper extension at %s (load status 4)." % EXTENSION
		},
		"the first call fails"
	)
	assert_eq(second, first, "the second call answers the same error")
	assert_eq(loads, [1], "the extension is loaded once")
	dotnet.free()


func test_the_error_variable_is_reported() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_OK, loads, _host_answering("{}"))
	OS.set_environment(ERROR_VARIABLE, "the loader threw FileNotFoundException: no helper")
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": PING}),
		{
			"error":
			"The C# helper failed to load: the loader threw FileNotFoundException: no helper"
		},
		"the variable's text"
	)
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": PING}).get("error"),
		"The C# helper failed to load: the loader threw FileNotFoundException: no helper",
		"remembered on the next call"
	)
	assert_eq(loads, [1], "the extension is loaded once")
	OS.unset_environment(ERROR_VARIABLE)
	dotnet.free()


func test_an_absent_callable_is_reported() -> void:
	var loads: Array = [0]
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_OK, loads, RefCounted.new())
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": PING}),
		{"error": "The C# helper extension loaded but installed no 'godot_mcp_dotnet' callable."},
		"no meta on the host"
	)
	dotnet.free()


func test_the_first_call_loads_and_later_calls_do_not() -> void:
	var loads: Array = [0]
	var reply := '{"ok":true,"result":{}}'
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_OK, loads, _host_answering(reply))
	assert_eq(
		dotnet.handle({"extension": EXTENSION, "request": PING}),
		{"result": {"reply": reply, "loadedNow": true}},
		"the first call loads"
	)
	assert_eq(
		dotnet.handle({"extension": "D:/elsewhere/godot_mcp_dotnet.gdextension", "request": PING}),
		{"result": {"reply": reply, "loadedNow": false}},
		"a later call, naming another path, is answered by the loaded helper"
	)
	assert_eq(loads, [1], "the extension is loaded once")
	dotnet.free()


func test_the_reply_string_is_passed_through_untouched() -> void:
	var loads: Array = [0]
	var reply := '{"ok":true,"result":{"id":"18446744073709551615","big":18446744073709551615}}'
	var requests: Array = []
	var host := RefCounted.new()
	host.set_meta(
		META_NAME,
		func(request: String) -> String:
			requests.append(request)
			return reply
	)
	var dotnet: Node = _module(GDExtensionManager.LOAD_STATUS_ALREADY_LOADED, loads, host)
	var request := '{"op":"ping","id":"18446744073709551615"}'
	var outcome: Dictionary = dotnet.handle({"extension": EXTENSION, "request": request})
	assert_eq(outcome, {"result": {"reply": reply, "loadedNow": true}}, "ALREADY_LOADED loads")
	assert_true(
		outcome.get("result", {}).get("reply") == reply, "the reply is byte for byte the helper's"
	)
	assert_eq(requests, [request], "the request reaches the helper untouched")
	dotnet.free()


## A module whose load_extension answers status and counts its calls in loads[0], reading the
## helper's callable from host.
func _module(status: int, loads: Array, host: Object) -> Node:
	var dotnet: Node = _dotnet_script.new()
	dotnet.load_extension = func(_path: String) -> int:
		loads[0] += 1
		return status
	dotnet.meta_host = host
	return dotnet


## A meta host whose helper callable answers every request with reply.
func _host_answering(reply: String) -> RefCounted:
	var host := RefCounted.new()
	host.set_meta(META_NAME, func(_request: String) -> String: return reply)
	return host
