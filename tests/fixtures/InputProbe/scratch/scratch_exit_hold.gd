extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", is an until step: godot-mcp.json paces it at 30 s,
## its until's limit, and gives the scene an exit hold of 3 s. The step's until holds at once, and
## the server's GetStatus after it sets a second flag; the wait after the last step, the 3 s exit
## hold, sent only after that step's window closed, starts the timers through the hook on that
## wait's start: a line at 1 s of game time, inside the hold, which the profile expects, and an
## error at 25 s, past the hold but inside the 30 s the step's pace would wait after it. Green: were
## the wait after the last step that step's pace, the error would fall inside it and turn the scene
## red; were there no wait, the expected line would be missing.

const HELD_S := 1.0
const FIRE_S := 25.0
const WAIT_STARTED_GROUP := "godot_mcp_wait_started"

var armed: bool = false
var _armed_once: bool = false
var _hold_armed: bool = false


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _ready() -> void:
	add_to_group(WAIT_STARTED_GROUP)


func _arm() -> void:
	set_note("armed")
	armed = true


func GetStatus() -> String:
	if armed and not _armed_once:
		_armed_once = true
		_hold_armed = true
	return super()


func _on_godot_mcp_wait_started(_kind: String) -> void:
	if not _hold_armed:
		return
	_hold_armed = false
	get_tree().create_timer(HELD_S).timeout.connect(_held)
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _held() -> void:
	print("inside the exit hold")


func _fire() -> void:
	push_error("error past the exit hold, inside the last step's pace after it")


func GetStepUntil(index: int) -> Dictionary:
	if index == 1:
		return {"node": str(get_path()), "expression": "node.armed"}
	return {}
