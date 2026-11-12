@tool
extends Node
## A screenshot wait's shot condition, a child of the clock (godot_mcp_time.gd): a scratch step's
## until carries the server-only shotWhen, a condition the clock's conditions module builds a probe
## for as it builds a wait's. While the wait polls, the check runs the probe at each
## RenderingServer.frame_post_draw, where the state it sees is the one that frame shows, and keeps
## the frame of its first holding check as the wait's shot; it never changes when the wait is met
## or where its window ends. A probe the conditions module refuses, or a check that fails, ends the
## check unheld with its text as the reply's shotError. One check runs at a time: the wait's end
## (finish), a cancel of the request it rode on (drop_for) or the connection's end, which cancels
## every running request, disconnects it. It also grabs a met wait's frame when no draw check took
## one (capture_this_frame).

## The clock this check belongs to, its parent: its bridge's conditions and frame modules.
var time: Node:
	get:
		return get_parent()
## The running check {params, probe?, error?, image?}, the params those of the wait it rides on;
## empty while none runs.
var _check: Dictionary = {}


func _init() -> void:
	name = "MidShot"


## Starts the shot check params.shotWhen asks for, dropping any check already running; a wait
## without one starts none. A refused probe connects no draw handler and is kept as the check's
## error. The handler is this node's own method, bound to nothing, so no Dictionary holds a Callable
## bound to itself.
func start(params: Dictionary) -> void:
	var shot_when: Variant = params.get("shotWhen")
	if not shot_when is Dictionary:
		return
	drop()
	_check = {"params": params}
	var failures: Dictionary = {}
	var kind: String = str(shot_when.get("kind", ""))
	var probe: Variant = time.bridge._conditions.make_probe(kind, shot_when, failures)
	if probe is String:
		_check["error"] = probe
		return
	_check["probe"] = probe
	RenderingServer.frame_post_draw.connect(_check_draw)


## Ends the running check into result: shotHeld, and shotError for a refused probe or a failed
## check. Returns the held frame's image, or null when it did not hold or no check ran.
func finish(result: Dictionary) -> Variant:
	if _check.is_empty():
		return null
	var image: Variant = _check.get("image")
	result["shotHeld"] = image != null
	if _check.has("error"):
		result["shotError"] = _check["error"]
	drop()
	return image


## The frame a screenshot wait whose params are params, ended with outcome, captures: drawn_image,
## its met frame's, for a wait without shotWhen, which leaves any running check alone; else its own
## check's held frame, or null, once that check is ended into outcome's result (finish), or dropped
## when the wait failed. A wait with shotWhen whose check no longer runs (a later shotWhen wait
## replaced it) is not held.
func take(outcome: Dictionary, drawn_image: Variant, params: Dictionary) -> Variant:
	if not params.get("shotWhen") is Dictionary:
		return drawn_image
	if _check.is_empty() or not is_same(_check["params"], params):
		if outcome.has("result"):
			outcome["result"]["shotHeld"] = false
		return null
	if not outcome.has("result"):
		drop()
		return null
	return finish(outcome["result"])


## The image of the frame drawn before the next process_frame, which from a request's handler or
## a process_frame is the frame running now, or null when that frame is not drawn.
func capture_this_frame() -> Image:
	var shot: Array[Image] = []
	var grab := func() -> void: shot.append(time.bridge._frame.grab_frame())
	RenderingServer.frame_post_draw.connect(grab, CONNECT_ONE_SHOT)
	await get_tree().process_frame
	if RenderingServer.frame_post_draw.is_connected(grab):
		RenderingServer.frame_post_draw.disconnect(grab)
	return null if shot.is_empty() else shot[0]


## Drops the running check when it rode on the request whose params are params (the very
## Dictionary).
func drop_for(params: Dictionary) -> void:
	if not _check.is_empty() and is_same(_check["params"], params):
		drop()


## Drops the running check, if any.
func drop() -> void:
	if RenderingServer.frame_post_draw.is_connected(_check_draw):
		RenderingServer.frame_post_draw.disconnect(_check_draw)
	_check = {}


## Checks the probe for the frame just drawn: the first holding check keeps the frame and ends the
## checks; a failed one ends them with its text as the check's error. A call after the check ended
## or was replaced by one with no probe does nothing: emit_signalp copies the slots before calling
## them, so a slot disconnected during an emission is still called in it (core/object/object.cpp
## L1212-1218 in 4.7.2).
func _check_draw() -> void:
	if not _check.has("probe"):
		return
	var seen: Array = (_check["probe"] as Callable).call()
	if seen.size() > 2:
		_check["error"] = str(seen[2])
	elif seen[0]:
		_check["image"] = time.bridge._frame.grab_frame()
	else:
		return
	RenderingServer.frame_post_draw.disconnect(_check_draw)
