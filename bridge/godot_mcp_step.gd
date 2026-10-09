extends Node
## The godot-mcp bridge's step, a child of its clock (godot_mcp_time.gd): advances the scene tree
## by whole drawn frames or by physics ticks and leaves it paused, under the clock's running mark;
## with an until condition it stops at the first counted frame (tick) that meets it.
##
## Stepping: a frame runs its physics ticks, then process, then draw (main/main.cpp L4970-5096 in
## 4.7.2), and process_frame is emitted before the nodes' _process (scene/main/scene_tree.cpp
## L713, L719), so an unpause made from _process would give a partial frame. As the editor's own
## "next frame" does (scene/debugger/scene_debugger.cpp L773-781), a step unpauses and re-pauses
## inside RenderingServer.frame_post_draw handlers, so it covers whole frames, physics included,
## and a capture in the Nth handler shows frame N. An await made inside a handler waits for the
## next emission, since an emission calls the slots it had when it began
## (core/object/object.cpp L1212-1220). A physics step counts SceneTree.physics_frame, which is
## emitted before the nodes' _physics_process (scene_tree.cpp L649-655), and pausing turns the
## physics servers off (scene_tree.cpp L1126-1145), so it re-pauses at the emission after the
## last tick it runs. The number of ticks in a frame varies (main/main_timer_sync.cpp L355-356)
## unless the game runs with --fixed-fps.
##
## Until: the condition is checked where the step counts: a frame at its frame_post_draw, so the
## check sees the state that frame drew, and a tick at the next tick's physics_frame, where the
## step would re-pause; never before the first, so a step runs at least one frame.

const PAUSE_CHANGED := (
	"The game changed its own pause state during the step, " + "after %d of %d frames."
)
const STEP_STALLED := (
	"The step stopped after %d of %d frames: no frame was drawn for too long "
	+ "(is the window minimized?); the game is left paused."
)

## The clock this step belongs to, for its running mark, its waits on a signal and its bridge.
var time: Node
## The tree a step pauses and counts frames on, get_tree() when null, and the signal its frames
## are drawn at: gdtest's stand-ins replace both, since its nodes are outside any tree and its
## headless display server draws nothing.
var frames_tree: Object = null
var draws: Signal = RenderingServer.frame_post_draw


## Refuses a step with a screenshot on a headless game, which draws no frames, a step that would
## wait for draws that never come, and an until condition wait_for would refuse; else runs it
## under the step mark until it ends or its deadline passes: params.deadlineMs, the server's own
## allowance for it, or params.backstopMs when the server sends one.
func _guarded_step(params: Dictionary, result: Dictionary) -> String:
	var headless: String = (
		time.bridge._frame.headless_refusal() if bool(params.get("screenshot", false)) else ""
	)
	if not headless.is_empty():
		return headless
	if not DisplayServer.window_can_draw():
		return time.NO_DRAW_REFUSAL
	if OS.low_processor_usage_mode:
		return time.LOW_PROCESSOR_REFUSAL
	var until: Variant = _until_of(params)
	if until is String:
		return until
	var count: int = maxi(1, int(params.get("count", 1)))
	var deadline: SceneTreeTimer = time._begin(
		"step", float(params.get("deadlineMs", 10000 + 100 * count)), params
	)
	var error: String = await _step(params, result, until)
	time._end(deadline)
	_release_until(until)
	return error


## The until state of params.until, a wait condition {kind, ...}: {kind, failures, probe,
## release?}, empty when params has none, or a String saying why the condition cannot be checked,
## in wait_for's words. A signal condition is connected here and a gameMs one's clock starts
## here, counting unpaused process frames' delta as wait_for's does; release undoes either.
func _until_of(params: Dictionary) -> Variant:
	if not params.get("until") is Dictionary:
		return {}
	var condition: Dictionary = params["until"]
	var until: Dictionary = {"kind": str(condition.get("kind", "")), "failures": {}}
	var probe: Variant
	match until["kind"]:
		"signal":
			probe = time.signal_probe(condition, until)
		"gameMs":
			probe = _game_time_probe(int(condition.get("gameMs", 0)), until)
		_:
			probe = time.bridge._conditions.make_probe(until["kind"], condition, until["failures"])
	if probe is String:
		return probe
	until["probe"] = probe
	return until


## A probe met once target game milliseconds have passed, on a clock ticked at each process_frame
## from now on; until.release disconnects it.
func _game_time_probe(target: int, until: Dictionary) -> Callable:
	var clock: Dictionary = {"seconds": 0.0, "frames": 0}
	var tick: Callable = time._tick_game_clock.bind(clock)
	var frames: Signal = _tree().process_frame
	frames.connect(tick)
	until["release"] = func() -> void: frames.disconnect(tick)
	return time._check_game_time.bind("gameMs", target, clock)


func _release_until(until: Dictionary) -> void:
	if until.has("release"):
		until["release"].call()


func _tree() -> Object:
	return frames_tree if frames_tree != null else get_tree()


## Leaves the tree paused and says how far a step got before its deadline.
func _stalled(counted: int, count: int) -> String:
	_tree().paused = true
	return STEP_STALLED % [counted, count]


## From the next frame_post_draw (a running game runs whole frames until then) unpauses the
## tree, runs params.count frames (or physics ticks), or fewer when until is met first, and
## pauses it again, writing both counts, frame (Engine.get_process_frames() at the stop) and
## until's answer into result, and the capture into result.screenshot when params.screenshot is
## set. Only frames (ticks) that found the tree running count; one that found it paused means the
## game paused itself, and fails the step.
func _step(params: Dictionary, result: Dictionary, until: Dictionary) -> String:
	var count: int = maxi(1, int(params.get("count", 1)))
	var physics: bool = str(params.get("unit", "process")) == "physics"
	var capture: bool = bool(params.get("screenshot", false))
	var others: Dictionary = {"count": 0}
	var ran: Variant = await _run_step(count, physics, capture, others, until)
	if ran is String:
		return ran
	result["frame"] = Engine.get_process_frames()
	result["processFrames"] = others["count"] if physics else ran[0]
	result["physicsFrames"] = ran[0] if physics else others["count"]
	var failed: String = _until_report(until, result)
	if not failed.is_empty() or not capture:
		return failed
	return await _save_capture(ran[1], physics, ran[0], count, params, result)


