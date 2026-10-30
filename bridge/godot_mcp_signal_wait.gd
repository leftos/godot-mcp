extends Node
## The godot-mcp bridge's signal wait, a child of its clock (godot_mcp_time.gd): waits for a
## node's next emission of a signal, for wait_for's signal kind and a capture's signal start, and
## builds the signal probe a step's until shares. await has no timeout, so a variadic lambda
## catches the emission and the clock's poll checks it once a frame. SceneTree emits process_frame
## every frame, paused or not (scene/main/scene_tree.cpp L713 in 4.7.2), so a signal wait on a
## paused tree reaches its call too.

## The clock this wait belongs to, for its call, its poll and the bridge.
var time: Node


## Resolves on params.node's next emission of params.signal, with its arguments. await has no
## timeout, so a variadic lambda catches the emission and the wait polls it once a frame. With
## params.call, the node and its signal are checked now, and the signal is connected in frame's
## next emission (the tree's process_frame), right before the method is called there, as a probe
## wait calls it (the clock's _start_probe_wait): an emission the call makes at once is caught, and
## one in the frames before the call is not. SceneTree emits process_frame every frame, paused or
## not (scene/main/scene_tree.cpp L713 in 4.7.2), so a signal wait on a paused tree reaches its call
## too.
func _wait_for_signal(params: Dictionary, timeout_ms: int, frame: Signal) -> Dictionary:
	var source: Variant = _signal_source(params)
	if source is String:
		return {"error": source}
	if params.get("call") is Dictionary:
		await frame
	var hold: Dictionary = {}
	var caught: Variant = signal_probe(params, hold)
	if caught is String:
		return {"error": caught}
	return await _call_and_poll_signal(caught, params, timeout_ms, hold)


## Calls params.call, if any (the clock's _call_once), then polls caught, the connected signal
## probe, and releases hold's connection either way. Returns the signal outcome, with call: {value}
## when a method was called, or {error} when the call failed.
func _call_and_poll_signal(
	caught: Callable, params: Dictionary, timeout_ms: int, hold: Dictionary
) -> Dictionary:
	var called: Dictionary = {}
	var failed: String = time._call_once(params, called)
	if not failed.is_empty():
		hold["release"].call()
		return {"error": failed}
	var outcome: Dictionary = await time._poll_capturing(caught, timeout_ms, params)
	hold["release"].call()
	return time._with_call(_as_signal_outcome(outcome), called)


## A signal start, connected as wait_for's signal wait connects (_wait_for_signal): now, or with
## start.call in frame's next emission right before the call, whose {value} goes into called; then
## polled (the clock's _poll), so it is met on the first check after the emission, as that wait is.
## Its connection is released however the poll ends. Returns the outcome with args
## (_as_signal_outcome).
func _poll_signal_start(params: Dictionary, called: Dictionary, frame: Signal) -> Dictionary:
	var condition: Dictionary = params["start"]
	var source: Variant = _signal_source(condition)
	if source is String:
		return {"error": source}
	if condition.get("call") is Dictionary:
		await frame
	var hold: Dictionary = {}
	var caught: Variant = signal_probe(condition, hold)
	if caught is String:
		return {"error": caught}
	var outcome: Dictionary = {"error": time._call_once(condition, called)}
	if str(outcome["error"]).is_empty():
		outcome = await time._poll(caught, float(condition.get("timeoutMs", 10000)), params)
	hold["release"].call()
	return _as_signal_outcome(outcome)


## A probe met once params.node has emitted params.signal since now, with that first emission's
## arguments as its value, connected now; hold.release disconnects it. A String saying why when
## the node or its signal is missing. A step's until shares it.
func signal_probe(params: Dictionary, hold: Dictionary) -> Variant:
	var found: Variant = _signal_source(params)
	if found is String:
		return found
	var node: Node = found
	var signal_name: String = time._text(params, "signal")
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
		if fired.is_empty():
			return [false, null]
		return [true, time.bridge._json.to_json(fired[0])]


## params.node when it is there and has params.signal; else a String saying which is missing.
func _signal_source(params: Dictionary) -> Variant:
	var node_name: String = time._text(params, "node")
	var signal_name: String = time._text(params, "signal")
	var node: Node = time.bridge._find_node(node_name)
	if node == null:
		return time.bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	if not node.has_signal(signal_name):
		return "%s has no signal '%s'" % [node.get_path(), signal_name]
	return node


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
