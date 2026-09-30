extends "res://scratch/scratch_probe.gd"
## A scene that is red only beside ScratchMarker: every frame of its life it looks for the marker
## file ScratchMarker holds, and its last step push_errors when it ever saw it. Alone it is green.

const MARKER := "res://.godot/scratch_beside.marker"

var _seen: bool = false


func _init() -> void:
	add_step("look", func() -> void: set_note("looking"))
	add_step("look again", func() -> void: set_note("still looking"))
	add_step("judge", _judge)


func _process(_delta: float) -> void:
	if not _seen and FileAccess.file_exists(ProjectSettings.globalize_path(MARKER)):
		_seen = true


func _judge() -> void:
	if _seen:
		push_error("ScratchBeside saw the marker of a scene played beside it")
	else:
		set_note("alone")
