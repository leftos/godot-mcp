extends "res://gd_test.gd"
## The watch (bridge/godot_mcp_watch.gd): its window clock, change points and caps, minDelta, freed
## nodes, expression errors and its one-watch rule. Frames are fed to advance by hand, so no frame
## runs; the watched nodes are in no tree, so the watch names them by their names.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame, so the watch's connection can be read.
signal test_frame

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar top: Node\n\n\n"
	+ "func _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)

var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")


func test_the_window_counts_only_unpaused_frames_and_lists_the_paused_ones() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	assert_eq(watch.begin(_rotation(3), test_frame), "", "begins")
	var running: Dictionary = watch._watch
	watch.advance(running, false, 100, 0.25, 1000)
	watch.advance(running, true, 101, 0.25, 1010)
	watch.advance(running, true, 102, 0.25, 1020)
	watch.advance(running, false, 103, 0.25, 1030)
	assert_eq(running["state"], "running", "two unpaused frames of three")
	watch.advance(running, false, 104, 0.25, 1040)
	assert_eq(test_frame.get_connections().size(), 0, "a full window lets go of its frames")
	assert_true(not running.has("handler"), "its handler, bound to it, is let go")
	var result: Dictionary = watch.stop()["result"]
	assert_eq(result["startFrame"], 100, "the first frame's number")
	assert_eq(result["frames"], 3, "the unpaused frames")
	assert_eq(result["gameMs"], 750, "three frames of 250 ms")
	assert_eq(result["wallMs"], 40, "from the first frame to the last")
	assert_eq(result["paused"], [[1, 2]], "the paused frames, joined")
	assert_true(not result.has("stopped"), "the window ran to its end")
	assert_eq(result["tracks"][0]["points"], [[0, 0, 0.0]], "a still value is one point")
	assert_eq(result["tracks"][0]["changes"], 0, "no change")
	_free(rig)


func test_a_freed_node_is_recorded_once_and_its_track_stops() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "position"}], "frames": 10
	}
	watch.begin(params, test_frame)
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.25, 0)
	rig["mover"].free()
	watch.advance(running, false, 1, 0.25, 10)
	watch.advance(running, false, 2, 0.25, 20)
	var track: Dictionary = watch.stop()["result"]["tracks"][0]
	var freed: Dictionary = {"$freed": true}
	assert_eq(track["points"], [[0, 0, {"x": 0.0, "y": 0.0}], [1, 250, freed]], "freed once")
	assert_eq(track["last"], freed, "the last sample")
	assert_eq(track["changes"], 1, "one change")
	_free(rig)


func test_a_track_keeps_its_first_200_and_last_50_points_and_counts_the_rest() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "z_index"}], "frames": 300
	}
	watch.begin(params, test_frame)
	var running: Dictionary = watch._watch
	for frame in 300:
		rig["mover"].z_index = frame
		watch.advance(running, false, frame, 0.25, frame)
	var track: Dictionary = watch.stop()["result"]["tracks"][0]
	var points: Array = track["points"]
	assert_eq(points.size(), 250, "200 and 50")
	assert_eq(points[199][2], 199, "the last of the first 200")
	assert_eq(points[200][2], 250, "the first of the last 50")
	assert_eq(points[249][2], 299, "the last")
	assert_eq(track["dropped"], 50, "the ones between")
	assert_eq(track["changes"], 299, "every change counted")
	assert_eq(
		[track["min"], track["minAt"], track["max"], track["maxAt"]], [0, 0, 299, 299], "range"
	)
	_free(rig)


