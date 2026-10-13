extends "res://gd_test.gd"
## The watch's bus levels (bridge/godot_mcp_watch_buses.gd, through godot_mcp_watch_audio.gd and
## godot_mcp_watch.gd): heard ranges above the threshold, the peak's maximum, volume and mute
## changes, buses resolved by name at every sample, "*" and its 16-bus cap, start's bus settings
## and the Master and time_scale warnings. Nothing is mixed here, so the peaks are fed through the
## watch's bus_peak seam by bus name, and frames are fed to advance by hand.
# gdlint: disable=private-method-call

## Stands in for the tree's process_frame.
signal test_frame

const BRIDGE_SOURCE := "extends Node\n\nvar _json: GDScript\nvar _inspect: Node\nvar _audio: Node\n"
const MUTE_SOURCE := "extends Node\n\nvar muted: bool = false\n"
const MASTER_WARNING := (
	"Master read -200 dB while the bridge muted it (the mute option); the buses that send to it "
	+ "still read their own levels, so watch those by name"
)
const TIME_SCALE_WARNING := (
	"Engine.time_scale was not 1 in a sampled frame; audio does not follow it, so a voice's length "
	+ "in gameMs is its length in seconds times time_scale"
)

var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")
var _monitors_script: GDScript = load_bridge_script("godot_mcp_watch_monitors.gd")


func test_peaks_above_the_threshold_are_heard_ranges_broken_by_a_paused_frame() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_begin(rig, {"buses": ["Effects"]})
	var peaks: Array = [-200.0, -30.0, -20.0, -80.0, -20.0, -10.0, -10.0]
	for frame in 5:
		_frame(rig, frame, peaks[frame], "Effects")
	rig["peaks"]["Effects"] = 0.0
	rig["watch"].advance(rig["watch"]._watch, true, 5, 0.016, 5)
	_frame(rig, 6, peaks[6], "Effects")
	var bus: Dictionary = _stop(rig)["buses"][0]
	var expected: Dictionary = {
		"name": "Effects",
		"peakMaxDb": -10.0,
		"peakMaxAt": 6,
		"heard": [[1, 2], [4, 4], [6, 6]],
		"heardFrames": 4,
		"sampledFrames": 6,
		"volumeDb": {"first": 0.0, "last": 0.0, "changes": 0},
		"mute": {"first": false, "last": false, "changes": 0},
	}
	assert_eq(bus, expected, "a range per run of loud sampled frames; the paused one is not read")
	_free(rig)


func test_the_first_maximum_is_peak_max_at_and_a_peak_equal_to_the_threshold_is_not_heard() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_begin(rig, {"buses": ["Effects"], "thresholdDb": -40.0})
	var peaks: Array = [-40.0, -39.0, -40.0, -39.0]
	for frame in peaks.size():
		_frame(rig, frame, peaks[frame], "Effects")
	var bus: Dictionary = _stop(rig)["buses"][0]
	assert_eq(bus["heard"], [[1, 1], [3, 3]], "only above the threshold")
	assert_eq(bus["heardFrames"], 2, "two frames heard")
	assert_eq([bus["peakMaxDb"], bus["peakMaxAt"]], [-39.0, 1], "the first of the maxima")
	_free(rig)


func test_a_bus_removed_during_the_window_is_skipped_with_no_error_and_keeps_its_summary() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_add_bus("Music")
	_begin(rig, {"buses": ["Effects", "Music"]})
	rig["peaks"]["Music"] = -50.0
	_frame(rig, 0, -10.0, "Effects")
	_frame(rig, 1, -10.0, "Effects")
	AudioServer.remove_bus(AudioServer.get_bus_index("Effects"))
	_frame(rig, 2, -10.0, "Effects")
	_frame(rig, 3, -10.0, "Effects")
	var buses: Array = _stop(rig)["buses"]
	assert_eq(
		[buses[0]["name"], buses[0]["sampledFrames"], buses[0]["heard"]],
		["Effects", 2, [[0, 1]]],
		"read while it was there"
	)
	assert_eq(
		[buses[1]["sampledFrames"], buses[1]["heard"]],
		[4, [[0, 3]]],
		"Music, moved down an index, still read by its name"
	)
	_free(rig)


