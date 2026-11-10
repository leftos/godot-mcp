# gdlint: disable=max-public-methods, private-method-call
extends "res://gd_test.gd"
## The dormant mode (bridge/godot_mcp_dormant.gd): how the bridge decides to run, the attach and
## join file parse, the dormant file, when an ended connection goes dormant again, and one poll's
## outcome. Every file lives in this run's own folder (run_dir.gd), never this project's own
## .godot/godot-mcp/, where the other tests' bridges must still find no endpoint.

const RunDir := preload("res://run_dir.gd")

var _dormant_script: GDScript = load_bridge_script("godot_mcp_dormant.gd")
var _raw_events_script: GDScript = load_bridge_script("godot_mcp_raw_events.gd")
var _pads_script: GDScript = load_bridge_script("godot_mcp_gamepad.gd")
var _input_script: GDScript = load_bridge_script("godot_mcp_input.gd")
var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")


## Keeps the warnings the engine logs while it is added, for a test that pins one.
class WarningRecorder:
	extends Logger

	var warnings: PackedStringArray = []

	func _log_error(
		_function: String,
		_file: String,
		_line: int,
		code: String,
		rationale: String,
		_editor_notify: bool,
		error_type: int,
		_script_backtraces: Array[ScriptBacktrace]
	) -> void:
		if error_type == ERROR_TYPE_WARNING:
			warnings.append(rationale if not rationale.is_empty() else code)


func test_decide_mode_takes_off_then_environment_then_attach_then_armed() -> void:
	var decide: Callable = _dormant_script.decide_mode
	assert_eq(
		decide.call(true, true, true, true, false), "off", "switched off wins over everything"
	)
	assert_eq(
		decide.call(true, true, true, false, false), "run", "the environment wins over the files"
	)
	assert_eq(
		decide.call(false, true, true, false, false), "attach", "attach.json wins over armed.json"
	)
	assert_eq(
		decide.call(false, false, true, false, false), "dormant", "armed.json alone is dormant"
	)
	assert_eq(decide.call(false, false, false, false, false), "off", "nothing found is off")
	assert_eq(decide.call(false, false, true, true, false), "off", "switched off is never dormant")


func test_decide_mode_never_makes_a_headless_game_dormant() -> void:
	var decide: Callable = _dormant_script.decide_mode
	assert_eq(decide.call(false, false, true, false, true), "off", "an armed folder alone")
	assert_eq(decide.call(true, false, true, false, true), "run", "the environment still runs it")
	assert_eq(
		decide.call(false, true, true, false, true), "attach", "attach.json still attaches it"
	)


func test_parse_endpoint_reads_a_join_file() -> void:
	var text: String = '{"port": 4567, "token": "t0k", "shutOutRealGamepads": true, "quiet": true}'
	var endpoint: Dictionary = _dormant_script.parse_endpoint(text, false)
	assert_eq(endpoint.get("port"), 4567, "the port, as an int")
	assert_eq(typeof(endpoint.get("port")), TYPE_INT, "the port is an int, not JSON's float")
	assert_eq(endpoint.get("token"), "t0k", "the token")
	assert_eq(endpoint.get("shutOutRealGamepads"), true, "the pad shut-out")
	assert_eq(endpoint.get("quiet"), true, "quiet")


func test_parse_endpoint_reads_mute() -> void:
	var text: String = '{"port": 4567, "token": "t0k", "quiet": false, "mute": true}'
	assert_eq(_dormant_script.parse_endpoint(text, false).get("mute"), true, "mute true")
	text = '{"port": 4567, "token": "t0k", "mute": false}'
	assert_eq(_dormant_script.parse_endpoint(text, false).get("mute"), false, "mute false")


