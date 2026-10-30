# gdlint: disable=max-public-methods, private-method-call
# Each test is a public method the runner finds by its test_ prefix, so the count is the coverage;
# the tests call the clock's private helpers directly.
extends "res://gd_test.gd"
## The clock's pure logic (bridge/godot_mcp_time.gd): wait_for's reading of the server's fields
## (test_conditions.gd has its condition kinds and expression parsing, test_equals_conditions.gd
## its JSON comparison).
## The node is never added to the tree; only functions that do not need it are called.

## Stands in for the tree's process_frame in a game-time wait, so its connections can be read.
signal test_frame

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_paused_refusal_lets_only_a_check_once_wait_run_while_paused() -> void:
	var time: Node = _time_script.new()
	assert_eq(
		time._paused_refusal(true, 1, false), time.PAUSED_REFUSAL, "a timed wait while paused"
	)
	assert_eq(time._paused_refusal(true, 0, false), "", "a check-once wait while paused")
	assert_eq(time._paused_refusal(false, 1, false), "", "a timed wait while running")
	assert_eq(time._paused_refusal(false, 0, false), "", "a check-once wait while running")
	time.free()


func test_paused_refusal_lets_a_game_time_limited_wait_run_while_paused() -> void:
	var time: Node = _time_script.new()
	assert_eq(time._paused_refusal(true, 1, true), "", "a limitGameMs wait while paused")
	assert_eq(time._paused_refusal(true, 1, false), time.PAUSED_REFUSAL, "a plain wait is refused")
	time.free()


func test_a_limited_poll_stops_once_the_seen_game_time_reaches_the_limit() -> void:
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	var params: Dictionary = {"limitGameMs": 100, "_gameClock": clock}
	_time_script.advance_game_clock(clock, false, 0.06)
	_time_script.advance_game_clock(clock, false, 0.06)
	assert_true(not _time_script._stops_polling(params), "120 ms counted, 60 ms seen: goes on")
	_time_script.advance_game_clock(clock, false, 0.06)
	assert_true(_time_script._stops_polling(params), "120 ms seen: stops")


func test_a_condition_met_on_the_frame_the_limit_is_reached_wins() -> void:
	var time: Node = _time_script.new()
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	var params: Dictionary = {"limitGameMs": 100, "_gameClock": clock}
	_time_script.advance_game_clock(clock, false, 0.1)
	_time_script.advance_game_clock(clock, false, 0.05)
	var outcome: Dictionary = time._poll(func() -> Array: return [true, 1], 60000.0, params)
	time._add_game_ms(outcome, params)
	assert_eq(outcome["result"]["met"], true, "met in the frame its limit was reached")
	assert_eq(outcome["result"]["gameMs"], 100, "the game time the check saw, not the frame's own")
	time.free()


func test_a_wait_without_a_game_time_limit_never_stops_on_game_time() -> void:
	var time: Node = _time_script.new()
	var params: Dictionary = {}
	assert_true(not time._start_game_limit(params).is_valid(), "no clock without limitGameMs")
	assert_true(not params.has("_gameClock"), "no clock in the params")
	assert_true(not _time_script._stops_polling(params), "no game-time stop")
	assert_true(time._keeps_polling([false, 0], 100000, 10, 5000.0, params), "goes on")
	time.free()


func test_a_paused_frame_moves_the_seen_game_time_to_the_counted_one_only() -> void:
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	_time_script.advance_game_clock(clock, false, 0.1)
	_time_script.advance_game_clock(clock, true, 0.5)
	assert_approx(clock["seconds"], 0.1, "the paused frame added nothing")
	assert_approx(clock["seen"], 0.1, "the frame before it is seen")
	_time_script.advance_game_clock(clock, true, 0.5)
	assert_approx(clock["seen"], 0.1, "a second paused frame adds nothing either")


func test_poll_with_timeout_zero_checks_once_without_a_frame() -> void:
	var time: Node = _time_script.new()
	var met: Dictionary = time._poll(func() -> Array: return [true, 7], 0)["result"]
	var unmet: Dictionary = time._poll(func() -> Array: return [false, "idle"], 0)["result"]
	assert_eq(met["met"], true, "met")
	assert_eq(met["value"], 7, "the value seen")
	assert_eq(met["frames"], 0, "no frame waited for when met")
	assert_eq(unmet["met"], false, "not met is a result")
	assert_eq(unmet["last"], "idle", "the last value seen")
	assert_eq(unmet["frames"], 0, "no frame waited for when not met")
	time.free()


