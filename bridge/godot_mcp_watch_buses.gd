extends RefCounted
## The godot-mcp bridge's watch bus levels, a static helper of the watch's audio
## (godot_mcp_watch_audio.gd): reads each watched bus once an unpaused frame, its peak (the largest
## left or right peak over its channels, which the mix writes after the bus's volume, mute and
## solo, servers/audio/audio_server.cpp L624-650 in 4.7.2), its volume_db and its mute, and
## summarises them: the peak's maximum and its first frame, the heard ranges (consecutive sampled
## frames whose peak was above the threshold), and the volume and mute's first and last values and
## how often they changed. A bus is resolved by name at every read, never kept by index: a bus
## added, renamed or removed during the window shifts the indices, and a peak read at an index
## that is gone logs an engine error (ERR_FAIL_INDEX_V, L1208-1220), so a bus that is missing is
## skipped and a rename reads as a removal.

## The most buses "*" follows, the first by index; the rest are counted as skipped.
const MAX_BUSES := 16
## A bus counts as heard above this peak unless the watch names its own (Godot's
## audio/buses/channel_disable_threshold_db default, audio_server.cpp L1527).
const DEFAULT_THRESHOLD_DB := -60.0
## What an inactive channel or a silenced bus reads (AUDIO_MIN_PEAK_DB, core/math/audio_frame.h).
const MIN_PEAK_DB := -200.0
## The bus the bridge's mute silences (godot_mcp_audio.gd).
const MASTER_BUS := 0
const MASTER_WARNING := (
	"Master read -200 dB while the bridge muted it (the mute option); the buses that send to it "
	+ "still read their own levels, so watch those by name"
)


## A bus's peak now: the largest left or right peak over its channels, in dB.
static func peak(index: int) -> float:
	var loudest: float = MIN_PEAK_DB
	for channel in AudioServer.get_bus_channels(index):
		loudest = maxf(loudest, AudioServer.get_bus_peak_volume_left_db(index, channel))
		loudest = maxf(loudest, AudioServer.get_bus_peak_volume_right_db(index, channel))
	return loudest


## The levels state of audio (the request's audio track), {} without buses: the buses followed
## (the names given, or for ["*"] every bus now up to MAX_BUSES, Master first), the buses "*"
## skipped, the threshold, the peak reader (peak_of, a Callable taking a bus index), and whether
## Master was read while the bridge muted it.
static func begin(audio: Dictionary, peak_of: Callable) -> Dictionary:
	var names: Variant = audio.get("buses")
	if not names is Array:
		return {}
	var skipped: int = 0
	if names == ["*"]:
		names = []
		for index in mini(AudioServer.get_bus_count(), MAX_BUSES):
			names.append(AudioServer.get_bus_name(index))
		skipped = maxi(0, AudioServer.get_bus_count() - MAX_BUSES)
	var buses: Array = []
	for name: Variant in names:
		buses.append(
			{
				"name": str(name),
				"sampled": 0,
				"heard": [],
				"heard_frames": 0,
				"volume": {},
				"mute": {}
			}
		)
	return {
		"buses": buses,
		"skipped": skipped,
		"threshold": float(audio.get("thresholdDb", DEFAULT_THRESHOLD_DB)),
		"peak": peak_of,
		"master_muted": false,
	}


## Reads every followed bus that exists now at frame, an unpaused one; bridge_muted is whether the
## bridge mutes Master now.
static func sample(state: Dictionary, frame: int, bridge_muted: bool) -> void:
	if state.is_empty():
		return
	for bus: Dictionary in state["buses"]:
		var index: int = AudioServer.get_bus_index(bus["name"])
		if index < 0:
			continue
		_read(state, bus, index, frame)
		if index == MASTER_BUS and bridge_muted:
			state["master_muted"] = true


