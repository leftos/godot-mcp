extends RefCounted
## The godot-mcp bridge's watch audio, a static helper of the watch (godot_mcp_watch.gd): follows
## the audio players (AudioStreamPlayer, 2D and 3D) at or under a node, those there at start and
## those SceneTree.node_added brings in during the window, de-duplicated by instance id, and reads
## each once a frame, paused frames too. A player's voice is its playback: has_stream_playback()
## first, so no read logs an error, then the instance id of get_stream_playback() and
## stream_paused. A voice starts when the id is greater than any seen on the player (ids only grow,
## and the latest reverts to an older voice when the newest ends first); pauses and resumes when
## stream_paused flips, which follows the player's oldest voice
## (scene/audio/audio_stream_player_internal.cpp L191-197 in 4.7.2); and ends only when the player
## holds no playback: finished when its finished signal fired since the last read, else freed when
## the player is gone, else stopped. Each player keeps its first VOICE_EVENTS voices and the watch
## its first MAX_VOICES; the server shares them across players.

## The most players a watch follows at a time; the rest are counted as skipped.
const MAX_PLAYERS := 200
## The voices a player keeps; past them they are only counted.
const VOICE_EVENTS := 300
## The voices a watch keeps across its players; past them they are only counted.
const MAX_VOICES := 1000
## INT64_MIN, below every playback id: a playback is a RefCounted, whose id has bit 63 set and so
## reads negative (core/object/object.cpp L2291-2295, L2484-2486 in 4.7.2), which rules out 0; no
## id equals it, since the validator is never 0 (L2474-2477).
const NO_VOICE := -9223372036854775807 - 1
const NOT_FOUND_HINT := "get_scene_tree lists the nodes' paths"


## Whether node is one of the three audio player classes.
static func is_player(node: Node) -> bool:
	return node is AudioStreamPlayer or node is AudioStreamPlayer2D or node is AudioStreamPlayer3D


## A watch's audio state from params.audio, {} without one, or the refusal of an under node that
## does not exist: the bridge, the under node (the root when none is named), which nodes are
## players (is_player_check), the players followed by instance id (_new_entry), every player seen
## (followed or skipped), the counts of players followed after start and skipped, the voices kept
## and per path, each path's starts and ends, every voice read, and the node_added connection.
static func begin(params: Dictionary, bridge: Node, is_player_check: Callable) -> Variant:
	var audio: Variant = params.get("audio")
	if not audio is Dictionary or not (audio as Dictionary).get("players") is Dictionary:
		return {}
	var players: Dictionary = audio["players"]
	var tree := Engine.get_main_loop() as SceneTree
	var under: Node = tree.root
	if players.get("under") is String:
		under = bridge._find_node(players["under"])
		if under == null:
			return bridge._inspect.not_found(players["under"], NOT_FOUND_HINT)
	var state: Dictionary = {
		"bridge": bridge,
		"under": under,
		"is_player": is_player_check,
		"players": {},
		"seen": {},
		"followed": 0,
		"skipped": 0,
		"voices": [],
		"kept": {},
		"counts": {},
		"total": 0,
		"node_added": tree.node_added,
	}
	for node: Node in [under] + under.find_children("*", "", true, false):
		if is_player_check.call(node) and not _is_bridges(state, node):
			_follow(state, node, true)
	var on_added := func(node: Node) -> void: _on_node_added(state, node)
	state["on_added"] = on_added
	tree.node_added.connect(on_added)
	return state


## Follows a player that entered the tree at or under the under node, the bridge's own left out.
static func _on_node_added(state: Dictionary, node: Node) -> void:
	if not state["is_player"].call(node) or _is_bridges(state, node):
		return
	var under: Variant = state["under"]
	if not is_instance_valid(under):
		return
	if under == node or (under as Node).is_ancestor_of(node):
		_follow(state, node, false)


static func _is_bridges(state: Dictionary, node: Node) -> bool:
	var bridge: Variant = state["bridge"]
	return is_instance_valid(bridge) and (bridge == node or (bridge as Node).is_ancestor_of(node))