func test_poll_stops_when_cancelled() -> void:
	var time: Node = _time_script.new()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [false, "idle"]
	var outcome: Dictionary = time._poll(probe, 60000.0, {"_cancelled": true})["result"]
	assert_eq(outcome["met"], false, "a cancelled wait is not met")
	assert_eq(outcome["last"], "idle", "the last value seen")
	assert_eq(outcome["frames"], 0, "no frame waited for")
	assert_eq(checks, [1], "checked once")
	time.free()


func test_keeps_polling_counts_frames_for_a_frame_counted_wait() -> void:
	var time: Node = _time_script.new()
	var unmet: Array = [false, "idle"]
	var counted: Dictionary = {"timeoutFrames": 90}
	assert_true(time._keeps_polling(unmet, 89, 4999, 5000.0, counted), "short of timeoutFrames")
	assert_true(not time._keeps_polling(unmet, 90, 0, 5000.0, counted), "at timeoutFrames")
	assert_true(not time._keeps_polling(unmet, 10, 5000, 5000.0, counted), "the real bound")
	counted["_cancelled"] = true
	assert_true(not time._keeps_polling(unmet, 0, 0, 5000.0, counted), "cancelled")
	assert_true(time._keeps_polling(unmet, 1000, 10, 5000.0, {}), "a timed wait counts no frames")
	assert_true(not time._keeps_polling([true, 1], 0, 0, 5000.0, {}), "met")
	assert_true(not time._keeps_polling([false, null, "x"], 0, 0, 5000.0, {}), "failed")
	time.free()


func test_a_game_ms_check_is_unmet_until_enough_game_time_has_passed() -> void:
	var time: Node = _time_script.new()
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	assert_eq(time._check_game_time("gameMs", 50, clock), [false, 0], "no frame yet")
	time.advance_game_clock(clock, false, 0.03125)
	assert_eq(time._check_game_time("gameMs", 50, clock), [false, 31], "31.25 ms of 50")
	time.advance_game_clock(clock, false, 0.03125)
	assert_eq(time._check_game_time("gameMs", 50, clock), [true, 62], "past it within a frame")
	time.free()


func test_a_paused_frame_adds_no_game_time_and_no_frame() -> void:
	var clock: Dictionary = {"seconds": 0.25, "frames": 2}
	_time_script.advance_game_clock(clock, true, 0.5)
	assert_approx(clock["seconds"], 0.25, "the game time is kept")
	assert_eq(clock["frames"], 2, "the frame count is kept")


func test_a_frames_check_counts_only_unpaused_frames() -> void:
	var time: Node = _time_script.new()
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	time.advance_game_clock(clock, false, 0.016)
	time.advance_game_clock(clock, true, 0.016)
	time.advance_game_clock(clock, false, 0.016)
	assert_eq(time._check_game_time("frames", 3, clock), [false, 2], "two of three ran unpaused")
	time.advance_game_clock(clock, false, 0.016)
	assert_eq(time._check_game_time("frames", 3, clock), [true, 3], "the third")
	time.free()


func test_a_game_time_wait_disconnects_its_clock_when_the_poll_ends() -> void:
	var time: Node = _time_script.new()
	var params: Dictionary = {"kind": "gameMs", "gameMs": 100, "_cancelled": true}
	var outcome: Dictionary = time._wait_for_game_time("gameMs", params, 60000, test_frame)
	assert_eq(outcome["result"]["met"], false, "a cancelled wait is not met")
	assert_eq(outcome["result"]["last"], 0, "no game time counted")
	assert_eq(test_frame.get_connections().size(), 0, "the clock is disconnected")
	time.free()


func test_backstop_ms_overrides_the_deadline() -> void:
	var time: Node = _time_script.new()
	assert_approx(time._bound_ms({"backstopMs": 55000}, 11000.0), 55000.0, "the backstop wins")
	assert_approx(time._bound_ms({"deadlineMs": 11000}, 11000.0), 11000.0, "an older server's")
	assert_approx(time._bound_ms({}, 0.0), 0.0, "a check-once wait stays at 0")
	time.free()


