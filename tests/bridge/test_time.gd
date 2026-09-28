extends "res://gd_test.gd"
## The clock's pure logic (bridge/godot_mcp_time.gd): wait_for's JSON comparison, its condition
## kinds, expression parsing and its reading of the server's fields. The node is never added to
## the tree; only functions that do not need it are called.
# gdlint: disable=private-method-call

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_json_equal_matches_numbers_across_int_and_float_within_tolerance() -> void:
	var time: Node = _time_script.new()
	assert_true(time._json_equal(3, 3.0), "an int property against a JSON number")
	assert_true(time._json_equal(0.1 + 0.2, 0.3), "within 1e-6")
	assert_true(not time._json_equal(1.0, 1.00001), "outside 1e-6")
	assert_true(not time._json_equal(1, "1"), "a number is not its string")
	assert_true(not time._json_equal(true, 1.0), "a bool is not a number")
	time.free()


func test_json_equal_compares_containers_member_by_member() -> void:
	var time: Node = _time_script.new()
	assert_true(time._json_equal({"x": 1, "y": 2.0}, {"y": 2, "x": 1.0}), "same keys, any order")
	assert_true(not time._json_equal({"x": 1}, {"x": 1, "y": 2}), "a missing key")
	assert_true(not time._json_equal({"x": 1}, {"z": 1}), "a different key")
	assert_true(time._json_equal([1, [2, {"a": "b"}]], [1.0, [2.0, {"a": "b"}]]), "nested")
	assert_true(not time._json_equal([1, 2], [2, 1]), "order counts in an array")
	assert_true(not time._json_equal([1], [1, 1]), "a different length")
	assert_true(not time._json_equal([], {}), "an array is not an object")
	time.free()


func test_json_equal_compares_other_values_by_type_and_value() -> void:
	var time: Node = _time_script.new()
	assert_true(time._json_equal("done", "done"), "equal strings")
	assert_true(not time._json_equal("done", "idle"), "different strings")
	assert_true(time._json_equal(null, null), "null and null")
	assert_true(not time._json_equal(null, false), "null is not false")
	time.free()


func test_make_probe_builds_a_check_for_each_known_kind() -> void:
	var time: Node = _time_script.new()
	var exists: Variant = time._make_probe("exists", {"node": "Main", "exists": true})
	var property: Variant = time._make_probe(
		"property", {"node": "Main", "property": "state", "equals": "done"}
	)
	var expression: Variant = time._make_probe("expression", {"expression": "1 + 1 == 2"})
	assert_true(exists is Callable, "exists")
	assert_true(property is Callable, "property")
	assert_true(expression is Callable, "expression")
	time.free()


func test_make_probe_refuses_an_unknown_kind() -> void:
	var time: Node = _time_script.new()
	assert_eq(
		time._make_probe("signal", {}), "unknown condition kind 'signal'", "signal polls apart"
	)
	assert_eq(time._make_probe("", {}), "unknown condition kind ''", "no kind")
	time.free()


func test_parse_expression_reports_a_parse_error_with_expressions_text() -> void:
	var time: Node = _time_script.new()
	var parsed: Variant = time._parse_expression("1 +", "")
	assert_true(parsed is String, "a String, not a Callable")
	assert_true(
		str(parsed).begins_with("the expression does not parse: "), "the prefix: %s" % parsed
	)
	assert_true(str(parsed).length() > "the expression does not parse: ".length(), "the text")
	time.free()


func test_parse_expression_accepts_its_inputs() -> void:
	var time: Node = _time_script.new()
	assert_true(time._parse_expression("node == null", "Main") is Callable, "node named")
	assert_true(
		time._parse_expression("tree != root and Engine != Input", "") is Callable, "inputs"
	)
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
