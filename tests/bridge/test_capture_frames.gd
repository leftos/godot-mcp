extends "res://gd_test.gd"
## capture_frames' pure logic on the clock (bridge/godot_mcp_time.gd): which points a frame's game
## time makes due, the entries a grab gives them, the result a stopped capture answers, and its
## refusals. The node is never added to the tree; only functions that do not need it are called.
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
	time._running = "monitor"
	assert_eq(time._capture_refusal(false), time.MONITORING_REFUSAL, "while a monitor runs")
	time._running = "step"
	assert_eq(time._capture_refusal(false), time.STEPPING_REFUSAL, "while a step runs")
	time._running = "frames"
	assert_eq(time._capture_refusal(false), time.CAPTURING_REFUSAL, "while another capture runs")
	assert_eq(time._monitor_refusal("Main", "position"), time.CAPTURING_REFUSAL, "a monitor")
	time.free()
