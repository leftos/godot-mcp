extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", is an until step: godot-mcp.json paces it at 30 s,
## its until's limit, and gives the scene an exit hold of 3 s. The step's until holds at once, and it
## arms a timer that prints a line 2 s of game time later, inside the hold, which the profile
## expects, and one that push_errors at 10 s, past the hold but inside the 30 s the step's pace would
## wait after it. Green: were the wait after the last step that step's pace, the error would fall
## inside it and turn the scene red; were there no wait, the expected line would be missing.

const HELD_S := 2.0
const FIRE_S := 10.0

var armed: bool = false


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	get_tree().create_timer(HELD_S).timeout.connect(_held)
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)
	armed = true


func _held() -> void:
	print("inside the exit hold")


func _fire() -> void:
	push_error("error past the exit hold, inside the last step's pace after it")


func GetStepUntil(index: int) -> Dictionary:
	if index == 1:
		return {"node": str(get_path()), "expression": "node.armed"}
	return {}
