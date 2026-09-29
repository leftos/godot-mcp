extends Node
## The time tests' probe (time_probe.tscn): a pausable node that counts the frames and physics
## ticks it processes and sums their delta, repaints its Swatch from the frame count each frame
## so a screenshot tells frames apart, and arms a timer that marks it done.

signal fired(value: Variant)

var process_frames: int = 0
var physics_ticks: int = 0
var process_delta: float = 0.0
var physics_delta: float = 0.0
var state: String = "idle"
var n: int = 3
## The game seconds start_clock's clock had summed at the end of each process frame it ran in,
## keyed by that frame's Engine.get_process_frames().
var clock_log: Dictionary = {}
var _clocking: bool = false
var _clock_seconds: float = 0.0
var _pause_at_ms: int = 0


func _process(delta: float) -> void:
	process_frames += 1
	process_delta += delta
	($Swatch as ColorRect).color = Color8((process_frames % 16) * 16, 0, 0)
	if _clocking:
		_tick_clock(delta)


## Starts a clock that sums this node's process delta from this frame's _process on, as the
## bridge's game clock sums it, logging the sum each frame in clock_log; with pause_at_ms above 0,
## it pauses the tree in the frame its whole milliseconds reach pause_at_ms, and stops. Returns
## this frame's Engine.get_process_frames().
func start_clock(pause_at_ms: int = 0) -> int:
	clock_log = {}
	_clock_seconds = 0.0
	_pause_at_ms = pause_at_ms
	_clocking = true
	return Engine.get_process_frames()


## The sum clock_log holds for process frame frame, or null when the clock did not run in it.
func clock_at(frame: int) -> Variant:
	return clock_log.get(frame)


## Calls a method on a null instance, a GDScript runtime error that ends this method with null.
func fail_on_null() -> int:
	var missing: Node = null
	missing.queue_free()
	return 1


func _tick_clock(delta: float) -> void:
	_clock_seconds += delta
	clock_log[Engine.get_process_frames()] = _clock_seconds
	if _pause_at_ms > 0 and floori(_clock_seconds * 1000.0) >= _pause_at_ms:
		get_tree().paused = true
		_clocking = false


func _physics_process(delta: float) -> void:
	physics_ticks += 1
	physics_delta += delta


## After ms milliseconds, even while the tree is paused (create_timer's process_always), adds a
## child named Armed, sets state to "done" and emits fired(ms).
func arm(ms: int) -> void:
	await get_tree().create_timer(ms / 1000.0).timeout
	var armed := Node.new()
	armed.name = "Armed"
	add_child(armed)
	state = "done"
	fired.emit(ms)


## After ms milliseconds, stops the game drawing frames, as a minimized window does: with the
## render loop off the main loop skips its draw (main/main.cpp L5081-5087 in 4.7.2), so no
## frame_post_draw is emitted.
func stop_drawing_after(ms: int) -> void:
	await get_tree().create_timer(ms / 1000.0).timeout
	RenderingServer.render_loop_enabled = false


## After ms milliseconds, even while the tree is paused, pauses the tree, as a game's own
## pause menu would.
func pause_after(ms: int) -> void:
	await get_tree().create_timer(ms / 1000.0).timeout
	get_tree().paused = true
