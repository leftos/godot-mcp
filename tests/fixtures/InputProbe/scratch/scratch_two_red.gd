extends "res://scratch/scratch_probe.gd"
## A scene whose second and third steps each push_error on their own, sharing no state: without
## options.keepGoing it stops red at index 1; with it, both are failures and the last step plays.


func _init() -> void:
	add_step("calm", func() -> void: set_note("calm"))
	add_step("first", _fail_first)
	add_step("second", _fail_second)
	add_step("last", func() -> void: set_note("last played"))


func _fail_first() -> void:
	set_note("first fails")
	push_error("first independent failure")


func _fail_second() -> void:
	set_note("second fails")
	push_error("second independent failure")
