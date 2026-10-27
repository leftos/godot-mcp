extends "res://scratch/scratch_probe.gd"
## A scene red only on its first play, the one beside ScratchHolder: it counts its plays in a file
## in the project's .godot folder. On the first, its first step's until holds until ScratchHolder's
## file exists, then it writes that it looked, and its last step push_errors that it saw the
## holder. On a later play, the alone one, its until holds at once and it never looks: green.

const HELD := "res://.godot/scratch_held.marker"
const LOOKED := "res://.godot/scratch_looked.marker"
const PLAYS := "res://.godot/scratch_beside_held.plays"

var looked: bool = false
var _saw_holder: bool = false


func _init() -> void:
	add_step("look", func() -> void: set_note("looking for the holder"))
	add_step("judge", _judge)


func _ready() -> void:
	if _count_play() > 1:
		looked = true


func _process(_delta: float) -> void:
	if not looked and FileAccess.file_exists(ProjectSettings.globalize_path(HELD)):
		_saw_holder = true
		_write(LOOKED, "looked at by ScratchBesideHeld")
		looked = true


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "expression": "node.looked"}
	return {}


func _judge() -> void:
	if _saw_holder:
		push_error("ScratchBesideHeld saw the holder beside it")
	else:
		set_note("alone")


## Counts this play in the plays file and returns its number, 1 for the first.
func _count_play() -> int:
	var path: String = ProjectSettings.globalize_path(PLAYS)
	var plays: int = 0
	if FileAccess.file_exists(path):
		plays = FileAccess.get_file_as_string(path).to_int()
	plays += 1
	_write(PLAYS, str(plays))
	return plays


func _write(res_path: String, text: String) -> void:
	var path: String = ProjectSettings.globalize_path(res_path)
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_error("ScratchBesideHeld could not write %s" % res_path)
		return
	file.store_string(text)
	file.close()
