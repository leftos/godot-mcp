extends "res://gd_test.gd"
## The watch's audio players (bridge/godot_mcp_watch_audio.gd, driven through godot_mcp_watch.gd):
## starts by a greater playback id, pauses and resumes on stream_paused, ends and their reasons,
## following through node_added, the 200-player cap, start's snapshot and the connections cut at
## the end. A real player cannot play here (the root is not in the tree while the tests run), so
## stand-in players hold their voices by hand, and frames are fed to advance by hand.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame.
signal test_frame

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar _audio: Node\nvar top: Node\n\n\n"
	+ "func _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)
## A stand-in player: its voices are RefCounted playbacks, the newest last, as the engine's.
const PLAYER_SOURCE := (
	"extends Node\n\nsignal finished\n\n"
	+ 'var stream: Resource\nvar bus: StringName = &"Effects"\nvar max_polyphony: int = 1\n'
	+ "var stream_paused: bool = false\nvar voices: Array = []\n\n\n"
	+ "func has_stream_playback() -> bool:\n\treturn not voices.is_empty()\n\n\n"
	+ "func get_stream_playback() -> Object:\n"
	+ "\tif voices.is_empty():\n"
	+ '\t\tpush_error("Player is inactive. Call play() before requesting get_stream_playback().")\n'
	+ "\t\treturn null\n"
	+ "\treturn voices[-1]\n\n\n"
	+ "func play() -> void:\n\tvoices.append(RefCounted.new())\n"
)
const MUTE_SOURCE := "extends Node\n\nvar muted: bool = true\n"
const NOT_FOUND := (
	"No node named 'Nowhere' anywhere under /root in the running game; get_scene_tree lists the "
	+ "nodes' paths."
)

var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")
var _player_script: GDScript = _script_from(PLAYER_SOURCE)
var _player_3d_script: GDScript = _script_from(
	PLAYER_SOURCE.replace("extends Node\n", "extends Node3D\n")
)


func test_a_play_is_a_start_with_its_stream_and_bus() -> void:
	var rig: Dictionary = _rig()
	var hit: Node = _player(rig["sounds"], "Hit")
	var sound := AudioStreamWAV.new()
	sound.resource_path = "res://sounds/hit.wav"
	hit.stream = sound
	var tone: Node = _player(rig["sounds"], "Tone")
	tone.stream = AudioStreamWAV.new()
	tone.max_polyphony = 3
	_begin(rig)
	_frame(rig, 0)
	hit.play()
	_frame(rig, 1)
	tone.play()
	_frame(rig, 2)
	_frame(rig, 3)
	var audio: Dictionary = _stop(rig)
	var hit_start: Dictionary = {"stream": "res://sounds/hit.wav", "bus": "Effects"}
	var tone_start: Dictionary = {
		"stream": {"class": "AudioStreamWAV"}, "bus": "Effects", "maxPolyphony": 3
	}
	assert_eq(
		audio["voices"],
		[[1, 16, "start", "Hit", hit_start], [2, 32, "start", "Tone", tone_start]],
		"each play is a start at the next sample, with its stream and bus"
	)
	var counts: Dictionary = {"starts": 1, "ends": 0}
	assert_eq(audio["voiceCounts"], {"Hit": counts, "Tone": counts}, "one start each")
	assert_eq(audio["voiceTotal"], 2, "every voice read")
	assert_true(not audio.has("playersFollowed"), "both were found at start")
	_free(rig)


func test_a_greater_id_is_a_second_start_and_a_revert_to_the_older_voice_is_none() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Poly")
	player.max_polyphony = 2
	_begin(rig)
	_frame(rig, 0)
	player.play()
	_frame(rig, 1)
	player.play()
	_frame(rig, 2)
	player.voices.pop_back()
	_frame(rig, 3)
	_frame(rig, 4)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[1, "start"], [2, "start"]], "the older voice's id again is none")
	_free(rig)


