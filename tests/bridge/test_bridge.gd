extends "res://gd_test.gd"
## The bridge's pure helpers (bridge/godot_mcp_bridge.gd), on an instance never added to the
## tree. Constructing it runs _init, whose endpoint lookup finds no GODOT_MCP_* variables and no
## attach file here, so it registers no logger; _ready, which frees a bridge that is off, never
## runs outside the tree.
# gdlint: disable=private-method-call

## The root meta a counting script (_counting_script) counts its frames in.
const COUNTER := "godot_mcp_test_counter"
## A stopped run_script's answer when it changed neither the time scale nor the pause.
const SCRIPT_STOPPED := (
	"stopped: its coroutine will not resume. A coroutine it awaited on another "
	+ "object, such as a node's own method, keeps running; restart_project stops everything."
)

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")
var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")
var _frame_script: GDScript = load_bridge_script("godot_mcp_frame.gd")
var _dormant_script: GDScript = load_bridge_script("godot_mcp_dormant.gd")
var _window_script: GDScript = load_bridge_script("godot_mcp_window.gd")


func test_cancel_of_an_answered_request_replies_false() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._time = _time_script.new()
	var waiting: Dictionary = {"kind": "exists", "node": "Main", "timeoutMs": 10000}
	var answered: Dictionary = {"kind": "exists", "node": "Main", "timeoutMs": 10000}
	bridge._track(17, "wait_for", answered)
	bridge._track(18, "wait_for", waiting)
	bridge._track(19, "ping", {})
	bridge._running_requests.erase(17)
	assert_true(not bridge._cancel(17), "an answered request")
	assert_true(not answered.has("_cancelled"), "an answered request's params are left alone")
	assert_true(not bridge._cancel(19), "a request that is not cancellable")
	assert_true(not bridge._cancel(42), "an unknown request")
	assert_true(bridge._cancel(18), "a running wait")
	assert_eq(waiting.get("_cancelled"), true, "its params are marked, ending its poll")
	bridge._time.free()
	bridge.free()


func test_a_lost_connection_cancels_and_forgets_the_running_requests() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._time = _time_script.new()
	var waiting: Dictionary = {"kind": "exists", "node": "Main", "timeoutMs": 10000}
	bridge._track(18, "wait_for", waiting)
	bridge._end_connection()
	assert_true(bridge._connection_lost, "the connection is marked lost")
	assert_eq(waiting.get("_cancelled"), true, "the running wait is cancelled, ending its poll")
	assert_true(bridge._running_requests.is_empty(), "no running request is left to cancel")
	bridge._time.free()
	bridge.free()


func test_save_preview_leaves_a_narrow_image_alone() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._frame = _frame_script.new()
	var path: String = OS.get_temp_dir().path_join("godot_mcp_narrow_preview.png")
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)
	var result: Dictionary = {}
	var image: Image = Image.create_empty(8, 4, false, Image.FORMAT_RGBA8)
	assert_eq(
		bridge._frame.save_preview(image, path, 16, result), OK, "a narrow image saves nothing"
	)
	assert_true(not FileAccess.file_exists(path), "no file is written")
	assert_true(result.is_empty(), "no preview is reported")
	bridge._frame.free()
	bridge.free()


func test_save_preview_scales_a_wide_image_down() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._frame = _frame_script.new()
	var path: String = OS.get_temp_dir().path_join("godot_mcp_wide_preview.png")
	var result: Dictionary = {}
	var image: Image = Image.create_empty(32, 16, false, Image.FORMAT_RGBA8)
	assert_eq(
		bridge._frame.save_preview(image, path, 16, result), OK, "a wide image is scaled down"
	)
	assert_eq(result.get("previewPath"), path, "the preview's path")
	assert_eq(result.get("previewWidth"), 16, "the preview's width")
	assert_eq(result.get("previewHeight"), 8, "the preview's height, kept in proportion")
	assert_true(FileAccess.file_exists(path), "the file is written")
	DirAccess.remove_absolute(path)
	bridge._frame.free()
	bridge.free()


func test_a_suspended_execute_returns_a_function_state() -> void:
	var instance: Object = _compile(_counting_script("pass"))
	var value: Variant = instance.call("execute", _tree())
	assert_true(
		value is Object and (value as Object).is_class("GDScriptFunctionState"),
		"execute called without await answers its GDScriptFunctionState at its first await"
	)
	_tree().root.remove_meta(COUNTER)
	_frames(1)


