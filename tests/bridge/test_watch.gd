# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## The watch (bridge/godot_mcp_watch.gd): its window clock, change points and caps, minDelta, freed
## nodes, expression errors, signal tracks and its one-watch rule. Frames are fed to advance by
## hand, so no frame runs; the watched nodes are in no tree, so the watch names them by their
## names, and a signal's frame count and a group's members come from the test.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame, so the watch's connection can be read.
signal test_frame

## The hint a refusal carries when the source reaches for GDScript syntax Expression has none of.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NOT_GDSCRIPT_HINT := "Godot's Expression is not GDScript: it has no lambdas (func), no if/else, no is or as, no not in (write not (a in b)), and no statements; it has calls, indexing, literals and operators such as and, or, not, in, ==, !=, <, <=, >, >=, +, -, *, / and %."

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar _logger: Logger\nvar top: Node\n\n\n"
	+ "func _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)

## A game method an expression calls, which raises the game's own error.
const SHOUT_SOURCE := "extends Node2D\n\n\nfunc shout():\n\tpush_error('shout')\n\treturn 1\n"

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


func test_an_engine_error_a_called_method_raises_stays_out_of_the_feed_and_reads_null() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var logger: Logger = load_bridge_script("godot_mcp_logger.gd").new()
	rig["bridge"]._logger = logger
	var spec: Dictionary = {"name": "first", "node": "Mover", "expression": "node.get_child(0)"}
	watch.begin({"expressions": [spec], "frames": 3}, test_frame)
	var running: Dictionary = watch._watch
	var child := Node.new()
	OS.add_logger(logger)
	watch.advance(running, false, 0, 0.25, 0)
	rig["mover"].add_child(child)
	watch.advance(running, false, 1, 0.25, 10)
	OS.remove_logger(logger)
	# The runner fails a test on any engine error; the one get_child raised is expected, so it is
	# taken here and checked to be the only one.
	var raised: PackedStringArray = Engine.get_main_loop().get("_recorder").take()
	assert_eq(raised.size(), 1, "the engine raised one error: %s" % [raised])
	assert_true(raised.size() == 1 and raised[0].contains("out of bounds"), "%s" % [raised])
	assert_eq(logger.take_pending(), [[], 0], "and the bridge's logger kept none of it")
	assert_true(is_same(running["tracks"][0]["last"], child), "the track then reads the child")
	var track: Dictionary = watch.stop()["result"]["tracks"][0]
	assert_eq(track["points"][0], [0, 0, null], "the frame before the child reads null")
	_free(rig)


func test_an_error_game_code_an_expression_calls_raises_still_reaches_the_feed() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var logger: Logger = load_bridge_script("godot_mcp_logger.gd").new()
	rig["bridge"]._logger = logger
	var script := GDScript.new()
	script.source_code = SHOUT_SOURCE
	script.reload()
	rig["mover"].set_script(script)
	var specs: Array = [
		{"name": "first", "node": "Mover", "expression": "node.get_child(0)"},
		{"name": "shout", "node": "Mover", "expression": "node.shout()"},
	]
	watch.begin({"expressions": specs, "frames": 2}, test_frame)
	OS.add_logger(logger)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	OS.remove_logger(logger)
	# Both errors reach the runner, which would fail the test on them; they are expected.
	var raised: PackedStringArray = Engine.get_main_loop().get("_recorder").take()
	assert_eq(raised.size(), 2, "get_child's error and the game's: %s" % [raised])
	var entries: Array = logger.take_pending()[0]
	var messages: Array = entries.map(func(entry: Dictionary) -> String: return entry["message"])
	assert_eq(messages, ["shout"], "the game's error lands, get_child's does not")
	var tracks: Array = watch.stop()["result"]["tracks"]
	assert_eq(tracks[1]["points"], [[0, 0, 1]], "the game method's value")
	_free(rig)


func test_an_expression_track_with_text_after_its_expression_is_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var spec: Dictionary = {"name": "ternary", "expression": "true if false else false"}
	var refusal: String = watch.begin({"expressions": [spec], "frames": 1}, test_frame)
	assert_eq(
		refusal,
		(
			"The expression of track 'ternary' does not parse: text follows a complete expression, "
			+ "and Expression would ignore it. "
			+ NOT_GDSCRIPT_HINT
		),
		"the whole refusal, the hint included"
	)
	assert_eq(watch._watch, {}, "no watch begins")
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


