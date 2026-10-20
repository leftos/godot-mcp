extends "res://scratch/scratch_probe.gd"
## Three steps whose until is an expression on the root's finished property, which each step
## clears and a timer sets. The first sets Engine.time_scale to 0.5 and its timer runs 0.6 s of
## game time, 1.2 s of real time, past its 1 s pace in real time but inside it in game time; the
## others' run 0.3 s, and the last's restores the time scale.

var finished: bool = false


func _init() -> void:
	add_step("scaled", _start_scaled)
	add_step("first", _start.bind("first", 0.3))
	add_step("second", _start.bind("second", 0.3))


func _start_scaled() -> void:
	Engine.time_scale = 0.5
	_start("scaled", 0.6)


func _start(step_name: String, seconds: float) -> void:
	finished = false
	get_tree().create_timer(seconds).timeout.connect(_finish.bind(step_name))


func _finish(step_name: String) -> void:
	if step_name == "second":
		Engine.time_scale = 1.0
	set_note("%s finished" % step_name)
	finished = true


func GetStepUntil(_index: int) -> Dictionary:
	return {"node": str(get_path()), "expression": "node.finished"}
