extends "res://scratch/scratch_probe.gd"
## A scene paced at 0.1 s with an exit hold of 2 s whose last step, "arm", starts a game-time timer
## as it plays that pushes a warning 2.5 s after the step's 0.1 s pace ends, its cut: past the hold
## measured from the cut. The server's GetStatus after that step awaits 1.5 s of game time before
## it answers, a starved host's round trip while the game plays on (a real-time sleep would stall
## the game's clock with it), so a hold measured from after the server's round trips would end at
## least 1.5 s later and hold the warning. Only the hold's window judges a warning, and only where
## the profile sets scratch.warnings to "red": past the hold, neither the feed nor the stop's line
## patterns judge it, so a slow stop that takes the warning into its own lines leaves the scene
## green. Green with no exit error: the bridge measures the hold in game time from the cut and
## prints its end marker itself, whatever the latency.

const CUT_S := 0.1
const PAST_HOLD_S := 2.5
const ROUND_TRIP_S := 1.5

var _armed: bool = false


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	add_step("arm", _arm)


func _arm() -> void:
	set_note("armed")
	_armed = true
	get_tree().create_timer(CUT_S + PAST_HOLD_S).timeout.connect(_fire)


func GetStatus() -> String:
	if _armed:
		_armed = false
		await get_tree().create_timer(ROUND_TRIP_S).timeout
	return super()


func _fire() -> void:
	push_warning("warning past the exit hold measured from the last step's cut")
