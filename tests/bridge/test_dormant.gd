extends "res://gd_test.gd"
## The dormant mode (bridge/godot_mcp_dormant.gd): how the bridge decides to run, the attach and
## join file parse, the dormant file, when an ended connection goes dormant again, and one poll's
## outcome. Every file lives in a folder under the OS temp folder, never this project's own
## .godot/godot-mcp/, where the other tests' bridges must still find no endpoint.

var _dormant_script: GDScript = load_bridge_script("godot_mcp_dormant.gd")
var _raw_events_script: GDScript = load_bridge_script("godot_mcp_raw_events.gd")
var _pads_script: GDScript = load_bridge_script("godot_mcp_gamepad.gd")


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


## A dormant waiter on dir for pid 4242, never added to the tree, so only poll() looks.
func _waiter(dir: String) -> Node:
	var waiter: Node = _dormant_script.new()
	waiter.state_dir = dir
	waiter.pid = 4242
	waiter.started_unix_ms = 1700000000000
	return waiter


## An empty folder under the OS temp folder for one test.
func _fresh_dir(test_name: String) -> String:
	var dir: String = OS.get_temp_dir().path_join("godot_mcp_dormant_test").path_join(test_name)
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