func test_min_delta_keeps_a_float_an_int_and_a_vector_only_when_they_move_that_far() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties":
		[
			{"node": "Mover", "property": "rotation", "minDelta": 0.5},
			{"node": "Mover", "property": "z_index", "minDelta": 2},
			{"node": "Mover", "property": "position", "minDelta": 1.0},
		],
		"frames": 5,
	}
	assert_eq(watch.begin(params, test_frame), "", "numeric tracks take minDelta")
	var running: Dictionary = watch._watch
	var rotations: Array = [0.0, 0.3, 0.6, 0.7, 1.2]
	var layers: Array = [0, 1, 2, 3, 5]
	var positions: Array = [
		Vector2(0, 0), Vector2(0.5, 0), Vector2(0.5, 0.9), Vector2(1.2, 0.5), Vector2(1.2, 1.6)
	]
	for frame in 5:
		rig["mover"].rotation = rotations[frame]
		rig["mover"].z_index = layers[frame]
		rig["mover"].position = positions[frame]
		watch.advance(running, false, frame, 0.25, frame)
	var tracks: Array = watch.stop()["result"]["tracks"]
	assert_eq(_frames(tracks[0]), [0, 2, 4], "a float moved by 0.5 or more")
	assert_eq(_frames(tracks[1]), [0, 2, 4], "an int moved by 2 or more")
	assert_eq(_frames(tracks[2]), [0, 3, 4], "a vector with a component moved by 1 or more")
	assert_eq(tracks.map(func(track: Dictionary) -> int: return track["changes"]), [2, 2, 2], "")
	assert_eq(tracks[1]["last"], 5, "last covers every sample")
	assert_eq(tracks[2]["maxAt"], {"x": 3, "y": 4}, "a vector's range is per component")
	_free(rig)


func test_min_delta_is_refused_on_a_bool_track() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "visible", "minDelta": 1}], "frames": 5
	}
	var refusal: String = watch.begin(params, test_frame)
	assert_eq(refusal, _watch_script.MIN_DELTA_REFUSAL % ["Mover:visible", "bool"], "")
	assert_eq(watch._watch, {}, "no watch begins")
	_free(rig)


func test_an_expression_error_is_recorded_once_and_the_track_recovers() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var track_spec: Dictionary = {
		"name": "pick", "node": "Mover", "expression": "[1, 2][node.z_index]"
	}
	assert_eq(watch.begin({"expressions": [track_spec], "frames": 4}, test_frame), "", "parses")
	var running: Dictionary = watch._watch
	var layers: Array = [0, 5, 5, 1]
	for frame in 4:
		rig["mover"].z_index = layers[frame]
		watch.advance(running, false, frame, 0.25, frame)
	var track: Dictionary = watch.stop()["result"]["tracks"][0]
	var values: Array = track["points"].map(func(point: Array) -> Variant: return point[2])
	assert_eq(_frames(track), [0, 1, 3], "the error once, then the recovery")
	assert_eq([values[0], values[2]], [1, 2], "the values around it")
	assert_true(values[1] is Dictionary and values[1].has("$error"), "the error: %s" % [values[1]])
	assert_eq(track["name"], "pick", "keyed by its name")
	_free(rig)


func test_one_watch_at_a_time_and_a_held_one_until_it_is_collected() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	watch.begin(_rotation(1), test_frame)
	assert_eq(watch.begin(_rotation(1), test_frame), _watch_script.RUNNING_REFUSAL, "running")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	assert_eq(watch.begin(_rotation(1), test_frame), _watch_script.HELD_REFUSAL, "held")
	assert_true(watch.stop().has("result"), "collected")
	assert_eq(watch.begin(_rotation(1), test_frame), "", "a new one begins")
	_free(rig)


func test_a_stop_with_no_watch_is_refused_saying_why() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	assert_eq(watch.stop()["error"], _watch_script.NONE_STARTED, "none started")
	watch.begin(_rotation(5), test_frame)
	var result: Dictionary = watch.stop()["result"]
	assert_eq(result["stopped"], "stop", "a stop before the window ends says so")
	assert_eq(watch.stop()["error"], _watch_script.COLLECTED, "already returned")
	_free(rig)


