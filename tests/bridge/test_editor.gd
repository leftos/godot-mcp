extends "res://gd_test.gd"
## The bridge's editor actions (bridge/godot_mcp_editor.gd) on a stand-in EditorInterface: the
## re-entry guard's busy flag, the bridge holding the frames that arrive while it is set, save's
## effect check and close's refusal of an unsaved scene.
# gdlint: disable=private-method-call

## The guard's hang guard, as the module's own DEADLINE_MS.
const DEADLINE_MS := 30000
## The most polls the loopback connection gets to connect: a hang guard, not a time.
const CONNECT_TRIES := 10000

const RunDir := preload("res://run_dir.gd")

var _editor_script: GDScript = load_bridge_script("godot_mcp_editor.gd")
var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")
var _dormant_script: GDScript = load_bridge_script("godot_mcp_dormant.gd")


## Stands in for EditorInterface: the scenes it holds, and the calls made on it, in order.
class FakeEditor:
	extends RefCounted

	var open_scenes: PackedStringArray = []
	var unsaved: PackedStringArray = []
	var root: Node = Node.new()
	var calls: Array = []
	var file_system: FakeFileSystem = FakeFileSystem.new()

	func get_open_scenes() -> PackedStringArray:
		return open_scenes

	func get_unsaved_scenes() -> PackedStringArray:
		return unsaved

	func get_edited_scene_root() -> Node:
		return root

	func is_playing_scene() -> bool:
		return false

	func get_playing_scene() -> String:
		return ""

	func get_resource_filesystem() -> FakeFileSystem:
		return file_system

	## Writes the edited scene's file, leaving it in the unsaved list until the test clears it.
	func save_scene() -> int:
		calls.append("save_scene")
		var file: FileAccess = FileAccess.open(root.scene_file_path, FileAccess.WRITE)
		file.store_string("[gd_scene format=3]\n")
		file.close()
		return OK

	func close_scene() -> int:
		calls.append("close_scene")
		var kept: PackedStringArray = []
		for scene: String in open_scenes:
			if scene != root.scene_file_path:
				kept.append(scene)
		open_scenes = kept
		return OK


## Stands in for EditorFileSystem: never scanning or importing.
class FakeFileSystem:
	extends RefCounted

	func is_scanning() -> bool:
		return false

	func is_importing() -> bool:
		return false


func test_guarded_holds_busy_from_the_call_until_the_effect_shows() -> void:
	var editor: FakeEditor = FakeEditor.new()
	var module: Node = _module(editor)
	var seen: Dictionary = {"shown": false, "busy_in_call": false}
	var call: Callable = func() -> void: seen["busy_in_call"] = module.busy
	var check: Callable = func() -> bool: return seen["shown"]
	var pending: Variant = module.call("guarded", call, check, DEADLINE_MS)
	pending.completed.connect(func(shown: bool) -> void: seen["result"] = shown)
	assert_true(seen["busy_in_call"], "busy is set while the call runs")
	_frames(1)
	assert_true(module.busy, "busy holds while the effect has not shown")
	seen["shown"] = true
	_frames(1)
	assert_true(not module.busy, "busy clears once the effect shows")
	assert_eq(seen.get("result"), true, "guarded says the effect showed")
	_free(module, editor)


func test_guarded_clears_busy_at_the_deadline() -> void:
	var editor: FakeEditor = FakeEditor.new()
	var module: Node = _module(editor)
	var clock: Dictionary = {"ms": 1000}
	module.now_ms = func() -> int: return clock["ms"]
	var seen: Dictionary = {}
	var never: Callable = func() -> bool: return false
	var pending: Variant = module.call("guarded", func() -> void: pass, never, DEADLINE_MS)
	pending.completed.connect(func(shown: bool) -> void: seen["result"] = shown)
	_frames(1)
	assert_true(module.busy, "busy holds before the deadline")
	clock["ms"] = 1000 + DEADLINE_MS
	_frames(1)
	assert_true(not module.busy, "busy clears at the deadline")
	assert_eq(seen.get("result"), false, "guarded says the effect did not show")
	_free(module, editor)


func test_guarded_clears_busy_when_the_call_raises() -> void:
	var editor: FakeEditor = FakeEditor.new()
	var module: Node = _module(editor)
	var raising: Callable = func() -> void:
		var missing: Object = null
		missing.call("save_scene")
	module.call("guarded", raising, func() -> bool: return true, DEADLINE_MS)
	var errors: PackedStringArray = (Engine.get_main_loop() as Object).get("_recorder").take()
	assert_eq(errors.size(), 1, "the call's own error is logged: %s" % [errors])
	assert_true(not module.busy, "busy clears after a call that raised")
	_free(module, editor)


