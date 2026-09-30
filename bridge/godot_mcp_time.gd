extends Node
## The godot-mcp bridge's clock, a child of the bridge: pauses, resumes and steps the scene tree,
## sets Engine.time_scale, waits for a condition checked each frame, and samples a property
## each frame.
##
## It runs while the tree is paused: the bridge is PROCESS_MODE_ALWAYS and this child inherits
## it (scene/main/node.cpp L907-935 in 4.7.2), and SceneTree emits process_frame and
## physics_frame every frame, paused or not (scene/main/scene_tree.cpp L649, L713).
##
## Stepping: a frame runs its physics ticks, then process, then draw (main/main.cpp L4970-5096),
## and process_frame is emitted before the nodes' _process (scene_tree.cpp L713, L719), so an
## unpause made from _process would give a partial frame. As the editor's own "next frame" does
## (scene/debugger/scene_debugger.cpp L773-781), a step unpauses and re-pauses inside
## RenderingServer.frame_post_draw handlers, so it covers whole frames, physics included, and a
## capture in the Nth handler shows frame N. An await made inside a handler waits for the next
## emission, since an emission calls the slots it had when it began (core/object/object.cpp
## L1212-1220). A physics step counts SceneTree.physics_frame, which
## is emitted before the nodes' _physics_process (scene_tree.cpp L649-655), and pausing turns
## the physics servers off (scene_tree.cpp L1126-1145), so it re-pauses at the emission after
## the last tick it runs. The number of ticks in a frame varies (main/main_timer_sync.cpp
## L355-356) unless the game runs with --fixed-fps.

## Wakes a step's wait: true for the awaited emission, false for the step's deadline.
signal step_woken(arrived: bool)

## Numbers in a property wait match within this.
const EQUAL_TOLERANCE := 1e-6
const PAUSED_REFUSAL := (
	"The game is paused, so only a signal wait or a check-once wait (timeoutMs 0) can be met; "
	+ "resume or step it first."
)
const STEPPING_REFUSAL := (
	"A step is still running on this game; wait for its reply before pause, resume or "
	+ "another step."
)
const MONITORING_REFUSAL := (
	"A monitor_property is still running on this game; wait for its reply before pause, "
	+ "resume, a step or another monitor."
)
const CAPTURING_REFUSAL := (
	"A capture_frames is still running on this game; wait for its reply before pause, resume, "
	+ "a step, a monitor or another capture."
)
## monitor's PAUSED_REFUSAL for capture_frames, whose clock is the game's own.
const CAPTURE_PAUSED_REFUSAL := (
	"The game is paused, so its game time does not advance and capture_frames cannot reach its "
	+ "points; resume it first, or step it with frame_control and its screenshot option."
)
const MONITOR_STALLED := (
	"The monitor stopped after %d of %d frames: its deadline passed before the rest ran "
	+ "(is the game running slowly?)."
)
## A step counts drawn frames, and a frame is drawn only when a window can draw, or, in
## low-processor mode, when something changed (main/main.cpp L5071-5086 in 4.7.2).
const NO_DRAW_REFUSAL := (
	"The game's window cannot draw (is it minimized?), so frames cannot be stepped; "
	+ "restore the window first."
)
const LOW_PROCESSOR_REFUSAL := (
	"The game runs in low-processor mode, which draws only when something changes, "
	+ "so frames cannot be stepped by their draws."
)
const PAUSE_CHANGED := (
	"The game changed its own pause state during the step, " + "after %d of %d frames."
)
const STEP_STALLED := (
	"The step stopped after %d of %d frames: no frame was drawn for too long "
	+ "(is the window minimized?); the game is left paused."
)
## An expression's inputs besides node. Expression resolves only its inputs and its base
## instance's members, not singletons (core/math/expression.cpp L707-734 in 4.7.2).
const EXPRESSION_INPUTS: PackedStringArray = ["root", "tree", "Input", "Engine"]
## The wait kinds counted on the game's own clock (_wait_for_game_time).
const GAME_TIME_KINDS: PackedStringArray = ["gameMs", "frames"]
const NOT_DRAWN_WARNING := (
	"The frame the condition was met on was not drawn (is the window minimized, or the game in "
	+ "low-processor mode?), so there is no screenshot."
)
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ "uiChanged wait was met; send the input first"
)

## The bridge this clock belongs to, for its node lookup, JSON conversion and screenshots.
var bridge: Node
## What runs now, "step", "monitor" or "frames" (capture_frames), or empty: while one runs, pause,
## resume, a step, a monitor and a capture are refused until it ends.
var _running: String = ""
## Whether the running step's or monitor's deadline has passed.
var _deadline_passed: bool = false
## The params Dictionary of the running step or monitor, the very one its request handler holds,
## so a cancel ends only that request; null while none runs.
var _running_params: Variant = null