## Unpauses the tree inside the next frame_post_draw and runs count frames (or physics ticks),
## counting the other unit's frames (ticks) that found the tree running into others.count.
## Returns [counted, the capture or null], or a String when the deadline passed or the game
## paused itself.
func _run_step(
	count: int, physics: bool, capture: bool, others: Dictionary, until: Dictionary
) -> Variant:
	var tree: Object = _tree()
	var count_other := func() -> void:
		if not tree.paused:
			others["count"] += 1
	var other_signal: Signal = tree.process_frame if physics else tree.physics_frame
	if not await time._next(draws):
		return _stalled(0, count)
	other_signal.connect(count_other)
	tree.paused = false
	var ran: Array = [0, null]
	if physics:
		ran[0] = await _run_ticks(count, until)
	else:
		ran = await _run_frames(count, capture, until)
	other_signal.disconnect(count_other)
	if time._deadline_passed:
		return _stalled(ran[0], count)
	if _ended_early(ran[0], count, until):
		return PAUSE_CHANGED % [ran[0], count]
	return ran


## Whether a step stopped short of count for a reason other than its until: a frame (tick) that
## found the tree paused, or a draw that never came.
static func _ended_early(counted: int, count: int, until: Dictionary) -> bool:
	return counted < count and not bool(until.get("stopped", false))


## Checks until's probe, keeping its answer in until.seen; true, marking until.stopped, when it is
## met or cannot be met, so the step ends here. False at once without an until.
static func _until_met(until: Dictionary) -> bool:
	if not until.has("probe"):
		return false
	var seen: Array = until["probe"].call()
	until["seen"] = seen
	until["stopped"] = seen.size() > 2 or seen[0]
	return until["stopped"]


## Writes until's answer into result as wait_for reports a wait's: met, with value when met and
## last when not (args and no last for a signal), and failedChecks. Returns the text of a check
## that could not be met, or "" (and writes nothing) without an until.
func _until_report(until: Dictionary, result: Dictionary) -> String:
	if not until.has("probe"):
		return ""
	var seen: Array = until.get("seen", [false, null])
	if seen.size() > 2:
		return str(seen[2])
	var outcome: Dictionary = {"result": {"met": seen[0]}}
	outcome["result"]["value" if seen[0] else "last"] = seen[1]
	time.bridge._conditions.add_failed_checks(outcome, until["failures"])
	if until["kind"] == "signal":
		outcome = time._as_signal_outcome(outcome)
	result.merge(outcome["result"])
	return ""


## Saves the step's capture into result.screenshot; a physics step's is the frame drawn after its
## last tick, and a stall there names the frames the step was asked for beside the ones it counted.
func _save_capture(
	image: Image, physics: bool, counted: int, count: int, params: Dictionary, result: Dictionary
) -> String:
	if physics:
		if not await time._next(draws):
			return _stalled(counted, count)
		image = time.bridge._frame.grab_frame()
	var saved: Variant = time.bridge._frame.save_screenshot(image, params)
	if saved is String:
		return saved
	result["screenshot"] = saved
	return ""


## Lets count whole frames run, each counted at its frame_post_draw when its process_frame found
## the tree running and until checked there, then captures the last when asked (inside its
## frame_post_draw, so it shows that frame) and pauses the tree there. Stops at a frame that
## found the tree paused, or at the first whose check met until. Returns [frames counted, the
## capture or null].
func _run_frames(count: int, capture: bool, until: Dictionary) -> Array:
	var tree: Object = _tree()
	var running: Array[bool] = [false]
	var note := func() -> void: running[0] = not tree.paused
	tree.process_frame.connect(note)
	var counted: int = 0
	while counted < count:
		var drawn: bool = await time._next(draws)
		if not drawn or not running[0]:
			break
		running[0] = false
		counted += 1
		if _until_met(until):
			break
	tree.process_frame.disconnect(note)
	if _ended_early(counted, count, until):
		return [counted, null]
	var image: Image = null
	if capture:
		image = time.bridge._frame.grab_frame()
	tree.paused = true
	return [counted, image]


## Lets count physics ticks run, each counted when its physics_frame found the tree running, and
## pauses the tree at the next tick's physics_frame, which stops that tick's _physics_process
## and physics server step. What that tick has run by then leaks through: main/main.cpp
## L4972-4999 flushes input, calls iteration_prepare and runs the physics servers' sync() and
## flush_queries() (area and body callbacks into scripts) before physics_frame is emitted.
## until is checked at each physics_frame after a counted tick, before the pause check, so a game
## that paused itself on the tick its condition became true reports met, as the process unit does;
## a met check pauses there. Stops at a tick that found the tree paused with until unmet.
## Returns the ticks counted.
func _run_ticks(count: int, until: Dictionary) -> int:
	var tree: Object = _tree()
	var counted: int = 0
	while counted < count:
		var ticked: bool = await time._next(tree.physics_frame)
		if not ticked:
			return counted
		if counted > 0 and _until_met(until):
			tree.paused = true
			return counted
		if tree.paused:
			return counted
		counted += 1
	if await time._next(tree.physics_frame):
		_until_met(until)
		tree.paused = true
	return counted
