# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## The watch's stack tracks (bridge/godot_mcp_watch.gd): the call stack a kept emission's event
## ends with, the bridge's own folder left out, each language's frames in order and at most
## STACK_FRAMES of each, a connection a later stack track asks for, the kept limit, and start's
## listing of the track. Frames are fed to the watch by an injected backtrace, so no capture runs.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame, so the watch's connection can be read.
signal test_frame

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar _logger: Logger\nvar top: Node\n\n\n"
	+ "func _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)

var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")


func test_a_stack_track_ends_each_event_with_the_emitting_call_stack() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	_count_frames(watch, 0)
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	_emit_hit(mover)
	var events: Array = watch.stop()["result"]["events"]
	assert_eq(events[0].size(), 8, "[frame, gameMs, node, signal, args, track, ticks, stack]")
	var stack: Array = events[0][7]
	assert_true(not stack.is_empty(), "the emission's call stack: %s" % [stack])
	assert_true(
		str(stack[0]).ends_with("in _emit_hit"), "the emitting function first: %s" % [stack]
	)
	for frame: String in stack:
		assert_true(
			not frame.contains("godot_mcp_watch.gd"), "the watch's own frame dropped: %s" % [frame]
		)
	_free(rig)


func test_a_track_without_stack_keeps_the_seven_item_event() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	mover.add_user_signal("ping")
	_count_frames(watch, 0)
	var params: Dictionary = {
		"signals":
		[{"node": "Mover", "signal": "hit", "stack": true}, {"node": "Mover", "signal": "ping"}],
		"frames": 5,
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	_emit_hit(mover)
	mover.emit_signal("ping")
	var events: Array = watch.stop()["result"]["events"]
	assert_eq(events[0].size(), 8, "the stack track's event ends with its stack")
	assert_eq(events[1].size(), 7, "the plain track's event keeps its seven items")
	_free(rig)


func test_the_stack_is_capped_at_ten_frames() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	_count_frames(watch, 0)
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	_deep_emit(mover, 15)
	var events: Array = watch.stop()["result"]["events"]
	var stack: Array = events[0][7]
	assert_eq(stack.size(), 10, "at most STACK_FRAMES frames")
	assert_true(str(stack[0]).ends_with("in _emit_hit"), "the most recent first: %s" % [stack])
	_free(rig)


func test_a_stack_track_is_described_in_start() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	rig["mover"].add_user_signal("hit")
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	var started: Dictionary = watch._started(watch._watch)
	var wanted: Array = [{"node": "Mover", "signal": "hit", "connected": 1, "stack": true}]
	assert_eq(started["signals"], wanted, "the track says it records a stack")
	_free(rig)


func test_an_emission_over_the_kept_limit_captures_no_stack() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	_count_frames(watch, 0)
	var captures: Array = []
	watch.backtraces = func() -> Array:
		captures.append(true)
		return []
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	for index in 301:
		mover.emit_signal("hit")
	var result: Dictionary = watch.stop()["result"]
	assert_eq(
		result["eventTracks"], [{"kept": 300, "total": 301}], "the 301st is counted, not kept"
	)
	assert_eq(result["events"].size(), 300, "no event past the kept limit")
	assert_eq(captures.size(), 300, "a stack captured only for the kept emissions")
	_free(rig)


func test_a_stack_drops_the_bridge_folder_and_puts_gdscript_before_csharp() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	_count_frames(watch, 0)
	var bridge_dir: String = watch.get_script().resource_path.get_base_dir()
	watch.backtraces = func() -> Array:
		return [
			_fake_backtrace("C#", [_fake_frame("Probe.cs", 7, "void CsProbe.CsSignals.EmitAll()")]),
			_fake_backtrace(
				"GDScript",
				[
					_fake_frame(bridge_dir.path_join("godot_mcp_watch.gd"), 2, "_emission_stack"),
					_fake_frame(bridge_dir.path_join("godot_mcp_input.gd"), 40, "_send_and_record"),
					_fake_frame("res://Game.gd", 12, "poke"),
				]
			),
		]
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	_emit_hit(mover)
	var stack: Array = watch.stop()["result"]["events"][0][7]
	assert_eq(
		stack,
		["res://Game.gd:12 in poke", "Probe.cs:7 in void CsProbe.CsSignals.EmitAll()"],
		"the bridge's frames left out, GDScript's first"
	)
	_free(rig)


func test_each_languages_frames_are_capped_at_ten() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	_count_frames(watch, 0)
	var gdscript_frames: Array = []
	var csharp_frames: Array = []
	for index in 15:
		gdscript_frames.append(_fake_frame("res://Game.gd", index, "call%d" % index))
		csharp_frames.append(_fake_frame("Probe.cs", index, "Cs%d" % index))
	watch.backtraces = func() -> Array:
		return [_fake_backtrace("GDScript", gdscript_frames), _fake_backtrace("C#", csharp_frames)]
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit", "stack": true}], "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	_emit_hit(mover)
	var stack: Array = watch.stop()["result"]["events"][0][7]
	assert_eq(stack.size(), 20, "ten frames of each language")
	assert_eq(stack[0], "res://Game.gd:0 in call0", "GDScript's newest first")
	assert_eq(stack[10], "Probe.cs:0 in Cs0", "then C#'s newest first")
	_free(rig)


func test_a_later_stack_track_captures_a_connection_an_earlier_track_made() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	mover.add_to_group("movers")
	_in_group(rig, "movers", 0, "ping")
	_count_frames(watch, 0)
	var params: Dictionary = {
		"signals":
		[{"group": "movers", "signal": "ping"}, {"node": "Mover", "signal": "ping", "stack": true}],
		"frames": 5,
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	mover.emit_signal("ping")
	var result: Dictionary = watch.stop()["result"]
	var events: Array = result["events"]
	assert_eq(events.size(), 1, "one emission, kept once")
	assert_eq(events[0].size(), 8, "the later track's stack on the earlier track's connection")
	assert_true(not (events[0][7] as Array).is_empty(), "the connection captures a stack")
	assert_eq(
		result["eventTracks"],
		[{"kept": 1, "total": 1}, {"kept": 0, "total": 0}],
		"under the earlier track"
	)
	_free(rig)


## The engine's frame count, given whether the watch counts physics ticks, and the reads it answers.
func _count_frames(watch: Node, first: int) -> Array:
	var count: Array = [first]
	watch.frame_counter = func(_physics: bool) -> int: return count[0]
	return count


## Adds size nodes Card<n> under the rig's top to group, each with the user signal signal_name,
## and has the watch find a group's members among top's children (gdtest runs before the root
## enters the tree, so the tree has no groups); returns them.
func _in_group(rig: Dictionary, group: String, size: int, signal_name: String) -> Array:
	var top: Node = rig["top"]
	rig["watch"].group_members = func(wanted: String) -> Array:
		return top.get_children().filter(func(node: Node) -> bool: return node.is_in_group(wanted))
	var nodes: Array = []
	for index in size:
		var node := Node.new()
		node.name = "Card%d" % index
		node.add_user_signal(signal_name)
		node.add_to_group(group)
		rig["top"].add_child(node)
		nodes.append(node)
	return nodes


## A Node2D Mover under WatchRig, a bridge finding nodes under WatchRig, and a watch on it.
func _rig() -> Dictionary:
	var top := Node.new()
	top.name = "WatchRig"
	var mover := Node2D.new()
	mover.name = "Mover"
	top.add_child(mover)
	var script := GDScript.new()
	script.source_code = BRIDGE_SOURCE
	script.reload()
	var bridge: Node = script.new()
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	var watch: Node = _watch_script.new()
	watch.bridge = bridge
	watch.usec_clock = func() -> int: return 0
	return {"top": top, "mover": mover, "bridge": bridge, "watch": watch}


## A frame of an injected backtrace, as ScriptBacktrace.get_frame_file, get_frame_line and
## get_frame_function report one.
class FakeFrame:
	var file: String
	var line: int
	var function: String

	func _init(frame_file: String, frame_line: int, frame_function: String) -> void:
		file = frame_file
		line = frame_line
		function = frame_function


## One language's backtrace, as Engine.capture_script_backtraces returns it, for a test to inject.
class FakeBacktrace:
	var language: String
	var frames: Array

	func _init(language_name: String, stack_frames: Array) -> void:
		language = language_name
		frames = stack_frames

	func get_language_name() -> String:
		return language

	func get_frame_count() -> int:
		return frames.size()

	func get_frame_file(index: int) -> String:
		return (frames[index] as FakeFrame).file

	func get_frame_line(index: int) -> int:
		return (frames[index] as FakeFrame).line

	func get_frame_function(index: int) -> String:
		return (frames[index] as FakeFrame).function


static func _fake_frame(file: String, line: int, function: String) -> FakeFrame:
	return FakeFrame.new(file, line, function)


static func _fake_backtrace(language: String, frames: Array) -> FakeBacktrace:
	return FakeBacktrace.new(language, frames)


## Emits hit on mover from this function, so a stack track records this call first.
func _emit_hit(mover: Node) -> void:
	mover.emit_signal("hit")


## Emits hit on mover under depth more calls of this function, so a stack fills past its cap.
func _deep_emit(mover: Node, depth: int) -> void:
	if depth > 0:
		_deep_emit(mover, depth - 1)
		return
	_emit_hit(mover)


func _free(rig: Dictionary) -> void:
	rig["watch"].drop()
	rig["watch"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()