func test_parse_endpoint_defaults_the_flags_and_lets_the_quiet_variable_force_quiet() -> void:
	var text: String = '{"port": 1, "token": "t", "quiet": false}'
	var plain: Dictionary = _dormant_script.parse_endpoint(text, false)
	assert_eq(plain.get("shutOutRealGamepads"), false, "no shut-out key is no shut-out")
	assert_eq(plain.get("quiet"), false, "quiet false stays false")
	assert_eq(plain.get("mute"), false, "no mute key is no mute")
	var forced: Dictionary = _dormant_script.parse_endpoint(text, true)
	assert_eq(forced.get("quiet"), true, "GODOT_MCP_QUIET=1 makes it quiet")


func test_parse_endpoint_re_picks_the_hover_without_the_key() -> void:
	var text: String = '{"port": 1, "token": "t"}'
	assert_eq(_dormant_script.parse_endpoint(text, false).get("repickHover"), true, "on by default")


func test_env_endpoint_stops_the_hover_re_pick_only_with_its_variable() -> void:
	OS.set_environment("GODOT_MCP_PORT", "5")
	OS.set_environment("GODOT_MCP_TOKEN", "env")
	assert_eq(_dormant_script.env_endpoint().get("repickHover"), true, "on without the variable")
	OS.set_environment("GODOT_MCP_NO_REPICK_HOVER", "1")
	assert_eq(_dormant_script.env_endpoint().get("repickHover"), false, "off with it set to 1")
	OS.unset_environment("GODOT_MCP_NO_REPICK_HOVER")
	OS.unset_environment("GODOT_MCP_PORT")
	OS.unset_environment("GODOT_MCP_TOKEN")


func test_parse_endpoint_refuses_malformed_text_and_missing_keys() -> void:
	var parse: Callable = _dormant_script.parse_endpoint
	assert_eq(parse.call("{not json", false), {}, "text that is not JSON")
	assert_eq(parse.call("", false), {}, "an empty file")
	assert_eq(parse.call("[1, 2]", false), {}, "JSON that is not an object")
	assert_eq(parse.call('{"token": "t"}', false), {}, "no port")
	assert_eq(parse.call('{"port": 1}', false), {}, "no token")


func test_goes_dormant_again_only_for_a_file_endpoint_in_an_armed_folder() -> void:
	var again: Callable = _dormant_script.goes_dormant_again
	assert_true(again.call("attach", true, false), "an attach.json endpoint, armed")
	assert_true(again.call("join", true, false), "a join file endpoint, armed")
	assert_true(not again.call("env", true, false), "a run's endpoint never goes dormant")
	assert_true(not again.call("attach", false, false), "a disarmed folder stays idle")
	assert_true(not again.call("join", false, false), "a disarmed folder stays idle after a join")
	assert_true(not again.call("", true, false), "no endpoint at all")


func test_goes_dormant_again_never_for_a_headless_game() -> void:
	var again: Callable = _dormant_script.goes_dormant_again
	assert_true(not again.call("attach", true, true), "a headless attach.json game")
	assert_true(not again.call("join", true, true), "a headless joined game")


func test_the_file_paths_sit_under_the_folder() -> void:
	var dir: String = "C:/game/.godot/godot-mcp"
	assert_eq(_dormant_script.armed_path(dir), dir + "/armed.json", "armed.json")
	assert_eq(_dormant_script.dormant_path(dir, 42), dir + "/dormant/42.json", "the dormant file")
	assert_eq(_dormant_script.join_path(dir, 42), dir + "/join-42.json", "the join file")


func test_write_dormant_file_makes_its_folder_and_writes_pid_and_start() -> void:
	var dir: String = _fresh_dir("write")
	var path: String = _dormant_script.write_dormant_file(dir, 4242, 1700000000123)
	assert_eq(path, dir.path_join("dormant/4242.json"), "the path written")
	var text: String = FileAccess.get_file_as_string(path)
	var written: Variant = JSON.parse_string(text)
	assert_true(written is Dictionary, "the file is a JSON object: %s" % text)
	if written is Dictionary:
		assert_eq(written.get("pid"), 4242, "its pid")
		assert_eq(written.get("startedUnixMs"), 1700000000123, "its start")
	assert_true(not text.contains("."), "the numbers are written as integers: %s" % text)
	assert_eq(
		_dormant_script.dormant_content(7, 9), {"pid": 7, "startedUnixMs": 9}, "the content shape"
	)
	_dormant_script.remove_file(path)
	assert_true(not FileAccess.file_exists(path), "remove_file deletes it")
	_dormant_script.remove_file(path)
	_dormant_script.remove_file("")
	_remove_tree(dir)


