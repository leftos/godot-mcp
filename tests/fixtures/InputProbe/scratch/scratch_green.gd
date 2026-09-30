extends "res://scratch/scratch_probe.gd"
## Three steps that each leave a note and nothing else: green.


func _init() -> void:
	add_step("open", func() -> void: set_note("opened"))
	add_step("move", func() -> void: set_note("moved"))
	add_step("close", func() -> void: set_note("closed"))
