# gdlint: disable=max-public-methods, private-method-call, max-file-lines
# Each test is a public method the runner finds by its test_ prefix, so the count is the coverage;
# the tests call the clock's private helpers directly.
extends "res://gd_test.gd"
## The clock's pure logic (bridge/godot_mcp_time.gd): wait_for's reading of the server's fields
## (test_conditions.gd has its condition kinds and expression parsing, test_equals_conditions.gd
## its JSON comparison).
## The clock is given a stand-in tree only where a wait reads one; every other test calls only
## functions that do not need it.

## Stands in for the tree's process_frame in a game-time wait, so its connections can be read.
signal test_frame

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


## Records each line printed while it is registered, as [text, whether it went to stderr].
class LineRecorder:
	extends Logger

	var lines: Array = []
	var _lock := Mutex.new()

	func _log_message(message: String, error: bool) -> void:
		_lock.lock()
		lines.append([message.strip_edges(), error])
		_lock.unlock()

	## The recorded lines whose text is text.
	func of(text: String) -> Array:
		_lock.lock()
		var found: Array = lines.filter(func(line: Array) -> bool: return line[0] == text)
		_lock.unlock()
		return found


## A wait whose params carry an end marker ends the step's window in the frame its poll ended:
## the marker goes to stdout, then stderr, and the logger's entries hold it as a mark between the
## entries logged before the poll's last check and those logged after it.
# gdlint: ignore=max-line-length
func test_a_wait_with_an_end_marker_prints_it_to_stdout_and_stderr_and_marks_the_logger_when_it_ends(
) -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker met"
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	rig["target"].log_to_feed(Logger.ERROR_TYPE_ERROR, "before")
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, {"endMarker": marker}
	)
	rig["target"].log_to_feed(Logger.ERROR_TYPE_ERROR, "after")
	OS.remove_logger(recorder)
	assert_eq(recorder.of(marker), [[marker, false], [marker, true]], "stdout, then stderr")
	var entries: Array = rig["bridge"]._logger.take_pending()[0]
	assert_eq(
		entries.map(func(entry: Dictionary) -> Array: return [entry["type"], entry["message"]]),
		[["error", "before"], ["mark", marker], ["error", "after"]],
		"the mark falls between the entries logged before and after the wait's end"
	)
	assert_eq(outcome["result"]["met"], true, "the outcome is kept")
	_free_then_rig(rig)


func test_an_unmet_wait_still_prints_its_end_marker() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker unmet"
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [false, "idle"], 0, {"endMarker": marker}
	)
	OS.remove_logger(recorder)
	assert_eq(outcome["result"]["met"], false, "the wait ended unmet")
	assert_eq(recorder.of(marker).size(), 2, "its marker is printed to both streams all the same")
	assert_eq(outcome["result"].get("endMarked"), true, "and the result says so")
	assert_eq(
		rig["bridge"]._logger.take_pending()[0],
		[{"type": "mark", "message": marker}],
		"the logger holds its mark"
	)
	_free_then_rig(rig)


## A shot condition's wait carries endMarkerAtGameMs, the step's pace: met before the game time its
## last check saw reaches the pace, its poll ends nothing (a captured one is held to the pace,
## test_a_shot_met_before_its_end_marker_game_ms_prints_it_once_the_clock_reaches_it); at the
## pace, it ends the window.
func test_a_shot_wait_met_before_its_limit_prints_no_marker() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker shot"
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	var params: Dictionary = {"endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock}
	_time_script.advance_game_clock(clock, false, 0.06)
	_time_script.advance_game_clock(clock, false, 0.06)
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var early: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, params
	)
	var early_lines: int = recorder.of(marker).size()
	var early_entries: Array = rig["bridge"]._logger.take_pending()[0]
	_time_script.advance_game_clock(clock, false, 0.06)
	var at_pace: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, params
	)
	OS.remove_logger(recorder)
	assert_eq(early_lines, 0, "met with 60 ms seen of 100: no marker printed")
	assert_eq(early_entries, [], "and no mark")
	assert_true(not early["result"].has("endMarked"), "and the result does not say endMarked")
	assert_eq(recorder.of(marker).size(), 2, "met with 120 ms seen: the marker is printed")
	assert_eq(at_pace["result"].get("endMarked"), true, "and the result says so")
	_free_then_rig(rig)