## Runs params.action: pause, resume, step {count, unit, screenshot, previewMaxWidth} or
## time_scale {scale}. Returns {result: {paused, timeScale, processFrames, physicsFrames[,
## screenshot]}}, or {error}.
func frame_control(params: Dictionary) -> Dictionary:
	var result: Dictionary = {"processFrames": 0, "physicsFrames": 0}
	var action: String = str(params.get("action", ""))
	if not _running.is_empty() and action in ["step", "pause", "resume"]:
		return {"error": _busy_refusal()}
	var error: String = ""
	match action:
		"pause":
			get_tree().paused = true
		"resume":
			get_tree().paused = false
		"time_scale":
			error = _set_time_scale(float(params.get("scale", 0.0)))
		"step":
			error = await _guarded_step(params, result)
		_:
			error = "unknown frame action '%s'" % action
	if not error.is_empty():
		return {"error": error}
	result["paused"] = get_tree().paused
	result["timeScale"] = Engine.time_scale
	return {"result": result}


## Engine.time_scale scales process and physics delta (main/main.cpp L4936-4940, L5001, L5062
## in 4.7.2), and must stay above 0 (Engine.xml L350).
func _set_time_scale(scale: float) -> String:
	if scale <= 0.0:
		return "time_scale needs a scale greater than 0; got %s" % scale
	Engine.time_scale = scale
	return ""


## Why a step, a monitor or a capture cannot start while the one running goes on.
func _busy_refusal() -> String:
	match _running:
		"monitor":
			return MONITORING_REFUSAL
		"frames":
			return CAPTURING_REFUSAL
	return STEPPING_REFUSAL


## Refuses a step that would wait for draws that never come; else runs it under the step mark
## until it ends or its deadline passes: params.deadlineMs, the server's own allowance for it, or
## params.backstopMs when the server sends one.
func _guarded_step(params: Dictionary, result: Dictionary) -> String:
	if not DisplayServer.window_can_draw():
		return NO_DRAW_REFUSAL
	if OS.low_processor_usage_mode:
		return LOW_PROCESSOR_REFUSAL
	var count: int = maxi(1, int(params.get("count", 1)))
	var deadline: SceneTreeTimer = _begin(
		"step", float(params.get("deadlineMs", 10000 + 100 * count)), params
	)
	var error: String = await _step(params, result)
	_end(deadline)
	return error


## Marks kind ("step", "monitor" or "frames") running for the request holding params, set before
## the first await so a request read in the same frame is refused, and arms its deadline:
## params.backstopMs when the server sends one (its allowance measured in load-adjusted time, so
## the server cancels sooner), else deadline_ms, the server's own allowance; either way a run the
## server has given up on still frees the mark. The timer ignores pause and time scale, and counts
## clip time in a recording, where it can fire before the server's cancel; that is harmless since
## a healthy step, monitor or capture counts the same fixed steps the timer does, so only a
## stalled one reaches it, and it then answers as the server's cancel would (_on_deadline).
## Returns the deadline.
func _begin(kind: String, deadline_ms: float, params: Dictionary) -> SceneTreeTimer:
	_running = kind
	_running_params = params
	_deadline_passed = false
	# process_always and ignore_time_scale: the deadline ignores pause and the time scale.
	var seconds: float = _bound_ms(params, deadline_ms) / 1000.0
	var deadline: SceneTreeTimer = get_tree().create_timer(seconds, true, false, true)
	deadline.timeout.connect(_on_deadline)
	return deadline


## The deadline a request runs under: params.backstopMs when the server sends one, else
## fallback_ms, the limit an older server's request carries.
func _bound_ms(params: Dictionary, fallback_ms: float) -> float:
	return float(params.get("backstopMs", fallback_ms))


## Disarms the deadline _begin armed and clears the mark.
func _end(deadline: SceneTreeTimer) -> void:
	if deadline.timeout.is_connected(_on_deadline):
		deadline.timeout.disconnect(_on_deadline)
	_running = ""
	_running_params = null


## Ends the running step, monitor or capture as its deadline would, when params is the very
## Dictionary its request holds (a step leaves the tree paused and answers STEP_STALLED; a monitor
## answers MONITOR_STALLED; a capture answers the frames taken, stopped and missed). Returns
## whether it ended one.
func cancel(params: Dictionary) -> bool:
	if _running.is_empty() or not is_same(params, _running_params):
		return false
	_on_deadline()
	return true


func _on_deadline() -> void:
	_deadline_passed = true
	step_woken.emit(false)


## Waits for source's next emission, resuming inside it, or for the step's deadline; true when
## source came first.
func _next(source: Signal) -> bool:
	if _deadline_passed:
		return false
	var wake := func() -> void: step_woken.emit(true)
	source.connect(wake, CONNECT_ONE_SHOT)
	var arrived: bool = await step_woken
	if source.is_connected(wake):
		source.disconnect(wake)
	return arrived


