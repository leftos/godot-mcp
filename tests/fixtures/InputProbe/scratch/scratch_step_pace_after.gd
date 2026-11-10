extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", godot-mcp.json paces at 2 s: that step only
## arms a flag, and the server's GetStatus after it starts a 1.8 s game-time timer that fires
## inside the wait after the last step (that step's 2 s, which starts after the step's window
## closes, which is after GetStatus). Red with no failed step and the error in its exit; were
## the wait after the scene's 0.1 s instead, the game would be stopped before the timer fires.

const FIRE_S := 1.8

var _armed: bool = false


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	_armed = true


func GetStatus() -> String:
	if _armed:
		_armed = false
		get_tree().create_timer(FIRE_S).timeout.connect(_fire)
	return super()


func _fire() -> void:
	push_error("error in the last step's pace after it")
