extends "res://scratch/scratch_probe.gd"
## A scene whose first step arms a callback that printerrs a failure in the first frame of the second step:
## the line lands in that step's window, which it fails, as a game's GD.PrintErr from a later frame does.
## printerr reaches stderr only, never the error feed.


func _init() -> void:
	add_step("arm", _arm)
	add_step("wait", func() -> void: set_note("waiting"))
	add_step("after", func() -> void: set_note("after"))


func _arm() -> void:
	set_note("armed")
	get_tree().process_frame.connect(_print_once_waiting)


func _print_once_waiting() -> void:
	if _current != "wait":
		return

	get_tree().process_frame.disconnect(_print_once_waiting)
	printerr("ERROR: late failure")