## Leaves the tree paused and says how far a step got before its deadline.
func _stalled(counted: int, count: int) -> String:
	get_tree().paused = true
	return STEP_STALLED % [counted, count]


## From the next frame_post_draw (a running game runs whole frames until then) unpauses the
## tree, runs params.count frames (or physics ticks) and pauses it again, writing both counts
## into result, and the capture into result.screenshot when params.screenshot is set. Only
## frames (ticks) that found the tree running count; one that found it paused means the game
## paused itself, and fails the step.
func _step(params: Dictionary, result: Dictionary) -> String:
	var count: int = maxi(1, int(params.get("count", 1)))
	var physics: bool = str(params.get("unit", "process")) == "physics"
	var capture: bool = bool(params.get("screenshot", false))
	var others: Dictionary = {"count": 0}
	var ran: Variant = await _run_step(count, physics, capture, others)
	if ran is String:
		return ran
	result["processFrames"] = others["count"] if physics else count
	result["physicsFrames"] = count if physics else others["count"]
	if not capture:
		return ""
	return await _save_capture(ran[1], physics, count, params, result)


## Unpauses the tree inside the next frame_post_draw and runs count frames (or physics ticks),
## counting the other unit's frames (ticks) that found the tree running into others.count.
## Returns [counted, the capture or null], or a String when the deadline passed or the game
## paused itself.
func _run_step(count: int, physics: bool, capture: bool, others: Dictionary) -> Variant:
	var tree: SceneTree = get_tree()
	var count_other := func() -> void:
		if not tree.paused:
			others["count"] += 1
	var other_signal: Signal = tree.process_frame if physics else tree.physics_frame
	if not await _next(RenderingServer.frame_post_draw):
		return _stalled(0, count)
	other_signal.connect(count_other)
	tree.paused = false
	var ran: Array = [0, null]
	if physics:
		ran[0] = await _run_ticks(count)
	else:
		ran = await _run_frames(count, capture)
	other_signal.disconnect(count_other)
	if _deadline_passed:
		return _stalled(ran[0], count)
	if ran[0] < count:
		return PAUSE_CHANGED % [ran[0], count]
	return ran


## Saves the step's capture into result.screenshot; a physics step's is the frame drawn after
## its last tick.
func _save_capture(
	image: Image, physics: bool, count: int, params: Dictionary, result: Dictionary
) -> String:
	if physics:
		if not await _next(RenderingServer.frame_post_draw):
			return _stalled(count, count)
		image = bridge._frame.grab_frame()
	var saved: Variant = bridge._frame.save_screenshot(image, params)
	if saved is String:
		return saved
	result["screenshot"] = saved
	return ""


## Lets count whole frames run, each counted at its frame_post_draw when its process_frame found
## the tree running, then captures the last when asked (inside its frame_post_draw, so it shows
## that frame) and pauses the tree there. Stops at a frame that found the tree paused. Returns
## [frames counted, the capture or null].
func _run_frames(count: int, capture: bool) -> Array:
	var tree: SceneTree = get_tree()
	var running: Array[bool] = [false]
	var note := func() -> void: running[0] = not tree.paused
	tree.process_frame.connect(note)
	var counted: int = 0
	while counted < count:
		var drawn: bool = await _next(RenderingServer.frame_post_draw)
		if not drawn or not running[0]:
			break
		running[0] = false
		counted += 1
	tree.process_frame.disconnect(note)
	if counted < count:
		return [counted, null]
	var image: Image = null
	if capture:
		image = bridge._frame.grab_frame()
	tree.paused = true
	return [counted, image]


## Lets count physics ticks run, each counted when its physics_frame found the tree running, and
## pauses the tree at the next tick's physics_frame, which stops that tick's _physics_process
## and physics server step. What that tick has run by then leaks through: main/main.cpp
## L4972-4999 flushes input, calls iteration_prepare and runs the physics servers' sync() and
## flush_queries() (area and body callbacks into scripts) before physics_frame is emitted.
## Stops at a tick that found the tree paused. Returns the ticks counted.
func _run_ticks(count: int) -> int:
	var tree: SceneTree = get_tree()
	var counted: int = 0
	while counted < count:
		var ticked: bool = await _next(tree.physics_frame)
		if not ticked or tree.paused:
			return counted
		counted += 1
	if await _next(tree.physics_frame):
		tree.paused = true
	return counted


## Samples params.property (a path such as position:x) of params.node at each of params.samples
## frames, or physics ticks with params.unit physics, the first at the next one, each at the
## frame's (tick's) start, before the nodes process it. Returns {result: {samples: [{frame,
## value}], requested, droppedDuplicates, elapsedMs[, pausedAtFrame]}} or {error}; with
## params.changesOnly (the default) a sample equal to the last one kept is dropped and counted,
## the first always kept. It stops early, with pausedAtFrame, at a frame that finds the game has
## paused itself, and ends at params.deadlineMs, the server's allowance, with the frames it got.
func monitor(params: Dictionary) -> Dictionary:
	var refusal: String = _monitor_refusal(_text(params, "node"), _text(params, "property"))
	if not refusal.is_empty():
		return {"error": refusal}
	var count: int = maxi(1, int(params.get("samples", 60)))
	var deadline: SceneTreeTimer = _begin(
		"monitor", float(params.get("deadlineMs", 10000 + 100 * count)), params
	)
	var outcome: Dictionary = await _sample(params, count)
	_end(deadline)
	return outcome