func test_a_cancelled_run_script_never_resumes() -> void:
	var bridge: Node = _recording_bridge()
	var params: Dictionary = {"source": _counting_script("pass")}
	bridge._track(5, "run_script", params)
	bridge._handle_run_script(5, params)
	_frames(2)
	assert_eq(_tree().root.get_meta(COUNTER, -1), 2, "the script counts each frame")
	assert_true(bridge.sent.is_empty(), "a suspended script is not answered yet")
	assert_true(bridge._cancel(5), "a suspended script is cancelled")
	_frames(3)
	assert_eq(_tree().root.get_meta(COUNTER, -1), 2, "its coroutine never resumes")
	assert_eq(bridge.sent.size(), 1, "the cancel answers the request once")
	var reply: Dictionary = bridge.sent[0] if not bridge.sent.is_empty() else {}
	assert_eq(reply.get("id"), 5, "the answer is the script request's")
	assert_eq(reply.get("ok"), false, "a stopped script fails")
	assert_eq(reply.get("error"), SCRIPT_STOPPED, "it says the script was stopped")
	assert_true(bridge._running_scripts.is_empty(), "no script is left running")
	assert_true(bridge._running_requests.is_empty(), "no request is left running")
	_tree().root.remove_meta(COUNTER)
	_free_bridge(bridge)


func test_a_cancelled_run_script_restores_time_scale_and_paused() -> void:
	var bridge: Node = _recording_bridge()
	var params: Dictionary = {
		"source": _counting_script("Engine.time_scale = 2.0\n\ttree.paused = true")
	}
	bridge._track(5, "run_script", params)
	bridge._handle_run_script(5, params)
	_frames(1)
	assert_approx(Engine.time_scale, 2.0, "the script sped time up")
	assert_true(bridge._cancel(5), "a suspended script is cancelled")
	assert_approx(Engine.time_scale, 1.0, "the time scale is restored")
	assert_true(not _tree().paused, "the pause is restored")
	var error: String = str(bridge.sent[0].get("error")) if not bridge.sent.is_empty() else ""
	assert_true(
		error.begins_with(
			"stopped: its coroutine will not resume; restored Engine.time_scale to 1"
		),
		"the answer names the restored time scale first: %s" % error
	)
	assert_true(
		error.contains(" and SceneTree.paused to false. A coroutine it awaited on another object"),
		"then the restored pause: %s" % error
	)
	Engine.time_scale = 1.0
	_tree().paused = false
	_tree().root.remove_meta(COUNTER)
	_free_bridge(bridge)


func test_a_finished_run_script_keeps_its_time_scale() -> void:
	var bridge: Node = _recording_bridge()
	var source: String = (
		"\n"
		. join(
			[
				"extends RefCounted",
				"",
				"",
				"func execute(tree: SceneTree) -> Variant:",
				"\tEngine.time_scale = 2.0",
				"\tawait tree.process_frame",
				"\treturn 7",
			]
		)
	)
	var params: Dictionary = {"source": source}
	bridge._track(6, "run_script", params)
	bridge._handle_run_script(6, params)
	assert_true(bridge.sent.is_empty(), "a suspended script is not answered yet")
	_frames(1)
	assert_eq(bridge.sent.size(), 1, "the script is answered once it returns")
	var reply: Dictionary = bridge.sent[0] if not bridge.sent.is_empty() else {}
	assert_eq(reply.get("ok"), true, "a finished script succeeds")
	assert_eq(reply.get("result", {}).get("value"), 7, "with the value execute returned")
	assert_approx(Engine.time_scale, 2.0, "a finished script keeps its time scale")
	assert_true(bridge._running_scripts.is_empty(), "no script is left running")
	Engine.time_scale = 1.0
	_free_bridge(bridge)


func test_a_lost_connection_drops_running_scripts() -> void:
	var bridge: Node = _recording_bridge()
	var params: Dictionary = {"source": _counting_script("Engine.time_scale = 2.0")}
	bridge._track(7, "run_script", params)
	bridge._handle_run_script(7, params)
	_frames(1)
	bridge._end_connection()
	_frames(3)
	assert_eq(_tree().root.get_meta(COUNTER, -1), 1, "the dropped script never resumes")
	assert_true(bridge.sent.is_empty(), "nothing is sent on a lost connection")
	assert_approx(Engine.time_scale, 2.0, "nothing is restored with no server to tell")
	assert_true(bridge._running_scripts.is_empty(), "no script is left running")
	assert_true(bridge._running_requests.is_empty(), "no request is left running")
	Engine.time_scale = 1.0
	_tree().root.remove_meta(COUNTER)
	_free_bridge(bridge)


func test_a_cancelled_call_method_restores_and_never_replies() -> void:
	var bridge: Node = _recording_bridge()
	# An Inspect child whose call_method runs a method that speeds time up and awaits release.
	var probe: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"signal release",
					"",
					"",
					"func handle(_command: String, _params: Dictionary) -> Variant:",
					"\tEngine.time_scale = 3.0",
					"\tawait release",
					'\treturn {"value": "done"}',
				]
			)
		)
	)
	bridge._inspect = probe
	var params: Dictionary = {"node": "/root/CallProbe", "method": "hold", "args": []}
	bridge._track(9, "call_method", params)
	bridge._handle_inspect(9, params, "call_method")
	assert_true(bridge.sent.is_empty(), "a method still running is not answered yet")
	assert_true(bridge._cancel(9), "a running call_method is cancelled")
	assert_approx(Engine.time_scale, 1.0, "the time scale is restored")
	assert_eq(bridge.sent.size(), 1, "the cancel answers the request")
	var error: String = str(bridge.sent[0].get("error")) if not bridge.sent.is_empty() else ""
	assert_true(
		error.begins_with(
			(
				"no longer awaited: the method keeps running on its node; "
				+ "restored Engine.time_scale to 1"
			)
		),
		"the answer says the method keeps running and what was restored: %s" % error
	)
	assert_true(error.ends_with("; restart_project stops it."), "and how to stop it: %s" % error)
	probe.emit_signal("release")
	assert_eq(bridge.sent.size(), 1, "the method's late end is never answered")
	assert_true(bridge._running_calls.is_empty(), "no call is left awaited")
	Engine.time_scale = 1.0
	probe.free()
	_free_bridge(bridge)


