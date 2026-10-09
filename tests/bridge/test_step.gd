# gdlint: disable=private-method-call
# The tests call the step's and the clock's private helpers directly.
extends "res://gd_test.gd"
## The step (bridge/godot_mcp_step.gd, the clock's Step child) and the clock's running mark it
## runs under. The nodes are never added to the tree: a step's loops run on a stand-in tree and a
## stand-in draw signal the tests emit by hand, one frame at a time.

## Stands in for RenderingServer.frame_post_draw, which a headless game never emits.
signal test_draw

## wait_for's refusal of a uiChanged condition no gesture has taken a baseline for.
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ "uiChanged wait was met; send the input first"
)
## wait_for's refusal of a signal condition on a node a bare name does not find.
const MISSING_NODE := (
	"No node named 'Missing' anywhere under /root in the running game; "
	+ "get_scene_tree lists the nodes' paths."
)
## The stall text a step of 5 frames that counted 3 makes for a draw that never came.
const STALLED_3_OF_5 := (
	"The step stopped after 3 of 5 frames: no frame was drawn for too long "
	+ "(is the window minimized?); the game is left paused."
)

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


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
	time._running = "step"
	time._running_params = running
	assert_true(not time.cancel(running.duplicate()), "an equal Dictionary is another request's")
	assert_true(not time._deadline_passed, "the running step goes on")
	assert_eq(woken, [], "nothing is woken")
	time.free()


func test_a_step_until_is_checked_after_each_frame_and_ends_at_the_draw_that_met() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [checks[0] >= 3, checks[0]]
	var run: Dictionary = _start(rig, {"count": 10}, _until(probe))
	assert_eq(checks[0], 0, "no check before the first frame")
	_frame(rig)
	_frame(rig)
	assert_eq(checks[0], 2, "one check a counted frame")
	assert_true(not run["ended"].has("error"), "the step goes on while unmet")
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "met at the third draw, the step ends")
	assert_eq(run["result"]["processFrames"], 3, "the frames stepped")
	assert_eq(run["result"]["met"], true, "met")
	assert_eq(run["result"]["value"], 3, "the met check's value")
	assert_true(rig["tree"].paused, "paused on the met frame")
	_frame(rig)
	assert_eq(checks[0], 3, "no check after the step ended")
	_free_step_rig(rig)


func test_a_step_until_already_true_still_runs_one_frame() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [true, "ready"]
	var run: Dictionary = _start(rig, {"count": 1000}, _until(probe))
	assert_eq(checks[0], 0, "not checked at the step's start")
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends after its first frame")
	assert_eq(run["result"]["processFrames"], 1, "one frame ran")
	assert_eq(run["result"]["value"], "ready", "the value seen")
	_free_step_rig(rig)


func test_a_physics_step_until_is_checked_at_the_tick_after_a_counted_one() -> void:
	var rig: Dictionary = _step_rig()
	var checks: Array = [0]
	var probe := func() -> Array:
		checks[0] += 1
		return [checks[0] >= 2, checks[0]]
	var run: Dictionary = _start(rig, {"count": 10, "unit": "physics"}, _until(probe))
	rig["tree"].physics_frame.emit()
	assert_eq(checks[0], 0, "no check before the first tick has run")
	rig["tree"].physics_frame.emit()
	assert_eq(checks[0], 1, "checked as the second tick starts")
	assert_true(not rig["tree"].paused, "unmet, the ticks go on")
	rig["tree"].physics_frame.emit()
	assert_eq(run["ended"], {"error": ""}, "met at the third tick's start")
	assert_eq(run["result"]["physicsFrames"], 2, "the ticks that ran")
	assert_eq(run["result"]["met"], true, "met")
	assert_true(rig["tree"].paused, "re-paused there, before that tick runs")
	_free_step_rig(rig)


func test_a_step_until_never_met_runs_count_frames_and_answers_last() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [false, "idle"]
	var run: Dictionary = _start(rig, {"count": 2}, _until(probe))
	_frame(rig)
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "not met is a result")
	assert_eq(run["result"]["processFrames"], 2, "count frames ran")
	assert_eq(run["result"]["met"], false, "not met")
	assert_eq(run["result"]["last"], "idle", "the last value seen")
	assert_true(not run["result"].has("value"), "no value")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_every_step_result_has_the_frame_it_stopped_on() -> void:
	var rig: Dictionary = _step_rig()
	var run: Dictionary = _start(rig, {"count": 1}, {})
	_frame(rig)
	assert_eq(run["ended"], {"error": ""}, "the step ends")
	assert_eq(run["result"]["frame"], Engine.get_process_frames(), "the engine's frame at the stop")
	assert_true(not run["result"].has("met"), "no until, no met")
	_free_step_rig(rig)


func test_a_step_until_that_cannot_be_met_fails_the_step_with_its_text() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [false, null, "no node 'Gone' in the running game"]
	var run: Dictionary = _start(rig, {"count": 10}, _until(probe))
	_frame(rig)
	assert_eq(run["ended"], {"error": "no node 'Gone' in the running game"}, "the check's text")
	assert_eq(run["result"]["processFrames"], 1, "the one frame it ran")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_a_physics_step_until_reports_met_when_the_game_paused_on_the_met_tick() -> void:
	var rig: Dictionary = _step_rig()
	var probe := func() -> Array: return [true, "gone"]
	var run: Dictionary = _start(rig, {"count": 10, "unit": "physics"}, _until(probe))
	rig["tree"].physics_frame.emit()
	rig["tree"].paused = true
	rig["tree"].physics_frame.emit()
	assert_eq(run["ended"], {"error": ""}, "met, not the game's own pause change")
	assert_eq(run["result"]["met"], true, "met")
	assert_eq(run["result"]["value"], "gone", "the met check's value")
	assert_eq(run["result"]["physicsFrames"], 1, "the tick that ran")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


