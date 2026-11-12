extends "res://scratch/scratch_shot_until_step.gd"
## ScratchShotUntilStep's steps with a shot condition that never holds: GetStepShotUntil(0) waits on
## never, which no step sets, so a captured run leaves step 0 unshot once its until is met.

var never: bool = false


func GetStepShotUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "never", "equals": true}
	return {}
