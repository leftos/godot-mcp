extends "res://scratch/scratch_probe.gd"
## A scene whose second step push_errors: red at index 1, and the third step never plays.


func _init() -> void:
	add_step("calm", func() -> void: set_note("calm"))
	add_step("fails", _fail)
	add_step("never", func() -> void: set_note("never played"))


func _fail() -> void:
	set_note("about to fail")
	push_error("scratch step two failed")