func test_armed_quiet_reads_armed_json() -> void:
	var dir: String = _fresh_dir("quiet")
	assert_true(not _dormant_script.armed_quiet(dir), "no armed.json is not quiet")
	_write(dir.path_join("armed.json"), '{"quiet": true, "shutOutRealGamepads": false}')
	assert_true(_dormant_script.armed_quiet(dir), "quiet true")
	_write(dir.path_join("armed.json"), "{broken")
	assert_true(not _dormant_script.armed_quiet(dir), "a malformed armed.json is not quiet")
	_remove_tree(dir)


func test_armed_mute_is_the_arms_mute_or_quiet() -> void:
	var dir: String = _fresh_dir("mute")
	var armed: String = dir.path_join("armed.json")
	assert_true(not _dormant_script.armed_mute(dir), "no armed.json is not muted")
	_write(armed, '{"quiet": false, "mute": true}')
	assert_true(_dormant_script.armed_mute(dir), "mute true")
	_write(armed, '{"quiet": true, "shutOutRealGamepads": false}')
	assert_true(_dormant_script.armed_mute(dir), "quiet alone, which always silences")
	_write(armed, '{"quiet": false, "mute": false}')
	assert_true(not _dormant_script.armed_mute(dir), "neither")
	_write(armed, "{broken")
	assert_true(not _dormant_script.armed_mute(dir), "a malformed armed.json is not muted")
	_remove_tree(dir)


func test_frees_silently_is_a_switched_off_or_headless_armed_game() -> void:
	var dir: String = _fresh_dir("silent")
	assert_true(not _dormant_script.frees_silently(dir), "no armed.json, not switched off")
	_write(dir.path_join("armed.json"), "{}")
	assert_true(_dormant_script.frees_silently(dir), "an armed folder in a headless game")
	OS.set_environment("GODOT_MCP_OFF", "1")
	DirAccess.remove_absolute(dir.path_join("armed.json"))
	assert_true(_dormant_script.frees_silently(dir), "switched off")
	OS.unset_environment("GODOT_MCP_OFF")
	assert_true(not _dormant_script.frees_silently(dir), "switched back on, unarmed")
	_remove_tree(dir)


func test_choose_leaves_a_headless_armed_game_off_and_still_follows_the_environment() -> void:
	var dir: String = _fresh_dir("choose")
	_write(dir.path_join("armed.json"), '{"quiet": false}')
	var armed_only: Dictionary = _dormant_script.choose(dir)
	OS.set_environment("GODOT_MCP_PORT", "5")
	OS.set_environment("GODOT_MCP_TOKEN", "env")
	var run: Dictionary = _dormant_script.choose(dir)
	OS.set_environment("GODOT_MCP_OFF", "1")
	var off: Dictionary = _dormant_script.choose(dir)
	OS.unset_environment("GODOT_MCP_OFF")
	OS.unset_environment("GODOT_MCP_PORT")
	OS.unset_environment("GODOT_MCP_TOKEN")
	_write(dir.path_join("attach.json"), '{"port": 6, "token": "file"}')
	var attach: Dictionary = _dormant_script.choose(dir)
	_remove_tree(dir)
	assert_eq(
		armed_only, {"mode": "off", "endpoint": {}, "source": ""}, "armed.json alone, headless"
	)
	assert_eq(run.get("mode"), "run", "the environment runs")
	assert_eq(run.get("source"), "env", "from the environment")
	assert_eq(run["endpoint"].get("port"), 5, "the environment's port")
	assert_eq(off, {"mode": "off", "endpoint": {}, "source": ""}, "GODOT_MCP_OFF=1 is off")
	assert_eq(attach.get("mode"), "attach", "attach.json beside armed.json attaches")
	assert_eq(attach.get("source"), "attach", "from attach.json")
	assert_eq(attach["endpoint"].get("token"), "file", "attach.json's token")