## Why a monitor cannot start now, or empty: a step or another monitor runs, the tree is paused
## (its frames would show the property frozen, so wait_for's rule applies), or the node or its
## property is missing.
func _monitor_refusal(node_name: String, property: String) -> String:
	if not _running.is_empty():
		return _busy_refusal()
	var refusal: String = _paused_refusal(get_tree().paused, 1)
	if not refusal.is_empty():
		return refusal
	if bridge._find_node(node_name) == null:
		return bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	var checked: Array = _check_property(node_name, property, null)
	return checked[2] if checked.size() > 2 else ""


## Takes count samples, one at each process_frame (physics_frame with unit physics), until the
## deadline passes. A node freed mid-way samples as null. A frame (tick) that finds the tree
## paused means the game paused itself: the monitor stops there, unsampled, and its number is
## returned as pausedAtFrame with the samples so far. process_frame and physics_frame are emitted
## paused or not (scene/main/scene_tree.cpp L649, L713).
func _sample(params: Dictionary, count: int) -> Dictionary:
	var physics: bool = str(params.get("unit", "process")) == "physics"
	var source: Signal = get_tree().physics_frame if physics else get_tree().process_frame
	var changes_only: bool = bool(params.get("changesOnly", true))
	var began: int = Time.get_ticks_msec()
	var kept: Array = []
	var dropped: int = 0
	var paused_at: int = -1
	for frame in count:
		if not await _next(source):
			return {"error": MONITOR_STALLED % [frame, count]}
		if get_tree().paused:
			paused_at = frame
			break
		var value: Variant = _check_property(_text(params, "node"), _text(params, "property"), null)[1]
		if changes_only and _repeats(kept, value):
			dropped += 1
		else:
			kept.append({"frame": frame, "value": value})
	var result: Dictionary = {
		"samples": kept,
		"requested": count,
		"droppedDuplicates": dropped,
		"elapsedMs": Time.get_ticks_msec() - began,
	}
	if paused_at >= 0:
		result["pausedAtFrame"] = paused_at
	return {"result": result}


## Whether value equals the last sample kept, in JSON space; false before the first.
func _repeats(kept: Array, value: Variant) -> bool:
	return not kept.is_empty() and _json_equal(value, kept[-1]["value"])


## Captures a frame at each of params.points, ascending seconds of game time from the request:
## the sum of each process frame's delta, which Engine.time_scale already scales, so the points
## follow the game's timers; a frame that finds the tree paused adds nothing, as it stops them.
## A point is due in the first frame whose sum reaches it; that frame is grabbed once at its
## frame_post_draw and saved as take_screenshot saves it (params.crop, no preview), and every point
## due in it shares the file. With params.call {node, method, args}, the method is called in the
## clock's first frame (_call_once), so the points count from its entry. Returns {result:
## frames_result}, with call: {value} when the method was called, stopped with the points missed
## when the deadline (params.deadlineMs, or params.backstopMs) or a cancel ends it first, or
## {error} when it cannot start, the call fails or a frame cannot be saved.
func capture_frames(params: Dictionary) -> Dictionary:
	var refusal: String = _capture_refusal(get_tree().paused)
	if not refusal.is_empty():
		return {"error": refusal}
	var points: Array = params["points"] if params.get("points") is Array else []
	var deadline: SceneTreeTimer = _begin(
		"frames", float(params.get("deadlineMs", 10000 + 100 * points.size())), params
	)
	var called: Dictionary = {}
	var taken: Variant = await _capture_points(points, params, called)
	_end(deadline)
	if taken is String:
		return {"error": taken}
	var result: Dictionary = frames_result(points, taken)
	if not called.is_empty():
		result["call"] = called
	return {"result": result}


## Why a capture cannot start now, or empty: a step, a monitor or another capture runs, or the
## tree is paused, so its game time would not advance.
func _capture_refusal(paused: bool) -> String:
	if not _running.is_empty():
		return _busy_refusal()
	return CAPTURE_PAUSED_REFUSAL if paused else ""