func test_a_node_signal_is_recorded_with_its_frame_game_time_and_arguments() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("hit")
	mover.add_user_signal("ping")
	var count: Array = _count_frames(watch, 10)
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "hit"}, {"node": "Mover", "signal": "ping"}],
		"frames": 5,
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	watch.advance(running, false, 10, 0.25, 0)
	mover.emit_signal("hit", 1, "two", Vector2(3, 4))
	var started: Dictionary = watch._started(running)
	var wanted: Array = [
		{"node": "Mover", "signal": "hit", "connected": 1},
		{"node": "Mover", "signal": "ping", "connected": 1},
	]
	assert_eq(started["signals"], wanted, "start lists each signal track")
	assert_true(not started.has("skipped"), "nothing skipped")
	watch.advance(running, false, 11, 0.25, 10)
	count[0] = 11
	mover.emit_signal("ping")
	count[0] = 12
	mover.emit_signal("ping")
	var result: Dictionary = watch.stop()["result"]
	var events: Array = [
		[0, 0, "Mover", "hit", [1, "two", {"x": 3.0, "y": 4.0}], 0],
		[1, 250, "Mover", "ping", [], 1],
		[2, 500, "Mover", "ping", [], 1],
	]
	assert_eq(
		result["events"], events, "each stamped with its frame and the clock before its delta"
	)
	assert_eq(result["eventCounts"], {"Mover:hit": 1, "Mover:ping": 2}, "counts")
	var tracks: Array = [{"kept": 1, "total": 1}, {"kept": 2, "total": 2}]
	assert_eq(result["eventTracks"], tracks, "each track's counts")
	_free(rig)


func test_a_watch_without_signal_tracks_has_no_events() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	watch.begin(_rotation(1), test_frame)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	var started: Dictionary = watch._started(watch._watch)
	var result: Dictionary = watch.stop()["result"]
	assert_true(not started.has("signals"), "no signals in start's reply")
	for key: String in ["events", "eventCounts", "eventTracks", "skipped"]:
		assert_true(not result.has(key), "no %s" % key)
	_free(rig)


func test_a_missing_node_or_signal_is_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var lacking: Dictionary = {"signals": [{"node": "Mover", "signal": "nope"}], "frames": 1}
	assert_eq(watch.begin(lacking, test_frame), "Mover has no signal 'nope'", "a missing signal")
	var missing: Dictionary = {"signals": [{"node": "Ghost", "signal": "hit"}], "frames": 1}
	assert_true(watch.begin(missing, test_frame).contains("Ghost"), "a missing node")
	assert_eq(watch._watch, {}, "no watch begins")
	_free(rig)


func test_a_group_track_connects_the_members_with_the_signal_and_skips_the_rest() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var cards: Array = _in_group(rig, "cards", 2, "dealt")
	var plain := Node.new()
	plain.name = "Plain"
	plain.add_to_group("cards")
	rig["top"].add_child(plain)
	var params: Dictionary = {"signals": [{"group": "cards", "signal": "dealt"}], "frames": 5}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	cards[1].emit_signal("dealt", 7)
	cards[0].emit_signal("dealt", 8)
	var skipped: Array = [{"node": "Plain", "reason": "no signal 'dealt'"}]
	var started: Dictionary = watch._started(watch._watch)
	assert_eq(started["signals"], [{"group": "cards", "signal": "dealt", "connected": 2}], "two")
	assert_eq(started["skipped"], skipped, "the member without it")
	var result: Dictionary = watch.stop()["result"]
	var nodes: Array = result["events"].map(func(event: Array) -> Variant: return event[2])
	assert_eq(nodes, ["Card1", "Card0"], "in emission order")
	assert_eq(result["skipped"], skipped, "skipped again in the timeline")
	var none: Dictionary = {"signals": [{"group": "cards", "signal": "nope"}], "frames": 1}
	assert_eq(watch.begin(none, test_frame), "no node in group 'cards' has signal 'nope'", "")
	var empty: Dictionary = {"signals": [{"group": "nobody", "signal": "dealt"}], "frames": 1}
	assert_eq(watch.begin(empty, test_frame), "group 'nobody' has no nodes", "an empty group")
	_free(rig)


func test_signal_tracks_connecting_over_200_nodes_are_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var many: Array = _in_group(rig, "many", 201, "went")
	var params: Dictionary = {"signals": [{"group": "many", "signal": "went"}], "frames": 1}
	var refusal: String = watch.begin(params, test_frame)
	assert_eq(refusal, _watch_script.CONNECTIONS_REFUSAL % 201, "201 nodes")
	assert_true(refusal.begins_with("the signal tracks connect 201 nodes; at most 200"), refusal)
	assert_eq(many[0].get_signal_connection_list("went"), [], "nothing connected")
	assert_eq(watch._watch, {}, "no watch begins")
	_free(rig)


