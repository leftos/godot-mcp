extends "res://scratch/scratch_probe.gd"
## A scene whose one step holds the main thread until the runner kills its game: killed, and its
## game stopped. The runner counts the step's call and its ceiling in load-adjusted time, which on
## a loaded machine may run up to 5 times their budgets in wall time (the 20.2 s ceiling at the
## pace godot-mcp.json gives it, about 101 s); the spin counts the game's wall time. So the spin
## outlasts every such backstop by minutes, and only the kill ends it.

const SPIN_MS := 600_000


func _init() -> void:
	add_step("spin", _spin)


func _spin() -> void:
	set_note("spinning")
	var end: int = Time.get_ticks_msec() + SPIN_MS
	while Time.get_ticks_msec() < end:
		pass
