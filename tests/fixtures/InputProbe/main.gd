extends Control
## The InputProbe's root: reports that the scene is up and which user arguments the game received.


func _ready() -> void:
	print("[probe] ready args=%s" % JSON.stringify(OS.get_cmdline_user_args()))
