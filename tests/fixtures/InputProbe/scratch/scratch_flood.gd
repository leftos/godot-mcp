extends "res://scratch/scratch_probe.gd"
## A scene whose second step push_errors the same long message on each of the next frames: red at
## index 1, and its errors fold into one entry with a count.

const FLOOD_FRAMES := 10

var _frames_left := 0


func _init() -> void:
	add_step("calm", func() -> void: set_note("calm"))
	add_step("floods", _flood)


func _process(_delta: float) -> void:
	if _frames_left > 0:
		_frames_left -= 1
		push_error("the same error every frame " + "x".repeat(3000))


func _flood() -> void:
	set_note("floods")
	_frames_left = FLOOD_FRAMES
