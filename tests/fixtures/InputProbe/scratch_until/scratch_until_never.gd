extends "res://scratch/scratch_probe.gd"
## Two steps whose until is a signal the scene never emits: the first is not met within its 1 s
## pace, so run_scratches turns it red and never plays the second, keepGoing or not.

@warning_ignore("unused_signal")
signal never_fired


func _init() -> void:
	add_step("wait", func() -> void: set_note("waits"))
	add_step("never", func() -> void: set_note("never played"))


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "signal": "never_fired"}
