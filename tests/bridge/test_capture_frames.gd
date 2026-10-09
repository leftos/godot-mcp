extends "res://gd_test.gd"
## capture_frames' pure logic on the clock (bridge/godot_mcp_time.gd): which points a frame's game
## time makes due, the entries a grab gives them, the result a stopped capture answers, its
## refusals, and the method call a capture or a game-time wait makes as its clock starts
## (_call_once), against a stand-in bridge. The clock node is never added to the tree; only
## functions that do not need it are called.
# gdlint: disable=private-method-call

const SAVED := {"path": "C:/shots/a.png", "width": 64, "height": 32}

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_nothing_is_due_before_the_first_point() -> void:
	assert_eq(_time_script.due_points([0.1, 0.3], 0, 0.0), [], "at the start")
	assert_eq(_time_script.due_points([0.1, 0.3], 0, 0.099), [], "just before the first")
	assert_eq(_time_script.due_points([0.1, 0.3], 1, 0.2), [], "between the points")


func test_a_point_is_due_at_or_after_its_time() -> void:
	assert_eq(_time_script.due_points([0.1, 0.3], 0, 0.1), [0.1], "exactly at it")
	assert_eq(_time_script.due_points([0.1, 0.3], 0, 0.15), [0.1], "after it, before the next")
	assert_eq(_time_script.due_points([0.1, 0.3], 2, 5.0), [], "all taken")


func test_two_points_due_in_one_frame_share_a_grab() -> void:
	var due: Array = _time_script.due_points([0.1, 0.3, 0.3], 1, 0.35)
	var entries: Array = _time_script.frame_entries(due, SAVED, 42, 0.35)
	assert_eq(due, [0.3, 0.3], "both equal points are due")
	assert_eq(entries.size(), 2, "one entry per point")
	assert_eq(entries[0]["path"], entries[1]["path"], "one file")
	assert_eq(entries[0]["frame"], 42, "the frame grabbed")
	assert_eq(entries[1]["frame"], 42, "the same frame")
	assert_eq(entries[0]["width"], 64, "the width saved")
	assert_eq(entries[0]["height"], 32, "the height saved")


func test_points_passed_in_one_long_frame_are_all_due() -> void:
	assert_eq(
		_time_script.due_points([0.1, 0.2, 0.3, 0.4], 0, 0.3), [0.1, 0.2, 0.3], "up to elapsed"
	)


func test_an_entry_says_how_late_its_frame_came() -> void:
	var entries: Array = _time_script.frame_entries([0.1, 0.3], SAVED, 7, 0.3504)
	assert_eq(entries[0]["at"], 0.1, "the point")
	assert_approx(entries[0]["gameSeconds"], 0.3504, "the frame's game time")
	assert_approx(entries[0]["late"], 0.25, "late to the millisecond")
	assert_approx(entries[1]["late"], 0.05, "the later point is less late")
	assert_approx(_time_script.frame_entries([0.5], SAVED, 1, 0.5)[0]["late"], 0.0, "on time")


func test_a_capture_that_took_every_point_has_no_stopped_or_missed() -> void:
	var entries: Array = _time_script.frame_entries([0.1], SAVED, 3, 0.12)
	var result: Dictionary = _time_script.frames_result([0.1], entries)
	assert_eq(result["frames"], entries, "the frames")
	assert_true(not result.has("stopped"), "not stopped")
	assert_true(not result.has("missed"), "nothing missed")


func test_a_cancel_mid_run_answers_stopped_and_missed() -> void:
	var time: Node = _time_script.new()
	var params: Dictionary = {"points": [0.05, 5.0, 6.0]}
	var woken: Array = []
	time.step_woken.connect(func(arrived: bool) -> void: woken.append(arrived))
	time._running = "frames"
	time._running_params = params
	assert_true(time.cancel(params), "the running capture is cancelled")
	assert_eq(woken, [false], "its wait is woken as by its deadline")
	var taken: Array = _time_script.frame_entries([0.05], SAVED, 9, 0.06)
	var result: Dictionary = _time_script.frames_result(params["points"], taken)
	assert_eq(result["frames"].size(), 1, "the frame taken")
	assert_eq(result["stopped"], true, "stopped")
	assert_eq(result["missed"], [5.0, 6.0], "the points not reached")
	time.free()


