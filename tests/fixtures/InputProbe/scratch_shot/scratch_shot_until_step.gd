extends "res://scratch/scratch_shot_paced.gd"
## ScratchShotPaced's steps with an until on step 0, met at the restore: GetStepUntil(0) waits on
## shot_ready false, so its until's met frame shows blue. Step 0 is shot in every run of this
## scene, so its until is a captured wait whose checks run at the draws after PlayStep, and the
## first of them already sees shot_ready true; it is met only once the restore sets it false.


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "shot_ready", "equals": false}
	return {}