func test_text_reads_only_string_fields() -> void:
	var time: Node = _time_script.new()
	assert_eq(time._text({"node": "Main"}, "node"), "Main", "a string")
	assert_eq(time._text({}, "node"), "", "left out")
	assert_eq(time._text({"node": null}, "node"), "", "null")
	assert_eq(time._text({"node": 3}, "node"), "", "a number")
	time.free()


func test_then_runs_once_and_only_when_met() -> void:
	var rig: Dictionary = _then_rig()
	var params: Dictionary = {
		"then": {"call": {"node": "CallTarget", "method": "add", "args": [1, 2]}}
	}
	var unmet: Dictionary = rig["time"]._finish_then(
		{"result": {"met": false, "last": null}}, params, {}
	)
	assert_eq(unmet["result"]["met"], false, "the outcome is kept")
	assert_true(not unmet["result"].has("then"), "nothing runs on a wait that was not met")
	assert_eq(rig["target"].calls, 0, "and no method is called")
	var met: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 3}}, params, {}
	)
	assert_eq(met["result"]["then"]["call"], {"value": 3}, "the met frame's call")
	assert_eq(rig["target"].calls, 1, "called once")
	var drawn: Dictionary = {"then_ran": {"frame": 7}}
	var replay: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 4}}, params, drawn
	)
	assert_eq(
		replay["result"]["then"], {"frame": 7}, "the draw check's outcome is kept, not run again"
	)
	assert_eq(rig["target"].calls, 1, "still once")
	_free_then_rig(rig)


func test_run_then_into_runs_the_action_once() -> void:
	var rig: Dictionary = _then_rig()
	var drawn: Dictionary = {
		"params": {"then": {"call": {"node": "CallTarget", "method": "add", "args": [1, 1]}}}
	}
	rig["time"]._run_then_into(drawn)
	rig["time"]._run_then_into(drawn)
	assert_true(drawn.has("then_ran"), "the outcome is kept")
	assert_eq(rig["target"].calls, 1, "a second draw check does not run it again")
	_free_then_rig(rig)


func test_then_calls_before_it_sets_the_time_scale() -> void:
	Engine.time_scale = 1.0
	var rig: Dictionary = _then_rig()
	var params: Dictionary = {
		"then": {"call": {"node": "CallTarget", "method": "scale_now"}, "timeScale": 0.5}
	}
	var ran: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 2}}, params, {}
	)
	assert_eq(
		ran["result"]["then"]["call"],
		{"value": 1.0},
		"the method sees the scale before then sets it"
	)
	assert_approx(ran["result"]["then"]["timeScale"], 0.5, "the scale then set")
	assert_approx(Engine.time_scale, 0.5, "and the engine's")
	_free_then_rig(rig)
	Engine.time_scale = 1.0


func test_then_with_only_a_time_scale_sets_it() -> void:
	Engine.time_scale = 1.0
	var rig: Dictionary = _then_rig()
	var ran: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 1}}, {"then": {"timeScale": 0.25}}, {}
	)
	assert_true(not ran["result"]["then"].has("call"), "no call")
	assert_approx(Engine.time_scale, 0.25, "the scale set")
	_free_then_rig(rig)
	Engine.time_scale = 1.0


func test_a_failed_then_call_skips_the_time_scale_and_names_the_met_frame() -> void:
	Engine.time_scale = 1.0
	var rig: Dictionary = _then_rig()
	var frame: int = Engine.get_process_frames()
	var with_scale: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 4}},
		{"then": {"call": {"node": "CallTarget", "method": "fail"}, "timeScale": 0.5}},
		{}
	)
	assert_true(with_scale.has("error"), "the wait fails: %s" % with_scale)
	assert_eq(
		with_scale["error"],
		(
			(
				"The condition was met after 4 frames (frame %d), but then.call failed, "
				+ "so timeScale was not set: the method failed"
			)
			% frame
		),
		"the met frame and the reason"
	)
	assert_approx(Engine.time_scale, 1.0, "the scale is not set")
	var without_scale: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 1}},
		{"then": {"call": {"node": "CallTarget", "method": "fail"}}},
		{}
	)
	assert_eq(
		without_scale["error"],
		(
			"The condition was met after 1 frames (frame %d), but then.call failed: the method failed"
			% frame
		),
		"no timeScale given, no clause"
	)
	_free_then_rig(rig)
	Engine.time_scale = 1.0