func test_a_detach_drops_the_watch() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	watch.begin(_rotation(5), test_frame)
	watch.drop()
	assert_eq(test_frame.get_connections().size(), 0, "its frames are let go")
	assert_eq(watch.stop()["error"], _watch_script.DROPPED, "a stop says it was dropped")
	assert_eq(watch.begin(_rotation(5), test_frame), "", "a new one begins")
	_free(rig)


func test_last_keeps_a_containers_contents_as_sampled() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var list: Array = [1]
	rig["mover"].set_meta("list", list)
	var spec: Dictionary = {"name": "list", "node": "Mover", "expression": "node.get_meta('list')"}
	watch.begin({"expressions": [spec], "frames": 1}, test_frame)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	list.append(2)
	var track: Dictionary = watch.stop()["result"]["tracks"][0]
	assert_eq(track["last"], [1], "the game's later change is not in last")
	assert_eq(track["first"], [1], "nor in first")
	_free(rig)


func test_an_expression_runs_once_a_sampled_frame() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var spec: Dictionary = {
		"name": "count",
		"node": "Mover",
		"expression": "node.set_meta('runs', node.get_meta('runs', 0) + 1)",
	}
	watch.begin({"expressions": [spec], "frames": 3}, test_frame)
	assert_eq(rig["mover"].get_meta("runs", 0), 0, "not run when the watch begins")
	for frame in 3:
		watch.advance(watch._watch, false, frame, 0.25, frame)
	assert_eq(rig["mover"].get_meta("runs", 0), 3, "once in each of three frames")
	watch.stop()
	_free(rig)


func test_start_lists_a_named_tracks_node_and_property() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "rotation", "name": "turn"}],
		"expressions": [{"name": "kids", "node": "Mover", "expression": "node.get_child_count()"}],
		"frames": 5,
	}
	watch.begin(params, test_frame)
	watch.advance(watch._watch, false, 7, 0.25, 0)
	var started: Dictionary = watch._started(watch._watch)
	assert_eq(started["startFrame"], 7, "the first frame's number")
	var wanted: Array = [
		{"name": "turn", "node": "Mover", "property": "rotation"}, {"name": "kids", "node": "Mover"}
	]
	assert_eq(started["tracks"], wanted, "each track's name beside its resolved node")
	watch.stop()
	_free(rig)


func test_a_start_that_a_stop_or_a_cancel_ends_before_its_first_frame_is_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var stopped: Dictionary = {}
	_start_into(watch, _rotation(5), false, test_frame, stopped)
	assert_true(watch.stop().has("result"), "the stop collects it")
	assert_eq(stopped, {"error": _watch_script.ENDED_EARLY}, "the start is refused")
	var params: Dictionary = _rotation(5)
	var cancelled: Dictionary = {}
	_start_into(watch, params, false, test_frame, cancelled)
	assert_true(watch.cancel(params), "a cancel ends it")
	assert_eq(cancelled, {"error": _watch_script.ENDED_EARLY}, "the start is refused")
	assert_eq(watch.stop()["error"], _watch_script.NONE_STARTED, "and nothing is held")
	_free(rig)


func test_a_failed_call_ends_the_watch_as_though_none_had_started() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	watch.begin(_rotation(1), test_frame)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	watch.stop()
	var params: Dictionary = _rotation(5)
	params["call"] = {"node": "Nope", "method": "go"}
	var outcome: Dictionary = {}
	_start_into(watch, params, false, test_frame, outcome)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	assert_true(str(outcome.get("error", "")).contains("Nope"), "the call's failure: %s" % outcome)
	assert_eq(test_frame.get_connections().size(), 0, "its frames are let go")
	assert_eq(watch.stop()["error"], _watch_script.NONE_STARTED, "a stop says none started")
	assert_eq(watch.begin(_rotation(1), test_frame), "", "the next start begins")
	_free(rig)