func test_an_off_run_ignores_attach_json_and_warns_about_nothing() -> void:
	var dir: String = _fresh_dir("off_attach")
	_write(dir.path_join("armed.json"), "{}")
	_write(dir.path_join("attach.json"), '{"port": 6, "token": "file"}')
	var attached: Dictionary = _dormant_script.choose(dir)
	OS.set_environment("GODOT_MCP_OFF", "1")
	var valid: Dictionary = _dormant_script.choose(dir)
	# OFF short-circuits attach_endpoint, so a malformed file is never parsed and warns nothing.
	_write(dir.path_join("attach.json"), "{broken")
	var malformed: Dictionary = _dormant_script.choose(dir)
	OS.unset_environment("GODOT_MCP_OFF")
	_remove_tree(dir)
	assert_eq(attached.get("mode"), "attach", "a valid attach.json attaches a headless game")
	assert_eq(valid, {"mode": "off", "endpoint": {}, "source": ""}, "GODOT_MCP_OFF=1 wins")
	assert_eq(malformed, {"mode": "off", "endpoint": {}, "source": ""}, "a malformed one too")


func test_a_poll_joins_on_a_valid_join_file_and_deletes_both_files() -> void:
	var dir: String = _fresh_dir("join")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	waiter.enter()
	var dormant_file: String = _dormant_script.dormant_path(dir, 4242)
	assert_true(FileAccess.file_exists(dormant_file), "entering writes the dormant file")
	waiter.poll()
	assert_true(joined.is_empty(), "no join file, no join")
	assert_true(waiter.is_polling(), "the wait goes on")
	var join_file: String = _dormant_script.join_path(dir, 4242)
	_write(join_file, '{"port": 7, "token": "j", "shutOutRealGamepads": false, "quiet": true}')
	waiter.poll()
	assert_eq(joined.size(), 1, "a valid join file joins")
	assert_eq(joined[0].get("port") if not joined.is_empty() else null, 7, "with its endpoint")
	assert_true(not FileAccess.file_exists(join_file), "the join file is deleted")
	assert_true(not FileAccess.file_exists(dormant_file), "the dormant file is deleted")
	assert_true(not waiter.is_polling(), "the wait is over")
	waiter.free()
	_remove_tree(dir)


func test_a_poll_takes_an_attach_file_written_after_the_game_went_dormant() -> void:
	var dir: String = _fresh_dir("late_attach")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	waiter.enter()
	var attach_file: String = dir.path_join("attach.json")
	_write(attach_file, '{"port": 6, "token": "file"}')
	waiter.poll()
	var endpoint: Dictionary = joined[0] if not joined.is_empty() else {}
	assert_eq(joined.size(), 1, "an attach file written while dormant joins")
	assert_eq(endpoint.get("port"), 6, "with its port")
	assert_eq(endpoint.get("token"), "file", "and its token")
	assert_true(FileAccess.file_exists(attach_file), "the attach file is left for the server")
	assert_true(not waiter.is_polling(), "the wait is over")
	waiter.free()
	_remove_tree(dir)


func test_a_poll_prefers_its_join_file_to_the_attach_file() -> void:
	var dir: String = _fresh_dir("join_over_attach")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	waiter.enter()
	_write(dir.path_join("attach.json"), '{"port": 6, "token": "file"}')
	_write(_dormant_script.join_path(dir, 4242), '{"port": 7, "token": "j"}')
	waiter.poll()
	assert_eq(joined.size(), 1, "one join")
	assert_eq(joined[0].get("token") if not joined.is_empty() else null, "j", "the join file's")
	waiter.free()
	_remove_tree(dir)


