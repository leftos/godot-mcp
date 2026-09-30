extends "res://scratch/scratch_probe.gd"
## A scene with one green step that holds a node it never frees, so the game leaks it at exit.

var _orphan: Node = Node.new()


func _init() -> void:
	add_step("hold", func() -> void: set_note("holding %s" % _orphan.get_class()))
