extends "res://scratch/scratch_probe.gd"
## A scene whose first step arms a timer that printerrs a failure 0.75 s of game time later: at
## the default pace of 0.5 s the line lands in the second step's window, which it fails, as a
## game's GD.PrintErr from a timer does. printerr reaches stderr only, never the error feed.


func _init() -> void:
	add_step("arm", _arm)
	add_step("wait", func() -> void: set_note("waiting"))
	add_step("after", func() -> void: set_note("after"))


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(0.75).timeout.connect(func() -> void: printerr("ERROR: late failure"))
