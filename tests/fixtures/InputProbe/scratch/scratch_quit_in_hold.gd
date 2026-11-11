extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s whose last step, "arm", only arms a flag: the server's GetStatus after it
## sets a second flag, and the wait after the last step, the scene's 3 s exit hold, starts a 1 s
## game-time timer through the hook on that wait's start. The game closes the bridge connection
## inside the hold, so the hold's wait ends with no end marker and its window closes the way it did
## before the bridge marked window ends (the fallback this pins); the game exits 0, a clean exit,
## so the scene is green.

const QUIT_S := 1.0
const WAIT_STARTED_GROUP := "godot_mcp_wait_started"

var _armed: bool = false
var _hold_armed: bool = false


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
		_hold_armed = true
	return super()


func _on_godot_mcp_wait_started(_kind: String) -> void:
	if not _hold_armed:
		return
	_hold_armed = false
	get_tree().create_timer(QUIT_S).timeout.connect(_quit)


func _quit() -> void:
	get_tree().quit(0)
