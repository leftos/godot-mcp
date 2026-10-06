extends Node
## The godot-mcp bridge's watch, a child of the bridge: samples properties of nodes and Godot
## Expressions once a frame over a window, beside every other tool, and keeps each track's change
## points for the watch tool.
##
## A track samples at SceneTree.process_frame, emitted each frame before the nodes' _process, paused
## or not (scene/main/scene_tree.cpp L713, L719 in 4.7.2), or at physics_frame with unit physics
## (L649, L655), so a sample shows the state the frame before left. A frame that finds the tree
## paused is neither sampled nor counted toward the window, and is listed in paused. Values are read
## raw and converted to JSON only when kept: a sample is kept when it differs from the last kept
## one, and a track keeps its first FIRST_POINTS change points and a ring of its last LAST_POINTS,
## counting the ones between in dropped. A signal track connects a variadic lambda to a node, or to
## every member of a group at start that has the signal, and records each emission with the frame
## it fires in; it keeps its first TRACK_EVENTS and counts the rest. Monitors (frame_ms, Godot's
## built-in monitors and custom ones) are read each sampled frame and summarised at the end by
## godot_mcp_watch_monitors.gd. One watch runs at a time; one whose window ended holds its timeline
## until a stop collects it. It never takes the clock's running mark, so steps, waits and captures
## run beside it.

## Emitted when the watch's first frame has run and when a watch ends.
signal changed

const TIME_SCRIPT := "godot_mcp_time.gd"
const CONDITIONS_SCRIPT := "godot_mcp_conditions.gd"
const MONITORS_SCRIPT := "godot_mcp_watch_monitors.gd"
## The change points a track keeps from its start, and from its end in a ring.
const FIRST_POINTS := 200
const LAST_POINTS := 50
## The events a signal track keeps; past them it only counts its emissions.
const TRACK_EVENTS := 300
## The most nodes a watch's signal tracks connect together.
const MAX_CONNECTIONS := 200
## The most skipped group members a reply lists.
const MAX_SKIPPED := 50
## The most real time a watch's deadline grows by while the game is paused.
const MAX_PAUSED_MS := 600000
## The window when the request names none, as the server sends it.
const DEFAULT_FRAMES := 600
## The component names of each vector type, which minDelta, min and max read one by one.
const VECTOR_COMPONENTS := {
	TYPE_VECTOR2: "xy",
	TYPE_VECTOR2I: "xy",
	TYPE_VECTOR3: "xyz",
	TYPE_VECTOR3I: "xyz",
	TYPE_VECTOR4: "xyzw",
	TYPE_VECTOR4I: "xyzw",
}
const RUNNING_REFUSAL := (
	'A watch is already running on this game; end it with watch {action: "stop"} before '
	+ "starting another."
)
const RUN_REFUSAL := (
	"A watch run is still running on this game; it answers when its window ends, and "
	+ "no other watch can start or stop until then."
)
const HELD_REFUSAL := (
	"The last watch's window has ended and its timeline is not collected yet; collect it with "
	+ 'watch {action: "stop"} before starting another.'
)
const NONE_STARTED := (
	"No watch runs: none has started in this game run (restart_project and stop_project drop a "
	+ 'watch); start one with watch {action: "start"}.'
)
const COLLECTED := (
	"No watch runs: the last one's timeline has already been returned; start one with "
	+ 'watch {action: "start"}.'
)
const DROPPED := (
	"No watch runs: the last one was dropped when the connection to the server ended; start one "
	+ 'with watch {action: "start"}.'
)
const DROPPED_ERROR := "The watch was dropped: the connection to the server ended."
const ENDED_EARLY := "The watch ended before its first frame; start it again."
const MIN_DELTA_REFUSAL := (
	"minDelta applies to a numeric track (an int, a float or a vector); " + "track '%s' reads %s."
)
const CONNECTIONS_REFUSAL := (
	"the signal tracks connect %d nodes; at most 200 (narrow a group track or watch a signal on "
	+ "a parent)"
)

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## The engine's frame count now, given whether the watch counts physics ticks: what a signal's
## emission is stamped with (_engine_frames; a test sets its own).
var frame_counter: Callable
## A group's nodes now, in tree order: what a group track connects (_tree_group; a test, whose nodes
## are in no tree, sets its own).
var group_members: Callable
## The real time now in microseconds: what frame_ms and the sampler's cost are measured with
## (Time.get_ticks_usec; a test sets its own).
var usec_clock: Callable
## The Movie Maker file the run writes, "" when it records none: whether frame_ms warns that it
## measures render speed (Engine.get_write_movie_path; a test sets its own).
var movie_path: Callable
## The clock's script, for its game clock and its JSON comparison.
var _time_script: GDScript
## The conditions' script, for its expression inputs and its condition parse.
var _conditions_script: GDScript
## The monitors' script, which resolves, reads and summarises a watch's monitors.
var _monitors_script: GDScript
## The watch running or held, or empty (see _new_watch for its fields).
var _watch: Dictionary = {}
## Why no watch runs, while _watch is empty.
var _idle_reason: String = NONE_STARTED