func test_then_pause_pauses_the_tree_after_the_call_and_the_scale() -> void:
	Engine.time_scale = 1.0
	var rig: Dictionary = _tree_then_rig()
	var params: Dictionary = {
		"then":
		{
			"call": {"node": "CallTarget", "method": "scale_now"},
			"timeScale": 0.5,
			"pause": true,
		}
	}
	var met: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 2}}, params, {}
	)
	assert_eq(
		met["result"]["then"]["call"],
		{"value": 1.0},
		"the call sees the running tree and the scale before then set it"
	)
	assert_approx(met["result"]["then"]["timeScale"], 0.5, "the scale then set")
	assert_eq(met["result"]["then"]["paused"], true, "then reports the pause")
	assert_true(rig["tree"].paused, "the tree is paused")
	Engine.time_scale = 1.0
	_free_then_rig(rig)


func test_a_failed_then_call_does_not_pause() -> void:
	Engine.time_scale = 1.0
	var rig: Dictionary = _tree_then_rig()
	var failed: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 4}},
		{
			"then":
			{"call": {"node": "CallTarget", "method": "fail"}, "timeScale": 0.5, "pause": true}
		},
		{}
	)
	assert_true(failed.has("error"), "the wait fails: %s" % failed)
	assert_true(
		failed["error"].ends_with("; the game was not paused: the method failed"),
		"the text names the pause it did not make: %s" % failed["error"]
	)
	assert_true(not rig["tree"].paused, "the tree keeps running")
	Engine.time_scale = 1.0
	_free_then_rig(rig)


func test_then_pause_false_does_not_pause() -> void:
	var rig: Dictionary = _tree_then_rig()
	var met: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 1}}, {"then": {"pause": false}}, {}
	)
	assert_true(not met["result"]["then"].has("paused"), "no pause is reported")
	assert_true(not rig["tree"].paused, "and none is set")
	_free_then_rig(rig)


func test_an_unmet_wait_with_pause_leaves_the_tree_running() -> void:
	var rig: Dictionary = _tree_then_rig()
	var unmet: Dictionary = rig["time"]._finish_then(
		{"result": {"met": false, "last": null}}, {"then": {"pause": true}}, {}
	)
	assert_eq(unmet["result"]["met"], false, "the outcome is kept")
	assert_true(not unmet["result"].has("then"), "nothing runs on a wait that was not met")
	assert_true(not rig["tree"].paused, "the tree keeps running")
	_free_then_rig(rig)


func test_a_then_pause_while_a_capture_runs_fails_and_leaves_the_tree_running() -> void:
	var rig: Dictionary = _tree_then_rig()
	rig["time"]._running = "frames"
	var refused: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 5}}, {"then": {"pause": true}}, {}
	)
	assert_true(refused.has("error"), "the wait fails: %s" % refused)
	assert_true(not rig["tree"].paused, "the tree keeps running")
	assert_true(
		refused["error"].begins_with("The condition was met after 5 frames (frame "),
		"the text names the met frame: %s" % refused["error"]
	)
	assert_true(
		(
			refused["error"]
			. ends_with(
				(
					"; the game was not paused, because A capture_frames is still running on this game; "
					+ "wait for its reply before pause, resume, a step or another capture."
				)
			)
		),
		"the text names the pause it did not make: %s" % refused["error"]
	)
	_free_then_rig(rig)


func test_a_signal_wait_connects_before_its_call_so_an_emission_the_call_makes_is_met() -> void:
	var rig: Dictionary = _then_rig()
	var params: Dictionary = {
		"node": "CallTarget",
		"signal": "pinged",
		"call": {"node": "CallTarget", "method": "ping", "args": [4]},
	}
	var hold: Dictionary = {}
	var caught: Variant = rig["time"].signal_probe(params, hold)
	var outcome: Dictionary = await rig["time"]._call_and_poll_signal(caught, params, 1000, hold)
	assert_true(outcome.has("result"), "the wait answers: %s" % outcome)
	assert_eq(outcome["result"]["met"], true, "the emission inside the call is caught")
	assert_eq(outcome["result"]["args"], [4], "with its arguments")
	assert_eq(outcome["result"]["call"], {"value": 4}, "and the call's value")
	assert_eq(rig["target"].get_signal_connection_list("pinged").size(), 0, "released")
	_free_then_rig(rig)


