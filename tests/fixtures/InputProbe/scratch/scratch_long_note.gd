extends "res://scratch/scratch_probe.gd"
## A scene whose one step leaves a 1500-character note: green, its [scratch] line longer than the
## output buffer's 1000-character page cut and its status (GetStatus's reply) longer than the 500
## default, both kept whole under options.detailChars; the line, not the status, proves the uncut read.

const NOTE_LENGTH := 1500


func _init() -> void:
	add_step("long", func() -> void: set_note("n".repeat(NOTE_LENGTH)))
