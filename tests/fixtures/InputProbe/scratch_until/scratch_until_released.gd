extends "res://scratch/scratch_probe.gd"
## Two steps: the first's until is an expression on the root's released property, which a frame
## sets once the release file a test writes in the project's .godot folder exists, so the scene
## holds that step, and its scratch session stays live, until the test releases it; the step's
## pace in godot-mcp.json is only its cap. The second only leaves a note.

const RELEASE := "res://.godot/scratch_released.marker"

var released: bool = false


func _init() -> void:
	add_step("hold", func() -> void: set_note("holding until released"))
	add_step("done", func() -> void: set_note("released"))


func _process(_delta: float) -> void:
	if not released and FileAccess.file_exists(ProjectSettings.globalize_path(RELEASE)):
		released = true


func GetStepUntil(index: int) -> Dictionary:
	if index == 0:
		return {"node": str(get_path()), "expression": "node.released"}
	return {}
