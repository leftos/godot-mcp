extends "res://scratch/scratch_probe.gd"
## One step whose GetStepUntil answers a string, which is no wait_for condition.


func _init() -> void:
	add_step("worded", func() -> void: set_note("never played"))


func GetStepUntil(_index: int) -> String:
	return "soon"
