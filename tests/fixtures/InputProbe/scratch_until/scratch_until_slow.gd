extends "res://scratch/scratch_probe.gd"
## One step whose until is the root's done signal, emitted by a 1 s timer the step starts: it holds
## far past the 0.1 s pace godot-mcp.json gives the scene, so the until must end at its condition,
## not at its pace.

signal done


func _init() -> void:
	add_step("slow", _emit_later)


func _emit_later() -> void:
	get_tree().create_timer(1.0).timeout.connect(_finish)


func _finish() -> void:
	set_note("slow done")
	done.emit()


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "signal": "done"}
