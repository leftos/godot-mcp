extends "res://scratch/scratch_probe.gd"
## Two paced steps whose GetStepUntil gives no condition, but starts a thread that logs an error on
## the first read of the second step's until only: the run's read of every step's until before the
## walk logs that error from that thread, the second step's own read at its play logs nothing, and
## the error must still fail that step rather than pass over with the read.

var _thread_raised: bool = false


func _init() -> void:
	add_step("first", func() -> void: set_note("first done"))
	add_step("second", func() -> void: set_note("second done"))


func GetStepUntil(index: int) -> Dictionary:
	if index == 1 and not _thread_raised:
		_thread_raised = true
		var thread := Thread.new()
		thread.start(func() -> void: push_error("scratch until read thread error"))
		thread.wait_to_finish()
	return {}