func test_a_command_that_arrives_while_busy_runs_after_it_in_order() -> void:
	var editor: FakeEditor = _editor_with_unsaved_scene()
	var module: Node = _module(editor)
	var bridge: Node = _recording_bridge(module)
	var server: TCPServer = TCPServer.new()
	assert_eq(server.listen(0, "127.0.0.1"), OK, "a loopback server listens")
	bridge._stream = _connected_stream(server.get_local_port())
	bridge._buffer = _frame_bytes(1, "editor", {"action": "save"})
	bridge._buffer.append_array(_frame_bytes(2, "ping", {}))
	bridge._read_frames()
	assert_true(module.busy, "the save holds the bridge")
	assert_eq(bridge.sent.size(), 0, "neither the save nor the ping is answered yet")
	bridge._read_frames()
	assert_eq(bridge.sent.size(), 0, "the ping still waits while the save holds")
	assert_true(not bridge._buffer.is_empty(), "the ping's bytes stay in the buffer")
	editor.unsaved = []
	_frames(1)
	assert_true(not module.busy, "the save's effect shows")
	bridge._read_frames()
	var ids: Array = bridge.sent.map(func(message: Dictionary) -> Variant: return message.get("id"))
	assert_eq(ids, [1, 2], "the save is answered, then the ping")
	assert_eq(bridge.sent[0].get("ok"), true, "the save succeeded: %s" % [bridge.sent[0]])
	bridge._stream.disconnect_from_host()
	server.stop()
	bridge.free()
	_free(module, editor)


func test_save_is_done_once_the_scene_leaves_the_unsaved_list_and_its_file_is_new() -> void:
	var editor: FakeEditor = _editor_with_unsaved_scene()
	var module: Node = _module(editor)
	var path: String = editor.root.scene_file_path
	var since: int = int(Time.get_unix_time_from_system())
	editor.save_scene()
	assert_true(not module._saved(path, since), "a scene still in the unsaved list is not saved")
	editor.unsaved = []
	assert_true(module._saved(path, since), "out of the unsaved list, written at or after the call")
	assert_true(not module._saved(path, since + 3600), "a file older than the call is not saved")
	_free(module, editor)


func test_close_refuses_an_unsaved_scene_without_discard() -> void:
	var editor: FakeEditor = _editor_with_unsaved_scene()
	var module: Node = _module(editor)
	var bridge: Node = _recording_bridge(module)
	var path: String = editor.root.scene_file_path
	module.handle_editor(1, {"action": "close"})
	assert_eq(
		bridge.sent[0].get("error") if not bridge.sent.is_empty() else null,
		"The scene %s has unsaved changes; save it first or pass discard: true." % path,
		"close without discard"
	)
	assert_eq(editor.calls, [], "the scene is not closed")
	module.handle_editor(2, {"action": "close", "discard": true})
	assert_eq(editor.calls, ["close_scene"], "with discard the scene is closed")
	assert_eq(bridge.sent.size(), 2, "the close with discard is answered")
	if bridge.sent.size() == 2:
		assert_eq(bridge.sent[1].get("ok"), true, "it succeeded: %s" % [bridge.sent[1]])
	bridge.free()
	_free(module, editor)


## A stand-in editor whose one open scene, current and unsaved, is a file in the run's folder.
func _editor_with_unsaved_scene() -> FakeEditor:
	var editor: FakeEditor = FakeEditor.new()
	var path: String = ProjectSettings.globalize_path(RunDir.path("godot_mcp_editor_scene.tscn"))
	editor.root.scene_file_path = path
	editor.open_scenes = [path]
	editor.unsaved = [path]
	return editor


func _module(editor: FakeEditor) -> Node:
	var module: Node = _editor_script.new()
	module.editor_interface = editor
	return module


## A bridge in editor mode whose frames are kept in its sent Array, with module as its editor
## actions; a subclass with no _ready, so it never dials.
func _recording_bridge(module: Node) -> Node:
	var script := GDScript.new()
	script.source_code = (
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
	script.reload()
	var bridge: Node = script.new()
	bridge._mode = _dormant_script.MODE_EDITOR
	bridge._editor = module
	module.bridge = bridge
	bridge._handlers = bridge._command_handlers()
	bridge._handlers.merge(module.handlers())
	return bridge


## A stream connected to the loopback server on port, polled until it connects.
func _connected_stream(port: int) -> StreamPeerTCP:
	var stream: StreamPeerTCP = StreamPeerTCP.new()
	stream.connect_to_host("127.0.0.1", port)
	var tries: int = 0
	while stream.get_status() == StreamPeerTCP.STATUS_CONNECTING and tries < CONNECT_TRIES:
		OS.delay_msec(1)
		stream.poll()
		tries += 1
	assert_eq(stream.get_status(), StreamPeerTCP.STATUS_CONNECTED, "the loopback stream connects")
	return stream


## One request as the server frames it: a 4-byte big-endian length, then the UTF-8 JSON.
func _frame_bytes(id: int, command: String, params: Dictionary) -> PackedByteArray:
	var payload: PackedByteArray = (
		JSON.stringify({"id": id, "command": command, "params": params}).to_utf8_buffer()
	)
	var size: int = payload.size()
	var bytes: PackedByteArray = PackedByteArray(
		[(size >> 24) & 0xFF, (size >> 16) & 0xFF, (size >> 8) & 0xFF, size & 0xFF]
	)
	bytes.append_array(payload)
	return bytes


func _frames(count: int) -> void:
	for _frame in count:
		(Engine.get_main_loop() as SceneTree).process_frame.emit()


func _free(module: Node, editor: FakeEditor) -> void:
	module.free()
	editor.root.free()
