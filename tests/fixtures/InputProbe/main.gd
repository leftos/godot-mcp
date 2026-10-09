extends Control
## The InputProbe's root: reports that the scene is up and which user arguments the game received,
## gives MenuA the focus the gamepad tests move, and writes an exit marker as it leaves the tree.

## The user argument naming the file written as the scene leaves the tree: a game that quits
## leaves it, one that is killed does not.
const EXIT_MARKER_ARG := "--exit-marker="


func _ready() -> void:
	($Menu/MenuA as Button).grab_focus()
	print("[probe] ready args=%s" % JSON.stringify(OS.get_cmdline_user_args()))


func _exit_tree() -> void:
	for arg: String in OS.get_cmdline_user_args():
		if not arg.begins_with(EXIT_MARKER_ARG):
			continue
		var path: String = arg.trim_prefix(EXIT_MARKER_ARG)
		var file := FileAccess.open(path, FileAccess.WRITE)
		if file == null:
			push_error("probe: cannot write the exit marker %s" % path)
			return
		file.store_string("exited")


## Raises an error from the fixture's own script, for the test that run_script tells errors
## located in the script it runs from errors raised elsewhere.
func probe_push_error() -> void:
	push_error("probe fixture error")