func test_the_off_variable_keeps_the_bridge_off_despite_a_port_and_token() -> void:
	OS.set_environment("GODOT_MCP_PORT", "1")
	OS.set_environment("GODOT_MCP_TOKEN", "token")
	OS.set_environment("GODOT_MCP_OFF", "1")
	var bridge: Node = _bridge_script.new()
	var switched_off: Dictionary = bridge._endpoint
	var logger: Variant = bridge._logger
	# The runner's tests run before the root enters the tree, so _ready is called by hand.
	bridge._ready()
	var freed: bool = bridge.is_queued_for_deletion()
	OS.unset_environment("GODOT_MCP_OFF")
	var switched_on: Dictionary = _dormant_script.find_endpoint(bridge._state_dir)
	bridge.free()
	OS.unset_environment("GODOT_MCP_PORT")
	OS.unset_environment("GODOT_MCP_TOKEN")
	assert_eq(switched_off, {}, "GODOT_MCP_OFF=1 finds no endpoint")
	assert_true(logger == null, "a switched-off bridge registers no logger")
	assert_true(freed, "a switched-off bridge frees itself in _ready")
	assert_eq(switched_on.get("port"), 1, "without GODOT_MCP_OFF the same variables find the port")


## A bridge whose frames are kept in its sent Array rather than written to a socket: a subclass
## with no _ready, so it neither frees itself nor dials. _free_bridge frees it.
func _recording_bridge() -> Node:
	var bridge: Node = _compile(
		(
			"\n"
			. join(
				[
					'extends "%s"' % _bridge_script.resource_path,
					"",
					"var sent: Array = []",
					"",
					"",
					"func _ready() -> void:",
					"\tpass",
					"",
					"",
					"func _send(message: Dictionary) -> void:",
					"\tsent.append(message)",
				]
			)
		)
	)
	bridge._time = _time_script.new()
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	return bridge


func _free_bridge(bridge: Node) -> void:
	bridge._time.free()
	bridge.free()


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()


## A run_script source whose execute runs setup, then counts each frame it sees in the root's
## COUNTER meta, returning at the first frame after the meta is removed.
func _counting_script(setup: String) -> String:
	return (
		"\n"
		. join(
			[
				"extends RefCounted",
				"",
				"",
				"func execute(tree: SceneTree) -> Variant:",
				"\t%s" % setup,
				'\ttree.root.set_meta("%s", 0)' % COUNTER,
				"\tvar counting: bool = true",
				"\twhile counting:",
				"\t\tawait tree.process_frame",
				'\t\tcounting = tree.root.has_meta("%s")' % COUNTER,
				"\t\tif counting:",
				'\t\t\ttree.root.set_meta("%s", tree.root.get_meta("%s") + 1)' % [COUNTER, COUNTER],
				"\treturn null",
			]
		)
	)


## Emits the tree's process_frame count times: the frames a suspended coroutine resumes on, as
## the runner's own loop never runs one while the tests do.
func _frames(count: int) -> void:
	for _frame in count:
		_tree().process_frame.emit()


func test_a_quiet_override_placement_is_recognised_and_left_alone_headless() -> void:
	var type_key := "display/window/size/initial_position_type"
	var position_key := "display/window/size/initial_position"
	var type_before: Variant = ProjectSettings.get_setting(type_key)
	var position_before: Variant = ProjectSettings.get_setting(position_key)
	assert_true(
		not _window_script.parked_by_override(), "the default placement is not a parked one"
	)
	ProjectSettings.set_setting(type_key, 0)
	ProjectSettings.set_setting(position_key, Vector2i(-9999, -9999))
	assert_true(_window_script.parked_by_override(), "a quiet override's placement is")
	assert_true(
		not _window_script.restore_parked_window(), "a headless run has no window to restore"
	)
	ProjectSettings.set_setting(position_key, Vector2i(-9999, 0))
	assert_true(not _window_script.parked_by_override(), "another absolute position is not")
	ProjectSettings.set_setting(type_key, type_before)
	ProjectSettings.set_setting(position_key, position_before)


func _tree() -> SceneTree:
	return Engine.get_main_loop() as SceneTree