func test_a_cancel_ends_its_own_watch_as_its_deadline_would() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = _rotation(5)
	watch.begin(params, test_frame)
	assert_true(not watch.cancel(_rotation(5)), "an equal request that is not its own")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	assert_true(watch.cancel(params), "its own request")
	assert_eq(test_frame.get_connections().size(), 0, "its frames are let go")
	var result: Dictionary = watch.stop()["result"]
	assert_eq([result["stopped"], result["frames"]], ["deadline", 1], "as a deadline ends it")
	_free(rig)


func test_the_deadline_grows_by_the_paused_time_up_to_its_cap() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = _rotation(100)
	params["deadlineMs"] = 1000
	watch.begin(params, test_frame)
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.01, 0)
	watch.advance(running, true, 1, 0.01, 400)
	watch.advance(running, false, 2, 0.01, 1400)
	assert_eq(running["state"], "running", "1000 ms and the 400 ms paused")
	watch.advance(running, false, 3, 0.01, 1401)
	assert_eq(watch.stop()["result"]["stopped"], "deadline", "past both")
	var long_paused: Dictionary = {"deadline_ms": 1000.0, "paused_ms": 700000, "began_ms": 0}
	assert_true(not _watch_script._past_deadline(long_paused, 601000), "paused time up to 600 s")
	assert_true(_watch_script._past_deadline(long_paused, 601001), "and no more")
	_free(rig)


func test_a_game_ms_window_ends_on_the_game_clock() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "rotation"}], "gameMs": 500
	}
	watch.begin(params, test_frame)
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.25, 0)
	assert_eq(running["state"], "running", "250 ms of 500")
	watch.advance(running, false, 1, 0.25, 10)
	var result: Dictionary = watch.stop()["result"]
	assert_eq([result["frames"], result["gameMs"]], [2, 500], "two frames of 250 ms")
	assert_true(not result.has("stopped"), "the window ran to its end")
	_free(rig)


func test_a_run_refuses_a_stop_and_a_second_watch_and_answers_at_its_end() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var outcome: Dictionary = {}
	_start_into(watch, _rotation(2), true, test_frame, outcome)
	assert_eq(watch.stop()["error"], _watch_script.RUN_REFUSAL, "a stop")
	assert_eq(watch.begin(_rotation(2), test_frame), _watch_script.RUN_REFUSAL, "a second watch")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	assert_true(outcome.is_empty(), "a run waits past its first frame")
	watch.advance(watch._watch, false, 1, 0.25, 10)
	assert_eq(outcome.get("result", {}).get("frames"), 2, "the run answers its timeline")
	_free(rig)


func test_unit_physics_samples_on_the_physics_frame() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var tree := Engine.get_main_loop() as SceneTree
	var params: Dictionary = _rotation(5)
	params["unit"] = "physics"
	var outcome: Dictionary = {}
	_start_into(watch, params, false, watch.frame_source(params), outcome)
	var handler: Callable = watch._watch["handler"]
	assert_true(tree.physics_frame.is_connected(handler), "on physics_frame")
	assert_true(not tree.process_frame.is_connected(handler), "not on process_frame")
	assert_true(watch._watch["physics"], "counting physics ticks")
	watch.stop()
	assert_true(not tree.physics_frame.is_connected(handler), "a stop lets go")
	_free(rig)


## Starts a watch through _start, which answers into outcome once it resumes.
func _start_into(
	watch: Node, params: Dictionary, run: bool, source: Signal, outcome: Dictionary
) -> void:
	outcome.merge(await watch._start(params, run, source))


func _rotation(frames: int) -> Dictionary:
	return {"properties": [{"node": "Mover", "property": "rotation"}], "frames": frames}


static func _frames(track: Dictionary) -> Array:
	return track["points"].map(func(point: Array) -> Variant: return point[0])


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
	return {"top": top, "mover": mover, "bridge": bridge, "watch": watch}


func _free(rig: Dictionary) -> void:
	rig["watch"].drop()
	rig["watch"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()
