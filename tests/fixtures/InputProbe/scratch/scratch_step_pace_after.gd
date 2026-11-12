extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", godot-mcp.json paces at 2 s: that step starts a
## 3.5 s game-time timer as it plays, which fires 1.5 s after its 2 s pace ends, its cut. The exit
## hold after the last step is measured in game time from that cut, and the bridge prints its end
## marker itself, so the error is the exit's whatever the server's latency; this pins that the
## wait after the last step is that step's own 2 s pace. Red with no failed step and the error in
## its exit; were the wait after the scene's 0.1 s instead, the game would be stopped long before
## the timer fires.

const FIRE_S := 3.5


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _fire() -> void:
	push_error("error in the last step's pace after it")
