extends "res://scratch/scratch_probe.gd"
## One step whose until names a node the scene does not have: the bridge refuses the wait.


func _init() -> void:
	add_step("missing", func() -> void: set_note("never played"))


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": "NoSuchNode", "signal": "done"}