func _init() -> void:
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_time_script = load(script_dir.path_join(TIME_SCRIPT)) as GDScript
	_conditions_script = load(script_dir.path_join(CONDITIONS_SCRIPT)) as GDScript
	_monitors_script = load(script_dir.path_join(MONITORS_SCRIPT)) as GDScript
	frame_counter = _engine_frames
	group_members = _tree_group
	usec_clock = Callable(Time, "get_ticks_usec")
	movie_path = Callable(Engine, "get_write_movie_path")


func _engine_frames(physics: bool) -> int:
	return Engine.get_physics_frames() if physics else Engine.get_process_frames()


func _tree_group(group: String) -> Array:
	return (Engine.get_main_loop() as SceneTree).get_nodes_in_group(group)


## Runs a watch request, {action: "start" | "run" | "stop", properties, expressions, signals,
## monitors, frames | gameMs, unit, call, budgetMs, deadlineMs[, backstopMs]}; answers {result} or
## {error}. start answers after the watch's first frame with {startFrame, tracks[, signals,
## skipped, monitors, call]}; run answers the timeline when the window ends; stop ends the watch
## and answers its timeline (_collect).
func handle(params: Dictionary) -> Dictionary:
	var action: String = str(params.get("action", ""))
	match action:
		"start", "run":
			return await _start(params, action == "run", frame_source(params))
		"stop":
			return stop()
	return {"error": "unknown watch action '%s'; use start, stop or run" % action}


## The tree's process_frame, or its physics_frame with unit physics: the signal a watch samples on.
func frame_source(params: Dictionary) -> Signal:
	var tree := Engine.get_main_loop() as SceneTree
	var physics: bool = str(params.get("unit", "process")) == "physics"
	return tree.physics_frame if physics else tree.process_frame


## Begins a watch on source and waits for its first frame (start) or its end (run); one that ended
## before its first frame (a stop or a cancel came first) is refused.
func _start(params: Dictionary, run: bool, source: Signal) -> Dictionary:
	var refusal: String = begin(params, source)
	if not refusal.is_empty():
		return {"error": refusal}
	var watch: Dictionary = _watch
	watch["run"] = run
	while _awaits(watch, run):
		await changed
	if not str(watch["error"]).is_empty():
		return {"error": watch["error"]}
	if int(watch["start_frame"]) < 0:
		return _ended_early(watch)
	if run:
		return {"result": _collect(watch)}
	return {"result": _started(watch)}


## The refusal of a watch that ended before its first frame, let go when a stop has not collected
## it already.
func _ended_early(watch: Dictionary) -> Dictionary:
	if is_same(_watch, watch):
		_release(watch)
		_idle_reason = NONE_STARTED
	return {"error": ENDED_EARLY}


## Whether a start or a run still waits: a start for the watch's first frame, a run for its end.
static func _awaits(watch: Dictionary, run: bool) -> bool:
	if not str(watch["error"]).is_empty() or watch["state"] != "running":
		return false
	return run or int(watch["start_frame"]) < 0


## Resolves params' tracks and connects the watch to source, whose emissions are its frames;
## returns why it cannot start, or "".
func begin(params: Dictionary, source: Signal) -> String:
	if not _watch.is_empty():
		return _busy_refusal()
	var tracks: Array = []
	var refusal: String = _resolve(params.get("properties"), _property_track, tracks)
	if refusal.is_empty():
		refusal = _resolve(params.get("expressions"), _expression_track, tracks)
	var signals: Array = []
	if refusal.is_empty():
		refusal = _resolve(params.get("signals"), _signal_track, signals)
	if refusal.is_empty():
		refusal = _connections_refusal(signals)
	var perf: Variant = refusal
	if refusal.is_empty():
		perf = _monitors_script.begin(params, movie_path)
	if perf is String:
		return perf
	_watch = _new_watch(params, tracks, source)
	_watch["signals"] = signals
	_watch["perf"] = perf
	_connect_signals(_watch)
	source.connect(_watch["handler"])
	return ""


func _busy_refusal() -> String:
	if _watch["state"] == "held":
		return HELD_REFUSAL
	return RUN_REFUSAL if _watch["run"] else RUNNING_REFUSAL


