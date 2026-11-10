extends "res://scratch/scratch_probe.gd"
## A scene whose only step only arms a flag: the server's GetStatus after it, the last
## scene-visible call before the step's window closes, starts a 1.8 s game-time timer. The
## wait after the last step is the 2 s godot-mcp.json paces this scene at, and it starts only
## after that window closes, which is after GetStatus, so the error is in the exit with no
## failed step whatever the load.

const FIRE_S := 1.8

var _armed: bool = false


func _init() -> void:
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
	push_error("late error after the last step")
