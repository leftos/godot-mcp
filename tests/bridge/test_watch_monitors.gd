extends "res://gd_test.gd"
## The watch's monitors (bridge/godot_mcp_watch_monitors.gd, driven through godot_mcp_watch.gd):
## the built-in name table, frame_ms from an injected microsecond clock, custom monitors, the
## summaries' percentiles, budget and spikes, and the warnings. Frames are fed to advance by hand,
## so no frame runs or draws and a built-in monitor holds still.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame.
signal test_frame

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar top: Node\n\n\n"
	+ "func _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)
## An inspect stand-in whose call_now (options.call) takes 200 ms of the clock it is given.
const SLOW_CALL_SOURCE := (
	"extends Node\n\nvar clock: Array\n\n\n"
	+ "func call_now(_params: Dictionary) -> Variant:\n"
	+ "\tclock[0] += 200000\n"
	+ '\treturn {"value": null}\n'
)
const SERIES_ID := "gdtest/series"
const NO_MONITOR := (
	"No monitor 'gdtest/missing': not one of Godot's 59 built-in monitors (such as object/nodes "
	+ "or raster/total_draw_calls), not frame_ms, and no custom monitor has that id; a game "
	+ "registers one with Performance.add_custom_monitor."
)
const NOT_A_NUMBER := (
	"Custom monitor 'gdtest/series' returned a String, not a number; watch a monitor whose "
	+ "callable returns an int or float."
)
const COST_WARNING := (
	"sampling took 3.00 ms a frame on average (1 track), which slows the frames it watches; watch "
	+ "fewer tracks"
)
const MOVIE_WARNING := (
	"the session records with Movie Maker, so frame_ms measures how fast frames render, not what "
	+ "a player sees"
)
const RASTER_WARNING := (
	"no frame was drawn during the watch (minimized, headless or low-processor mode), so "
	+ "raster/* monitors repeat the last drawn frame's values"
)

var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")
var _monitors_script: GDScript = load_bridge_script("godot_mcp_watch_monitors.gd")


func test_the_name_table_has_one_entry_per_monitor_constant_in_enum_order() -> void:
	var constants: Array = Array(ClassDB.class_get_enum_constants("Performance", "Monitor"))
	constants.erase("MONITOR_MAX")
	var names: Dictionary = _monitors_script.NAMES
	assert_eq(names.keys(), constants, "one entry per Performance.Monitor constant, in enum order")


func test_frame_ms_includes_the_time_options_call_takes() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var now: Array = [0]
	watch.usec_clock = func() -> int: return now[0]
	var slow_call: Node = _node_from(SLOW_CALL_SOURCE)
	slow_call.clock = now
	rig["bridge"]._inspect.free()
	rig["bridge"]._inspect = slow_call
	var params: Dictionary = {
		"monitors": ["frame_ms"], "frames": 2, "call": {"node": "Mover", "method": "go"}
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.016, 0)
	now[0] += 16000
	watch.advance(running, false, 1, 0.016, 1)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	assert_eq([monitor["max"], monitor["maxAt"]], [216.0, 1], "the call's 200 ms and 16 ms after")
	_free(rig)


func test_a_watch_that_starts_paused_reads_its_first_frame_from_the_last_paused_one() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var now: Array = [0]
	watch.usec_clock = func() -> int: return now[0]
	var params: Dictionary = {"monitors": ["frame_ms"], "budgetMs": 25.0, "frames": 2}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	var frames: Array = [[true, 0], [true, 50000], [false, 66000], [false, 82000]]
	for index in frames.size():
		now[0] = frames[index][1]
		watch.advance(running, frames[index][0], index, 0.016, index)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	assert_eq(monitor["samples"], 2, "both unpaused frames read")
	assert_eq([monitor["max"], monitor["maxAt"]], [16.0, 2], "16 ms after the last paused frame")
	_free(rig)


func test_frame_ms_under_movie_maker_warns() -> void:
	var rig: Dictionary = _rig()
	rig["watch"].movie_path = func() -> String: return "clip.avi"
	var timeline: Dictionary = _timeline(rig, {"monitors": ["frame_ms"]}, 2)
	assert_eq(timeline["warning"], MOVIE_WARNING, "frame_ms measures render speed")
	_free(rig)


