extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", arms a timer that quits the game with code 0 after
## 1 s of game time, inside the scene's 3 s exit hold: the game closes the bridge connection in the
## wait after its last step and exits 0, a clean exit, so the scene is green.

const QUIT_S := 1.0


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(QUIT_S).timeout.connect(_quit)


func _quit() -> void:
	get_tree().quit(0)
