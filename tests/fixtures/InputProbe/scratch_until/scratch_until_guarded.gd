extends "res://scratch/scratch_probe.gd"
## One step whose until is the root's done signal, emitted by a 5 s timer the step starts: it holds
## past a short harness guard, so the step is met only within the 30 s guard its step entry in
## godot-mcp.json gives it.

signal done


func _init() -> void:
	add_step("batch", _emit_later)


func _emit_later() -> void:
	get_tree().create_timer(5.0).timeout.connect(_finish)


func _finish() -> void:
	set_note("batch done")
	done.emit()


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "signal": "done"}
