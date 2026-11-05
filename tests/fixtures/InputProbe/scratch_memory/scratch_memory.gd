extends "res://scratch/scratch_probe.gd"
## One step that allocates 1536 MB and keeps it: played under a memory ceiling below that, its game is
## killed; above it, the scene is green. The pace after the step, given by the call, gives the
## runner's watch the seconds it reads the game's private memory in.

const MEGABYTES: int = 1536
const MEGABYTE: int = 1048576
const PAGE: int = 4096

var _held: PackedByteArray = PackedByteArray()


func _init() -> void:
	add_step("grow", _grow)


func _grow() -> void:
	_held.resize(MEGABYTES * MEGABYTE)
	var offset: int = 0
	while offset < _held.size():
		_held[offset] = 1
		offset += PAGE
	set_note("grew %d MB" % MEGABYTES)
