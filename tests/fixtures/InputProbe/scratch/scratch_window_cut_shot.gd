extends "res://scratch/scratch_window_cut.gd"
## ScratchWindowCut with a shot condition on its first step: GetStepShotUntil(0) holds once PlayStep
## has played it, so a captured run shoots the step at once and the bridge holds that wait to the
## step's pace, where the window ends; the line and error its GetStatus makes afterwards are the
## second step's. Uncaptured, it plays as ScratchWindowCut does.


func GetStepShotUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "_current", "equals": "first"}
	return {}