## Reads the bus at index into its summary: the peak, rounded to 0.1 dB, against its maximum and
## the threshold; the volume, rounded to 0.01 dB, and the mute against their last values.
static func _read(state: Dictionary, bus: Dictionary, index: int, frame: int) -> void:
	var level: float = state["peak"].call(index)
	var rounded: float = snappedf(level, 0.1)
	bus["sampled"] = int(bus["sampled"]) + 1
	if not bus.has("peak_max") or rounded > float(bus["peak_max"]):
		bus["peak_max"] = rounded
		bus["peak_at"] = frame
	if level > float(state["threshold"]):
		_hear(bus, frame)
	_change(bus["volume"], snappedf(AudioServer.get_bus_volume_db(index), 0.01))
	_change(bus["mute"], AudioServer.is_bus_mute(index))


## Adds frame to the bus's last heard range when that range ends at the frame before, else starts
## a range: a paused frame, or one the bus was missing in, breaks a range.
static func _hear(bus: Dictionary, frame: int) -> void:
	bus["heard_frames"] = int(bus["heard_frames"]) + 1
	var ranges: Array = bus["heard"]
	if not ranges.is_empty() and int(ranges[-1][1]) == frame - 1:
		ranges[-1][1] = frame
	else:
		ranges.append([frame, frame])


## Folds value into a {first, last, changes} summary, empty before its first value.
static func _change(summary: Dictionary, value: Variant) -> void:
	if summary.is_empty():
		summary.merge({"first": value, "last": value, "changes": 0})
		return
	if value != summary["last"]:
		summary["last"] = value
		summary["changes"] = int(summary["changes"]) + 1


## Adds start's buses to audio: [{name, index, volumeDb, mute, solo, send, effects: [class]}], a
## bus missing now as {name, missing: true}, and busesSkipped when "*" skipped any.
static func describe(audio: Dictionary, state: Dictionary) -> void:
	if state.is_empty():
		return
	audio["buses"] = state["buses"].map(func(bus: Dictionary) -> Dictionary: return _settings(bus))
	if int(state["skipped"]) > 0:
		audio["busesSkipped"] = state["skipped"]


static func _settings(bus: Dictionary) -> Dictionary:
	var index: int = AudioServer.get_bus_index(bus["name"])
	if index < 0:
		return {"name": bus["name"], "missing": true}
	var effects: Array = []
	for effect in AudioServer.get_bus_effect_count(index):
		effects.append(AudioServer.get_bus_effect(index, effect).get_class())
	return {
		"name": bus["name"],
		"index": index,
		"volumeDb": snappedf(AudioServer.get_bus_volume_db(index), 0.01),
		"mute": AudioServer.is_bus_mute(index),
		"solo": AudioServer.is_bus_solo(index),
		"send": str(AudioServer.get_bus_send(index)),
		"effects": effects,
	}


## Adds the timeline's buses to audio: [{name, heard: [[fromFrame, toFrame]], heardFrames,
## sampledFrames, and once read peakMaxDb, peakMaxAt, volumeDb and mute as {first, last,
## changes}}], and busesSkipped when "*" skipped any.
static func add_to(audio: Dictionary, state: Dictionary) -> void:
	if state.is_empty():
		return
	audio["buses"] = state["buses"].map(func(bus: Dictionary) -> Dictionary: return _summary(bus))
	if int(state["skipped"]) > 0:
		audio["busesSkipped"] = state["skipped"]


static func _summary(bus: Dictionary) -> Dictionary:
	var summary: Dictionary = {
		"name": bus["name"],
		"heard": bus["heard"],
		"heardFrames": bus["heard_frames"],
		"sampledFrames": bus["sampled"],
	}
	if bus.has("peak_max"):
		summary["peakMaxDb"] = bus["peak_max"]
		summary["peakMaxAt"] = bus["peak_at"]
		summary["volumeDb"] = bus["volume"]
		summary["mute"] = bus["mute"]
	return summary


## The warnings the levels give: Master read while the bridge muted it.
static func warnings(state: Dictionary) -> PackedStringArray:
	if not state.is_empty() and state["master_muted"]:
		return [MASTER_WARNING]
	return []