## Adds the track make builds from each spec in specs to tracks; returns the first refusal, or "".
func _resolve(specs: Variant, make: Callable, tracks: Array) -> String:
	if not specs is Array:
		return ""
	for spec: Variant in specs:
		var track: Variant = make.call(spec if spec is Dictionary else {})
		if track is String:
			return track
		tracks.append(track)
	return ""


## A watch: its state (running, then held), the request's params (a cancel matches them), its
## tracks, the frame signal and its handler, the window (kind frames or gameMs, and its target), the
## deadline and the real time paused so far, the game clock, the first frame's number and time, the
## paused ranges, why it stopped early, the call's {value}, an error that ends it, and for signal
## tracks the kept events, each emitter's count, the connections, and the last frame's engine
## count and game time (an emission in it is stamped with that time).
func _new_watch(params: Dictionary, tracks: Array, source: Signal) -> Dictionary:
	var physics: bool = str(params.get("unit", "process")) == "physics"
	var kind: String = "gameMs" if params.has("gameMs") else "frames"
	var fallback_ms: float = 10000.0 + 100.0 * DEFAULT_FRAMES
	var watch: Dictionary = {
		"state": "running",
		"params": params,
		"tracks": tracks,
		"source": source,
		"physics": physics,
		"kind": kind,
		"target": int(params.get(kind, DEFAULT_FRAMES)),
		"deadline_ms": float(params.get("backstopMs", params.get("deadlineMs", fallback_ms))),
		"paused_ms": 0,
		"clock": {"seconds": 0.0, "frames": 0},
		"start_frame": -1,
		"began_ms": 0,
		"last_ms": 0,
		"wall_ms": 0,
		"paused": [],
		"stopped": "",
		"call": {},
		"error": "",
		"run": false,
		"signals": [],
		"events": [],
		"event_counts": {},
		"connections": [],
		"frame_count": -1,
		"frame_ms": 0,
	}
	watch["handler"] = _on_frame.bind(watch)
	return watch


## A property track {node, property, name?, minDelta?}: refused when the node or the property is
## missing, or when minDelta is set and the first read is not numeric.
func _property_track(spec: Dictionary) -> Variant:
	var node_name: String = str(spec.get("node", ""))
	var property: String = str(spec.get("property", ""))
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	var first: String = property.get_slice(":", 0)
	if not bridge._json.has_property(node, first):
		return "'%s' has no property '%s'." % [node.get_path(), first]
	var track: Dictionary = _new_track(spec, node)
	track["path"] = NodePath(property)
	return _checked_min_delta(track)


## An expression track {name, expression, node?, minDelta?}, parsed once with wait_for's inputs
## (node too, also the base instance, when given) through the conditions' parse_condition:
## refused when its node is missing, when it does not parse (Expression ignores trailing text and
## GDScript syntax it has no operator for) or when minDelta is set and its first read is not
## numeric.
func _expression_track(spec: Dictionary) -> Variant:
	var name: String = str(spec.get("name", ""))
	var node: Node = null
	if spec.get("node") is String:
		node = bridge._find_node(spec["node"])
		if node == null:
			return bridge._inspect.not_found(spec["node"], "get_scene_tree lists the nodes' paths")
	var names: PackedStringArray = _conditions_script.EXPRESSION_INPUTS.duplicate()
	if node != null:
		names.append("node")
	var parsed: Variant = _conditions_script.parse_condition(str(spec.get("expression", "")), names)
	if parsed is String:
		return "The expression of track '%s' does not parse: %s" % [name, parsed]
	var track: Dictionary = _new_track(spec, node)
	track["expression"] = parsed
	return _checked_min_delta(track)


## A signal track {node, signal} or {group, signal}: its emitter as given (the node's path, or the
## group), its signal, the nodes to connect (the node, or the group's members at start that have the
## signal), the members skipped, and its kept and total emissions. Refused when the node is missing
## or lacks the signal, when the group has no nodes, or when none of them has it.
func _signal_track(spec: Dictionary) -> Variant:
	var signal_name: String = str(spec.get("signal", ""))
	var track: Dictionary = {
		"signal": signal_name, "nodes": [], "skipped": [], "kept": 0, "total": 0, "connected": 0
	}
	if spec.get("group") is String:
		track["emitter"] = {"group": spec["group"]}
		return _group_members(track, spec["group"])
	var node_name: String = str(spec.get("node", ""))
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	if not node.has_signal(signal_name):
		return "%s has no signal '%s'" % [_path_of(node), signal_name]
	track["emitter"] = {"node": _path_of(node)}
	(track["nodes"] as Array).append(node)
	return track


