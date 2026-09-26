extends Node
## Counts the presses of probe_jump (JoyButton A on any pad), for the gamepad tests; they read the
## pad's own state through Input in run_script.

var jump_count: int = 0


func _input(event: InputEvent) -> void:
	if event.is_action_pressed("probe_jump"):
		jump_count += 1