func test_a_wait_without_an_end_marker_prints_nothing() -> void:
	var rig: Dictionary = _then_rig()
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, {}
	)
	OS.remove_logger(recorder)
	var printed: Array = recorder.lines.filter(
		func(line: Array) -> bool: return str(line[0]).contains("end marker")
	)
	assert_eq(printed, [], "no marker line")
	assert_eq(rig["bridge"]._logger.take_pending(), [[], 0], "no mark")
	assert_true(not outcome["result"].has("endMarked"), "no endMarked")
	_free_then_rig(rig)


## endMarked is the server's sign that the bridge ended the window and the feed holds its mark, so
## it is on the result of a wait that printed its marker; a dormant bridge, with no logger, ends
## nothing: no marker, no endMarked, and the server closes the window its own way.
# gdlint: ignore=function-name
func test_the_result_says_endMarked() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker said"
	var dormant: String = "[test] end marker dormant"
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, {"endMarker": marker}
	)
	var logger: Logger = rig["bridge"]._logger
	rig["bridge"]._logger = null
	var unmarked: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, {"endMarker": dormant}
	)
	OS.remove_logger(recorder)
	rig["bridge"]._logger = logger
	assert_eq(outcome["result"].get("endMarked"), true, "endMarked: true")
	assert_true(not unmarked["result"].has("endMarked"), "no logger: no endMarked")
	assert_eq(recorder.of(dormant), [], "no logger: no marker printed")
	_free_then_rig(rig)


## A paced step's pace is one wait whose call is PlayStep, carrying keepRaised: the call runs in the
## clock's first frame, which the clock counts, so the pace starts in PlayStep's frame; an error the
## call logs is its raised, not its failure, as for an until's call. Without keepRaised it fails.
func test_a_keep_raised_wait_calls_in_its_clocks_first_frame_and_keeps_the_calls_error() -> void:
	var rig: Dictionary = _then_rig()
	rig["time"].wait_tree = _compile("extends RefCounted\n\nvar paused: bool = false\n")
	var call: Dictionary = {"node": "CallTarget", "method": "fail", "args": []}
	var kept: Array = []
	var failed: Array = []
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	var keeping := func() -> void:
		var called: Dictionary = {}
		var params: Dictionary = {"call": call, "keepRaised": true}
		var why: String = await rig["time"]._start_with_call(params, called, clock, test_frame)
		kept.append([why, called])
	var failing := func() -> void:
		var other: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
		var why: String = await rig["time"]._start_with_call({"call": call}, {}, other, test_frame)
		failed.append(why)
	keeping.call()
	failing.call()
	test_frame.emit()
	rig["bridge"]._logger.take_pending()
	assert_eq(kept.size(), 1, "the keep-raised start answered")
	if kept.size() == 1:
		assert_eq(kept[0][0], "", "the call's logged error is not its failure")
		assert_eq(kept[0][1], {"value": 3, "raised": "the method failed"}, "its value and raised")
	assert_eq(clock["frames"], 1, "the call's frame is the clock's first")
	assert_eq(failed, ["the method failed"], "without keepRaised the logged error fails the call")
	_free_then_rig(rig)


