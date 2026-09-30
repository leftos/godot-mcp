extends "res://scratch/scratch_probe.gd"
## A scene whose only step arms a timer that push_errors 3 s of game time later: at the pace of
## 2 s godot-mcp.json gives it, the error lands half a pace into the wait after the last step, so
## the scene is red with no failed step and the error in its exit.

const FIRE_S := 3.0


func _init() -> void:
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _fire() -> void:
	push_error("late error after the last step")
