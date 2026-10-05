# gdlint: disable=max-public-methods, private-method-call
# Each test is a public method the runner finds by its test_ prefix, so the count is the coverage;
# the tests call the clock's private helpers directly.
extends "res://gd_test.gd"
## The clock's pure logic (bridge/godot_mcp_time.gd): wait_for's JSON comparison and its reading
## of the server's fields (test_conditions.gd has its condition kinds and expression parsing).
## The node is never added to the tree; only functions that do not need it are called.

## Stands in for the tree's process_frame in a game-time wait, so its connections can be read.
signal test_frame

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_json_equal_matches_numbers_across_int_and_float_within_tolerance() -> void:
	var time: Node = _time_script.new()
	assert_true(time.json_equal(3, 3.0), "an int property against a JSON number")
	assert_true(time.json_equal(0.1 + 0.2, 0.3), "within 1e-6")
	assert_true(not time.json_equal(1.0, 1.00001), "outside 1e-6")
	assert_true(not time.json_equal(1, "1"), "a number is not its string")
	assert_true(not time.json_equal(true, 1.0), "a bool is not a number")
	time.free()


func test_json_equal_compares_containers_member_by_member() -> void:
	var time: Node = _time_script.new()
	assert_true(time.json_equal({"x": 1, "y": 2.0}, {"y": 2, "x": 1.0}), "same keys, any order")
	assert_true(not time.json_equal({"x": 1}, {"x": 1, "y": 2}), "a missing key")
	assert_true(not time.json_equal({"x": 1}, {"z": 1}), "a different key")
	assert_true(time.json_equal([1, [2, {"a": "b"}]], [1.0, [2.0, {"a": "b"}]]), "nested")
	assert_true(not time.json_equal([1, 2], [2, 1]), "order counts in an array")
	assert_true(not time.json_equal([1], [1, 1]), "a different length")
	assert_true(not time.json_equal([], {}), "an array is not an object")
	time.free()


func test_json_equal_compares_other_values_by_type_and_value() -> void:
	var time: Node = _time_script.new()
	assert_true(time.json_equal("done", "done"), "equal strings")
	assert_true(not time.json_equal("done", "idle"), "different strings")
	assert_true(time.json_equal(null, null), "null and null")
	assert_true(not time.json_equal(null, false), "null is not false")
	time.free()


func test_paused_refusal_lets_only_a_check_once_wait_run_while_paused() -> void:
	var time: Node = _time_script.new()
	assert_eq(time._paused_refusal(true, 1), time.PAUSED_REFUSAL, "a timed wait while paused")
	assert_eq(time._paused_refusal(true, 0), "", "a check-once wait while paused")
	assert_eq(time._paused_refusal(false, 1), "", "a timed wait while running")
	assert_eq(time._paused_refusal(false, 0), "", "a check-once wait while running")
	time.free()


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


func test_cancel_ends_a_running_step_as_its_deadline() -> void:
	var time: Node = _time_script.new()
	var params: Dictionary = {"action": "step", "count": 5}
	var woken: Array = []
	time.step_woken.connect(func(arrived: bool) -> void: woken.append(arrived))
	time._running = "step"
	time._running_params = params
	assert_true(time.cancel(params), "the running step is cancelled")
	assert_true(time._deadline_passed, "as its deadline passing would")
	assert_eq(woken, [false], "the step's wait is woken as by its deadline")
	time.free()


func test_cancel_ignores_another_requests_params() -> void:
	var time: Node = _time_script.new()
	var running: Dictionary = {"action": "step", "count": 5}
	var woken: Array = []
	time.step_woken.connect(func(arrived: bool) -> void: woken.append(arrived))
	assert_true(not time.cancel(running), "nothing runs")
	time._running = "monitor"
	time._running_params = running
	assert_true(not time.cancel(running.duplicate()), "an equal Dictionary is another request's")
	assert_true(not time._deadline_passed, "the running monitor goes on")
	assert_eq(woken, [], "nothing is woken")
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
