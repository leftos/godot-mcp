extends "res://scratch/scratch_probe.gd"
## A scene that prints an error-looking line on both streams while it starts, and renames its own
## root: red at the boot, since a line printed before the first step that a pattern matches fails
## the scene there and plays no step; the runner still reads its one step from the root at the
## path the running game reports, not the name in the file.


func _init() -> void:
	add_step("settle", func() -> void: set_note("settled"))


func _ready() -> void:
	name = "LaunchNoiseRenamed"
	print("ERROR: launch noise")
	printerr("ERROR: launch noise")
