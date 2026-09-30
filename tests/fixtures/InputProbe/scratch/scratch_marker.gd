extends "res://scratch/scratch_probe.gd"
## A scene that holds a marker file in the project's .godot folder from its start to its exit, so
## ScratchBeside, played beside it, sees the marker and goes red; played alone, ScratchBeside is
## green. Green itself unless the marker could not be written.

const MARKER := "res://.godot/scratch_beside.marker"


func _init() -> void:
	add_step("hold", _hold)
	add_step("still", func() -> void: set_note("still holding"))
	add_step("release", func() -> void: set_note("releasing at exit"))


func _ready() -> void:
	var path: String = ProjectSettings.globalize_path(MARKER)
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	if file != null:
		file.store_string("held by ScratchMarker")
		file.close()


func _exit_tree() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(MARKER))


func _hold() -> void:
	if FileAccess.file_exists(ProjectSettings.globalize_path(MARKER)):
		set_note("holding the marker")
	else:
		push_error("ScratchMarker could not write its marker %s" % MARKER)
