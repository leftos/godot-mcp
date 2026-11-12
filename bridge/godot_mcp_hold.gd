@tool
extends Node
## The exit hold after a scratch run's last played step, a child of the clock (godot_mcp_time.gd):
## the step's wait carries the server-only holdGameMs and holdEndMarker, and once that wait ends
## its window (its cut, _end_window), a game clock starting at the cut frame runs on, ticked each
## process frame as a wait's clock is (advance_game_clock: a paused frame adds nothing). In the
## frame it reaches holdGameMs, the hold prints holdEndMarker to stdout and stderr and marks the
## logger, the way the cut's marker is placed, so the exit's window ends at the cut plus the hold
## in game time whatever the server's latency; the step's reply is not held for it. With no logger
## then (a dormant bridge), it prints and marks nothing. One hold runs at a time: a new hold, a
## cancel of the request it rode on while that request is pending (drop_for) or the connection's
## end (drop) drops it, printing nothing; it dies with the bridge.

## The clock this hold belongs to, its parent: its tree, frame delta, bridge and advance_game_clock.
var time: Node:
	get:
		return get_parent()
## The running hold {clock, gameMs, marker, params}, the params those of the wait it rode on; empty
## while none runs.
var _hold: Dictionary = {}
## The tree whose process_frame ticks the running hold, or null.
var _ticking: Object = null


func _init() -> void:
	name = "Hold"


## Starts the hold params carries (holdGameMs, holdEndMarker) from this frame, the cut's, dropping
## any hold already running; a wait without them starts none. The tick is connected inside the
## cut's process_frame emission, which does not call it (emit_signalp copies the slots first,
## core/object/object.cpp L1212-1218 in 4.7.2), so the clock counts from the frame after the cut.
func start(params: Dictionary) -> void:
	if not params.has("holdGameMs") or not params.has("holdEndMarker"):
		return
	drop()
	_hold = {
		"clock": {"seconds": 0.0, "seen": 0.0, "frames": 0},
		"gameMs": int(params["holdGameMs"]),
		"marker": str(params["holdEndMarker"]),
		"params": params,
	}
	_ticking = time._tree()
	_ticking.process_frame.connect(_tick)


## Whether a hold runs now.
func running() -> bool:
	return not _hold.is_empty()


## The running hold's clock {seconds, seen, frames}, or empty.
func clock() -> Dictionary:
	return _hold.get("clock", {})


## Drops the running hold when it rode on the request whose params are params (the very
## Dictionary), printing nothing.
func drop_for(params: Dictionary) -> void:
	if running() and is_same(_hold["params"], params):
		drop()


## Drops the running hold, if any, printing nothing.
func drop() -> void:
	if _ticking != null and _ticking.process_frame.is_connected(_tick):
		_ticking.process_frame.disconnect(_tick)
	_ticking = null
	_hold = {}


## Advances the hold's clock by this frame and ends the hold once it reaches holdGameMs.
func _tick() -> void:
	var paused: bool = time._tree().paused
	if reached(_hold["clock"], int(_hold["gameMs"]), paused, time.get_process_delta_time()):
		_end()


## Adds a frame of delta seconds to clock, nothing when paused (the clock's advance_game_clock, as a
## wait's clock counts it); true once its whole game milliseconds reach game_ms, as a gameMs wait is
## met.
func reached(clock: Dictionary, game_ms: int, paused: bool, delta: float) -> bool:
	time.advance_game_clock(clock, paused, delta)
	return floori(float(clock["seconds"]) * 1000.0) >= game_ms


## Ends the hold: prints its marker to stdout and stderr and marks the bridge's logger, or with no
## logger, nothing.
func _end() -> void:
	var marker: String = str(_hold["marker"])
	drop()
	var logger: Variant = time.bridge.get("_logger") if time.bridge != null else null
	if logger == null:
		return
	print(marker)
	printerr(marker)
	logger.mark(marker)
