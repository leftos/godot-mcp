extends "res://scratch/scratch_probe.gd"
## Three steps a partial run cannot take alone: "build" sets the flag "use" needs, and "note" only
## leaves a line. Its profile declares "build" as the scene's setup step, so a partial run plays it
## beside the ones it selects.

var _built: bool = false


func _init() -> void:
	add_step("build", _build)
	add_step("use", _use)
	add_step("note", _leave)


func _build() -> void:
	_built = true
	set_note("built")


func _use() -> void:
	if not _built:
		push_error("use ran before build")
		return
	set_note("used")


func _leave() -> void:
	set_note("noted")