## A shot condition met before its endMarkerAtGameMs, once captured, holds the wait until the game
## time a check sees reaches it, then ends the window: the server sends no remainder wait.
func test_a_shot_met_before_its_end_marker_game_ms_prints_it_once_the_clock_reaches_it() -> void:
	var rig: Dictionary = _then_rig()
	var tree: RefCounted = _compile("extends RefCounted\n\nsignal process_frame\n")
	rig["time"].wait_tree = tree
	var marker: String = "[test] end marker held"
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	var params: Dictionary = {"endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock}
	_time_script.advance_game_clock(clock, false, 0.06)
	_time_script.advance_game_clock(clock, false, 0.06)
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcomes: Array = []
	var holding := func() -> void:
		outcomes.append(
			await rig["time"]._hold_to_window_end(
				{"result": {"met": true}}, params, 60000.0, Time.get_ticks_msec()
			)
		)
	holding.call()
	tree.process_frame.emit()
	var held: bool = outcomes.is_empty() and recorder.of(marker).is_empty()
	_time_script.advance_game_clock(clock, false, 0.06)
	tree.process_frame.emit()
	OS.remove_logger(recorder)
	assert_true(held, "60 ms seen of 100: still held, nothing printed")
	assert_eq(recorder.of(marker).size(), 2, "120 ms seen: the marker is printed")
	assert_eq(outcomes.size(), 1, "and the wait answers")
	if outcomes.size() == 1:
		assert_eq(outcomes[0]["result"].get("endMarked"), true, "with endMarked")
	assert_eq(
		rig["bridge"]._logger.take_pending()[0],
		[{"type": "mark", "message": marker}],
		"the logger holds its mark"
	)
	_free_then_rig(rig)


## A wait met on its limitGameMs clock reports the game time its met check saw, not the frame a
## capture awaited after it ticks in: the capture is stood in for by hand, since it renders.
# gdlint: ignore=max-line-length
func test_a_met_capture_reports_the_game_ms_of_the_met_frame_not_the_capture_frame() -> void:
	var rig: Dictionary = _then_rig()
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	for tick in 2:
		_time_script.advance_game_clock(clock, false, 0.06)
	var marker: String = "[test] met capture"
	var params: Dictionary = {"limitGameMs": 100, "endMarker": marker, "_gameClock": clock}
	_time_script.advance_game_clock(clock, false, 0.06)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, params
	)
	_time_script.advance_game_clock(clock, false, 0.06)
	_time_script._add_game_ms(outcome, params)
	assert_eq(outcome["result"]["gameMs"], 120, "the met frame's game ms, not the capture's")
	_free_then_rig(rig)