func test_an_end_is_finished_freed_or_stopped_in_that_order() -> void:
	var rig: Dictionary = _rig()
	var players: Array = []
	for name: String in ["Finished", "FinishedThenFreed", "Freed", "Stopped"]:
		players.append(_player(rig["sounds"], name))
	_begin(rig)
	_frame(rig, 0)
	for player: Node in players:
		player.play()
	_frame(rig, 1)
	players[0].voices.clear()
	players[0].finished.emit()
	players[1].voices.clear()
	players[1].finished.emit()
	players[1].free()
	players[2].free()
	players[3].voices.clear()
	_frame(rig, 2)
	var ends: Array = _stop(rig)["voices"].filter(
		func(voice: Array) -> bool: return voice[2] == "end"
	)
	assert_eq(
		ends,
		[
			[2, 32, "end", "Finished", {"reason": "finished"}],
			[2, 32, "end", "FinishedThenFreed", {"reason": "finished"}],
			[2, 32, "end", "Freed", {"reason": "freed"}],
			[2, 32, "end", "Stopped", {"reason": "stopped"}],
		],
		"finished before freed before stopped"
	)
	_free(rig)


func test_a_finished_flag_is_cleared_at_every_sample() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Poly")
	player.max_polyphony = 2
	_begin(rig)
	_frame(rig, 0)
	player.play()
	player.play()
	_frame(rig, 1)
	player.voices.pop_back()
	player.finished.emit()
	_frame(rig, 2)
	player.voices.clear()
	_frame(rig, 3)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[1, "start"], [3, "end"]], "the newest voice's end is no event")
	assert_eq(audio["voices"][1][4], {"reason": "stopped"}, "a finished two samples back")
	_free(rig)


func test_stream_paused_flipping_is_a_pause_then_a_resume_and_never_an_end() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Music")
	_begin(rig)
	_frame(rig, 0)
	player.play()
	_frame(rig, 1)
	player.stream_paused = true
	_frame(rig, 2)
	_frame(rig, 3)
	player.stream_paused = false
	_frame(rig, 4)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[1, "start"], [2, "pause"], [4, "resume"]], "on each flip")
	assert_eq(audio["voices"][1][4], {}, "a pause carries nothing")
	assert_eq(audio["voiceCounts"], {"Music": {"starts": 1, "ends": 0}}, "no end")
	_free(rig)


func test_a_player_with_no_playback_reads_no_event_and_logs_no_error() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Idle")
	_begin(rig)
	_frame(rig, 0)
	player.finished.emit()
	_frame(rig, 1)
	var audio: Dictionary = _stop(rig)
	assert_eq(audio["voices"], [], "no voice")
	assert_eq(audio["voiceTotal"], 0, "none read")
	_free(rig)


func test_a_voice_is_read_in_a_paused_frame() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Menu")
	_begin(rig)
	_frame(rig, 0)
	player.play()
	rig["watch"].advance(rig["watch"]._watch, true, 1, 0.016, 1)
	player.voices.clear()
	player.finished.emit()
	rig["watch"].advance(rig["watch"]._watch, true, 2, 0.016, 2)
	var audio: Dictionary = _stop(rig)
	assert_eq(
		audio["voices"],
		[
			[1, 16, "start", "Menu", {"bus": "Effects"}],
			[2, 16, "end", "Menu", {"reason": "finished"}],
		],
		"both in paused frames, at the game time the pause holds"
	)
	_free(rig)


func test_a_player_added_during_the_window_is_followed_once_however_often_it_enters() -> void:
	var rig: Dictionary = _rig()
	_begin(rig)
	_frame(rig, 0)
	var late: Node = _player(rig["sounds"], "Late")
	var elsewhere: Node = _player(rig["top"], "Elsewhere")
	var plain := Node.new()
	rig["sounds"].add_child(plain)
	var tree := Engine.get_main_loop() as SceneTree
	for node: Node in [late, late, elsewhere, plain]:
		tree.node_added.emit(node)
	late.play()
	elsewhere.play()
	_frame(rig, 1)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[1, "start"]], "one start, the late player's")
	assert_eq(audio["voices"][0][3], "Late", "the player under the node")
	assert_eq(audio["playersFollowed"], 1, "followed once")
	_free(rig)


