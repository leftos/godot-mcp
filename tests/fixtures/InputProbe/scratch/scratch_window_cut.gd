extends "res://scratch/scratch_probe.gd"
## A scene whose GetStatus, which the runner calls only after a step's pace wait has ended, prints a
## line and pushes an error once, after its first step: both come after that step's window ended,
## so they are the next step's, which they fail, or the exit's when the first step is the last one
## played. The line the first step prints as it plays stays its own. No timer: the runner's own call
## order places them, so no server latency decides the window.
##
## It also pins that the runner calls PlayStep inside the step's wait, as its call: a PlayStep with
## no wait started since the runner's last read (GetStepCount before the walk, GetStatus after each
## step) pushes an error, which fails that step. The wait's start is told through the
## godot_mcp_wait_started group's hook, which fires before the wait's call.

const WAIT_STARTED_GROUP := "godot_mcp_wait_started"

var _reported: bool = false
var _wait_started: bool = false


func _init() -> void:
	add_step("first", func() -> void: set_note("first played"))
	add_step("second", func() -> void: set_note("second played"))


func _ready() -> void:
	add_to_group(WAIT_STARTED_GROUP)


func _on_godot_mcp_wait_started(_kind: String) -> void:
	_wait_started = true


func GetStepCount() -> int:
	_wait_started = false
	return super()


func PlayStep(index: int) -> void:
	if not _wait_started:
		push_error("PlayStep ran before its pace wait started")
	_wait_started = false
	super(index)


func GetStatus() -> String:
	_wait_started = false
	if _current == "first" and not _reported:
		_reported = true
		print("[scratch] status after first")
		push_error("status error after first")
	return super()