## Runs the capture's clock from the next process_frame until every point is taken, the deadline
## passes or a cancel comes, calling params.call in its first frame into called. Returns the
## entries (frame_entries) taken, or a String saying why the call failed or a frame could not be
## saved.
func _capture_points(points: Array, params: Dictionary, called: Dictionary) -> Variant:
	var tree: SceneTree = get_tree()
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	var entries: Array = []
	while entries.size() < points.size():
		if not await _next(tree.process_frame):
			break
		var failed: String = _call_once(params, called)
		if not failed.is_empty():
			return failed
		advance_game_clock(clock, tree.paused, get_process_delta_time())
		var elapsed: float = clock["seconds"]
		var due: Array = due_points(points, entries.size(), elapsed)
		if due.is_empty():
			continue
		if not await _next(RenderingServer.frame_post_draw):
			break
		var saved: Variant = bridge._frame.save_screenshot(bridge._frame.grab_frame(), params)
		if saved is String:
			return saved
		entries.append_array(frame_entries(due, saved, Engine.get_process_frames(), elapsed))
	return entries


## Calls params.call {node, method, args} with the inspector's call_now, the first time it is
## asked in a request, putting its {value} into called; nothing when params has no call or called
## already holds its value. Returns why the call failed, or "".
##
## It runs inside a process_frame emission, which SceneTree::process sends before it processes
## the nodes (scene/main/scene_tree.cpp L713, L719 in 4.7.2), so a method called here has its
## nodes' _process run in the same frame with that frame's delta: the clock counts that delta
## too, and its game time is the game time the method's effect has seen.
func _call_once(params: Dictionary, called: Dictionary) -> String:
	if not called.is_empty() or not params.get("call") is Dictionary:
		return ""
	var outcome: Variant = bridge._inspect.call_now(params["call"])
	if outcome is String:
		return outcome
	called.merge(outcome)
	return ""


## The points from index taken on that are due at elapsed seconds: each at or before elapsed, in
## order, stopping at the first still to come.
static func due_points(points: Array, taken: int, elapsed: float) -> Array:
	var due: Array = []
	for index in range(taken, points.size()):
		if float(points[index]) > elapsed:
			break
		due.append(points[index])
	return due


## One entry per due point, all sharing saved's file: {at, frame, gameSeconds, late, path, width,
## height}, with late the seconds gameSeconds passed at by, to the millisecond.
static func frame_entries(due: Array, saved: Dictionary, frame: int, elapsed: float) -> Array:
	var entries: Array = []
	for at: Variant in due:
		(
			entries
			. append(
				{
					"at": at,
					"frame": frame,
					"gameSeconds": elapsed,
					"late": snappedf(elapsed - float(at), 0.001),
					"path": saved["path"],
					"width": saved["width"],
					"height": saved["height"],
				}
			)
		)
	return entries


## A capture's result: {frames}, plus stopped: true and missed, the points not taken, when it ended
## before taking them all.
static func frames_result(points: Array, entries: Array) -> Dictionary:
	var result: Dictionary = {"frames": entries}
	if entries.size() < points.size():
		result["stopped"] = true
		result["missed"] = points.slice(entries.size())
	return result


## Waits for params.kind (exists, property, signal, expression, uiChanged, gameMs or frames) with
## params {node, exists, property, equals, signal, expression, gameMs, frames, timeoutMs,
## screenshot, previewMaxWidth}. Returns {result: {met, elapsedMs, frames, value | args[,
## screenshot | warning]}}, with last instead of value on a timeout, or {error}. A timeoutMs of 0
## checks the condition once, now, paused or not. With screenshot, a met wait captures the frame
## it was met on (see _poll_capturing).
func wait_for(params: Dictionary) -> Dictionary:
	var kind: String = str(params.get("kind", ""))
	var timeout_ms: int = int(params.get("timeoutMs", 10000))
	if kind == "signal":
		return await _wait_for_signal(params, timeout_ms)
	var refusal: String = _paused_refusal(get_tree().paused, timeout_ms)
	if not refusal.is_empty():
		return {"error": refusal}
	if kind in GAME_TIME_KINDS:
		return await _wait_for_game_time(kind, params, timeout_ms, get_tree().process_frame)
	var probe: Variant = _make_probe(kind, params)
	if probe is String:
		return {"error": probe}
	return await _poll_capturing(probe, timeout_ms, params)


## Why a non-signal wait cannot run now, or empty: a pausable node's state cannot change while
## the tree is paused, so a non-signal wait could only time out; only a check-once wait
## (timeout_ms 0) runs while paused.
func _paused_refusal(paused: bool, timeout_ms: int) -> String:
	return PAUSED_REFUSAL if paused and timeout_ms > 0 else ""


## A Callable returning [met, value], or [false, null, error] when the wait cannot be met, for
## the condition; a String saying why there is none.
func _make_probe(kind: String, params: Dictionary) -> Variant:
	var node_name: String = _text(params, "node")
	var probe: Variant = "unknown condition kind '%s'" % kind
	match kind:
		"exists":
			probe = _check_exists.bind(node_name, bool(params.get("exists", true)))
		"property":
			var property: String = _text(params, "property")
			probe = _check_property.bind(node_name, property, params.get("equals"))
		"expression":
			probe = _parse_expression(_text(params, "expression"), node_name)
		"uiChanged":
			probe = _check_ui_changed if bridge._gestures.has_ui_baseline() else NO_UI_BASELINE
	return probe


