extends "res://scratch/scratch_probe.gd"
## Two steps whose until is the root's done signal: the first raises inside PlayStep, before it
## starts its timer, so its feed logs an error while its until's wait runs on; the second plays
## after it, as the run keeps going. Each step ends far inside the 2 s pace godot-mcp.json gives
## the scene.

signal done


func _init() -> void:
	add_step("raises", _raise_then_finish)
	add_step("after", _finish.bind("after done"))


func _raise_then_finish() -> void:
	push_error("scratch step raised")
	get_tree().create_timer(0.3).timeout.connect(_finish.bind("raises done"))


func _finish(note: String) -> void:
	set_note(note)
	done.emit()


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "signal": "done"}
