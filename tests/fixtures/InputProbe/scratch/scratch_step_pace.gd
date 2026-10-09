extends "res://scratch/scratch_probe.gd"
## Four steps that each leave a note: godot-mcp.json paces the scene at 0.25 s, its "slow" step by
## name at 1 s and its step 2 by index at 0.5 s, so each step's gameMs tells which pace it took.


func _init() -> void:
	add_step("quick", func() -> void: set_note("quick"))
	add_step("slow", func() -> void: set_note("slow"))
	add_step("settle", func() -> void: set_note("settle"))
	add_step("last", func() -> void: set_note("last"))
