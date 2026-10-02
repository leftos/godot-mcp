extends "res://scratch/scratch_probe.gd"
## A scene that push_errors in _ready, before any step, and has two clean steps: red at the boot
## (failedAt index -1, name "boot"), since the error feed is judged from the launch on, and its
## steps never play.


func _init() -> void:
	add_step("calm", func() -> void: set_note("calm"))
	add_step("settle", func() -> void: set_note("settled"))


func _ready() -> void:
	push_error("boot failure")