func test_a_poll_takes_one_attach_token_once() -> void:
	var dir: String = _fresh_dir("attach_once")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	var attach_file: String = dir.path_join("attach.json")
	_write(attach_file, '{"port": 6, "token": "first"}')
	waiter.enter()
	waiter.poll()
	assert_eq(joined.size(), 1, "the attach file's token is taken")
	waiter.enter()
	waiter.poll()
	assert_eq(joined.size(), 1, "the same token is not taken again")
	assert_true(waiter.is_polling(), "the game stays dormant")
	_write(attach_file, '{"port": 6, "token": "second"}')
	waiter.poll()
	assert_eq(joined.size(), 2, "a new token is taken")
	assert_eq(joined[1].get("token") if joined.size() > 1 else null, "second", "the new one")
	waiter.free()
	_remove_tree(dir)


func test_a_malformed_attach_file_is_warned_about_once_and_the_wait_goes_on() -> void:
	var dir: String = _fresh_dir("malformed_attach")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	waiter.enter()
	_write(dir.path_join("attach.json"), "{broken")
	waiter.poll()
	waiter.poll()
	assert_eq(waiter.warnings.size(), 1, "the malformed attach file is warned about once")
	var message: String = str(waiter.warnings[0]) if not waiter.warnings.is_empty() else ""
	assert_true(message.contains("holds no {port, token}"), "it says what it holds: %s" % message)
	assert_true(joined.is_empty(), "it joins nothing")
	assert_true(waiter.is_polling(), "the wait goes on")
	waiter.free()
	_remove_tree(dir)


func test_an_attach_file_that_vanishes_before_its_read_is_not_warned_about() -> void:
	var dir: String = _fresh_dir("vanishing_attach")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _vanishing_waiter(dir)
	waiter.enter()
	var attach_file: String = dir.path_join("attach.json")
	_write(attach_file, "{broken")
	waiter.poll()
	var vanished_warnings: int = waiter.warnings.size()
	var gone: bool = not FileAccess.file_exists(attach_file)
	_write(attach_file, "{broken")
	waiter.poll()
	waiter.poll()
	var later_warnings: int = waiter.warnings.size()
	waiter.free()
	_remove_tree(dir)
	assert_true(gone, "the file vanished at the read")
	assert_eq(vanished_warnings, 0, "a file gone before its read is not warned about")
	assert_eq(later_warnings, 1, "a later malformed file is still warned about once")


func test_an_attach_file_gone_before_the_boot_read_is_not_warned_about() -> void:
	var dir: String = _fresh_dir("vanishing_boot_attach")
	var attach_file: String = dir.path_join("attach.json")
	var recorder := WarningRecorder.new()
	OS.add_logger(recorder)
	var gone: Dictionary = _dormant_script.attach_endpoint_from(attach_file, "", true)
	var gone_warnings: int = recorder.warnings.size()
	_write(attach_file, "{broken")
	var malformed: Dictionary = _dormant_script.attach_endpoint_from(attach_file, "{broken", true)
	OS.remove_logger(recorder)
	_remove_tree(dir)
	assert_true(gone.is_empty() and malformed.is_empty(), "neither holds an endpoint")
	assert_eq(gone_warnings, 0, "a file gone before its read is not warned about")
	assert_eq(recorder.warnings.size(), 1, "a malformed file that is there is warned about once")


func test_writing_the_dormant_file_leaves_no_temp_file() -> void:
	var dir: String = _fresh_dir("dormant_atomic")
	_dormant_script.write_dormant_file(dir, 4242, 1)
	var path: String = _dormant_script.write_dormant_file(dir, 4242, 2)
	var json := JSON.new()
	var parsed: int = json.parse(FileAccess.get_file_as_string(path))
	var content: Variant = json.data if parsed == OK else null
	var temp_left: bool = FileAccess.file_exists(path + ".tmp")
	_remove_tree(dir)
	assert_eq(path, _dormant_script.dormant_path(dir, 4242), "it returns the dormant file's path")
	assert_true(content is Dictionary, "the file holds an object")
	var started: Variant = (
		(content as Dictionary).get("startedUnixMs") if content is Dictionary else null
	)
	assert_eq(started, 2, "the second write's content")
	assert_true(not temp_left, "no temp file is left")