func test_a_renamed_bus_reads_as_removed() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_begin(rig, {"buses": ["Effects"]})
	_frame(rig, 0, -10.0, "Effects")
	AudioServer.set_bus_name(AudioServer.get_bus_index("Effects"), "Sfx")
	_frame(rig, 1, -10.0, "Sfx")
	_frame(rig, 2, -10.0, "Sfx")
	var buses: Array = _stop(rig)["buses"]
	assert_eq(buses.size(), 1, "the new name is not followed")
	assert_eq([buses[0]["sampledFrames"], buses[0]["heard"]], [1, [[0, 0]]], "read once")
	_free(rig)


func test_volume_and_mute_changes_are_counted_with_the_first_and_last_value() -> void:
	var rig: Dictionary = _rig()
	var index: int = _add_bus("Effects")
	AudioServer.set_bus_volume_db(index, -3.0)
	_begin(rig, {"buses": ["Effects"]})
	_frame(rig, 0, -200.0, "Effects")
	AudioServer.set_bus_volume_db(index, -6.0)
	_frame(rig, 1, -200.0, "Effects")
	AudioServer.set_bus_volume_db(index, -12.0)
	AudioServer.set_bus_mute(index, true)
	_frame(rig, 2, -200.0, "Effects")
	_frame(rig, 3, -200.0, "Effects")
	var bus: Dictionary = _stop(rig)["buses"][0]
	assert_eq(bus["volumeDb"], {"first": -3.0, "last": -12.0, "changes": 2}, "two changes")
	assert_eq(bus["mute"], {"first": false, "last": true, "changes": 1}, "muted once")
	assert_eq([bus["heard"], bus["peakMaxDb"]], [[], -200.0], "never heard")
	_free(rig)


func test_star_follows_the_first_16_buses_master_first_and_counts_the_rest_skipped() -> void:
	var rig: Dictionary = _rig()
	for index in 16:
		_add_bus("Bus%d" % index)
	_begin(rig, {"buses": ["*"]})
	var started: Dictionary = rig["watch"]._started(rig["watch"]._watch)["audio"]
	assert_eq(started["buses"].size(), 16, "16 followed")
	assert_eq(started["buses"][0]["name"], "Master", "Master first")
	assert_eq(started["busesSkipped"], 1, "the 17th skipped")
	_frame(rig, 0, -200.0, "Master")
	var audio: Dictionary = _stop(rig)
	var names: Array = audio["buses"].map(func(bus: Dictionary) -> String: return bus["name"])
	assert_eq(names[-1], "Bus14", "the 16th by index is the last")
	assert_eq(audio["busesSkipped"], 1, "counted in the timeline too")
	_free(rig)


func test_master_watched_while_the_bridge_mutes_warns_after_the_monitors_warning() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_begin(rig, {"buses": ["Effects"]})
	rig["bridge"]._audio.muted = true
	_frame(rig, 0, -10.0, "Effects")
	assert_true(not rig["watch"].stop()["result"].has("warning"), "a child bus does not warn")
	rig["bridge"]._audio.muted = false
	var params: Dictionary = {
		"audio": {"buses": ["*"]}, "monitors": ["raster/total_draw_calls"], "frames": 300
	}
	assert_eq(rig["watch"].begin(params, test_frame), "", "begins")
	_frame(rig, 0, -10.0, "Effects")
	rig["bridge"]._audio.muted = true
	_frame(rig, 1, -10.0, "Effects")
	rig["bridge"]._audio.muted = false
	_frame(rig, 2, -10.0, "Effects")
	var warning: String = rig["watch"].stop()["result"]["warning"]
	assert_eq(
		warning,
		"%s; %s" % [_monitors_script.RASTER_WARNING, MASTER_WARNING],
		'Master through "*", muted at one sampled frame, joined after the monitors\' warning'
	)
	_free(rig)


func test_a_time_scale_other_than_1_in_a_sampled_frame_warns_once() -> void:
	var rig: Dictionary = _rig()
	_add_bus("Effects")
	_begin(rig, {"buses": ["Effects"]})
	_frame(rig, 0, -200.0, "Effects")
	assert_eq(_stop_result(rig).get("warning"), null, "no warning at scale 1")
	_begin(rig, {"buses": ["Effects"]})
	_frame(rig, 0, -200.0, "Effects")
	Engine.time_scale = 0.5
	_frame(rig, 1, -200.0, "Effects")
	_frame(rig, 2, -200.0, "Effects")
	Engine.time_scale = 1.0
	_frame(rig, 3, -200.0, "Effects")
	assert_eq(_stop_result(rig).get("warning"), TIME_SCALE_WARNING, "once, for two scaled frames")
	_free(rig)


