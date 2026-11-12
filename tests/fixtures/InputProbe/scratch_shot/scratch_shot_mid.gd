extends "res://scratch/scratch_shot_until_step.gd"
## ScratchShotUntilStep's steps with a shot condition: GetStepShotUntil(0) holds while the rect is
## red, so a captured run shoots step 0 mid-step, before its until is met at the restore.


func GetStepShotUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "shot_ready", "equals": true}
	return {}
