extends "res://scratch/scratch_probe.gd"
## A scene whose only step only arms a flag: the server's GetStatus after it sets a second flag, and
## the wait after the last step, which the server sends only after that step's window closed and its
## errors were taken, starts a 0.5 s game-time timer through the hook on that wait's start, so no
## margin against server latency is left. The wait is the 2 s godot-mcp.json paces this scene at, so
## the error is in the exit with no failed step.

const FIRE_S := 0.5
const WAIT_STARTED_GROUP := "godot_mcp_wait_started"

var _armed: bool = false
var _wait_armed: bool = false


func _init() -> void:
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
	push_error("late error after the last step")
