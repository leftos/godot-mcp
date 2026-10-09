extends Node
## The godot-mcp bridge's clock, a child of the bridge: pauses, resumes and steps the scene tree
## (the step itself runs in its Step child, godot_mcp_step.gd, under this clock's running mark),
## sets Engine.time_scale, waits for a condition checked each frame, and captures frames at
## points of game time.
##
## It runs while the tree is paused: the bridge is PROCESS_MODE_ALWAYS and this child inherits
## it (scene/main/node.cpp L907-935 in 4.7.2), and SceneTree emits process_frame and
## physics_frame every frame, paused or not (scene/main/scene_tree.cpp L649, L713).

## Wakes a step's wait: true for the awaited emission, false for the step's deadline.
signal step_woken(arrived: bool)

const STEP_SCRIPT := "godot_mcp_step.gd"
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
const CAPTURING_REFUSAL := (
	"A capture_frames is still running on this game; wait for its reply before pause, resume, "
	+ "a step or another capture."
)
## wait_for's PAUSED_REFUSAL for capture_frames, whose clock is the game's own.
const CAPTURE_PAUSED_REFUSAL := (
	"The game is paused, so its game time does not advance and capture_frames cannot reach its "
	+ "points; resume it first, or step it with frame_control and its screenshot option."
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
## The wait kinds counted on the game's own clock (_wait_for_game_time).
const GAME_TIME_KINDS: PackedStringArray = ["gameMs", "frames"]
const NOT_DRAWN_WARNING := (
	"The frame the condition was met on was not drawn (is the window minimized, or the game in "
	+ "low-processor mode?), so there is no screenshot."
)

## The bridge this clock belongs to, for its node lookup, JSON conversion and screenshots.
var bridge: Node
## What runs now, "step" or "frames" (capture_frames), or empty: while one runs, pause, resume, a
## step and a capture are refused until it ends.
var _running: String = ""
## Whether the running step's or capture's deadline has passed.
var _deadline_passed: bool = false
## The params Dictionary of the running step or capture, the very one its request handler holds,
## so a cancel ends only that request; null while none runs.
var _running_params: Variant = null
## The Step child (godot_mcp_step.gd beside this script), which runs a step's frames.
var _stepper: Node


func _init() -> void:
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_stepper = (load(script_dir.path_join(STEP_SCRIPT)) as GDScript).new()
	_stepper.name = "Step"
	_stepper.time = self
	add_child(_stepper)


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
			error = await _stepper._guarded_step(params, result)
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


## Why a step or a capture cannot start while the one running goes on.
func _busy_refusal() -> String:
	return CAPTURING_REFUSAL if _running == "frames" else STEPPING_REFUSAL


## Marks kind ("step" or "frames") running for the request holding params, set before
## the first await so a request read in the same frame is refused, and arms its deadline:
## params.backstopMs when the server sends one (its allowance measured in load-adjusted time, so
## the server cancels sooner), else deadline_ms, the server's own allowance; either way a run the
## server has given up on still frees the mark. The timer ignores pause and time scale, and counts
## clip time in a recording, where it can fire before the server's cancel; that is harmless since
## a healthy step or capture counts the same fixed steps the timer does, so only a
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


## Ends the running step or capture as its deadline would, when params is the very Dictionary its
## request holds (a step leaves the tree paused and answers STEP_STALLED; a capture answers the
## frames taken, stopped and missed). Returns whether it ended one.
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


## Captures a frame at each of params.points, ascending seconds of game time from the request:
## the sum of each process frame's delta, which Engine.time_scale already scales, so the points
## follow the game's timers; a frame that finds the tree paused adds nothing, as it stops them.
## A point is due in the first frame whose sum reaches it; that frame is grabbed once at its
## frame_post_draw and saved as take_screenshot saves it (params.crop, no preview), and every point
## due in it shares the file. With params.call {node, method, args}, the method is called in the
## clock's first frame (_call_once), so the points count from its entry. With params.start, a
## wait condition, the clock starts in the frame it is met instead (_capture_from_start). Returns
## {result: frames_result}, with call: {value} when the method was called and start when given,
## stopped with the points missed when the deadline (params.deadlineMs, or params.backstopMs), a
## cancel or the start's timeout ends it first, or {error} when it cannot start, the start's
## condition or then fails, the call fails or a frame cannot be saved, or on a headless game.
func capture_frames(params: Dictionary) -> Dictionary:
	var refusal: String = bridge._frame.headless_refusal()
	if refusal.is_empty():
		refusal = _capture_refusal(get_tree().paused)
	if not refusal.is_empty():
		return {"error": refusal}
	var points: Array = params["points"] if params.get("points") is Array else []
	var deadline: SceneTreeTimer = _begin(
		"frames", float(params.get("deadlineMs", 10000 + 100 * points.size())), params
	)
	var called: Dictionary = {}
	var start: Dictionary = {}
	var taken: Variant = await _capture_from_start(points, params, called, start)
	_end(deadline)
	if taken is String:
		return {"error": taken}
	var result: Dictionary = frames_result(points, taken)
	if not called.is_empty():
		result["call"] = called
	if not start.is_empty():
		result["start"] = start
	return {"result": result}


## The capture's entries (_capture_points) once params.start, when given, is met (_await_start):
## from the met frame itself when it was met in a frame after the request, so this frame's delta
## counts, else from the next process_frame, as without a start. Writes the start's report into
## start. Returns [] when the start timed out, or a String saying why the start's condition, its
## then, the call or a frame failed.
func _capture_from_start(
	points: Array, params: Dictionary, called: Dictionary, start: Dictionary
) -> Variant:
	if not params.get("start") is Dictionary:
		return await _capture_points(points, params, called, false)
	var outcome: Dictionary = await _await_start(params, start)
	if outcome.has("error"):
		return str(outcome["error"])
	if not outcome["result"]["met"]:
		return []
	return await _capture_points(points, params, called, int(outcome["result"]["frames"]) > 0)


## Waits for params.start {kind, node, exists, property, equals, expression, timeoutMs, edge,
## then} as wait_for waits for a condition: a probe (make_probe) checked now and then once a frame
## (_poll), for start.timeoutMs of real time or until a cancel, running start.then in the frame it
## is met (_start_outcome). Writes start_report into start. Returns the poll's outcome, or {error}
## when the condition is refused, cannot be checked or then.call failed.
func _await_start(params: Dictionary, start: Dictionary) -> Dictionary:
	var condition: Dictionary = params["start"]
	var failures: Dictionary = {}
	var kind: String = str(condition.get("kind", ""))
	var probe: Variant = bridge._conditions.make_probe(kind, condition, failures)
	if probe is String:
		return {"error": probe}
	var outcome: Dictionary = await _poll(probe, float(condition.get("timeoutMs", 10000)), params)
	bridge._conditions.add_failed_checks(outcome, failures)
	outcome = _start_outcome(outcome, params)
	if outcome.has("result"):
		start.merge(start_report(outcome["result"], Engine.get_process_frames()))
	return outcome


## The start's poll outcome as the capture goes on from it: not met when a cancel came or the
## deadline passed while it polled (_poll checks met first), so a condition met after either runs
## no then and starts no clock, and nothing awaits between here and the clock's first frame; else
## with params.start.then run in the met frame (_finish_then).
func _start_outcome(outcome: Dictionary, params: Dictionary) -> Dictionary:
	var ended: bool = _deadline_passed or bool(params.get("_cancelled", false))
	if ended and outcome.has("result"):
		outcome["result"]["met"] = false
		return outcome
	return _finish_then(outcome, params["start"], {})


## A capture's start from its poll's result: {met: true, frame} when met in frame, {met: false,
## last} when it timed out, each with then when it ran and failedChecks when checks of it failed.
static func start_report(result: Dictionary, frame: int) -> Dictionary:
	var report: Dictionary = {"met": result["met"]}
	if result["met"]:
		report["frame"] = frame
	else:
		report["last"] = result.get("last", result.get("value"))
	for key: String in ["then", "failedChecks"]:
		if result.has(key):
			report[key] = result[key]
	return report


## Why a capture cannot start now, or empty: a step or another capture runs, or the
## tree is paused, so its game time would not advance.
func _capture_refusal(paused: bool) -> String:
	if not _running.is_empty():
		return _busy_refusal()
	return CAPTURE_PAUSED_REFUSAL if paused else ""


## Runs the capture's clock from the next process_frame, or from this one when in_frame (called
## inside a process_frame emission), until every point is taken, the deadline passes or a cancel
## comes, calling params.call in its first frame into called. Returns the entries (frame_entries)
## taken, or a String saying why the call failed or a frame could not be saved.
func _capture_points(
	points: Array, params: Dictionary, called: Dictionary, in_frame: bool
) -> Variant:
	var tree: SceneTree = get_tree()
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	var entries: Array = []
	var waits: bool = not in_frame
	while entries.size() < points.size():
		if waits and not await _next(tree.process_frame):
			break
		waits = true
		var failed: String = _call_once(params, called)
		if not failed.is_empty():
			return failed
		var took: Variant = await _take_due(points, params, clock, entries)
		if took is String:
			return took
		if not took:
			break
	return entries


## Counts this frame on clock and, when points fall due in it, grabs the frame at its
## frame_post_draw into entries. Returns true to go on, false when the deadline or a cancel came
## before the draw, or a String saying why the frame could not be saved.
func _take_due(points: Array, params: Dictionary, clock: Dictionary, entries: Array) -> Variant:
	advance_game_clock(clock, get_tree().paused, get_process_delta_time())
	var elapsed: float = clock["seconds"]
	var due: Array = due_points(points, entries.size(), elapsed)
	if due.is_empty():
		return true
	if not await _next(RenderingServer.frame_post_draw):
		return false
	var saved: Variant = bridge._frame.save_screenshot(bridge._frame.grab_frame(), params)
	if saved is String:
		return saved
	entries.append_array(frame_entries(due, saved, Engine.get_process_frames(), elapsed))
	return true


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
## screenshot, previewMaxWidth, call, then, edge}. Returns {result: {met, elapsedMs, frames, value
## | args[, call, then, screenshot | warning]}}, with last instead of value on a timeout, or
## {error}. A timeoutMs of 0 checks the condition once, now, paused or not; with call, once right
## after the call (_start_probe_wait). With screenshot, a met wait captures the frame it was met
## on; a met wait runs then, once, in that frame (see _poll_capturing).
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
	var failures: Dictionary = {}
	var probe: Variant = bridge._conditions.make_probe(kind, params, failures)
	if probe is String:
		return {"error": probe}
	var called: Dictionary = {}
	var failed: String = await _start_probe_wait(probe, params, timeout_ms, called)
	if not failed.is_empty():
		return {"error": failed}
	var outcome: Dictionary = await _poll_capturing(probe, timeout_ms, params)
	bridge._conditions.add_failed_checks(outcome, failures)
	return _with_call(outcome, called)


## Readies a probe wait's first check. With params.call: waits for the next process_frame and
## calls the method there (_call_once), after an edge probe's baseline check (_seed_edge), so the
## wait's first check, right after the call in that frame, can meet a rise the call made at once;
## the poll's frames and elapsedMs count from that check. Without a call, seeds a waiting edge
## screenshot wait, whose draw checks would otherwise first look at the first draw. Returns why the
## call or the baseline check failed, or "".
func _start_probe_wait(
	probe: Callable, params: Dictionary, timeout_ms: int, called: Dictionary
) -> String:
	if not params.get("call") is Dictionary:
		var screenshot: bool = bool(params.get("screenshot", false))
		return _seed_edge(probe, params) if screenshot and timeout_ms > 0 else ""
	await get_tree().process_frame
	var failed: String = _seed_edge(probe, params)
	if failed.is_empty():
		failed = _call_once(params, called)
	return failed


## outcome with call: {value} added to its result when the wait called a method.
static func _with_call(outcome: Dictionary, called: Dictionary) -> Dictionary:
	if not called.is_empty() and outcome.has("result"):
		outcome["result"]["call"] = called
	return outcome


## Why a non-signal wait cannot run now, or empty: a pausable node's state cannot change while
## the tree is paused, so a non-signal wait could only time out; only a check-once wait
## (timeout_ms 0) runs while paused.
func _paused_refusal(paused: bool, timeout_ms: int) -> String:
	return PAUSED_REFUSAL if paused and timeout_ms > 0 else ""


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
	return _with_call(outcome, called)


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


## Polls probe as _poll does, for params.backstopMs when the server sends one, else timeout_ms,
## until a cancel of the request; with params.screenshot a met wait also captures the frame it was
## met on: a waiting one checks probe at each frame's draw instead (_check_at_draws), a
## check-once one captures the draw of the frame it runs in. A met wait also runs params.then in
## the met frame (_finish_then). Returns _poll's outcome, its result with then and screenshot or
## warning when met and captured, or {error} when then.call failed.
func _poll_capturing(probe: Callable, timeout_ms: int, params: Dictionary) -> Dictionary:
	var bound_ms: float = _bound_ms(params, timeout_ms)
	if not bool(params.get("screenshot", false)):
		return _finish_then(await _poll(probe, bound_ms, params), params, {})
	var drawn: Dictionary = {"params": params}
	if timeout_ms > 0:
		probe = _check_at_draws(probe, drawn)
	var outcome: Dictionary = await _poll(probe, bound_ms, params)
	_stop_draw_checks(drawn)
	outcome = _finish_then(outcome, params, drawn)
	if outcome.has("error") or not outcome["result"]["met"]:
		return outcome
	return await _with_capture(drawn.get("image"), params, outcome["result"])


## With params.edge, checks probe once now, as the baseline the wait's later checks rise from:
## before params.call (_start_probe_wait), and before a waiting screenshot wait's draw checks
## begin, whose first answer to _poll is no check at all (_checked_since_draw), so without it an
## edge probe would first look at the first draw, and a condition rising before it would read as
## true from the start. An edge probe is never met on its first check, so no capture is skipped.
## Returns the check's failure text, or "".
func _seed_edge(probe: Callable, params: Dictionary) -> String:
	if not bool(params.get("edge", false)):
		return ""
	var seen: Array = probe.call()
	return str(seen[2]) if seen.size() > 2 else ""


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
## captures the frame just drawn and runs then there, in that frame (_run_then_into).
func _check_drawn_frame(drawn: Dictionary) -> void:
	if drawn.has("kept"):
		return
	var probe: Callable = drawn["probe"]
	var seen: Array = _keep(probe.call(), drawn)
	drawn["seen"] = seen
	if seen[0]:
		drawn["image"] = bridge._frame.grab_frame()
		_run_then_into(drawn)


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
	var checked: Array = _keep(probe.call(), drawn)
	if checked[0]:
		_run_then_into(drawn)
	return checked


## Keeps a met or failed answer in drawn.kept, so neither the draw nor the frame check runs the
## probe again; returns seen.
func _keep(seen: Array, drawn: Dictionary) -> Array:
	if seen.size() > 2 or seen[0]:
		drawn["kept"] = seen
	return seen


## outcome with params.then run once and attached to its result, or {error} with the failure text
## when then.call failed. Nothing when the wait has no then or was not met. drawn carries the
## outcome a draw check already ran, so the met frame's then is not run again in a later frame.
func _finish_then(outcome: Dictionary, params: Dictionary, drawn: Dictionary) -> Dictionary:
	if outcome.has("error") or not outcome["result"]["met"] or not params.get("then") is Dictionary:
		return outcome
	var ran: Dictionary
	if drawn.has("then_ran"):
		ran = drawn["then_ran"]
	else:
		ran = _run_then(params)
		if not drawn.is_empty():
			drawn["then_ran"] = ran
	if ran.has("failed"):
		return {"error": _then_failure_text(params, ran, int(outcome["result"]["frames"]))}
	outcome["result"]["then"] = ran
	return outcome


## Runs the met frame's then action into drawn once, so the draw check that met and the poll that
## later reads its answer agree on the outcome.
func _run_then_into(drawn: Dictionary) -> void:
	if not drawn.has("then_ran"):
		drawn["then_ran"] = _run_then(drawn["params"])


## Runs params.then now, once: its call through the inspector, then its timeScale. Returns
## {frame, call?, timeScale?}, {failed, frame} when the call failed, or {} when params has no then.
func _run_then(params: Dictionary) -> Dictionary:
	if not params.get("then") is Dictionary:
		return {}
	var then: Dictionary = params["then"]
	var frame: int = Engine.get_process_frames()
	var ran: Dictionary = {"frame": frame}
	if then.get("call") is Dictionary:
		var called: Variant = bridge._inspect.call_now(then["call"])
		if called is String:
			return {"failed": called, "frame": frame}
		ran["call"] = called
	if then.has("timeScale"):
		var scale: float = float(then["timeScale"])
		var refused: String = _set_time_scale(scale)
		if not refused.is_empty():
			return {"failed": refused, "frame": frame}
		ran["timeScale"] = scale
	return ran


## The text a failed then.call answers: the frames waited, the frame then ran in, and the reason;
## the ", so timeScale was not set" clause only when params.then gives a timeScale.
func _then_failure_text(params: Dictionary, ran: Dictionary, frames: int) -> String:
	var unset: String = ", so timeScale was not set" if params["then"].has("timeScale") else ""
	return (
		"The condition was met after %d frames (frame %d), but then.call failed%s: %s"
		% [frames, int(ran["frame"]), unset, ran["failed"]]
	)


## Adds the capture of the frame the wait was met on to result as result.screenshot: image when a
## draw check took it, else the draw of the frame running now; result.warning instead when that
## frame is not drawn, or at once on a headless game, which draws none.
func _with_capture(image: Image, params: Dictionary, result: Dictionary) -> Dictionary:
	var headless: String = bridge._frame.headless_warning()
	if not headless.is_empty():
		result["warning"] = headless
		return {"result": result}
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


## Resolves on params.node's next emission of params.signal, with its arguments. await has no
## timeout, so a variadic lambda catches the emission and the wait polls it once a frame.
func _wait_for_signal(params: Dictionary, timeout_ms: int) -> Dictionary:
	var hold: Dictionary = {}
	var caught: Variant = signal_probe(params, hold)
	if caught is String:
		return {"error": caught}
	var outcome: Dictionary = await _poll_capturing(caught, timeout_ms, params)
	hold["release"].call()
	return _as_signal_outcome(outcome)


## A probe met once params.node has emitted params.signal since now, with that first emission's
## arguments as its value, connected now; hold.release disconnects it. A String saying why when
## the node or its signal is missing. A step's until shares it.
func signal_probe(params: Dictionary, hold: Dictionary) -> Variant:
	var node_name: String = _text(params, "node")
	var signal_name: String = _text(params, "signal")
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	if not node.has_signal(signal_name):
		return "%s has no signal '%s'" % [node.get_path(), signal_name]
	var fired: Array = []
	var on_signal := func(...args: Array) -> void:
		if fired.is_empty():
			fired.append(args)
	node.connect(signal_name, on_signal)
	# A lambda holding a freed node logs an error when called, so release holds a weak reference.
	var held: WeakRef = weakref(node)
	hold["release"] = func() -> void:
		var source: Object = held.get_ref()
		if source != null and source.is_connected(signal_name, on_signal):
			source.disconnect(signal_name, on_signal)
	return func() -> Array:
		return [not fired.is_empty(), null if fired.is_empty() else bridge._json.to_json(fired[0])]


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
## arrays and objects member by member; anything else by type and value. The watch shares it.
static func json_equal(actual: Variant, wanted: Variant) -> bool:
	if _is_number(actual) and _is_number(wanted):
		return absf(float(actual) - float(wanted)) <= EQUAL_TOLERANCE
	if actual is Array and wanted is Array:
		return _arrays_equal(actual, wanted)
	if actual is Dictionary and wanted is Dictionary:
		return _dictionaries_equal(actual, wanted)
	return typeof(actual) == typeof(wanted) and actual == wanted


static func _arrays_equal(actual: Array, wanted: Array) -> bool:
	if actual.size() != wanted.size():
		return false
	for index in actual.size():
		if not json_equal(actual[index], wanted[index]):
			return false
	return true


static func _dictionaries_equal(actual: Dictionary, wanted: Dictionary) -> bool:
	if actual.size() != wanted.size():
		return false
	for key: Variant in wanted:
		if not actual.has(key) or not json_equal(actual[key], wanted[key]):
			return false
	return true


static func _is_number(value: Variant) -> bool:
	return value is int or value is float


## params[key] when it is a String, else empty: the server leaves out fields it has no value for.
func _text(params: Dictionary, key: String) -> String:
	return params[key] if params.get(key) is String else ""
