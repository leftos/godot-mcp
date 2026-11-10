extends "res://scratch/scratch_shot_paced.gd"
## ScratchShotPaced's step with a shot condition: GetStepShotUntil(0) holds while the rect is red,
## so a captured run shoots the step before its restore.


func GetStepShotUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "shot_ready", "equals": true}
	return {}
