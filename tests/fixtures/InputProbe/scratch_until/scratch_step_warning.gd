extends "res://scratch/scratch_probe.gd"
## A scene whose one step push_warnings: red at that step when the profile's scratch.warnings is
## "red", green when the key is left out.


func _init() -> void:
	add_step("warns", _warn)


func _warn() -> void:
	set_note("about to warn")
	push_warning("scratch step warned")
