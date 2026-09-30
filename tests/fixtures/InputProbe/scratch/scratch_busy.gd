extends "res://scratch/scratch_probe.gd"
## A scene whose one step holds the main thread for a minute, far past its ceiling at the pace
## godot-mcp.json gives it: killed, and its game stopped.

const SPIN_MS := 60_000


func _init() -> void:
	add_step("spin", _spin)


func _spin() -> void:
	set_note("spinning")
	var end: int = Time.get_ticks_msec() + SPIN_MS
	while Time.get_ticks_msec() < end:
		pass
