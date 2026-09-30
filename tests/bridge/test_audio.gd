extends "res://gd_test.gd"
## The mute (bridge/godot_mcp_audio.gd): what it remembers of the Master bus, what it gives back,
## and the mute it holds while the game unmutes the bus. Each test leaves bus 0 unmuted.

var _audio_script: GDScript = load_bridge_script("godot_mcp_audio.gd")


func test_the_mute_is_a_child_named_audio_that_runs_while_paused() -> void:
	var audio: Node = _audio_script.new()
	assert_eq(str(audio.name), "Audio", "its name")
	assert_eq(audio.process_mode, Node.PROCESS_MODE_ALWAYS, "it processes even paused")
	assert_true(not audio.muted, "it starts unmuted")
	audio.free()


func test_set_muted_mutes_the_master_bus_and_gives_back_its_own_mute() -> void:
	var audio: Node = _audio_script.new()
	AudioServer.set_bus_mute(0, false)
	audio.set_muted(true)
	assert_true(AudioServer.is_bus_mute(0), "muting mutes the Master bus")
	audio.set_muted(true)
	audio.set_muted(false)
	assert_true(
		not AudioServer.is_bus_mute(0), "an unmuted bus is unmuted again, a second on aside"
	)
	AudioServer.set_bus_mute(0, true)
	audio.set_muted(true)
	audio.set_muted(false)
	assert_true(AudioServer.is_bus_mute(0), "a bus muted beforehand stays muted after the restore")
	AudioServer.set_bus_mute(0, false)
	audio.set_muted(false)
	assert_true(not AudioServer.is_bus_mute(0), "turning off a mute already off changes nothing")
	audio.free()


func test_the_mute_holds_when_the_game_unmutes_the_bus() -> void:
	var audio: Node = _audio_script.new()
	AudioServer.set_bus_mute(0, false)
	audio.set_muted(true)
	AudioServer.set_bus_mute(0, false)
	audio._process(0.0)
	assert_true(AudioServer.is_bus_mute(0), "the next frame mutes the bus again")
	audio.set_muted(false)
	assert_true(not AudioServer.is_bus_mute(0), "the restore gives back the unmuted bus")
	audio._process(0.0)
	assert_true(not AudioServer.is_bus_mute(0), "unmuted, the frames leave the bus alone")
	audio.free()
