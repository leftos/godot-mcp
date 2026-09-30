extends Node
## The godot-mcp bridge's mute, a child of the bridge named Audio: silences the game by muting its
## Master bus for as long as the bridge asks, a run's or an attached game's whole session or a
## dormant game's wait. While muted it mutes the bus again whenever the game unmutes it (a
## settings menu restoring a saved volume), and unmuting gives the bus back its own mute.

## The Master bus. "The leftmost bus is the master bus. This bus outputs the mix to your speakers"
## (Audio buses, docs.godotengine.org/en/4.7/tutorials/audio/audio_buses.html), and the leftmost
## is index 0: AudioServer's bus_count starts at 1, that bus alone (the AudioServer class
## reference, docs.godotengine.org/en/4.7/classes/class_audioserver.html).
const MASTER_BUS := 0

## Whether the bridge asked for the mute.
var muted: bool = false
## The Master bus's own mute, taken as set_muted turned the mute on, given back as it turns it off.
var _own_mute: bool = false


func _init() -> void:
	name = "Audio"
	process_mode = Node.PROCESS_MODE_ALWAYS


## Turns the mute on, remembering the Master bus's own mute as it was, and mutes the bus; or turns
## it off, giving the bus back the mute remembered. Asking for the mute already set changes
## nothing, so a second on never takes the bridge's own mute for the game's.
func set_muted(on: bool) -> void:
	if on == muted:
		return
	muted = on
	if on:
		_own_mute = AudioServer.is_bus_mute(MASTER_BUS)
	AudioServer.set_bus_mute(MASTER_BUS, true if on else _own_mute)


func _process(_delta: float) -> void:
	if muted and not AudioServer.is_bus_mute(MASTER_BUS):
		AudioServer.set_bus_mute(MASTER_BUS, true)