## Waits for params.gameMs milliseconds of game time or params.frames unpaused process frames,
## counted by a clock that frame (the tree's process_frame) ticks from its next emission on. The
## clock lives outside the probe, which may run several times a frame (at a draw and at the
## frame), so the probe only reads it; it is disconnected however the poll ends. With params.call
## the clock starts with the method's call instead (_start_with_call), and a met or timed-out
## result adds call: {value}; a failed call answers {error} and no wait.
func _wait_for_game_time(
	kind: String, params: Dictionary, timeout_ms: int, frame: Signal
) -> Dictionary:
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	var called: Dictionary = {}
	var failed: String = await _start_with_call(params, called, clock, frame)
	if not failed.is_empty():
		return {"error": failed}
	var tick: Callable = _tick_game_clock.bind(clock)
	frame.connect(tick)
	var probe: Callable = _check_game_time.bind(kind, int(params.get(kind, 0)), clock)
	var outcome: Dictionary = await _poll_capturing(probe, timeout_ms, params)
	frame.disconnect(tick)
	if not called.is_empty() and outcome.has("result"):
		outcome["result"]["call"] = called
	return outcome


## With params.call: waits for frame's next emission, calls the method there (_call_once, which
## says why that frame is the one) and counts that frame on clock, so the wait's clock starts with
## the method's entry; a tick connected afterwards is not called in the emission it was connected
## in, since emit_signalp copies the slots before calling them (core/object/object.cpp
## L1212-1218 in 4.7.2). Returns why the call failed, or ""; without a call, "" at once.
func _start_with_call(
	params: Dictionary, called: Dictionary, clock: Dictionary, frame: Signal
) -> String:
	if not params.get("call") is Dictionary:
		return ""
	await frame
	var failed: String = _call_once(params, called)
	if failed.is_empty():
		_tick_game_clock(clock)
	return failed


## Advances clock by this frame, as _capture_points counts game time: this node's process delta,
## which Engine.time_scale already scales, unless the tree is paused.
func _tick_game_clock(clock: Dictionary) -> void:
	advance_game_clock(clock, get_tree().paused, get_process_delta_time())


## Adds a frame of delta seconds to clock {seconds, frames}; a paused frame adds nothing.
static func advance_game_clock(clock: Dictionary, paused: bool, delta: float) -> void:
	if paused:
		return
	clock["seconds"] += delta
	clock["frames"] += 1


## Met once clock has reached target: whole game milliseconds for gameMs, frames for frames; the
## value is what it has reached.
func _check_game_time(kind: String, target: int, clock: Dictionary) -> Array:
	var reached: int = int(clock["frames"])
	if kind == "gameMs":
		reached = floori(float(clock["seconds"]) * 1000.0)
	return [reached >= target, reached]


## Met on the first check whose UI differs from the input module's baseline, with the change as
## its value; a met wait uses the baseline up, and one that times out leaves it.
func _check_ui_changed() -> Array:
	var change: Dictionary = bridge._gestures.ui_change()
	if change.is_empty():
		return [false, null]
	bridge._gestures.use_up_ui_baseline()
	return [true, change]


## Polls probe as _poll does, for params.backstopMs when the server sends one, else timeout_ms,
## until a cancel of the request; with params.screenshot a met wait also captures the frame it was
## met on: a waiting one checks probe at each frame's draw instead (_check_at_draws), a
## check-once one captures the draw of the frame it runs in. Returns _poll's outcome, its result
## with screenshot or warning when met and captured.
func _poll_capturing(probe: Callable, timeout_ms: int, params: Dictionary) -> Dictionary:
	var bound_ms: float = _bound_ms(params, timeout_ms)
	if not bool(params.get("screenshot", false)):
		return await _poll(probe, bound_ms, params)
	var drawn: Dictionary = {}
	if timeout_ms > 0:
		probe = _check_at_draws(probe, drawn)
	var outcome: Dictionary = await _poll(probe, bound_ms, params)
	_stop_draw_checks(drawn)
	if outcome.has("error") or not outcome["result"]["met"]:
		return outcome
	return await _with_capture(drawn.get("image"), params, outcome["result"])


