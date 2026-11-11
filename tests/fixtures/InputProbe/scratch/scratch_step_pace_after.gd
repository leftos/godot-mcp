extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", godot-mcp.json paces at 2 s: that step only arms a
## flag, the server's GetStatus after it sets a second one, and the wait after the last step, sent
## only after that step's window closed, starts a 1.5 s game-time timer through the hook on that
## wait's start, so the error comes from the wait itself and not from server latency. Red with no
## failed step and the error in its exit; were the wait after the scene's 0.1 s instead, the game
## would be stopped long before the timer fires.

const FIRE_S := 1.5
const WAIT_STARTED_GROUP := "godot_mcp_wait_started"

var _armed: bool = false
var _wait_armed: bool = false


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _ready() -> void:
	add_to_group(WAIT_STARTED_GROUP)


func _arm() -> void:
	set_note("armed")
	_armed = true


func GetStatus() -> String:
	if _armed:
		_armed = false
		_wait_armed = true
	return super()


func _on_godot_mcp_wait_started(_kind: String) -> void:
	if not _wait_armed:
		return
	_wait_armed = false
	get_tree().create_timer(FIRE_S).timeout.connect(_fire)


func _fire() -> void:
	push_error("error in the last step's pace after it")
