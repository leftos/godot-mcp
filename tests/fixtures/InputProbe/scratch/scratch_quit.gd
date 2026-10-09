extends "res://scratch/scratch_probe.gd"
## A scene whose second step quits the game, so the step's own calls fail with the game gone:
## even with options.keepGoing the scene stops there and its last two steps never play.


func _init() -> void:
	add_step("calm", func() -> void: set_note("calm"))
	add_step("quit", _quit)
	add_step("after", func() -> void: set_note("after the quit"))
	add_step("last", func() -> void: set_note("last played"))


func _quit() -> void:
	set_note("quitting")
	get_tree().quit()