## Follows player once: counted as skipped past MAX_PLAYERS, else read from now on; one found at
## start takes the voice it holds as seen, so it is no start.
static func _follow(state: Dictionary, player: Node, at_start: bool) -> void:
	var id: int = player.get_instance_id()
	var seen: Dictionary = state["seen"]
	if seen.has(id):
		return
	seen[id] = true
	var players: Dictionary = state["players"]
	if players.size() >= MAX_PLAYERS:
		state["skipped"] = int(state["skipped"]) + 1
		return
	players[id] = _new_entry(player, at_start)
	if not at_start:
		state["followed"] = int(state["followed"]) + 1


## A followed player: the node, its path when followed, the highest playback id seen, whether it
## held a playback at the last read and whether that was paused, whether its finished signal fired
## since the last read, and the callable connected to finished.
static func _new_entry(player: Variant, at_start: bool) -> Dictionary:
	var entry: Dictionary = {
		"node": player,
		"path": _path_of(player),
		"highest": NO_VOICE,
		"held": false,
		"paused": false,
		"finished": false,
	}
	if at_start and player.has_stream_playback():
		entry["highest"] = player.get_stream_playback().get_instance_id()
		entry["held"] = true
		entry["paused"] = bool(player.stream_paused)
	var on_finished := func() -> void: entry["finished"] = true
	entry["on_finished"] = on_finished
	player.connect("finished", on_finished)
	return entry


## The node's path in the tree; its name when it is in none.
static func _path_of(node: Node) -> String:
	return str(node.get_path()) if node.is_inside_tree() else str(node.name)


## Reads every followed player at frame, stamped game_ms, and lets go of the freed ones.
static func sample(state: Dictionary, frame: int, game_ms: int) -> void:
	if state.is_empty():
		return
	var players: Dictionary = state["players"]
	var gone: Array = []
	var stamp: Array = [frame, game_ms]
	for id: int in players:
		if not _read(state, players[id], stamp):
			gone.append(id)
	for id: int in gone:
		players.erase(id)


## Reads one player, clearing its finished flag; returns false once the player is freed.
static func _read(state: Dictionary, entry: Dictionary, stamp: Array) -> bool:
	var finished: bool = entry["finished"]
	entry["finished"] = false
	var player: Variant = entry["node"]
	if not is_instance_valid(player):
		_end(state, entry, stamp, "finished" if finished else "freed")
		entry.erase("on_finished")
		return false
	if not player.has_stream_playback():
		_end(state, entry, stamp, "finished" if finished else "stopped")
		return true
	_read_voice(state, entry, player, stamp)
	return true


## Records the end of the voice the player held, for reason; none when it held none.
static func _end(state: Dictionary, entry: Dictionary, stamp: Array, reason: String) -> void:
	if not entry["held"]:
		return
	entry["held"] = false
	entry["paused"] = false
	_record(state, entry, stamp, "end", {"reason": reason})


## Reads a player holding a playback: a start for an id above the highest seen, else a pause or a
## resume when stream_paused flipped while it held one.
static func _read_voice(
	state: Dictionary, entry: Dictionary, player: Variant, stamp: Array
) -> void:
	var id: int = player.get_stream_playback().get_instance_id()
	var paused: bool = player.stream_paused
	var held: bool = entry["held"]
	entry["held"] = true
	if id > int(entry["highest"]):
		entry["highest"] = id
		entry["paused"] = paused
		_record(state, entry, stamp, "start", _voice_info(state, player))
		return
	if paused == bool(entry["paused"]):
		return
	entry["paused"] = paused
	if held:
		_record(state, entry, stamp, "pause" if paused else "resume", {})