func test_a_poll_deletes_a_malformed_join_file_and_stays_dormant() -> void:
	var dir: String = _fresh_dir("malformed")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var joined: Array = []
	waiter.joined.connect(func(endpoint: Dictionary) -> void: joined.append(endpoint))
	waiter.enter()
	var join_file: String = _dormant_script.join_path(dir, 4242)
	_write(join_file, '{"port": 7}')
	waiter.poll()
	assert_true(joined.is_empty(), "a join file with no token joins nothing")
	assert_true(not FileAccess.file_exists(join_file), "it is deleted")
	assert_true(waiter.is_polling(), "the wait goes on")
	assert_eq(waiter.warnings.size(), 1, "the malformed join file is warned about once")
	var message: String = str(waiter.warnings[0]) if not waiter.warnings.is_empty() else ""
	assert_true(
		message.contains("holds no {port, token}"), "it says what the file holds: %s" % message
	)
	assert_true(message.contains("still dormant"), "and that the wait goes on: %s" % message)
	var dormant_file: String = _dormant_script.dormant_path(dir, 4242)
	assert_true(FileAccess.file_exists(dormant_file), "the dormant file stays")
	waiter.leave()
	assert_true(not FileAccess.file_exists(dormant_file), "leaving deletes the dormant file")
	waiter.free()
	_remove_tree(dir)


func test_a_poll_after_armed_json_goes_ends_the_wait_as_disarmed() -> void:
	var dir: String = _fresh_dir("disarm")
	_write(dir.path_join("armed.json"), "{}")
	var waiter: Node = _waiter(dir)
	var disarmed: Array = []
	waiter.disarmed.connect(func() -> void: disarmed.append(true))
	waiter.enter()
	DirAccess.remove_absolute(dir.path_join("armed.json"))
	waiter.poll()
	assert_eq(disarmed.size(), 1, "a disarmed folder ends the wait")
	assert_true(not waiter.is_polling(), "no more polling")
	var dormant_file: String = _dormant_script.dormant_path(dir, 4242)
	assert_true(not FileAccess.file_exists(dormant_file), "the dormant file is deleted")
	waiter.free()
	_remove_tree(dir)


func test_restore_window_gives_back_only_a_window_park_window_moved() -> void:
	var window: GDScript = load_bridge_script("godot_mcp_window.gd")
	window.parked = false
	window.restore_window()
	assert_true(not window.parked, "a window never parked stays as it is")
	window.park_window()
	assert_true(window.parked, "parking marks the window parked")
	window.restore_window()
	assert_true(not window.parked, "restoring gives it back once")


func test_park_window_saves_the_vsync_mode_once_and_restore_window_clears_it() -> void:
	var window: GDScript = load_bridge_script("godot_mcp_window.gd")
	assert_eq(window.vsync_to_save(false, 1, -1), 1, "a first park saves the mode the window has")
	assert_eq(
		window.vsync_to_save(true, 0, 1), 1, "a second park keeps the mode the first one saved"
	)
	window.vsync_before = -1
	window.parked = false
	window.park_window()
	assert_eq(window.vsync_before, -1, "a headless park saves no mode")
	window.vsync_before = 2
	window.parked = true
	window.restore_window()
	assert_eq(window.vsync_before, -1, "restoring clears the saved mode")


func test_held_buttons_lists_each_button_a_mask_holds() -> void:
	var held: Callable = _raw_events_script.held_buttons
	assert_eq(held.call(0), [] as Array[int], "an empty mask holds none")
	var mask: int = MOUSE_BUTTON_MASK_LEFT | MOUSE_BUTTON_MASK_RIGHT | MOUSE_BUTTON_MASK_MB_XBUTTON2
	assert_eq(
		held.call(mask),
		[MOUSE_BUTTON_LEFT, MOUSE_BUTTON_RIGHT, MOUSE_BUTTON_XBUTTON2] as Array[int],
		"left, right and the second extra button, lowest first"
	)


