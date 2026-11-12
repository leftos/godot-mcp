extends "res://scratch/scratch_probe.gd"
## A scene whose only step starts a 2.5 s game-time timer as it plays, which fires 0.5 s after its
## 2 s pace (the one godot-mcp.json gives this scene) ends, its cut. The exit hold after the last
## step is measured in game time from that cut, and the bridge prints its end marker itself, which
## no server latency moves, so this pins that an error raised inside the wait after the last step
## is the exit's: the error is in the exit with no failed step.

const FIRE_S := 2.5


func _init() -> void:
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _fire() -> void:
	push_error("late error after the last step")
