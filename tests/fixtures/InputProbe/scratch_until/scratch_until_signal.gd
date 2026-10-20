extends "res://scratch/scratch_probe.gd"
## Three steps whose until is the root's done signal: the first emits it inside PlayStep, so only
## a wait that connects before its call catches it; the others start a 0.3 s timer that emits it.
## Each step ends far inside the 2 s pace godot-mcp.json gives the scene.

signal done


func _init() -> void:
	add_step("at once", _emit_now)
	add_step("timer", _emit_later.bind("timer"))
	add_step("timer again", _emit_later.bind("timer again"))


func _emit_now() -> void:
	set_note("at once done")
	done.emit()


func _emit_later(step_name: String) -> void:
	get_tree().create_timer(0.3).timeout.connect(_finish.bind(step_name))


func _finish(step_name: String) -> void:
	set_note("%s done" % step_name)
	done.emit()


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "signal": "done"}