## Fills track's nodes with group's members that have its signal, in tree order, and its skipped
## with the others; returns track, or the refusal of a group with no nodes or none with the signal.
func _group_members(track: Dictionary, group: String) -> Variant:
	var members: Array = group_members.call(group)
	if members.is_empty():
		return "group '%s' has no nodes" % group
	var signal_name: String = track["signal"]
	for member: Node in members:
		if member.has_signal(signal_name):
			(track["nodes"] as Array).append(member)
		else:
			var skipped: Dictionary = {
				"node": _path_of(member), "reason": "no signal '%s'" % signal_name
			}
			(track["skipped"] as Array).append(skipped)
	if (track["nodes"] as Array).is_empty():
		return "no node in group '%s' has signal '%s'" % [group, signal_name]
	return track


## The refusal of signal tracks that connect more than MAX_CONNECTIONS nodes together, each node and
## signal counted once however many tracks name it, or "".
static func _connections_refusal(signals: Array) -> String:
	var keys: Dictionary = {}
	for track: Dictionary in signals:
		for node: Node in track["nodes"]:
			keys[_connection_key(node, track["signal"])] = true
	var count: int = keys.size()
	return CONNECTIONS_REFUSAL % count if count > MAX_CONNECTIONS else ""


## Connects one variadic lambda to each node of each signal track, plainly, so an emission is
## recorded as it fires; counts each node's emissions from 0 and lets go of the track's nodes. A
## node and signal an earlier track connected is not connected again, so each emission is counted
## and kept once, under the first track that names it.
func _connect_signals(watch: Dictionary) -> void:
	var tracks: Array = watch["signals"]
	var counts: Dictionary = watch["event_counts"]
	var connected: Dictionary = {}
	for index in tracks.size():
		var track: Dictionary = tracks[index]
		var signal_name: String = track["signal"]
		for node: Node in track["nodes"]:
			var key: String = _connection_key(node, signal_name)
			if connected.has(key):
				continue
			connected[key] = true
			var path: String = _path_of(node)
			counts["%s:%s" % [path, signal_name]] = 0
			var on_signal := func(...args: Array) -> void: _record(watch, index, path, args)
			node.connect(signal_name, on_signal)
			(watch["connections"] as Array).append([node, signal_name, on_signal])
			track["connected"] = int(track["connected"]) + 1
		track.erase("nodes")


## One node's signal, whichever tracks name it.
static func _connection_key(node: Node, signal_name: String) -> String:
	return "%d:%s" % [node.get_instance_id(), signal_name]


## Records an emission of signal track index's signal by the node at path: counted always, and kept
## as [frame, gameMs, node, signal, args, track] while the track holds fewer than TRACK_EVENTS, its
## arguments converted to JSON now. A watch that ended records nothing (an emission calls the slots
## it copied before a disconnect).
func _record(watch: Dictionary, index: int, path: String, args: Array) -> void:
	if watch["state"] != "running":
		return
	var track: Dictionary = watch["signals"][index]
	var signal_name: String = track["signal"]
	var counts: Dictionary = watch["event_counts"]
	var key: String = "%s:%s" % [path, signal_name]
	counts[key] = int(counts.get(key, 0)) + 1
	track["total"] = int(track["total"]) + 1
	if int(track["kept"]) >= TRACK_EVENTS:
		return
	track["kept"] = int(track["kept"]) + 1
	var stamp: Array = _stamp(watch)
	var event: Array = [stamp[0], stamp[1], path, signal_name, bridge._json.to_json(args), index]
	(watch["events"] as Array).append(event)


## [frame, gameMs] for an emission now: the engine's frame count less startFrame, 0 before the
## first frame; and the game clock before the current frame's delta, which is the time the frame
## last advanced was sampled at when the emission is in it.
func _stamp(watch: Dictionary) -> Array:
	var start: int = watch["start_frame"]
	if start < 0:
		return [0, 0]
	var count: int = frame_counter.call(watch["physics"])
	if count == int(watch["frame_count"]):
		return [maxi(0, count - start), watch["frame_ms"]]
	return [maxi(0, count - start), floori(float(watch["clock"]["seconds"]) * 1000.0)]


## Disconnects every signal track's lambda from the nodes still alive (a freed emitter's slots
## went with it), then lets go of them, ending the cycle each lambda, which holds the watch, makes
## with it.
static func _disconnect_signals(watch: Dictionary) -> void:
	var connections: Array = watch["connections"]
	for connection: Array in connections:
		var node: Variant = connection[0]
		if is_instance_valid(node) and (node as Node).is_connected(connection[1], connection[2]):
			(node as Node).disconnect(connection[1], connection[2])
	connections.clear()