func test_players_past_200_are_counted_skipped() -> void:
	var rig: Dictionary = _rig()
	for index in 201:
		_player(rig["sounds"], "P%d" % index)
	_begin(rig)
	_frame(rig, 0)
	var started: Dictionary = rig["watch"]._started(rig["watch"]._watch)["audio"]
	assert_eq(started["players"]["count"], 200, "200 followed")
	assert_eq(started["playersSkipped"], 1, "the 201st skipped")
	var late: Node = _player(rig["sounds"], "Late")
	var tree := Engine.get_main_loop() as SceneTree
	tree.node_added.emit(late)
	tree.node_added.emit(late)
	_frame(rig, 1)
	var audio: Dictionary = _stop(rig)
	assert_eq(audio["playersSkipped"], 2, "a skipped player counted once")
	assert_true(not audio.has("playersFollowed"), "none followed past the cap")
	_free(rig)


func test_a_freed_player_makes_room_under_the_200_cap() -> void:
	var rig: Dictionary = _rig()
	var first: Node = _player(rig["sounds"], "P0")
	for index in range(1, 200):
		_player(rig["sounds"], "P%d" % index)
	_begin(rig)
	_frame(rig, 0)
	first.free()
	_frame(rig, 1)
	var late: Node = _player(rig["sounds"], "Late")
	(Engine.get_main_loop() as SceneTree).node_added.emit(late)
	late.play()
	_frame(rig, 2)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[2, "start"]], "the late player's start")
	assert_eq(audio["playersFollowed"], 1, "followed in the freed player's place")
	assert_true(not audio.has("playersSkipped"), "none skipped")
	_free(rig)


func test_the_watch_keeps_1000_voices_across_its_players_and_counts_the_rest() -> void:
	var rig: Dictionary = _rig()
	var players: Array = []
	for index in 5:
		players.append(_player(rig["sounds"], "P%d" % index))
	_begin(rig)
	_frame(rig, 0)
	for frame in range(1, 202):
		for player: Node in players:
			if frame <= 200 or player == players[0]:
				player.play()
		_frame(rig, frame)
	var audio: Dictionary = _stop(rig)
	assert_eq(audio["voices"].size(), 1000, "1000 voices kept")
	assert_eq(audio["voices"][-1][0], 200, "the 1001st, frame 201's, is not")
	assert_eq(audio["voiceTotal"], 1001, "every voice counted")
	assert_eq(audio["voiceCounts"]["P0"], {"starts": 201, "ends": 0}, "the dropped one too")
	_free(rig)


func test_a_3d_player_out_of_the_tree_starts_with_no_position_and_logs_no_error() -> void:
	var rig: Dictionary = _rig()
	var pooled: Node3D = _player_3d_script.new()
	pooled.name = "Pooled"
	rig["sounds"].add_child(pooled)
	_begin(rig)
	_frame(rig, 0)
	pooled.play()
	rig["sounds"].remove_child(pooled)
	_frame(rig, 1)
	var audio: Dictionary = _stop(rig)
	var start: Dictionary = {"bus": "Effects", "position": null}
	assert_eq(audio["voices"], [[1, 16, "start", "Pooled", start]], "position null")
	pooled.free()
	_free(rig)


func test_start_reports_the_driver_the_mix_rate_the_bridge_mute_and_what_plays() -> void:
	var rig: Dictionary = _rig()
	var music: Node = _player(rig["sounds"], "Music")
	_player(rig["sounds"], "Idle")
	music.play()
	_begin(rig)
	_frame(rig, 0)
	var started: Dictionary = rig["watch"]._started(rig["watch"]._watch)["audio"]
	var expected: Dictionary = {
		"driver": AudioServer.get_driver_name(),
		"mixRate": int(AudioServer.get_mix_rate()),
		"bridgeMuted": true,
		"players": {"count": 2, "playing": [{"path": "Music", "bus": "Effects"}]},
	}
	assert_eq(started, expected, "the snapshot")
	music.voices.clear()
	_frame(rig, 1)
	var audio: Dictionary = _stop(rig)
	assert_eq(_events(audio), [[1, "end"]], "a voice playing at start is no start")
	assert_eq(audio["voiceCounts"], {"Music": {"starts": 0, "ends": 1}}, "its end is counted")
	_free(rig)