func test_the_gamepad_release_all_lets_go_of_buttons_and_centres_moved_axes() -> void:
	var pads: Node = _recording_pads()
	pads._held_buttons[Vector2i(0, JOY_BUTTON_A)] = true
	pads._held_buttons[Vector2i(1, JOY_BUTTON_B)] = true
	pads._axes[Vector2i(0, JOY_AXIS_LEFT_X)] = -0.75
	pads._axes[Vector2i(0, JOY_AXIS_TRIGGER_RIGHT)] = 0.0
	pads.release_all()
	var sent: Array = pads.sent
	assert_true(sent.has(["button", 0, JOY_BUTTON_A, false]), "pad 0's A is released: %s" % [sent])
	assert_true(sent.has(["button", 1, JOY_BUTTON_B, false]), "pad 1's B is released")
	assert_true(sent.has(["axis", 0, JOY_AXIS_LEFT_X, 0.0]), "the moved stick is centred")
	assert_eq(sent.size(), 3, "an axis already at rest sends nothing")
	assert_true(pads._held_buttons.is_empty(), "no button is left held")
	assert_eq(pads._axes[Vector2i(0, JOY_AXIS_LEFT_X)], 0.0, "the stick holds 0")
	pads.free()


func test_going_dormant_again_releases_the_keys_and_actions_a_drive_held() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	var pads: Node = _recording_pads()
	var raw_events: Node = _raw_events_script.new()
	var capture: Node = _stub_capture()
	gestures.bridge = bridge
	pads.bridge = bridge
	bridge._gestures = gestures
	bridge._pads = pads
	bridge._capture = capture
	raw_events.bridge = bridge
	raw_events._gestures = gestures
	raw_events._gestures_script = _input_script
	gestures.send_key(KEY_SHIFT, true, 0, PackedStringArray())
	gestures.send_key(KEY_A, true, 97, PackedStringArray())
	gestures.send_key(KEY_A, false, 97, PackedStringArray())
	raw_events._play_action({"action": "ui_accept", "pressed": true})
	assert_true(Input.is_key_pressed(KEY_SHIFT), "the shift a drive held reaches Input")
	assert_true(not Input.is_key_pressed(KEY_A), "the A it released does not")
	assert_true(Input.is_action_pressed("ui_accept"), "the action it held reaches Input")
	assert_eq(gestures.held_keys().size(), 1, "only the shift is still held")
	raw_events.release_all()
	assert_true(not Input.is_key_pressed(KEY_SHIFT), "the held shift is let go")
	assert_true(not Input.is_action_pressed("ui_accept"), "the held action is let go")
	assert_true(gestures.held_keys().is_empty(), "no key is left held")
	capture.free()
	raw_events.free()
	pads.free()
	gestures.free()
	bridge.free()


func test_an_action_release_with_no_strength_given_carries_none() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	var pads: Node = _recording_pads()
	var raw_events: Node = _raw_events_script.new()
	var capture: Node = _recording_capture()
	gestures.bridge = bridge
	pads.bridge = bridge
	bridge._gestures = gestures
	bridge._pads = pads
	bridge._capture = capture
	raw_events.bridge = bridge
	raw_events._gestures = gestures
	raw_events._gestures_script = _input_script
	raw_events._play_action({"action": "ui_accept", "pressed": true})
	raw_events._play_action({"action": "ui_accept", "pressed": false})
	var sent: Array = capture.sent_events
	assert_eq(sent.size(), 2, "the press and the release reach Input")
	assert_eq(sent[0].strength, 1.0, "the press carries the strength left out")
	assert_eq(sent[1].strength, 0.0, "the release carries none")
	capture.free()
	raw_events.free()
	pads.free()
	gestures.free()
	bridge.free()


