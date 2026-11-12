extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", arms a flag; the server's GetStatus after that
## step, its last call to the game for the step, starts a 0.5 s game-time timer that quits the game
## with code 0: past the step's 0.1 s pace, its cut, and inside the scene's 3 s exit hold, which the
## bridge measures from that cut, whatever the server's latency up to that call. The game closes the
## bridge connection inside the hold, before the bridge prints the hold's end marker, so the exit's
## window closes the way it did before the bridge marked window ends (the fallback this pins); the
## game exits 0, a clean exit, so the scene is green.

const QUIT_S := 0.5

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
		get_tree().create_timer(QUIT_S).timeout.connect(_quit)
	return super()


func _quit() -> void:
	get_tree().quit(0)