func test_the_connections_are_cut_at_finish_and_at_drop() -> void:
	var rig: Dictionary = _rig()
	var player: Node = _player(rig["sounds"], "Hit")
	var tree := Engine.get_main_loop() as SceneTree
	var before: int = tree.node_added.get_connections().size()
	_begin(rig)
	assert_eq(tree.node_added.get_connections().size(), before + 1, "node_added connected")
	assert_eq(player.finished.get_connections().size(), 1, "finished connected")
	_frame(rig, 0)
	_stop(rig)
	assert_eq(tree.node_added.get_connections().size(), before, "cut at finish")
	assert_eq(player.finished.get_connections().size(), 0, "finished cut at finish")
	_begin(rig)
	_frame(rig, 0)
	rig["watch"].drop()
	assert_eq(tree.node_added.get_connections().size(), before, "cut at drop")
	assert_eq(player.finished.get_connections().size(), 0, "finished cut at drop")
	_free(rig)


func test_an_under_node_that_does_not_exist_is_refused_with_the_not_found_text() -> void:
	var rig: Dictionary = _rig()
	var tree := Engine.get_main_loop() as SceneTree
	var before: int = tree.node_added.get_connections().size()
	var params: Dictionary = {"audio": {"players": {"under": "Nowhere"}}, "frames": 10}
	assert_eq(rig["watch"].begin(params, test_frame), NOT_FOUND, "the not-found text")
	assert_eq(tree.node_added.get_connections().size(), before, "nothing connected")
	_free(rig)


## [frame, event] of each voice.
static func _events(audio: Dictionary) -> Array:
	return audio["voices"].map(func(voice: Array) -> Array: return [voice[0], voice[2]])


func _begin(rig: Dictionary) -> void:
	var params: Dictionary = {"audio": {"players": {"under": "Sounds"}}, "frames": 300}
	assert_eq(rig["watch"].begin(params, test_frame), "", "begins")


## Feeds frame, unpaused, to the running watch.
func _frame(rig: Dictionary, frame: int) -> void:
	rig["watch"].advance(rig["watch"]._watch, false, frame, 0.016, frame)


## Stops the watch and answers its audio.
func _stop(rig: Dictionary) -> Dictionary:
	return rig["watch"].stop()["result"]["audio"]


## A stand-in player named name under parent.
func _player(parent: Node, name: String) -> Node:
	var player: Node = _player_script.new()
	player.name = name
	parent.add_child(player)
	return player


## Sounds under AudioRig, a bridge finding nodes under AudioRig with its mute on, and a watch on it
## that follows the stand-in players.
func _rig() -> Dictionary:
	var top := Node.new()
	top.name = "AudioRig"
	var sounds := Node.new()
	sounds.name = "Sounds"
	top.add_child(sounds)
	var bridge: Node = _script_from(BRIDGE_SOURCE).new()
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	bridge._audio = _script_from(MUTE_SOURCE).new()
	var watch: Node = _watch_script.new()
	watch.bridge = bridge
	watch.movie_path = func() -> String: return ""
	watch.usec_clock = func() -> int: return 0
	var scripts: Array = [_player_script, _player_3d_script]
	watch.audio_player = func(node: Node) -> bool: return scripts.has(node.get_script())
	return {"top": top, "sounds": sounds, "bridge": bridge, "watch": watch}


static func _script_from(source: String) -> GDScript:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script


func _free(rig: Dictionary) -> void:
	rig["watch"].drop()
	rig["watch"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"]._audio.free()
	rig["bridge"].free()
	rig["top"].free()