## A track's state: its spec, its node (null for an expression without one), minDelta (0 for the
## default rule), the samples' summary and the kept points (_keep).
func _new_track(spec: Dictionary, node: Node) -> Dictionary:
	return {
		"spec": spec,
		"node": node,
		"has_node": node != null,
		"node_path": _path_of(node),
		"min_delta": float(spec.get("minDelta", 0.0)),
		"done": false,
		"samples": 0,
		"last": null,
		"has_kept": false,
		"last_kept": null,
		"kept": 0,
		"first_points": [],
		"ring": [],
		"ring_head": 0,
		"range_kind": -1,
		"min": [],
		"max": [],
		"min_at": [],
		"max_at": [],
	}


## The node's path in the tree; its name when it is in none; "" for no node.
static func _path_of(node: Node) -> String:
	if node == null:
		return ""
	return str(node.get_path()) if node.is_inside_tree() else str(node.name)


## track, or the refusal of its minDelta when a read now is neither a number nor a vector; an
## expression whose read fails is accepted. A track without minDelta is not read here, so an
## expression runs only in the frames it samples.
func _checked_min_delta(track: Dictionary) -> Variant:
	if float(track["min_delta"]) <= 0.0:
		return track
	var first: Variant = _read(track)
	if _range_kind(first) >= 0 or _is_error(first):
		return track
	return MIN_DELTA_REFUSAL % [_label(track), type_string(typeof(first))]


## The track's name for a message: its name, else its node and property.
func _label(track: Dictionary) -> String:
	var spec: Dictionary = track["spec"]
	if spec.get("name") is String:
		return spec["name"]
	return "%s:%s" % [track["node_path"], spec.get("property", "")]


## The track's value now, raw: a property through get_indexed, or {"$freed": true} once its node is
## freed (the track stops there); an expression's value, or {"$error": text} when it fails or its
## node is freed.
func _read(track: Dictionary) -> Variant:
	var node: Variant = track["node"]
	var freed: bool = track["has_node"] and not is_instance_valid(node)
	if track.has("path"):
		if freed:
			track["done"] = true
			return {"$freed": true}
		return (node as Node).get_indexed(track["path"])
	if freed:
		return {"$error": "its node %s was freed" % track["node_path"]}
	return _evaluate(track["expression"], node if track["has_node"] else null)


## Runs expression with wait_for's inputs, and node when there is one; show_error is off, since a
## failure would log every frame (expression.cpp L1494-1508 in 4.7.2).
func _evaluate(expression: Expression, node: Node) -> Variant:
	var tree := Engine.get_main_loop() as SceneTree
	var inputs: Array = [tree.root, tree, Input, Engine]
	if node != null:
		inputs.append(node)
	var value: Variant = expression.execute(inputs, node, false)
	if expression.has_execute_failed():
		return {"$error": expression.get_error_text()}
	return value


static func _is_error(value: Variant) -> bool:
	return value is Dictionary and value.size() == 1 and value.has("$error")


## TYPE_FLOAT for a number, the vector's type for a vector, else -1.
static func _range_kind(value: Variant) -> int:
	if value is int or value is float:
		return TYPE_FLOAT
	return typeof(value) if VECTOR_COMPONENTS.has(typeof(value)) else -1


## Runs a frame of the watch from the frame signal.
func _on_frame(watch: Dictionary) -> void:
	var physics: bool = watch["physics"]
	var frame_count: int = Engine.get_physics_frames() if physics else Engine.get_process_frames()
	var delta: float = get_physics_process_delta_time() if physics else get_process_delta_time()
	advance(watch, get_tree().paused, frame_count, delta, Time.get_ticks_msec())


## One frame of watch: frame_count is the engine's frame (tick) count, delta the frame's scaled
## delta, now_ms the real time. The first frame calls params.call and stamps startFrame. An unpaused
## frame is sampled and counted on the game clock; a paused one is listed. The watch ends when its
## window is full or its deadline (the window's allowance plus the real time paused, up to
## MAX_PAUSED_MS) has passed.
func advance(watch: Dictionary, paused: bool, frame_count: int, delta: float, now_ms: int) -> void:
	if watch["state"] != "running":
		return
	var now_usec: int = usec_clock.call()
	if int(watch["start_frame"]) < 0 and not _first_frame(watch, frame_count, now_ms):
		return
	var frame: int = frame_count - int(watch["start_frame"])
	watch["frame_count"] = frame_count
	watch["frame_ms"] = floori(float(watch["clock"]["seconds"]) * 1000.0)
	if paused:
		_note_paused(watch, frame, now_ms)
		_monitors_script.stamp(watch["perf"], now_usec)
	else:
		_sample(watch, frame, now_usec)
		_time_script.advance_game_clock(watch["clock"], false, delta)
	watch["last_ms"] = now_ms
	if _window_full(watch):
		finish(watch, "", now_ms)
	elif _past_deadline(watch, now_ms):
		finish(watch, "deadline", now_ms)


