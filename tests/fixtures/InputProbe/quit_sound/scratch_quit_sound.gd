extends "res://scratch/scratch_probe.gd"
## A scene that plays a sound in its quit work: its _exit_tree starts a voice on an
## AudioStreamPlayer it parented to the root in _init, before the scene itself entered it. A node's
## children exit from the last added to the first (scene/main/node.cpp L421, which walks the
## children map in insertion order, whatever move_child did, in 4.7.2), so the player exits after
## the scene and is still in the tree when the scene's _exit_tree plays it; a player out of the tree
## cannot (scene/audio/audio_stream_player_internal.cpp L145). Its one step only leaves a note, so
## a watch's window ends long before the quit.
##
## It lives outside the scratch folder and is run by its res:// path, since its verdict is racy and
## not tested: a voice held at exit is stopped only at the player's predelete, which queues its
## deletion for the mixing thread, and AudioServer.finish never frees what is left
## (servers/audio/audio_server.cpp L1289-1319, L1653-1663 in 4.7.2), so the game may report the
## playback leaked at exit, which turns a scene red.

const PLAYER_NAME := "QuitSound"
const MIX_RATE := 22050
## A tenth of a second at MIX_RATE.
const SAMPLES := 2205
## The samples of one period of a 441 Hz wave at MIX_RATE, and of its half.
const PERIOD := 50
const HALF_PERIOD := 25

var player: AudioStreamPlayer


func _init() -> void:
	add_step("note", func() -> void: set_note("noted"))
	player = AudioStreamPlayer.new()
	player.name = PLAYER_NAME
	player.stream = _beep()
	(Engine.get_main_loop() as SceneTree).root.add_child(player)


func _exit_tree() -> void:
	player.play()


## A tenth of a second of a 441 Hz square wave, 8-bit mono, built in code.
static func _beep() -> AudioStreamWAV:
	var data := PackedByteArray()
	data.resize(SAMPLES)
	for index in SAMPLES:
		data[index] = 64 if index % PERIOD < HALF_PERIOD else 192
	var stream := AudioStreamWAV.new()
	stream.format = AudioStreamWAV.FORMAT_8_BITS
	stream.mix_rate = MIX_RATE
	stream.stereo = false
	stream.data = data
	return stream
