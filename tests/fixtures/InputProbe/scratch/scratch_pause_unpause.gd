extends "res://scratch/scratch_probe.gd"
## A scene whose first step, an until step, pauses the tree, its until holding once it has; its
## second, a paced step, unpauses it in PlayStep. The second step's pace wait reaches the bridge on
## a paused tree and carries PlayStep as its call, so it must not be refused before PlayStep runs:
## green.

var _paused_it: bool = false


func _init() -> void:
	add_step("pause", _pause)
	add_step("unpause", _unpause)


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "_paused_it", "equals": true}
	return {}


func _pause() -> void:
	get_tree().paused = true
	_paused_it = true
	set_note("paused")


func _unpause() -> void:
	get_tree().paused = false
	set_note("unpaused")