## Stamps the watch's first frame and calls params.call there (Inspect's call_now, keeping its
## {value}), before anything is sampled; returns false when the call failed, which ends the watch
## with its error and lets it go as though none had started.
func _first_frame(watch: Dictionary, frame_count: int, now_ms: int) -> bool:
	watch["start_frame"] = frame_count
	watch["began_ms"] = now_ms
	watch["last_ms"] = now_ms
	var params: Dictionary = watch["params"]
	var outcome: Variant = {}
	if params.get("call") is Dictionary:
		outcome = bridge._inspect.call_now(params["call"])
	if outcome is String:
		watch["error"] = outcome
		finish(watch, "call", now_ms)
		_idle_reason = NONE_STARTED
		return false
	watch["call"] = outcome
	changed.emit()
	return true


## Lists frame as paused, joining it to the range before it, and counts the real time since the
## last frame as paused time.
static func _note_paused(watch: Dictionary, frame: int, now_ms: int) -> void:
	watch["paused_ms"] = int(watch["paused_ms"]) + now_ms - int(watch["last_ms"])
	var ranges: Array = watch["paused"]
	if not ranges.is_empty() and int(ranges[-1][1]) == frame - 1:
		ranges[-1][1] = frame
	else:
		ranges.append([frame, frame])


static func _window_full(watch: Dictionary) -> bool:
	var clock: Dictionary = watch["clock"]
	if watch["kind"] == "frames":
		return int(clock["frames"]) >= int(watch["target"])
	return floori(float(clock["seconds"]) * 1000.0) >= int(watch["target"])


static func _past_deadline(watch: Dictionary, now_ms: int) -> bool:
	var allowed: float = float(watch["deadline_ms"]) + mini(int(watch["paused_ms"]), MAX_PAUSED_MS)
	return now_ms - int(watch["began_ms"]) > allowed


## Samples every track still sampling at frame, stamped with the game time so far, then every
## monitor, which measures frame_ms to now_usec (the frame's stamp) and the sampling's own time
## from began.
func _sample(watch: Dictionary, frame: int, now_usec: int) -> void:
	var began: int = usec_clock.call()
	var game_ms: int = floori(float(watch["clock"]["seconds"]) * 1000.0)
	for track: Dictionary in watch["tracks"]:
		if not track["done"]:
			_sample_track(track, frame, game_ms)
	_monitors_script.sample(watch["perf"], frame, now_usec, began, usec_clock)


## Reads the track, adds the value to its summary, and keeps it as a change point when it differs
## from the last one kept (the first always is). A kept container is copied, since the game may
## change the one it read in place.
func _sample_track(track: Dictionary, frame: int, game_ms: int) -> void:
	var value: Variant = _read(track)
	var copy: Variant = value.duplicate(true) if value is Array or value is Dictionary else value
	track["samples"] = int(track["samples"]) + 1
	track["last"] = copy
	_add_to_range(track, value, frame)
	if track["has_kept"] and not differs(track["last_kept"], value, track["min_delta"]):
		return
	track["has_kept"] = true
	track["last_kept"] = copy
	_keep(track, [frame, game_ms, bridge._json.to_json(value)])


## Whether value is a change from last: numbers, and each component of two vectors of one type, by
## at least min_delta when it is above 0, else by more than the clock's EQUAL_TOLERANCE; anything
## else by the clock's JSON comparison (type and value, containers member by member).
func differs(last: Variant, value: Variant, min_delta: float) -> bool:
	var kind: int = _range_kind(value)
	if kind < 0 or kind != _range_kind(last):
		return not _time_script.json_equal(last, value)
	if kind == TYPE_FLOAT:
		return _moved(float(value) - float(last), min_delta)
	for index in (VECTOR_COMPONENTS[kind] as String).length():
		if _moved(float(value[index]) - float(last[index]), min_delta):
			return true
	return false


func _moved(difference: float, min_delta: float) -> bool:
	if min_delta > 0.0:
		return absf(difference) >= min_delta
	return absf(difference) > _time_script.EQUAL_TOLERANCE


## Adds a numeric value to the track's min and max (per component for a vector), from the first
## number or vector the track reads; a value of another kind is not counted.
static func _add_to_range(track: Dictionary, value: Variant, frame: int) -> void:
	var kind: int = _range_kind(value)
	if kind < 0:
		return
	if int(track["range_kind"]) < 0:
		track["range_kind"] = kind
	if kind != int(track["range_kind"]):
		return
	var components: Array = [value]
	if kind != TYPE_FLOAT:
		components = []
		for index in (VECTOR_COMPONENTS[kind] as String).length():
			components.append(value[index])
	for index in components.size():
		_extend(track, index, components[index], frame)


