extends "res://scratch/scratch_probe.gd"
## One step that leaves a note, and a line printed only from _exit_tree, after the steps: green,
## and the line a scratch.expectFor entry can name (ScratchRunnerTests).


func _init() -> void:
	add_step("open", func() -> void: set_note("opened"))


func _exit_tree() -> void:
	print("[scratch] store folder removed: exit-line")