func test_a_buses_only_watch_follows_no_player_connects_nothing_and_lists_the_settings() -> void:
	var rig: Dictionary = _rig()
	var index: int = _add_bus("Effects")
	AudioServer.set_bus_volume_db(index, -6.0)
	AudioServer.set_bus_solo(index, true)
	AudioServer.add_bus_effect(index, AudioEffectReverb.new())
	var tree := Engine.get_main_loop() as SceneTree
	var before: int = tree.node_added.get_connections().size()
	_begin(rig, {"buses": ["Effects", "Nowhere"]})
	assert_eq(tree.node_added.get_connections().size(), before, "node_added not connected")
	var started: Dictionary = rig["watch"]._started(rig["watch"]._watch)["audio"]
	var expected: Dictionary = {
		"driver": AudioServer.get_driver_name(),
		"mixRate": int(AudioServer.get_mix_rate()),
		"bridgeMuted": false,
		"buses":
		[
			{
				"name": "Effects",
				"index": index,
				"volumeDb": -6.0,
				"mute": false,
				"solo": true,
				"send": "Master",
				"effects": ["AudioEffectReverb"],
			},
			{"name": "Nowhere", "missing": true},
		],
	}
	assert_eq(started, expected, "the settings; an unknown bus is listed missing, not refused")
	_frame(rig, 0, -10.0, "Effects")
	var audio: Dictionary = _stop(rig)
	assert_eq(audio.keys(), ["buses"], "no voices without a players track")
	assert_eq(
		audio["buses"][1],
		{"name": "Nowhere", "heard": [], "heardFrames": 0, "sampledFrames": 0},
		"an unknown bus is never read"
	)
	_free(rig)


func _begin(rig: Dictionary, audio: Dictionary) -> void:
	var params: Dictionary = {"audio": audio, "frames": 300}
	assert_eq(rig["watch"].begin(params, test_frame), "", "begins")


## Feeds frame, unpaused, to the running watch, with bus reading peak.
func _frame(rig: Dictionary, frame: int, peak: float, bus: String) -> void:
	rig["peaks"][bus] = peak
	rig["watch"].advance(rig["watch"]._watch, false, frame, 0.016, frame)


## Stops the watch and answers its timeline.
func _stop_result(rig: Dictionary) -> Dictionary:
	return rig["watch"].stop()["result"]


## Stops the watch and answers its audio.
func _stop(rig: Dictionary) -> Dictionary:
	return _stop_result(rig)["audio"]


## Adds a bus named name, sending to Master, and answers its index.
static func _add_bus(name: String) -> int:
	AudioServer.add_bus()
	var index: int = AudioServer.get_bus_count() - 1
	AudioServer.set_bus_name(index, name)
	AudioServer.set_bus_send(index, "Master")
	return index


## A bridge with its mute off and a watch on it whose bus peaks are read from peaks by bus name;
## a bus not in peaks reads -200 dB, as an inactive one does; the audio is put back first, in case
## the test before stopped early.
func _rig() -> Dictionary:
	_reset_audio()
	var bridge: Node = _script_from(BRIDGE_SOURCE).new()
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	bridge._audio = _script_from(MUTE_SOURCE).new()
	var watch: Node = _watch_script.new()
	watch.bridge = bridge
	watch.movie_path = func() -> String: return ""
	watch.usec_clock = func() -> int: return 0
	var peaks: Dictionary = {}
	watch.bus_peak = func(index: int) -> float:
		return float(peaks.get(AudioServer.get_bus_name(index), -200.0))
	return {"bridge": bridge, "watch": watch, "peaks": peaks}


static func _script_from(source: String) -> GDScript:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script


## Lets the watch go and puts the audio back (_reset_audio).
func _free(rig: Dictionary) -> void:
	rig["watch"].drop()
	rig["watch"].free()
	rig["bridge"]._inspect.free()
	rig["bridge"]._audio.free()
	rig["bridge"].free()
	_reset_audio()


## The teardown of every test: the runner has no per-test hook, so _rig runs this before each test
## and the file's instance runs it as it is let go, after the last; a test a script error ended
## before its _free leaves nothing behind for the tests after it.
func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE:
		_reset_audio()


## Puts the bus layout back as the engine starts it: Master alone, at 0 dB, unmuted and not soloed,
## at time scale 1.
static func _reset_audio() -> void:
	while AudioServer.get_bus_count() > 1:
		AudioServer.remove_bus(AudioServer.get_bus_count() - 1)
	AudioServer.set_bus_volume_db(0, 0.0)
	AudioServer.set_bus_mute(0, false)
	AudioServer.set_bus_solo(0, false)
	Engine.time_scale = 1.0
