extends "res://scratch/scratch_probe.gd"
## A scene whose first step is a setup its root marks hidden through GetStepHidden, as a base
## class adding a step 0 would: the step keeps index 0 and plays as any other, and the scene's
## step list marks it.


func _init() -> void:
	add_step("fight setup", func() -> void: set_note("set up"))
	add_step("first view", func() -> void: set_note("first viewed"))
	add_step("second view", func() -> void: set_note("second viewed"))


func GetStepHidden(index: int) -> bool:
	return index == 0
