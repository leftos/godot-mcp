extends "res://scratch/scratch_probe.gd"
## Two paced steps whose GetStepUntil gives no condition, but logs an error for the second each time
## it is read: the run's read of every step's until before the walk must leave that error to the
## second step's own read at its play, not to the first step's window.


func _init() -> void:
	add_step("first", func() -> void: set_note("first done"))
	add_step("second", func() -> void: set_note("second done"))


func GetStepUntil(index: int) -> Dictionary:
	if index == 1:
		push_error("scratch until read raised")
	return {}