## A shot the poll ended before its pace, whose clock the capture's awaited frame carried past
## that pace, still ends its window: the marker prints once per stream and the result says so.
# gdlint: ignore=max-line-length
func test_a_shot_whose_clock_passes_its_pace_during_the_capture_still_ends_its_window() -> void:
	var rig: Dictionary = _then_rig()
	var tree: RefCounted = _compile("extends RefCounted\n\nsignal process_frame\n")
	rig["time"].wait_tree = tree
	var marker: String = "[test] end marker passed during capture"
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	for tick in 2:
		_time_script.advance_game_clock(clock, false, 0.06)
	var params: Dictionary = {"endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock}
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	# The capture's awaited frame is the one that carries seen past the pace, 100 ms.
	_time_script.advance_game_clock(clock, false, 0.06)
	var ended: Dictionary = await rig["time"]._hold_to_window_end(
		{"result": {"met": true}}, params, 60000.0, Time.get_ticks_msec()
	)
	OS.remove_logger(recorder)
	assert_eq(recorder.of(marker), [[marker, false], [marker, true]], "stdout, then stderr")
	assert_eq(ended["result"].get("endMarked"), true, "the result says the window ended")
	_free_then_rig(rig)


## A shot met before its pace reports the game ms of its window end, which the hold reaches before
## the reply, not its met check's: the server's stood-still check reads that figure.
func test_a_held_shot_reports_the_game_ms_of_its_window_end() -> void:
	var rig: Dictionary = _then_rig()
	var tree: RefCounted = _compile("extends RefCounted\n\nsignal process_frame\n")
	rig["time"].wait_tree = tree
	var marker: String = "[test] held shot window end"
	var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
	for tick in 2:
		_time_script.advance_game_clock(clock, false, 0.06)
	var params: Dictionary = {
		"limitGameMs": 100, "endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock
	}
	var outcomes: Array = []
	var holding := func() -> void:
		var ended: Dictionary = await rig["time"]._hold_to_window_end(
			{"result": {"met": true}}, params, 60000.0, Time.get_ticks_msec()
		)
		_time_script._add_game_ms(ended, params)
		outcomes.append(ended)
	holding.call()
	tree.process_frame.emit()
	_time_script.advance_game_clock(clock, false, 0.06)
	tree.process_frame.emit()
	assert_eq(outcomes.size(), 1, "the hold answers once the clock reaches the pace")
	if outcomes.size() == 1:
		assert_eq(outcomes[0]["result"]["gameMs"], 120, "the window end's game ms")
	_free_then_rig(rig)


## A held shot whose clock stands still (frames go by, the game paused) stays held until the server
## cancels the wait; the cancel still ends the window.
func test_a_held_shot_stays_held_while_its_clock_stands_still_and_a_cancel_ends_it() -> void:
	var rig: Dictionary = _then_rig()
	var tree: RefCounted = _compile("extends RefCounted\n\nsignal process_frame\n")
	rig["time"].wait_tree = tree
	var marker: String = "[test] end marker cancelled"
	var clock: Dictionary = {"seconds": 0.06, "seen": 0.06, "frames": 1}
	var params: Dictionary = {"endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock}
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcomes: Array = []
	var holding := func() -> void:
		outcomes.append(
			await rig["time"]._hold_to_window_end(
				{"result": {"met": true}}, params, 60000.0, Time.get_ticks_msec()
			)
		)
	holding.call()
	for frame in 3:
		_time_script.advance_game_clock(clock, true, 0.016)
		tree.process_frame.emit()
	var held: bool = outcomes.is_empty() and recorder.of(marker).is_empty()
	params["_cancelled"] = true
	tree.process_frame.emit()
	OS.remove_logger(recorder)
	assert_true(held, "three paused frames: still held, nothing printed")
	assert_eq(recorder.of(marker).size(), 2, "the cancel ends the window: printed once per stream")
	assert_eq(outcomes.size(), 1, "and the wait answers")
	if outcomes.size() == 1:
		assert_eq(outcomes[0]["result"].get("endMarked"), true, "with endMarked")
	_free_then_rig(rig)


## A paced step's keepRaised wait is never refused on a paused tree: its call (PlayStep, which may
## unpause it) runs, as an until's does.
func test_a_keep_raised_wait_on_a_paused_tree_is_not_refused_and_its_call_runs() -> void:
	var rig: Dictionary = _then_rig()
	var fixture: Node = _compile(
		"extends Node\n\n\nfunc _on_godot_mcp_wait_started(_kind: String) -> void:\n\tpass\n"
	)
	var tree: Object = _wait_tree_rig(fixture)
	tree.paused = true
	rig["time"].wait_tree = tree
	var params: Dictionary = {
		"kind": "gameMs",
		"gameMs": 100,
		"timeoutMs": 5000,
		"keepRaised": true,
		"_cancelled": true,
		"call": {"node": "CallTarget", "method": "add", "args": [1, 2]},
	}
	var outcomes: Array = []
	var waiting := func() -> void: outcomes.append(await rig["time"].wait_for(params))
	waiting.call()
	tree.process_frame.emit()
	assert_eq(outcomes.size(), 1, "the wait answers")
	if outcomes.size() == 1:
		assert_true(outcomes[0].has("result"), "not refused: %s" % outcomes[0])
	assert_eq(rig["target"].calls, 1, "its call ran")
	var plain: Dictionary = params.duplicate()
	plain.erase("keepRaised")
	var refused: Dictionary = await rig["time"].wait_for(plain)
	assert_eq(refused, {"error": rig["time"].PAUSED_REFUSAL}, "without keepRaised: refused")
	fixture.free()
	_free_then_rig(rig)


## endMarkerAtGameMs gates only a met shot wait: one that ends unmet is the step's last wait.
func test_an_unmet_shot_wait_before_its_end_marker_game_ms_still_ends_its_window() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker unmet shot"
	var clock: Dictionary = {"seconds": 0.06, "seen": 0.05, "frames": 2}
	var params: Dictionary = {"endMarker": marker, "endMarkerAtGameMs": 100, "_gameClock": clock}
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [false, "idle"], 0, params
	)
	OS.remove_logger(recorder)
	assert_eq(recorder.of(marker).size(), 2, "the marker is printed")
	assert_eq(outcome["result"].get("endMarked"), true, "and the result says so")
	_free_then_rig(rig)


func test_a_signal_wait_with_an_end_marker_ends_its_window() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker signal"
	var params: Dictionary = {
		"node": "CallTarget",
		"signal": "pinged",
		"call": {"node": "CallTarget", "method": "ping", "args": [4]},
		"endMarker": marker,
	}
	var hold: Dictionary = {}
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var caught: Variant = rig["time"].signal_wait.signal_probe(params, hold)
	var outcome: Dictionary = await rig["time"].signal_wait._call_and_poll_signal(
		caught, params, 1000, hold
	)
	OS.remove_logger(recorder)
	assert_eq(outcome["result"]["met"], true, "the signal wait is met")
	assert_eq(outcome["result"].get("endMarked"), true, "endMarked")
	assert_eq(recorder.of(marker).size(), 2, "the marker is printed")
	_free_then_rig(rig)


## A screenshot wait ends its window before any capture; unmet, it never reaches one.
func test_a_screenshot_wait_with_an_end_marker_ends_its_window() -> void:
	var rig: Dictionary = _then_rig()
	var marker: String = "[test] end marker screenshot"
	var recorder := LineRecorder.new()
	OS.add_logger(recorder)
	var outcome: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [false, "idle"], 0, {"endMarker": marker, "screenshot": true}
	)
	OS.remove_logger(recorder)
	assert_eq(outcome["result"].get("endMarked"), true, "endMarked")
	assert_eq(recorder.of(marker).size(), 2, "the marker is printed")
	_free_then_rig(rig)


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


