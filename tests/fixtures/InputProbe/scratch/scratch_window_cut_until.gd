extends "res://scratch/scratch_window_cut.gd"
## ScratchWindowCut with its first step an until step: GetStepUntil(0) holds once PlayStep has
## played it, so the step's window ends where that until wait ended, and the line and error its
## GetStatus makes afterwards are the second step's.


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "property": "_current", "equals": "first"}
	return {}
