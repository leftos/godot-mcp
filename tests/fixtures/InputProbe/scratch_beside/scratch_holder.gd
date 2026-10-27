extends "res://scratch/scratch_probe.gd"
## A scene that holds a file in the project's .godot folder from its start to its exit, and holds
## its first step, by an until on its looked property, until ScratchBesideHeld has written that it
## saw the file: so the two are alive together whatever order their engines boot in. Green.

const HELD := "res://.godot/scratch_held.marker"
const LOOKED := "res://.godot/scratch_looked.marker"

var looked: bool = false


func _init() -> void:
	add_step("hold", func() -> void: set_note("holding until looked at"))
	add_step("done", func() -> void: set_note("looked at"))


func _ready() -> void:
	var path: String = ProjectSettings.globalize_path(HELD)
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_error("ScratchHolder could not write %s" % HELD)
		return
	file.store_string("held by ScratchHolder")
	file.close()


func _process(_delta: float) -> void:
	if not looked and FileAccess.file_exists(ProjectSettings.globalize_path(LOOKED)):
		looked = true


func _exit_tree() -> void:
	DirAccess.remove_absolute(ProjectSettings.globalize_path(HELD))


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "expression": "node.looked"}
	return {}