func test_a_signal_wait_whose_call_fails_fails_and_releases_its_connection() -> void:
	var rig: Dictionary = _then_rig()
	var params: Dictionary = {
		"node": "CallTarget", "signal": "pinged", "call": {"node": "CallTarget", "method": "fail"}
	}
	var hold: Dictionary = {}
	var caught: Variant = rig["time"].signal_probe(params, hold)
	var outcome: Dictionary = await rig["time"]._call_and_poll_signal(caught, params, 1000, hold)
	assert_eq(outcome, {"error": "the method failed"}, "the call's error fails the wait")
	assert_eq(rig["target"].get_signal_connection_list("pinged").size(), 0, "released")
	_free_then_rig(rig)


func test_a_signal_wait_connects_in_its_calls_frame_so_an_earlier_emission_is_not_met() -> void:
	var rig: Dictionary = _then_rig()
	var params: Dictionary = {
		"node": "CallTarget",
		"signal": "pinged",
		"call": {"node": "CallTarget", "method": "ping", "args": [4]},
	}
	var outcomes: Array = []
	var waiting := func() -> void:
		outcomes.append(await rig["time"]._wait_for_signal(params, 1000, test_frame))
	waiting.call()
	assert_eq(outcomes, [], "the wait holds for the call's frame")
	rig["target"].pinged.emit(9)
	test_frame.emit()
	assert_eq(outcomes.size(), 1, "the wait answers in the call's frame")
	assert_eq(outcomes[0]["result"]["met"], true, "met: %s" % outcomes[0])
	assert_eq(outcomes[0]["result"]["args"], [4], "by the call's emission, not the one before it")
	assert_eq(rig["target"].get_signal_connection_list("pinged").size(), 0, "released")
	_free_then_rig(rig)


## A capture's signal start is met on its poll's first check after the emission, as a signal wait
## is: here the emission start.call makes in the call's frame, so the clock starts in that frame.
func test_a_signal_start_met_by_its_calls_emission_reports_args_and_releases_it() -> void:
	var rig: Dictionary = _then_rig()
	var ping: Dictionary = {"node": "CallTarget", "method": "ping", "args": [4]}
	var run: Dictionary = _run_signal_start(rig, {"call": ping, "timeoutMs": 1000}, {})
	assert_eq(run["outcomes"].size(), 1, "the start answers in its call's frame")
	assert_eq(run["outcomes"][0]["result"]["met"], true, "met: %s" % run["outcomes"][0])
	assert_eq(run["outcomes"][0]["result"]["frames"], 0, "on the check right after the call")
	assert_eq(run["start"].get("args"), [4], "the emission's arguments")
	assert_eq(run["start"].get("call"), {"value": 4}, "and the call's value")
	assert_eq(run["start"].get("frame"), Engine.get_process_frames(), "and the met frame")
	assert_true(not run["start"].has("last"), "no last: %s" % run["start"])
	assert_eq(rig["target"].get_signal_connection_list("pinged").size(), 0, "released")
	_free_then_rig(rig)


func test_a_signal_start_releases_its_connection_however_it_ends_unmet() -> void:
	var ping: Dictionary = {"node": "CallTarget", "method": "ping", "args": [4]}
	var fail: Dictionary = {"node": "CallTarget", "method": "fail"}
	var endings: Dictionary = {
		"timeout": [{"timeoutMs": 0}, {}],
		"cancel": [{"timeoutMs": 1000}, {"_cancelled": true}],
		"deadline": [{"call": ping, "timeoutMs": 1000}, {"_deadline": true}],
		"failed call": [{"call": fail, "timeoutMs": 1000}, {}],
	}
	for ending: String in endings:
		var rig: Dictionary = _then_rig()
		var run: Dictionary = _run_signal_start(rig, endings[ending][0], endings[ending][1])
		assert_eq(run["outcomes"].size(), 1, "%s: the start answers" % ending)
		var outcome: Dictionary = run["outcomes"][0]
		if ending == "failed call":
			assert_eq(outcome, {"error": "the method failed"}, "the call's error fails the start")
		else:
			var report: Dictionary = run["start"]
			report.erase("call")
			assert_eq(report, {"met": false}, "%s: not met, with no last or args" % ending)
		var connected: int = rig["target"].get_signal_connection_list("pinged").size()
		assert_eq(connected, 0, "%s: released" % ending)
		_free_then_rig(rig)