func test_a_raster_monitor_with_no_frame_drawn_warns() -> void:
	var rig: Dictionary = _rig()
	var timeline: Dictionary = _timeline(rig, {"monitors": ["raster/total_draw_calls"]}, 2)
	assert_eq(timeline["warning"], RASTER_WARNING, "two samples, no frame drawn between")
	_free(rig)


func test_two_warnings_are_joined() -> void:
	var rig: Dictionary = _rig()
	rig["watch"].movie_path = func() -> String: return "clip.avi"
	var params: Dictionary = {"monitors": ["frame_ms", "raster/total_draw_calls"]}
	var timeline: Dictionary = _timeline(rig, params, 2)
	assert_eq(timeline["warning"], MOVIE_WARNING + "; " + RASTER_WARNING, "both, in order")
	_free(rig)


func test_a_built_in_name_wins_over_a_custom_id_of_that_name() -> void:
	var rig: Dictionary = _rig()
	Performance.add_custom_monitor("object/nodes", func() -> Variant: return "custom")
	var timeline: Dictionary = _timeline(rig, {"monitors": ["object/nodes"]}, 1)
	Performance.remove_custom_monitor("object/nodes")
	var monitor: Dictionary = timeline["monitors"][0]
	assert_eq(monitor["samples"], 1, "read")
	assert_true(not monitor.has("custom") and not monitor.has("nonNumeric"), "the built-in one")
	_free(rig)