## Checks probe now and then once a frame until it is met, cannot be met, bound_ms has passed,
## params.timeoutFrames frames have passed (a recording's wait, which the server counts in movie
## frames), or the request's params are marked _cancelled (the server's cancel). Returns {result:
## {met, elapsedMs, frames, value | last}}, plus clipMs for a frame-counted wait, or {error}.
func _poll(probe: Callable, bound_ms: float, params: Dictionary = {}) -> Dictionary:
	var began: int = Time.get_ticks_msec()
	var frames: int = 0
	var seen: Array = probe.call()
	while _keeps_polling(seen, frames, Time.get_ticks_msec() - began, bound_ms, params):
		await get_tree().process_frame
		frames += 1
		seen = probe.call()
	if seen.size() > 2:
		return {"error": seen[2]}
	var result: Dictionary = {
		"met": seen[0], "elapsedMs": Time.get_ticks_msec() - began, "frames": frames
	}
	result["value" if seen[0] else "last"] = seen[1]
	if params.has("timeoutFrames"):
		_add_clip_ms(result, frames)
	return {"result": result}


## Whether a poll goes on after seeing seen, frames frames and elapsed_ms real ms in: not met and
## not failed, within bound_ms, not cancelled, and, for a frame-counted wait, short of
## params.timeoutFrames.
static func _keeps_polling(
	seen: Array, frames: int, elapsed_ms: int, bound_ms: float, params: Dictionary
) -> bool:
	if seen.size() != 2 or seen[0]:
		return false
	if elapsed_ms >= bound_ms or params.get("_cancelled", false):
		return false
	return not params.has("timeoutFrames") or frames < int(params["timeoutFrames"])


## Adds clipMs, the clip time frames movie frames make (frames x 1000 / the recording's rate), to
## a frame-counted wait's result; nothing when the game does not record.
func _add_clip_ms(result: Dictionary, frames: int) -> void:
	var fps: int = bridge._gestures.clip_fps()
	if fps > 0:
		result["clipMs"] = floori(float(frames) * 1000.0 / float(fps))


## For a waiting screenshot wait: checks probe at each frame_post_draw, where the state it sees
## is the one that frame shows, and captures the frame of the first met check into drawn.image.
## Returns the probe _poll then calls once a frame (_checked_since_draw).
func _check_at_draws(probe: Callable, drawn: Dictionary) -> Callable:
	drawn["probe"] = probe
	var on_draw: Callable = _check_drawn_frame.bind(drawn)
	drawn["on_draw"] = on_draw
	RenderingServer.frame_post_draw.connect(on_draw)
	return _checked_since_draw.bind(drawn)


func _stop_draw_checks(drawn: Dictionary) -> void:
	if drawn.has("on_draw"):
		RenderingServer.frame_post_draw.disconnect(drawn["on_draw"])


## Checks drawn.probe inside a frame_post_draw, until a check is met or fails; the met one
## captures the frame just drawn.
func _check_drawn_frame(drawn: Dictionary) -> void:
	if drawn.has("kept"):
		return
	var probe: Callable = drawn["probe"]
	var seen: Array = _keep(probe.call(), drawn)
	drawn["seen"] = seen
	if seen[0]:
		drawn["image"] = bridge._frame.grab_frame()


## The check of the frame drawn since the last call, or the met or failed one kept; nothing on the
## first call, whose frame's draw is still to come; and probe's own answer when no frame was drawn
## since the last call (a minimized window, low-processor mode), so the wait still ends.
func _checked_since_draw(drawn: Dictionary) -> Array:
	if drawn.has("kept"):
		return drawn["kept"]
	if drawn.has("seen"):
		var seen: Array = drawn["seen"]
		drawn.erase("seen")
		return seen
	if not drawn.has("started"):
		drawn["started"] = true
		return [false, null]
	var probe: Callable = drawn["probe"]
	return _keep(probe.call(), drawn)


## Keeps a met or failed answer in drawn.kept, so neither the draw nor the frame check runs the
## probe again; returns seen.
func _keep(seen: Array, drawn: Dictionary) -> Array:
	if seen.size() > 2 or seen[0]:
		drawn["kept"] = seen
	return seen


## Adds the capture of the frame the wait was met on to result as result.screenshot: image when a
## draw check took it, else the draw of the frame running now; result.warning instead when that
## frame is not drawn.
func _with_capture(image: Image, params: Dictionary, result: Dictionary) -> Dictionary:
	if image == null:
		image = await _capture_this_frame()
	if image == null:
		result["warning"] = NOT_DRAWN_WARNING
		return {"result": result}
	var saved: Variant = bridge._frame.save_screenshot(image, params)
	if saved is String:
		return {"error": saved}
	result["screenshot"] = saved
	return {"result": result}


## The image of the frame drawn before the next process_frame, which from a request's handler or
## a process_frame is the frame running now, or null when that frame is not drawn.
func _capture_this_frame() -> Image:
	var shot: Array[Image] = []
	var grab := func() -> void: shot.append(bridge._frame.grab_frame())
	RenderingServer.frame_post_draw.connect(grab, CONNECT_ONE_SHOT)
	await get_tree().process_frame
	if RenderingServer.frame_post_draw.is_connected(grab):
		RenderingServer.frame_post_draw.disconnect(grab)
	return null if shot.is_empty() else shot[0]