## Moves the track's min and max of component index to include component, first seen at frame.
static func _extend(track: Dictionary, index: int, component: Variant, frame: int) -> void:
	var mins: Array = track["min"]
	var maxs: Array = track["max"]
	if mins.size() <= index:
		mins.append(component)
		maxs.append(component)
		(track["min_at"] as Array).append(frame)
		(track["max_at"] as Array).append(frame)
		return
	if component < mins[index]:
		mins[index] = component
		track["min_at"][index] = frame
	if component > maxs[index]:
		maxs[index] = component
		track["max_at"][index] = frame


## Keeps point [frame, gameMs, value]: among the first FIRST_POINTS, else in the ring of the last
## LAST_POINTS, over its oldest once it is full.
static func _keep(track: Dictionary, point: Array) -> void:
	track["kept"] = int(track["kept"]) + 1
	var first_points: Array = track["first_points"]
	if first_points.size() < FIRST_POINTS:
		first_points.append(point)
		return
	var ring: Array = track["ring"]
	if ring.size() < LAST_POINTS:
		ring.append(point)
		return
	var head: int = track["ring_head"]
	ring[head] = point
	track["ring_head"] = (head + 1) % LAST_POINTS


## Ends watch, stopped early for stopped ("stop", "deadline", "call") or at its window's end (""):
## disconnects its frames and its signals, summarises its monitors and frees their series, and
## holds it; one that ended with an error is let go at once. The handler is bound to watch, so it
## is erased here, ending the Dictionary-Callable cycle.
func finish(watch: Dictionary, stopped: String, now_ms: int) -> void:
	var source: Signal = watch["source"]
	if source.is_connected(watch["handler"]):
		source.disconnect(watch["handler"])
	watch.erase("handler")
	_disconnect_signals(watch)
	_monitors_script.finish(watch)
	watch["state"] = "held"
	watch["stopped"] = stopped
	if int(watch["start_frame"]) >= 0:
		watch["wall_ms"] = now_ms - int(watch["began_ms"])
	if not str(watch["error"]).is_empty():
		_release(watch)
	changed.emit()


## Ends the watch that runs and answers its timeline, or the one held; refused while none runs or
## is held (saying why) and while a run holds it.
func stop() -> Dictionary:
	if _watch.is_empty():
		return {"error": _idle_reason}
	var watch: Dictionary = _watch
	if watch["run"]:
		return {"error": RUN_REFUSAL}
	if watch["state"] == "running":
		finish(watch, "stop", Time.get_ticks_msec())
	return {"result": _collect(watch)}


## Ends a running watch run as its deadline would, when params is the very Dictionary its request
## holds; its run then answers the timeline so far with stopped "deadline". Returns whether it did.
func cancel(params: Dictionary) -> bool:
	if _watch.is_empty() or _watch["state"] != "running" or not is_same(params, _watch["params"]):
		return false
	finish(_watch, "deadline", Time.get_ticks_msec())
	return true


## Drops the watch running or held when the connection ends and the bridge stays: a stop then says
## so, and a run waiting on it answers an error nobody reads.
func drop() -> void:
	if _watch.is_empty():
		return
	var watch: Dictionary = _watch
	watch["error"] = DROPPED_ERROR
	if watch["state"] == "running":
		finish(watch, "", Time.get_ticks_msec())
	# Defensive: a held watch's signals were already cut at finish.
	_disconnect_signals(watch)
	_release(watch)
	_idle_reason = DROPPED


func _release(watch: Dictionary) -> void:
	if is_same(_watch, watch):
		_watch = {}


## start's reply: {startFrame, tracks: [{name?, node?, property?}], signals?: [{node | group,
## signal, connected}], skipped?, skippedTotal?, monitors?: [{name, custom?}], call?}.
func _started(watch: Dictionary) -> Dictionary:
	var reply: Dictionary = {"startFrame": watch["start_frame"], "tracks": []}
	for track: Dictionary in watch["tracks"]:
		(reply["tracks"] as Array).append(_describe(track))
	var signals: Array = watch["signals"]
	if not signals.is_empty():
		reply["signals"] = signals.map(_describe_signal)
		_add_skipped(reply, signals)
	_monitors_script.describe(reply, watch["perf"])
	if not (watch["call"] as Dictionary).is_empty():
		reply["call"] = watch["call"]
	return reply