func test_a_paused_tree_is_refused() -> void:
	var time: Node = _time_script.new()
	assert_eq(time._capture_refusal(true), time.CAPTURE_PAUSED_REFUSAL, "paused")
	assert_eq(time._capture_refusal(false), "", "running")
	time.free()


func test_a_capture_refuses_and_is_refused_by_the_others() -> void:
	var time: Node = _time_script.new()
	time._running = "step"
	assert_eq(time._capture_refusal(false), time.STEPPING_REFUSAL, "while a step runs")
	time._running = "frames"
	assert_eq(time._capture_refusal(false), time.CAPTURING_REFUSAL, "while another capture runs")
	var step: Dictionary = time.frame_control({"action": "step"})
	assert_eq(step.get("error"), time.CAPTURING_REFUSAL, "a step while a capture runs")
	time.free()


## Whether a capture's points count from the met frame needs a running tree (the start's poll
## awaits process_frame), so the integration tests cover that; these cover the start's report.
func test_a_met_start_reports_its_frame_and_its_then() -> void:
	var then: Dictionary = {"frame": 40, "timeScale": 0.2}
	var report: Dictionary = _time_script.start_report(
		{"met": true, "elapsedMs": 120, "frames": 7, "value": true, "then": then}, 40
	)
	assert_eq(report, {"met": true, "frame": 40, "then": then}, "met, its frame and its then")


func test_a_start_that_timed_out_reports_its_last_value_and_no_frame() -> void:
	var report: Dictionary = _time_script.start_report(
		{"met": false, "elapsedMs": 300, "frames": 18, "last": "idle"}, 58
	)
	assert_eq(report, {"met": false, "last": "idle"}, "not met, with the last value seen")


func test_a_start_reports_its_failed_checks() -> void:
	var failed: Dictionary = {"count": 3, "error": "Invalid named index 'nope'"}
	var report: Dictionary = _time_script.start_report(
		{"met": false, "frames": 3, "last": null, "failedChecks": failed}, 9
	)
	assert_eq(report["failedChecks"], failed, "the failed checks carried")


func test_a_start_met_after_a_cancel_or_the_deadline_is_not_met_and_runs_no_then() -> void:
	var time: Node = _time_script.new()
	var scale: float = Engine.time_scale
	var condition: Dictionary = {"then": {"timeScale": 0.5}}
	var cancelled: Dictionary = time._start_outcome(
		{"result": {"met": true, "elapsedMs": 40, "frames": 3, "value": true}},
		{"_cancelled": true, "start": condition}
	)
	assert_eq(cancelled["result"]["met"], false, "a cancelled start is not met")
	assert_true(not cancelled["result"].has("then"), "no then ran")
	assert_eq(Engine.time_scale, scale, "the time scale is unchanged")
	assert_eq(
		_time_script.start_report(cancelled["result"], 9),
		{"met": false, "last": true},
		"reported as not met, with the value it last saw"
	)
	time._deadline_passed = true
	var passed: Dictionary = time._start_outcome(
		{"result": {"met": true, "frames": 3, "value": true}}, {"start": condition}
	)
	assert_eq(passed["result"]["met"], false, "a start met after the deadline is not met")
	assert_eq(Engine.time_scale, scale, "the time scale is still unchanged")
	time.free()


func test_call_once_puts_the_methods_value_into_called() -> void:
	var rig: Dictionary = _call_rig()
	var called: Dictionary = {}
	var call: Dictionary = {"node": "CallTarget", "method": "add", "args": [2, 3]}
	assert_eq(rig["time"]._call_once({"call": call}, called), "", "the call succeeds")
	assert_eq(called, {"value": 5}, "its value, as JSON")
	assert_eq(rig["time"]._call_once({"call": call}, called), "", "a second ask")
	assert_eq(rig["target"].calls, 1, "calls the method once only")
	assert_eq(rig["time"]._call_once({}, {}), "", "no call, nothing to do")
	_free_call_rig(rig)