func _check_exists(node_name: String, wanted: bool) -> Array:
	var present: bool = bridge._find_node(node_name) != null
	return [present == wanted, present]


## The property (a path such as position:x) as run_script returns values, compared in JSON
## space; not met while the node is missing, and failed once it is found without the property.
func _check_property(node_name: String, property: String, wanted: Variant) -> Array:
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return [false, null]
	var first: String = property.get_slice(":", 0)
	if not bridge._json.has_property(node, first):
		return [false, null, "'%s' has no property '%s'." % [node.get_path(), first]]
	var value: Variant = bridge._json.to_json(node.get_indexed(NodePath(property)))
	return [_json_equal(value, wanted), value]


## Parses source once with its inputs (node too when node_name is set, also the base instance)
## and returns the Callable that runs it; a String with Expression's error when it does not parse.
func _parse_expression(source: String, node_name: String) -> Variant:
	var names: PackedStringArray = EXPRESSION_INPUTS.duplicate()
	if not node_name.is_empty():
		names.append("node")
	var expression := Expression.new()
	if expression.parse(source, names) != OK:
		return "the expression does not parse: %s" % expression.get_error_text()
	return _run_expression.bind(expression, node_name, {})


## Met when the expression returns true. It is not run while its node is missing. A failed run
## counts as not met, and each distinct failure is pushed to the error feed once: execute runs
## with show_error off, since it would log the same failure every frame (expression.cpp
## L1494-1508).
func _run_expression(expression: Expression, node_name: String, reported: Dictionary) -> Array:
	var tree: SceneTree = get_tree()
	var inputs: Array = [tree.root, tree, Input, Engine]
	var node: Node = null
	if not node_name.is_empty():
		node = bridge._find_node(node_name)
		if node == null:
			return [false, null]
		inputs.append(node)
	var value: Variant = expression.execute(inputs, node, false)
	if expression.has_execute_failed():
		var failure: String = expression.get_error_text()
		if not reported.has(failure):
			reported[failure] = true
			push_error("godot-mcp wait_for: the expression failed: %s" % failure)
		return [false, null]
	return [value is bool and value == true, bridge._json.to_json(value)]


## Resolves on params.node's next emission of params.signal, with its arguments. await has no
## timeout, so a variadic lambda catches the emission and the wait polls it once a frame.
func _wait_for_signal(params: Dictionary, timeout_ms: int) -> Dictionary:
	var node_name: String = _text(params, "node")
	var signal_name: String = _text(params, "signal")
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return {
			"error": bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
		}
	if not node.has_signal(signal_name):
		return {"error": "%s has no signal '%s'" % [node.get_path(), signal_name]}
	var fired: Array = []
	var on_signal := func(...args: Array) -> void:
		if fired.is_empty():
			fired.append(args)
	node.connect(signal_name, on_signal)
	var caught := func() -> Array:
		return [not fired.is_empty(), null if fired.is_empty() else bridge._json.to_json(fired[0])]
	var outcome: Dictionary = await _poll_capturing(caught, timeout_ms, params)
	if is_instance_valid(node) and node.is_connected(signal_name, on_signal):
		node.disconnect(signal_name, on_signal)
	return _as_signal_outcome(outcome)


## A signal wait's outcome: the arguments as args instead of value, and no last.
func _as_signal_outcome(outcome: Dictionary) -> Dictionary:
	if outcome.has("error"):
		return outcome
	var result: Dictionary = outcome["result"]
	if result.has("value"):
		result["args"] = result["value"]
		result.erase("value")
	result.erase("last")
	return {"result": result}


## A JSON value compared with another: numbers within EQUAL_TOLERANCE, whatever their type;
## arrays and objects member by member; anything else by type and value.
func _json_equal(actual: Variant, wanted: Variant) -> bool:
	if _is_number(actual) and _is_number(wanted):
		return absf(float(actual) - float(wanted)) <= EQUAL_TOLERANCE
	if actual is Array and wanted is Array:
		return _arrays_equal(actual, wanted)
	if actual is Dictionary and wanted is Dictionary:
		return _dictionaries_equal(actual, wanted)
	return typeof(actual) == typeof(wanted) and actual == wanted


func _arrays_equal(actual: Array, wanted: Array) -> bool:
	if actual.size() != wanted.size():
		return false
	for index in actual.size():
		if not _json_equal(actual[index], wanted[index]):
			return false
	return true


func _dictionaries_equal(actual: Dictionary, wanted: Dictionary) -> bool:
	if actual.size() != wanted.size():
		return false
	for key: Variant in wanted:
		if not actual.has(key) or not _json_equal(actual[key], wanted[key]):
			return false
	return true


func _is_number(value: Variant) -> bool:
	return value is int or value is float


## params[key] when it is a String, else empty: the server leaves out fields it has no value for.
func _text(params: Dictionary, key: String) -> String:
	return params[key] if params.get(key) is String else ""