## A signal track as start lists it: {node | group, signal, connected}.
static func _describe_signal(track: Dictionary) -> Dictionary:
	var described: Dictionary = (track["emitter"] as Dictionary).duplicate()
	described["signal"] = track["signal"]
	described["connected"] = track["connected"]
	return described


## Adds the signal tracks' skipped group members to result, at most MAX_SKIPPED, with skippedTotal
## when there are more.
static func _add_skipped(result: Dictionary, signals: Array) -> void:
	var skipped: Array = []
	for track: Dictionary in signals:
		skipped.append_array(track["skipped"])
	if skipped.is_empty():
		return
	result["skipped"] = skipped.slice(0, MAX_SKIPPED)
	if skipped.size() > MAX_SKIPPED:
		result["skippedTotal"] = skipped.size()


## Adds the signal tracks' kept events, in emission order, every emitter's count (eventCounts), each
## track's kept and total emissions (eventTracks, which the server shares the events by) and the
## skipped members to a timeline.
static func _add_events(result: Dictionary, watch: Dictionary) -> void:
	var signals: Array = watch["signals"]
	result["events"] = watch["events"]
	result["eventCounts"] = watch["event_counts"]
	var counts: Array = []
	for track: Dictionary in signals:
		counts.append({"kept": track["kept"], "total": track["total"]})
	result["eventTracks"] = counts
	_add_skipped(result, signals)


## The timeline, letting the watch go: {startFrame, frames, gameMs, wallMs, tracks[, events,
## eventCounts, eventTracks, skipped, skippedTotal, monitors, warning, paused, stopped, call]};
## frames and gameMs count the unpaused frames sampled and their game time; the events keys only
## with signal tracks, monitors only with monitors, warning only when one applies.
func _collect(watch: Dictionary) -> Dictionary:
	var clock: Dictionary = watch["clock"]
	var result: Dictionary = {
		"startFrame": watch["start_frame"],
		"frames": clock["frames"],
		"gameMs": floori(float(clock["seconds"]) * 1000.0),
		"wallMs": watch["wall_ms"],
		"tracks": [],
	}
	for track: Dictionary in watch["tracks"]:
		(result["tracks"] as Array).append(_track_result(track))
	if not (watch["signals"] as Array).is_empty():
		_add_events(result, watch)
	_monitors_script.add_to(result, watch["perf"])
	if not (watch["paused"] as Array).is_empty():
		result["paused"] = watch["paused"]
	if not str(watch["stopped"]).is_empty():
		result["stopped"] = watch["stopped"]
	if watch["run"] and not (watch["call"] as Dictionary).is_empty():
		result["call"] = watch["call"]
	_release(watch)
	_idle_reason = COLLECTED
	return result


## A track's name, node path and property, as given and resolved.
static func _describe(track: Dictionary) -> Dictionary:
	var spec: Dictionary = track["spec"]
	var described: Dictionary = {}
	if spec.get("name") is String:
		described["name"] = spec["name"]
	if track["has_node"]:
		described["node"] = track["node_path"]
	if track.has("path"):
		described["property"] = str(spec.get("property", ""))
	return described


## A track's timeline: {name?, node?, property?, first, last, changes, points, dropped?, min?,
## max?, minAt?, maxAt?}; first, last, changes and the range cover every sample, points the kept
## ones.
func _track_result(track: Dictionary) -> Dictionary:
	var points: Array = (track["first_points"] as Array).duplicate()
	var ring: Array = track["ring"]
	var head: int = track["ring_head"]
	points.append_array(ring.slice(head) + ring.slice(0, head))
	var result: Dictionary = _describe(track)
	result["first"] = null if points.is_empty() else points[0][2]
	result["last"] = bridge._json.to_json(track["last"])
	result["changes"] = maxi(0, int(track["kept"]) - 1)
	result["points"] = points
	var dropped: int = int(track["kept"]) - points.size()
	if dropped > 0:
		result["dropped"] = dropped
	_add_range(result, track)
	return result


## Adds min, max, minAt and maxAt to a numeric track's result: numbers, or objects by component name
## for a vector.
func _add_range(result: Dictionary, track: Dictionary) -> void:
	var kind: int = track["range_kind"]
	if kind < 0:
		return
	var keys: Dictionary = {"min": "min", "max": "max", "minAt": "min_at", "maxAt": "max_at"}
	for key: String in keys:
		var values: Array = track[keys[key]]
		if kind == TYPE_FLOAT:
			result[key] = bridge._json.to_json(values[0])
			continue
		var by_name: Dictionary = {}
		var names: String = VECTOR_COMPONENTS[kind]
		for index in names.length():
			by_name[names[index]] = bridge._json.to_json(values[index])
		result[key] = by_name
