extends "res://gd_test.gd"
## The inspector's call_now for a game tool (bridge/godot_mcp_inspect.gd): params {tool,
## extension, request} go to the C# helper through the Dotnet child's call_now, stubbed here as
## test_state.gd stubs it, and its reply comes back as {tool, reply} untouched, as the helper's
## own text, as {value: null, tool, pending: true} after the pending call is forgotten, or as the
## first error the feed logged across the call, read from a stub logger.

const EXTENSION := "C:/cache/dotnet/0123abcd/godot_mcp_dotnet.gdextension"
const REQUEST := (
	'{"op":"tool_call","name":"SetMood",' + '"args":{"mood":"Angry","times":3},"maxDepth":8}'
)
const BRIDGE_SOURCE := "extends Node\n\nvar _dotnet: Node\nvar _logger: Object\n"
## A logger whose sequence is 7 and whose first error since 7 is its error; any other mark is a
## mistake the answer names.
const LOGGER_SOURCE := (
	'extends RefCounted\n\nvar error: String = ""\n\n\nfunc sequence() -> int:\n\treturn 7\n\n\n'
	+ "func first_error_since(mark: int) -> String:\n"
	+ '\treturn error if mark == 7 else "marked at %d" % mark\n'
)
const BAD_ENUM := "parameter 'mood': expected one of Calm, Angry, Sleepy, got \"Glum\""
const BIG := '{"ok":true,"result":{"value":9007199254740993,"type":"System.Int64"}}'

var _inspect_script: GDScript = load_bridge_script("godot_mcp_inspect.gd")
var _dotnet_script: GDScript = load_bridge_script("godot_mcp_dotnet.gd")


func test_a_value_answers_the_tool_and_the_helpers_reply_untouched() -> void:
	var rig: Dictionary = _rig(GDExtensionManager.LOAD_STATUS_OK, [BIG])
	var outcome: Variant = rig["inspect"].call_now(_params("Huge"))
	assert_eq(outcome, {"tool": "Huge", "reply": BIG}, "the reply's text, digits and all")
	assert_eq(rig["requests"], [REQUEST], "the request goes to the helper as the server built it")
	_free(rig)


func test_a_helper_error_is_its_own_text() -> void:
	var refused: String = JSON.stringify({"ok": false, "error": BAD_ENUM})
	var rig: Dictionary = _rig(GDExtensionManager.LOAD_STATUS_OK, [refused])
	var outcome: Variant = rig["inspect"].call_now(_params("SetMood"))
	assert_eq(outcome, BAD_ENUM, "the helper's refusal, word for word")
	_free(rig)


func test_a_load_error_is_its_own_text() -> void:
	var rig: Dictionary = _rig(GDExtensionManager.LOAD_STATUS_FAILED, [BIG])
	var outcome: Variant = rig["inspect"].call_now(_params("SetMood"))
	assert_eq(
		outcome,
		(
			"Godot could not load the C# helper extension at %s (load status %d)."
			% [EXTENSION, GDExtensionManager.LOAD_STATUS_FAILED]
		),
		"the Dotnet child's load failure"
	)
	assert_eq(rig["requests"], [], "nothing reaches a helper that did not load")
	_free(rig)


func test_a_pending_reply_forgets_the_call_and_answers_pending() -> void:
	var replies: Array = ['{"ok":true,"pending":"c1"}', '{"ok":true,"result":{"forgotten":"c1"}}']
	var rig: Dictionary = _rig(GDExtensionManager.LOAD_STATUS_OK, replies)
	var outcome: Variant = rig["inspect"].call_now(_params("FetchLater"))
	assert_eq(outcome, {"value": null, "tool": "FetchLater", "pending": true}, "pending, no value")
	assert_eq(
		rig["requests"], [REQUEST, '{"id":"c1","op":"forget"}'], "the pending call is forgotten"
	)
	_free(rig)


func test_an_error_logged_across_an_answered_call_fails_it() -> void:
	var rig: Dictionary = _rig(GDExtensionManager.LOAD_STATUS_OK, [BIG])
	rig["logger"].error = "CsTools complained"
	var outcome: Variant = rig["inspect"].call_now(_params("Complain"))
	assert_eq(outcome, "CsTools complained", "the feed's first error since the call's mark")
	_free(rig)


## {inspect, dotnet, bridge, logger, requests}: an inspector whose stand-in bridge has a stub
## logger and a Dotnet child whose load answers status and whose helper answers replies in turn,
## recording each request.
func _rig(status: int, replies: Array) -> Dictionary:
	var requests: Array = []
	var dotnet: Node = _dotnet_script.new()
	dotnet.load_extension = func(_path: String) -> int: return status
	var host := RefCounted.new()
	host.set_meta(
		"godot_mcp_dotnet",
		func(request: String) -> String:
			requests.append(request)
			return str(replies.pop_front())
	)
	dotnet.meta_host = host
	var bridge: Node = _compile(BRIDGE_SOURCE)
	bridge._dotnet = dotnet
	bridge._logger = _compile(LOGGER_SOURCE)
	var inspect: Node = _inspect_script.new()
	inspect._bridge = bridge
	return {
		"inspect": inspect,
		"dotnet": dotnet,
		"bridge": bridge,
		"logger": bridge._logger,
		"requests": requests
	}


func _free(rig: Dictionary) -> void:
	rig["inspect"].free()
	rig["dotnet"].free()
	rig["bridge"].free()


## A game tool call's params as the server sends them.
static func _params(tool_name: String) -> Dictionary:
	return {"tool": tool_name, "extension": EXTENSION, "request": REQUEST}


## An instance of a script compiled from source.
static func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
