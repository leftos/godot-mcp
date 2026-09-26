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


func _process(delta: float) -> void:
	process_frames += 1
	process_delta += delta
	($Swatch as ColorRect).color = Color8((process_frames % 16) * 16, 0, 0)


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