## A missing method or a wrong argument count names the node's path, which a node outside a
## running tree cannot give without an engine error, so the integration tests cover those.
func test_call_once_answers_a_missing_node_as_call_method_refuses_it() -> void:
	var rig: Dictionary = _call_rig()
	var called: Dictionary = {}
	var no_node: String = rig["time"]._call_once(
		{"call": {"node": "Nowhere", "method": "add"}}, called
	)
	assert_eq(
		no_node,
		rig["bridge"]._inspect.not_found("Nowhere", "get_scene_tree lists the nodes' paths"),
		"a missing node"
	)
	assert_eq(called, {}, "no value")
	assert_eq(rig["target"].calls, 0, "nothing is called")
	_free_call_rig(rig)


func test_call_once_fails_on_an_error_the_feed_logs_across_the_call() -> void:
	var rig: Dictionary = _call_rig()
	rig["target"].log_to_feed(Logger.ERROR_TYPE_ERROR, "an older error")
	var called: Dictionary = {}
	var clean: String = rig["time"]._call_once(
		{"call": {"node": "CallTarget", "method": "add", "args": [1, 1]}}, called
	)
	assert_eq(clean, "", "an error logged before the call is not the call's")
	var warned: Dictionary = {}
	var warning: String = rig["time"]._call_once(
		{"call": {"node": "CallTarget", "method": "warn"}}, warned
	)
	assert_eq(warning, "", "a warning does not fail the call")
	assert_eq(warned, {"value": 2}, "and its value comes back")
	var failed: Dictionary = {}
	var error: String = rig["time"]._call_once(
		{"call": {"node": "CallTarget", "method": "fail"}}, failed
	)
	assert_eq(error, "the method failed", "the first new error's message")
	assert_eq(failed, {}, "no value")
	_free_call_rig(rig)


## A clock whose bridge is a stand-in holding the inspector, the JSON module and an unregistered
## logger, and CallTarget, a node outside any tree (the runner's root is not in one yet) with
## add(a, b), warn() and fail(), the last two logging a warning or an error to that logger as the
## engine would.
func _call_rig() -> Dictionary:
	var bridge: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var _json: GDScript",
					"var _logger: Logger",
					"var _inspect: Node",
					"var target: Node",
					"",
					"",
					"func _find_node(element: String) -> Node:",
					"\treturn target if element == str(target.name) else null",
				]
			)
		)
	)
	var target: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var logger: Logger",
					"var calls: int = 0",
					"",
					"",
					"func add(a: int, b: int) -> int:",
					"\tcalls += 1",
					"\treturn a + b",
					"",
					"",
					"func warn() -> int:",
					"\tlog_to_feed(Logger.ERROR_TYPE_WARNING, 'a warning')",
					"\treturn 2",
					"",
					"",
					"func fail() -> int:",
					"\tlog_to_feed(Logger.ERROR_TYPE_ERROR, 'the method failed')",
					"\tlog_to_feed(Logger.ERROR_TYPE_ERROR, 'a second error')",
					"\treturn 3",
					"",
					"",
					"func log_to_feed(type: int, message: String) -> void:",
					"\tvar none: Array[ScriptBacktrace] = []",
					"\tlogger._log_error('f', 'res://t.gd', 1, message, '', false, type, none)",
				]
			)
		)
	)
	target.name = "CallTarget"
	var logger: Logger = load_bridge_script("godot_mcp_logger.gd").new()
	target.logger = logger
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = logger
	bridge._inspect = inspect
	bridge.target = target
	var time: Node = _time_script.new()
	time.bridge = bridge
	return {"time": time, "target": target, "bridge": bridge}


func _free_call_rig(rig: Dictionary) -> void:
	rig["time"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["target"].free()


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