## A capture that records nothing: what the input player's dispatch reports every sent event to.
func _stub_capture() -> Node:
	var script := GDScript.new()
	script.source_code = "extends Node\n\n\nfunc sent(_event: InputEvent) -> void:\n\tpass\n"
	script.reload()
	return script.new()


## A capture keeping every event the input player's dispatch reports, so a dispatched action's
## strength is visible.
func _recording_capture() -> Node:
	var script := GDScript.new()
	script.source_code = (
		"extends Node\n\n\nvar sent_events: Array = []\n\n\n"
		+ "func sent(event: InputEvent) -> void:\n\tsent_events.append(event)\n"
	)
	script.reload()
	return script.new()


## The gamepad with its two event senders recording [kind, device, index, value] in sent rather
## than dispatching, so no bridge is needed.
func _recording_pads() -> Node:
	var script := GDScript.new()
	script.source_code = (
		"\n"
		. join(
			[
				'extends "%s"' % _pads_script.resource_path,
				"",
				"var sent: Array = []",
				"",
				"",
				"func _send_button_event(device: int, button: int, pressed: bool) -> void:",
				'\tsent.append(["button", device, button, pressed])',
				"",
				"",
				"func _send_motion_event(device: int, axis: int, value: float) -> void:",
				'\tsent.append(["axis", device, axis, value])',
			]
		)
	)
	script.reload()
	return script.new()


## A dormant waiter on dir for pid 4242 that records what _warn would print, never added to the
## tree, so only poll() looks.
func _waiter(dir: String) -> Node:
	var script := GDScript.new()
	script.source_code = (
		"\n"
		. join(
			[
				'extends "%s"' % _dormant_script.resource_path,
				"",
				"var warnings: Array = []",
				"",
				"",
				"func _warn(message: String) -> void:",
				"\twarnings.append(message)",
			]
		)
	)
	script.reload()
	var waiter: Node = script.new()
	waiter.state_dir = dir
	waiter.pid = 4242
	waiter.started_unix_ms = 1700000000000
	return waiter


## A waiter as _waiter makes whose first read of attach.json finds it deleted between the
## existence check and the read, as the server's delete can land.
func _vanishing_waiter(dir: String) -> Node:
	var script := GDScript.new()
	script.source_code = (
		"\n"
		. join(
			[
				'extends "%s"' % _dormant_script.resource_path,
				"",
				"var warnings: Array = []",
				"var vanish: bool = true",
				"",
				"",
				"func _warn(message: String) -> void:",
				"\twarnings.append(message)",
				"",
				"",
				"func _read_attach(dir: String) -> Dictionary:",
				"\tif not vanish:",
				"\t\treturn super(dir)",
				"\tvanish = false",
				"\tDirAccess.remove_absolute(dir.path_join(ATTACH_FILE))",
				"\treturn {}",
			]
		)
	)
	script.reload()
	var waiter: Node = script.new()
	waiter.state_dir = dir
	waiter.pid = 4242
	waiter.started_unix_ms = 1700000000000
	return waiter


## An empty folder under this run's folder (run_dir.gd) for one test.
func _fresh_dir(test_name: String) -> String:
	var dir: String = ProjectSettings.globalize_path(
		RunDir.path("godot_mcp_dormant_test").path_join(test_name)
	)
	_remove_tree(dir)
	DirAccess.make_dir_recursive_absolute(dir)
	return dir


func _write(path: String, text: String) -> void:
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()


## Deletes dir with everything in it; nothing when it does not exist.
func _remove_tree(dir: String) -> void:
	if not DirAccess.dir_exists_absolute(dir):
		return
	for sub_dir in DirAccess.get_directories_at(dir):
		_remove_tree(dir.path_join(sub_dir))
	for file_name in DirAccess.get_files_at(dir):
		DirAccess.remove_absolute(dir.path_join(file_name))
	DirAccess.remove_absolute(dir)