## A wait reports its start to the godot_mcp_wait_started group before it checks anything, which is
## the hook a scratch fixture times its own event from. The tree is a stand-in, since a runner's
## nodes have none before its root enters; its call_group is what reaches the fixture. The wait ends
## on its first check, so no frame runs between the report and the answer.
func test_a_wait_reports_its_start_to_the_group_before_it_answers() -> void:
	var time: Node = _time_script.new()
	var fixture: Node = (
		_compile(
			(
				"\n"
				. join(
					[
						"extends Node",
						"",
						"var started: Array = []",
						"",
						"",
						"func _on_godot_mcp_wait_started(kind: String) -> void:",
						"\tstarted.append(kind)",
					]
				)
			)
		)
		as Node
	)
	var tree: Object = _wait_tree_rig(fixture)
	time.wait_tree = tree
	var outcome: Dictionary = await time.wait_for({"kind": "frames", "frames": 1, "timeoutMs": 0})
	assert_eq(
		tree.calls,
		[["godot_mcp_wait_started", "_on_godot_mcp_wait_started", "frames"]],
		"the wait's kind, told to the group as the wait starts, exactly once"
	)
	assert_eq(fixture.get("started"), ["frames"], "a node in the group hears it")
	assert_eq(outcome["result"]["met"], false, "the check-once wait is unmet at 0 frames")
	fixture.free()
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


func test_then_record_reads_the_movie_frame_in_the_met_frame() -> void:
	var rig: Dictionary = _then_rig()
	var ran: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 1}}, {"then": {"record": {"mark": "start"}}}, {}
	)
	var frame: int = Engine.get_process_frames()
	assert_eq(ran["result"]["then"].get("movieFrame"), frame, "the frame being written now")
	var without: Dictionary = rig["time"]._finish_then(
		{"result": {"met": true, "frames": 1}}, {"then": {"timeScale": 1.0}}, {}
	)
	assert_true(not without["result"]["then"].has("movieFrame"), "no record, no movie frame")
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
	var caught: Variant = rig["time"].signal_wait.signal_probe(params, hold)
	var outcome: Dictionary = await rig["time"].signal_wait._call_and_poll_signal(
		caught, params, 1000, hold
	)
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
	var caught: Variant = rig["time"].signal_wait.signal_probe(params, hold)
	var outcome: Dictionary = await rig["time"].signal_wait._call_and_poll_signal(
		caught, params, 1000, hold
	)
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
		outcomes.append(await rig["time"].signal_wait._wait_for_signal(params, 1000, test_frame))
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


## A tree a wait reads and told its start to: paused and a process_frame signal as a game's has,
## and a call_group that records each call and forwards it to fixture, which stands in for a scene
## in the group, since a real tree's group map cannot hold a node in a gdtest.
func _wait_tree_rig(fixture: Node) -> Object:
	var tree: Object = _compile(
		(
			"\n"
			. join(
				[
					"extends RefCounted",
					"",
					"signal process_frame",
					"",
					"var paused: bool = false",
					"var fixture: Node",
					"var calls: Array = []",
					"",
					"",
					"func call_group(group: String, method: String, kind: String) -> void:",
					"\tcalls.append([group, method, kind])",
					"\tfixture.call(method, kind)",
				]
			)
		)
	)
	tree.fixture = fixture
	return tree


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