## A missing signal's refusal names the node's path, which a node outside a running tree cannot
## give without an engine error, so this takes a missing node.
func test_a_signal_start_on_a_missing_node_fails_and_reports_nothing() -> void:
	var rig: Dictionary = _then_rig()
	var run: Dictionary = _run_signal_start(rig, {"node": "Nowhere", "timeoutMs": 1000}, {})
	assert_eq(run["outcomes"].size(), 1, "the start answers at once")
	var hint: String = "get_scene_tree lists the nodes' paths"
	var missing: String = rig["bridge"]._inspect.not_found("Nowhere", hint)
	assert_eq(run["outcomes"][0], {"error": missing}, "wait_for's not-found text")
	assert_eq(run["start"], {}, "no report")
	_free_then_rig(rig)


func test_stopping_draw_checks_lets_the_wait_state_go() -> void:
	var time: Node = _time_script.new()
	var drawn: Dictionary = {}
	time._check_at_draws(func() -> Array: return [false, null], drawn)
	var on_draw: Callable = drawn["on_draw"]
	assert_true(
		RenderingServer.frame_post_draw.is_connected(on_draw), "the wait checks at each draw"
	)
	time._stop_draw_checks(drawn)
	assert_true(
		not RenderingServer.frame_post_draw.is_connected(on_draw), "the draw check is disconnected"
	)
	assert_true(not drawn.has("on_draw"), "the state no longer holds the Callable bound to it")
	time.free()


## A clock whose bridge is a stand-in holding the inspector, the JSON module and an unregistered
## logger, and CallTarget, a node outside any tree with signal pinged(n), ping(n), which emits it
## and returns n, add(a, b), scale_now() and fail().
func _then_rig() -> Dictionary:
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
					"signal pinged(n: int)",
					"",
					"var logger: Logger",
					"var calls: int = 0",
					"",
					"",
					"func ping(n: int) -> int:",
					"\tpinged.emit(n)",
					"\treturn n",
					"",
					"",
					"func add(a: int, b: int) -> int:",
					"\tcalls += 1",
					"\treturn a + b",
					"",
					"",
					"func scale_now() -> float:",
					"\treturn Engine.time_scale",
					"",
					"",
					"func fail() -> int:",
					"\tlog_to_feed(Logger.ERROR_TYPE_ERROR, 'the method failed')",
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


## Runs a capture's start on CallTarget's pinged with fields over its condition and request over
## the capture's params (_deadline: true marks the capture's deadline passed), emitting test_frame
## once when the start holds for its call's frame. Returns {outcomes, start}: the start's outcome,
## once it answered, and its report.
func _run_signal_start(rig: Dictionary, fields: Dictionary, request: Dictionary) -> Dictionary:
	var condition: Dictionary = {"kind": "signal", "node": "CallTarget", "signal": "pinged"}
	condition.merge(fields, true)
	var params: Dictionary = {"start": condition}
	params.merge(request)
	rig["time"]._deadline_passed = bool(params.get("_deadline", false))
	var start: Dictionary = {}
	var outcomes: Array = []
	var waiting := func() -> void:
		outcomes.append(await rig["time"]._await_start(params, start, test_frame))
	waiting.call()
	if outcomes.is_empty():
		test_frame.emit()
	return {"outcomes": outcomes, "start": start}


func _free_then_rig(rig: Dictionary) -> void:
	rig["time"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["target"].free()


## The then rig with a stand-in tree (a paused bool) its clock pauses on, as test_step's is, since a
## gdtest node is outside any tree; the tests free it with _free_then_rig.
func _tree_then_rig() -> Dictionary:
	var rig: Dictionary = _then_rig()
	var tree: RefCounted = _compile("extends RefCounted\n\nvar paused: bool = false\n")
	rig["time"].then_tree = tree
	rig["tree"] = tree
	return rig


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
