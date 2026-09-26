extends Node
## The godot-mcp bridge's clock, a child of the bridge: pauses, resumes and steps the scene tree,
## sets Engine.time_scale, and waits for a condition checked each frame.
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

## The bridge this clock belongs to, for its node lookup, JSON conversion and screenshots.
var bridge: Node
## Whether a step is running; pause, resume and another step are refused until it ends.
var _stepping: bool = false
## Whether the running step's deadline has passed.
var _deadline_passed: bool = false


## Runs params.action: pause, resume, step {count, unit, screenshot, previewMaxWidth} or
## time_scale {scale}. Returns {result: {paused, timeScale, processFrames, physicsFrames[,
## screenshot]}}, or {error}.
func frame_control(params: Dictionary) -> Dictionary:
	var result: Dictionary = {"processFrames": 0, "physicsFrames": 0}
	var action: String = str(params.get("action", ""))
	if _stepping and action in ["step", "pause", "resume"]:
		return {"error": STEPPING_REFUSAL}
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


## Refuses a step that would wait for draws that never come; else marks a step running (set
## before the first await, so a request read in the same frame is refused) until it ends or
## its deadline passes: params.deadlineMs, the server's own allowance for the step, so a step
## the server has given up on still frees the mark.
func _guarded_step(params: Dictionary, result: Dictionary) -> String:
	if not DisplayServer.window_can_draw():
		return NO_DRAW_REFUSAL
	if OS.low_processor_usage_mode:
		return LOW_PROCESSOR_REFUSAL
	_stepping = true
	_deadline_passed = false
	var count: int = maxi(1, int(params.get("count", 1)))
	var deadline_ms: float = float(params.get("deadlineMs", 10000 + 100 * count))
	# process_always and ignore_time_scale: the deadline runs in real time, paused or not.
	var deadline: SceneTreeTimer = get_tree().create_timer(deadline_ms / 1000.0, true, false, true)
	deadline.timeout.connect(_on_deadline)
	var error: String = await _step(params, result)
	if deadline.timeout.is_connected(_on_deadline):
		deadline.timeout.disconnect(_on_deadline)
	_stepping = false
	return error


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
		image = bridge.get_viewport().get_texture().get_image()
	var saved: Variant = bridge._save_screenshot(image, params)
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
		image = bridge.get_viewport().get_texture().get_image()
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


## Waits for params.kind (exists, property, signal or expression) with params {node, exists,
## property, equals, signal, expression, timeoutMs}. Returns {result: {met, elapsedMs, frames,
## value | args}}, with last instead of value on a timeout, or {error}. A timeoutMs of 0 checks
## the condition once, now, paused or not.
func wait_for(params: Dictionary) -> Dictionary:
	var kind: String = str(params.get("kind", ""))
	var timeout_ms: int = int(params.get("timeoutMs", 10000))
	if kind == "signal":
		return await _wait_for_signal(_text(params, "node"), _text(params, "signal"), timeout_ms)
	var refusal: String = _paused_refusal(get_tree().paused, timeout_ms)
	if not refusal.is_empty():
		return {"error": refusal}
	var probe: Variant = _make_probe(kind, params)
	if probe is String:
		return {"error": probe}
	return await _poll(probe, timeout_ms)


## Why a non-signal wait cannot run now, or empty: a paused tree runs no frames to check it on,
## so only a check-once wait (timeout_ms 0), which needs none, runs while paused.
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
	return probe


## Checks probe now and then once a frame until it is met, cannot be met, or timeout_ms has
## passed. Returns {result: {met, elapsedMs, frames, value | last}} or {error}.
func _poll(probe: Callable, timeout_ms: int) -> Dictionary:
	var began: int = Time.get_ticks_msec()
	var frames: int = 0
	var seen: Array = probe.call()
	while seen.size() == 2 and not seen[0] and Time.get_ticks_msec() - began < timeout_ms:
		await get_tree().process_frame
		frames += 1
		seen = probe.call()
	if seen.size() > 2:
		return {"error": seen[2]}
	var result: Dictionary = {
		"met": seen[0], "elapsedMs": Time.get_ticks_msec() - began, "frames": frames
	}
	result["value" if seen[0] else "last"] = seen[1]
	return {"result": result}


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
	if not _has_property(node, first):
		return [false, null, "'%s' has no property '%s'." % [node.get_path(), first]]
	var value: Variant = bridge._json.to_json(node.get_indexed(NodePath(property)))
	return [_json_equal(value, wanted), value]


func _has_property(node: Node, property: String) -> bool:
	for entry: Dictionary in node.get_property_list():
		if entry["name"] == property:
			return true
	return false


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


## Resolves on the node's next emission of signal_name, with its arguments. await has no
## timeout, so a variadic lambda catches the emission and the wait polls it once a frame.
func _wait_for_signal(node_name: String, signal_name: String, timeout_ms: int) -> Dictionary:
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return {"error": "no node '%s' in the running game" % node_name}
	if not node.has_signal(signal_name):
		return {"error": "%s has no signal '%s'" % [node.get_path(), signal_name]}
	var fired: Array = []
	var on_signal := func(...args: Array) -> void:
		if fired.is_empty():
			fired.append(args)
	node.connect(signal_name, on_signal)
	var caught := func() -> Array:
		return [not fired.is_empty(), null if fired.is_empty() else bridge._json.to_json(fired[0])]
	var outcome: Dictionary = await _poll(caught, timeout_ms)
	var result: Dictionary = outcome["result"]
	if is_instance_valid(node) and node.is_connected(signal_name, on_signal):
		node.disconnect(signal_name, on_signal)
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
