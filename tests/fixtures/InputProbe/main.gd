extends Control
## The InputProbe's root: reports that the scene is up and which user arguments the game received,
## and gives MenuA the focus the gamepad tests move.


func _ready() -> void:
	($Menu/MenuA as Button).grab_focus()
	print("[probe] ready args=%s" % JSON.stringify(OS.get_cmdline_user_args()))


## Raises an error from the fixture's own script, for the test that run_script tells errors
## located in the script it runs from errors raised elsewhere.
func probe_push_error() -> void:
	push_error("probe fixture error")