func test_a_step_until_refuses_a_ui_changed_with_no_baseline_before_a_frame() -> void:
	var rig: Dictionary = _step_rig()
	var refused: Variant = rig["time"]._stepper._until_of(
		{"until": {"kind": "uiChanged", "uiChanged": true}}
	)
	assert_eq(refused, NO_UI_BASELINE, "wait_for's no-baseline text")
	assert_true(rig["time"]._running.is_empty(), "no step is marked running")
	assert_true(rig["tree"].paused, "no frame ran")
	_free_step_rig(rig)


func test_a_step_until_refuses_a_signal_on_a_missing_node_before_a_frame() -> void:
	var rig: Dictionary = _step_rig()
	var refused: Variant = rig["time"]._stepper._until_of(
		{"until": {"kind": "signal", "node": "Missing", "signal": "fired"}}
	)
	assert_eq(refused, MISSING_NODE, "wait_for's not-found text")
	assert_true(rig["time"]._running.is_empty(), "no step is marked running")
	assert_true(rig["tree"].paused, "no frame ran")
	_free_step_rig(rig)


func test_a_step_until_releases_its_signal_however_the_step_ends() -> void:
	for ending: String in ["met", "count", "cancel"]:
		var rig: Dictionary = _step_rig()
		var until: Variant = _signal_until(rig)
		assert_true(until is Dictionary, "the node and its signal are taken")
		assert_true(_armed(rig), "the until connects the signal")
		_start(rig, {"count": 2}, until)
		_end_step(rig, ending)
		rig["time"]._stepper._release_until(until)
		assert_true(not _armed(rig), "%s: the signal is released" % ending)
		_free_step_rig(rig)


func test_a_stalled_capture_names_the_requested_frames_not_the_frames_run() -> void:
	var rig: Dictionary = _step_rig()
	rig["time"]._deadline_passed = true
	var error: String = await rig["time"]._stepper._save_capture(null, true, 3, 5, {}, {})
	assert_eq(error, STALLED_3_OF_5, "the requested count, not the frames counted")
	assert_true(rig["tree"].paused, "left paused")
	_free_step_rig(rig)


## A clock not in any tree whose Step counts frames on a stand-in tree (paused, with process_frame
## and physics_frame) and at test_draw, and whose bridge holds the conditions, input, inspector and
## JSON modules, and a stand-in Target node outside any tree whose signal a signal until takes.
func _step_rig() -> Dictionary:
	var tree: RefCounted = _compile(
		"extends RefCounted\n\nsignal process_frame\nsignal physics_frame\n\nvar paused: bool = true\n"
	)
	var bridge: Node = _compile(
		(
			"extends Node\n\nvar _conditions: Node\nvar _gestures: Node\nvar _inspect: Node\n"
			+ "var _json: GDScript\nvar target: Node\n\n\nfunc _find_node(element: String) -> Node:\n"
			+ "\treturn target if element == str(target.name) else null\n"
		)
	)
	var target: Node = _compile("extends Node\n\nsignal fired(value: Variant)\n")
	target.name = "Target"
	bridge.target = target
	bridge._conditions = load_bridge_script("godot_mcp_conditions.gd").new()
	bridge._conditions.bridge = bridge
	bridge._gestures = load_bridge_script("godot_mcp_input.gd").new()
	bridge._inspect = load_bridge_script("godot_mcp_inspect.gd").new()
	bridge._inspect._bridge = bridge
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var time: Node = _time_script.new()
	time.bridge = bridge
	time._stepper.frames_tree = tree
	time._stepper.draws = test_draw
	return {"time": time, "tree": tree, "bridge": bridge, "target": target}


func _free_step_rig(rig: Dictionary) -> void:
	rig["time"].free()
	rig["bridge"]._conditions.free()
	rig["bridge"]._gestures.free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["target"].free()


## The until state of a signal condition on the rig's stand-in node, as _guarded_step builds one,
## or the String wait_for's refusal gives when the node or its signal is not there.
func _signal_until(rig: Dictionary) -> Variant:
	var condition: Dictionary = {"kind": "signal", "node": "Target", "signal": "fired"}
	return rig["time"]._stepper._until_of({"until": condition})


## Whether the rig's stand-in node has a listener on its signal.
func _armed(rig: Dictionary) -> bool:
	return not rig["target"].fired.get_connections().is_empty()


## Ends the step in flight as one of its three endings: its until met, its count run out, or the
## deadline a cancel reaches.
func _end_step(rig: Dictionary, ending: String) -> void:
	if ending == "met":
		rig["target"].fired.emit(1)
		_frame(rig)
	elif ending == "count":
		_frame(rig)
		_frame(rig)
	else:
		rig["time"]._deadline_passed = true
		_frame(rig)


## An until state around probe, as the step builds it from an expression condition.
static func _until(probe: Callable) -> Dictionary:
	return {"kind": "expression", "failures": {}, "probe": probe}


## Starts a step that answers into run.ended and run.result once it ends, and lets its lead-in
## draw pass, so the tree is unpaused and its first frame is to come.
func _start(rig: Dictionary, params: Dictionary, until: Dictionary) -> Dictionary:
	var run: Dictionary = {"ended": {}, "result": {}}
	_step_into(rig["time"]._stepper, params, until, run)
	test_draw.emit()
	return run


func _step_into(step: Node, params: Dictionary, until: Dictionary, run: Dictionary) -> void:
	run["ended"]["error"] = await step._step(params, run["result"], until)


## One stepped frame: its process_frame, then its draw.
func _frame(rig: Dictionary) -> void:
	rig["tree"].process_frame.emit()
	test_draw.emit()


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
