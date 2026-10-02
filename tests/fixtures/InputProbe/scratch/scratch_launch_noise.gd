extends "res://scratch/scratch_probe.gd"
## A scene that prints an error-looking line on both streams while it starts, and renames its own
## root, then plays one clean step: green, since lines printed before the first step are never
## judged (only the error feed is) and the runner calls the root at the path the running game
## reports, not the name in the file.


func _init() -> void:
	add_step("settle", func() -> void: set_note("settled"))


func _ready() -> void:
	name = "LaunchNoiseRenamed"
	print("ERROR: launch noise")
	printerr("ERROR: launch noise")