func test_an_emission_while_paused_or_before_the_first_sample_is_recorded() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	var count: Array = _count_frames(watch, 40)
	watch.begin({"signals": [{"node": "Mover", "signal": "ping"}], "frames": 5}, test_frame)
	var running: Dictionary = watch._watch
	mover.emit_signal("ping")
	watch.advance(running, false, 40, 0.25, 0)
	watch.advance(running, true, 41, 0.25, 10)
	count[0] = 41
	mover.emit_signal("ping")
	var events: Array = watch.stop()["result"]["events"]
	var stamps: Array = events.map(func(event: Array) -> Array: return event.slice(0, 2))
	assert_eq(stamps, [[0, 0], [1, 250]], "frame 0 before the first sample; a paused frame too")
	_free(rig)


func test_a_signal_track_keeps_its_first_300_events_and_counts_the_rest() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	_count_frames(watch, 0)
	watch.begin({"signals": [{"node": "Mover", "signal": "ping"}], "frames": 5}, test_frame)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	for index in 350:
		mover.emit_signal("ping", index)
	var result: Dictionary = watch.stop()["result"]
	assert_eq(result["events"].size(), 300, "the first 300")
	assert_eq(result["events"][299][4], [299], "in order")
	assert_eq(result["eventTracks"], [{"kept": 300, "total": 350}], "kept and total")
	assert_eq(result["eventCounts"], {"Mover:ping": 350}, "every emission counted")
	_free(rig)


func test_a_freed_emitter_simply_stops() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	_count_frames(watch, 0)
	watch.begin({"signals": [{"node": "Mover", "signal": "ping"}], "frames": 5}, test_frame)
	watch.advance(watch._watch, false, 0, 0.25, 0)
	mover.emit_signal("ping")
	mover.free()
	watch.advance(watch._watch, false, 1, 0.25, 10)
	var result: Dictionary = watch.stop()["result"]
	assert_eq(result["events"].size(), 1, "the one emission")
	assert_eq(result["eventCounts"], {"Mover:ping": 1}, "counted")
	_free(rig)


func test_stop_cancel_and_drop_disconnect_every_signal() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	for ending: String in ["stop", "cancel", "drop running", "drop held", "window"]:
		var params: Dictionary = {"signals": [{"node": "Mover", "signal": "ping"}], "frames": 1}
		watch.begin(params, test_frame)
		var running: Dictionary = watch._watch
		var handler: Callable = running["connections"][0][2]
		assert_true(mover.is_connected("ping", handler), "%s: connected" % ending)
		if ending in ["drop held", "window"]:
			watch.advance(running, false, 0, 0.25, 0)
		match ending:
			"stop", "window":
				watch.stop()
			"cancel":
				watch.cancel(params)
				assert_true(not mover.is_connected("ping", handler), "cancel itself disconnects")
				watch.stop()
			_:
				watch.drop()
		assert_true(not mover.is_connected("ping", handler), "%s: disconnected" % ending)
		assert_eq(running["connections"], [], "%s: let go" % ending)
	_free(rig)


func test_a_node_two_tracks_name_is_connected_and_counted_once() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var mover: Node = rig["mover"]
	mover.add_user_signal("ping")
	mover.add_to_group("movers")
	_in_group(rig, "movers", 0, "ping")
	_count_frames(watch, 0)
	var params: Dictionary = {
		"signals": [{"node": "Mover", "signal": "ping"}, {"group": "movers", "signal": "ping"}],
		"frames": 5,
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, false, 0, 0.25, 0)
	assert_eq(mover.get_signal_connection_list("ping").size(), 1, "one connection")
	var started: Dictionary = watch._started(watch._watch)
	var connected: Array = started["signals"].map(
		func(track: Dictionary) -> int: return track["connected"]
	)
	assert_eq(connected, [1, 0], "the first track that names it connects it")
	mover.emit_signal("ping")
	var result: Dictionary = watch.stop()["result"]
	assert_eq(result["eventCounts"], {"Mover:ping": 1}, "counted once")
	assert_eq(result["events"], [[0, 0, "Mover", "ping", [], 0]], "one event, the first track's")
	_free(rig)


## Sets the watch's frame counter to read count[0], starting at first, and returns count.
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