## A start's {stream?, bus, maxPolyphony?, position?}: the stream's resource path, or {class} for
## one with none; maxPolyphony above 1; a 2D or 3D player's global position, null out of the tree,
## where a Node3D's logs an error (scene/3d/node_3d.cpp L648-649 in 4.7.2).
static func _voice_info(state: Dictionary, player: Variant) -> Dictionary:
	var info: Dictionary = {}
	var stream: Variant = player.get("stream")
	if stream is Resource:
		var path: String = (stream as Resource).resource_path
		info["stream"] = (
			path if not path.is_empty() else {"class": (stream as Resource).get_class()}
		)
	info["bus"] = str(player.get("bus"))
	var polyphony: int = int(player.get("max_polyphony"))
	if polyphony > 1:
		info["maxPolyphony"] = polyphony
	if player is Node2D or player is Node3D:
		info["position"] = (
			state["bridge"]._json.to_json(player.global_position)
			if player.is_inside_tree()
			else null
		)
	return info


## Counts a voice event and keeps it as [frame, gameMs, event, path, info] while its path holds
## fewer than VOICE_EVENTS and the watch fewer than MAX_VOICES; a start or an end is also counted
## in its path's voiceCounts.
static func _record(
	state: Dictionary, entry: Dictionary, stamp: Array, event: String, info: Dictionary
) -> void:
	state["total"] = int(state["total"]) + 1
	var path: String = entry["path"]
	if event == "start" or event == "end":
		var counts: Dictionary = state["counts"]
		var count: Dictionary = counts.get(path, {"starts": 0, "ends": 0})
		count["starts" if event == "start" else "ends"] += 1
		counts[path] = count
	var kept: Dictionary = state["kept"]
	var voices: Array = state["voices"]
	if int(kept.get(path, 0)) >= VOICE_EVENTS or voices.size() >= MAX_VOICES:
		return
	kept[path] = int(kept.get(path, 0)) + 1
	voices.append([stamp[0], stamp[1], event, path, info])


## Disconnects node_added and each living player's finished, and lets go of their callables, which
## hold the state or the player's entry; a second call finds nothing left to cut.
static func release(state: Dictionary) -> void:
	if state.is_empty():
		return
	if state.has("on_added"):
		var node_added: Signal = state["node_added"]
		if node_added.is_connected(state["on_added"]):
			node_added.disconnect(state["on_added"])
		state.erase("on_added")
	for entry: Dictionary in (state["players"] as Dictionary).values():
		if not entry.has("on_finished"):
			continue
		var player: Variant = entry["node"]
		if is_instance_valid(player) and player.is_connected("finished", entry["on_finished"]):
			player.disconnect("finished", entry["on_finished"])
		entry.erase("on_finished")


## Adds start's audio to reply, when the watch has an audio track: {driver, mixRate, bridgeMuted,
## players: {count, playing: [{path, stream?, bus, maxPolyphony?, position?}]}, playersSkipped?},
## playing the followed players holding a playback now.
static func describe(reply: Dictionary, state: Dictionary) -> void:
	if state.is_empty():
		return
	var players: Dictionary = state["players"]
	var playing: Array = []
	for entry: Dictionary in players.values():
		var player: Variant = entry["node"]
		if is_instance_valid(player) and player.has_stream_playback():
			var described: Dictionary = {"path": entry["path"]}
			described.merge(_voice_info(state, player))
			playing.append(described)
	var audio: Dictionary = {
		"driver": AudioServer.get_driver_name(),
		"mixRate": int(AudioServer.get_mix_rate()),
		"bridgeMuted": bool(state["bridge"]._audio.muted),
		"players": {"count": players.size(), "playing": playing},
	}
	if int(state["skipped"]) > 0:
		audio["playersSkipped"] = state["skipped"]
	reply["audio"] = audio


## Adds the timeline's audio, when the watch has an audio track: {voices, voiceCounts, voiceTotal,
## playersFollowed?, playersSkipped?}; voiceTotal counts every voice read, which the server drops
## after sharing the voices.
static func add_to(result: Dictionary, state: Dictionary) -> void:
	if state.is_empty():
		return
	var audio: Dictionary = {
		"voices": state["voices"], "voiceCounts": state["counts"], "voiceTotal": state["total"]
	}
	if int(state["followed"]) > 0:
		audio["playersFollowed"] = state["followed"]
	if int(state["skipped"]) > 0:
		audio["playersSkipped"] = state["skipped"]
	result["audio"] = audio
