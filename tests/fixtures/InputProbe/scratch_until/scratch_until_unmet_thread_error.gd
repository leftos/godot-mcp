extends "res://scratch/scratch_probe.gd"
## Two steps whose second until is a signal the scene never emits, and whose first read starts a thread
## that logs an error: the run's read before the walk logs that error from the thread, the step's own
## read at its play logs nothing, and its until then hangs at its hang guard, so the step fails for the
## hung until while the entry the read passed over stays one of its own errors.

@warning_ignore("unused_signal")
signal never_fired

var _thread_raised: bool = false


func _init() -> void:
	add_step("first", func() -> void: set_note("first done"))
	add_step("second", func() -> void: set_note("second done"))


func GetStepUntil(index: int) -> Dictionary:
	if index != 1:
		return {}
	if not _thread_raised:
		_thread_raised = true
		var thread := Thread.new()
		thread.start(func() -> void: push_error("scratch until read thread error"))
		thread.wait_to_finish()
	return {"node": str(get_path()), "signal": "never_fired"}
