extends "res://scratch/scratch_probe.gd"
## Two paced steps whose GetStepUntil gives no condition, but logs a warning for the second each
## time it is read: with the profile's scratch.warnings "red", the run's read of every step's until
## before the walk must leave that warning to the second step's own read at its play, as it does
## an error, not to the first step's window.


func _init() -> void:
	add_step("first", func() -> void: set_note("first done"))
	add_step("second", func() -> void: set_note("second done"))


func GetStepUntil(index: int) -> Dictionary:
	if index == 1:
		push_warning("scratch until read warned")
	return {}