func test_a_custom_monitor_removed_mid_watch_is_counted_as_non_numeric() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	Performance.add_custom_monitor(SERIES_ID, func() -> Variant: return 4)
	assert_eq(watch.begin({"monitors": [SERIES_ID], "frames": 4}, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	for frame in 4:
		if frame == 2:
			Performance.remove_custom_monitor(SERIES_ID)
		watch.advance(running, false, frame, 0.016, frame)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	assert_eq([monitor["samples"], monitor["nonNumeric"]], [2, 2], "read twice, then gone twice")
	_free(rig)


func test_equal_readings_are_spikes_earliest_frame_first() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var reading: Array = [7]
	Performance.add_custom_monitor(SERIES_ID, func() -> Variant: return reading[0])
	assert_eq(watch.begin({"monitors": [SERIES_ID], "frames": 22}, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	for frame in 22:
		reading[0] = 9 if frame in [10, 15] else 7
		watch.advance(running, false, frame, 0.016, frame)
	var spikes: Array = watch.stop()["result"]["monitors"][0]["spikes"]
	Performance.remove_custom_monitor(SERIES_ID)
	assert_eq(spikes.size(), 20, "at most 20")
	assert_eq(spikes.slice(0, 3), [[10, 9], [15, 9], [0, 7]], "highest, then earliest")
	assert_eq(spikes[-1], [19, 7], "the earliest of the equal lowest ones")
	_free(rig)


func test_frame_ms_reads_each_interval_and_a_pause_is_no_reading() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var now: Array = [0]
	watch.usec_clock = func() -> int: return now[0]
	var params: Dictionary = {"monitors": ["frame_ms"], "budgetMs": 25.0, "frames": 4}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	var frames: Array = [
		[false, 0], [false, 16000], [true, 20000], [true, 500000], [false, 516000], [false, 616000]
	]
	for index in frames.size():
		now[0] = frames[index][1]
		watch.advance(running, frames[index][0], index, 0.016, index)
	var timeline: Dictionary = watch.stop()["result"]
	var monitor: Dictionary = timeline["monitors"][0]
	assert_eq(
		monitor["samples"], 3, "every unpaused frame but the first, which has no stamp before"
	)
	assert_eq(monitor["p50"], 16.0, "the frame after the pause reads from the last paused frame")
	assert_eq([monitor["max"], monitor["maxAt"]], [100.0, 5], "the longest interval and its frame")
	assert_eq(monitor["mean"], 44.0, "the mean")
	assert_eq(monitor["over"], {"budget": 25.0, "count": 1, "frames": 3}, "one reading over")
	assert_eq(monitor["spikes"], [[5, 100.0]], "the reading over the budget")
	assert_true(not timeline.has("warning"), "a quick sampler warns of nothing")
	_free(rig)


func test_a_custom_monitor_is_read_by_id_with_nearest_rank_percentiles() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var reading: Array = [0]
	Performance.add_custom_monitor(SERIES_ID, func() -> Variant: return reading[0])
	var series: Array = [7, 3, 20, 1, 15, 9, 12, 18, 4, 6, 11, 2, 19, 8, 14, 5, 17, 10, 13, 16]
	assert_eq(watch.begin({"monitors": [SERIES_ID], "frames": 20}, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	for frame in series.size():
		reading[0] = series[frame]
		watch.advance(running, false, frame, 0.016, frame)
	var started: Dictionary = watch._started(running)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	Performance.remove_custom_monitor(SERIES_ID)
	assert_eq(started["monitors"], [{"name": SERIES_ID, "custom": true}], "start lists it")
	assert_true(monitor["custom"], "a custom monitor says so")
	assert_eq([monitor["p50"], monitor["p95"], monitor["p99"]], [10, 19, 20], "nearest rank")
	assert_true(monitor["p50"] is int and monitor["max"] is int, "ints stay ints")
	assert_eq([monitor["max"], monitor["maxAt"], monitor["mean"]], [20, 2, 10.5], "max and mean")
	var spikes: Array = monitor["spikes"]
	assert_eq(spikes.size(), 20, "the highest readings, all 20 here")
	assert_eq(spikes.slice(0, 3), [[2, 20], [12, 19], [7, 18]], "highest first")
	_free(rig)


func test_frame_ms_spikes_are_the_readings_over_the_budget_highest_first_and_at_most_20() -> void:
	var rig: Dictionary = _rig()
	var monitor: Dictionary = _frame_ms(rig, range(1, 41), {"budgetMs": 10.0})
	assert_eq(monitor["over"], {"budget": 10.0, "count": 30, "frames": 40}, "30 of 40 over")
	var spikes: Array = monitor["spikes"]
	assert_eq(spikes.size(), 20, "at most 20")
	assert_eq([spikes[0], spikes[19]], [[40, 40.0], [21, 21.0]], "the highest, highest first")
	var quiet: Dictionary = _frame_ms(rig, [5, 6], {"budgetMs": 10.0})
	assert_eq(quiet["spikes"], [], "none over the budget is an empty list")
	assert_eq(quiet["over"]["count"], 0, "none over")
	_free(rig)


func test_a_constant_built_in_series_has_no_spikes() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	assert_eq(watch.begin({"monitors": ["object/nodes"], "frames": 3}, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	for frame in 3:
		watch.advance(running, false, frame, 0.016, frame)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	assert_eq(monitor["samples"], 3, "three readings")
	assert_eq(monitor["p50"], monitor["max"], "one value")
	assert_true(not monitor.has("spikes"), "no spikes in a constant series")
	assert_true(not monitor.has("custom") and not monitor.has("over"), "a built-in monitor")
	_free(rig)


func test_a_name_that_is_no_monitor_is_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	assert_eq(watch.begin({"monitors": ["gdtest/missing"]}, test_frame), NO_MONITOR, "refused")
	assert_true(watch._watch.is_empty(), "no watch starts")
	_free(rig)


func test_a_custom_monitor_whose_first_read_is_not_a_number_is_refused() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	Performance.add_custom_monitor(SERIES_ID, func() -> Variant: return "fast")
	var refusal: String = watch.begin({"monitors": [SERIES_ID]}, test_frame)
	Performance.remove_custom_monitor(SERIES_ID)
	assert_eq(refusal, NOT_A_NUMBER, "refused naming the type")
	_free(rig)


func test_a_later_non_numeric_read_is_skipped_and_counted() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var reading: Array = [1]
	Performance.add_custom_monitor(SERIES_ID, func() -> Variant: return reading[0])
	assert_eq(watch.begin({"monitors": [SERIES_ID], "frames": 3}, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	var series: Array = [1, "slow", 3.5]
	for frame in series.size():
		reading[0] = series[frame]
		watch.advance(running, false, frame, 0.016, frame)
	var monitor: Dictionary = watch.stop()["result"]["monitors"][0]
	Performance.remove_custom_monitor(SERIES_ID)
	assert_eq([monitor["samples"], monitor["nonNumeric"]], [2, 1], "two numbers, one skipped")
	assert_eq([monitor["max"], monitor["maxAt"]], [3.5, 2], "the numbers only")
	_free(rig)


func test_the_default_budget_is_one_and_a_half_frames_at_the_frame_cap_or_at_60() -> void:
	var rig: Dictionary = _rig()
	var max_fps: int = Engine.max_fps
	Engine.max_fps = 40
	var capped: Dictionary = _frame_ms(rig, [10], {})
	Engine.max_fps = 0
	var uncapped: Dictionary = _frame_ms(rig, [10], {})
	Engine.max_fps = max_fps
	assert_eq(capped["over"]["budget"], 37.5, "1.5 frames at 40 fps")
	assert_eq(uncapped["over"]["budget"], 25.0, "1.5 frames at 60 fps with no cap")
	_free(rig)


func test_a_sampler_over_2_ms_a_frame_warns() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var now: Array = [0]
	watch.usec_clock = func() -> int:
		now[0] += 3000
		return now[0]
	var params: Dictionary = {
		"properties": [{"node": "Mover", "property": "rotation"}], "frames": 2
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.016, 0)
	watch.advance(running, false, 1, 0.016, 1)
	var timeline: Dictionary = watch.stop()["result"]
	assert_eq(timeline["warning"], COST_WARNING, "3 ms a sampled frame")
	assert_true(not timeline.has("monitors"), "no monitors")
	_free(rig)


func test_a_monitor_with_no_sampled_frame_has_only_its_count() -> void:
	var rig: Dictionary = _rig()
	var watch: Node = rig["watch"]
	var params: Dictionary = {
		"monitors": ["frame_ms", "object/nodes"], "budgetMs": 25.0, "frames": 5
	}
	assert_eq(watch.begin(params, test_frame), "", "begins")
	watch.advance(watch._watch, true, 0, 0.016, 0)
	var monitors: Array = watch.stop()["result"]["monitors"]
	var over: Dictionary = {"budget": 25.0, "count": 0, "frames": 0}
	assert_eq(
		monitors,
		[{"name": "frame_ms", "samples": 0, "over": over}, {"name": "object/nodes", "samples": 0}],
		"no percentiles, maximum, mean or spikes"
	)
	_free(rig)


## The frame_ms summary of a watch whose frames each begin intervals[i] ms after the one before,
## the first at 0, none paused; params adds to its request.
func _frame_ms(rig: Dictionary, intervals: Array, params: Dictionary) -> Dictionary:
	var watch: Node = rig["watch"]
	var now: Array = [0]
	watch.usec_clock = func() -> int: return now[0]
	var request: Dictionary = {"monitors": ["frame_ms"], "frames": intervals.size() + 1}
	request.merge(params)
	assert_eq(watch.begin(request, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	watch.advance(running, false, 0, 0.016, 0)
	for index in intervals.size():
		now[0] += int(intervals[index]) * 1000
		watch.advance(running, false, index + 1, 0.016, index + 1)
	return watch.stop()["result"]["monitors"][0]


## The timeline of a watch of params over frames unpaused frames fed on a still clock.
func _timeline(rig: Dictionary, params: Dictionary, frames: int) -> Dictionary:
	var watch: Node = rig["watch"]
	watch.usec_clock = func() -> int: return 0
	params["frames"] = frames
	assert_eq(watch.begin(params, test_frame), "", "begins")
	var running: Dictionary = watch._watch
	for frame in frames:
		watch.advance(running, false, frame, 0.016, frame)
	return watch.stop()["result"]


## A Node2D Mover under WatchRig, a bridge finding nodes under WatchRig, and a watch on it, which
## sees no Movie Maker recording.
func _rig() -> Dictionary:
	var top := Node.new()
	top.name = "WatchRig"
	var mover := Node2D.new()
	mover.name = "Mover"
	top.add_child(mover)
	var bridge: Node = _node_from(BRIDGE_SOURCE)
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	var watch: Node = _watch_script.new()
	watch.bridge = bridge
	watch.movie_path = func() -> String: return ""
	return {"top": top, "mover": mover, "bridge": bridge, "watch": watch}


static func _node_from(source: String) -> Node:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()


func _free(rig: Dictionary) -> void:
	rig["watch"].drop()
	rig["watch"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()
