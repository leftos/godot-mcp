extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", godot-mcp.json paces at 2 s: the step arms a timer
## that push_errors 3 s of game time later, after the step's own wait but inside the wait after the
## last step, which is that step's 2 s. Red with no failed step and the error in its exit; were the
## wait after the scene's 0.1 s instead, the game would be stopped before the timer fires.

const FIRE_S := 3.0


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _fire() -> void:
	push_error("error in the last step's pace after it")
