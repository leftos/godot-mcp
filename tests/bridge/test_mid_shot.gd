# gdlint: disable=private-method-call
# The tests call the clock's private _poll_and_capture directly.
extends "res://gd_test.gd"
## A screenshot wait's shot condition (bridge/godot_mcp_mid_shot.gd, the clock's MidShot child): a
## wait carrying shotWhen checks it at each drawn frame while the wait polls, and its first holding
## frame is the wait's screenshot in place of the met frame. A headless game never draws, so the
## wait's probe emits RenderingServer.frame_post_draw by hand, one draw per emission, and the frame
## module stand-in tags each grabbed frame with the draw it came from.

const MARKER := "[test] mid shot end marker"

var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


## A shot condition that holds on an earlier draw than the wait's own condition: the wait is met,
## and its screenshot is the frame the shot condition first held on, not the met one.
func test_a_shot_condition_that_holds_mid_wait_is_the_waits_screenshot() -> void:
	var rig: Dictionary = _rig()
	var params: Dictionary = {"screenshot": true, "shotWhen": {"kind": "exists", "node": "Flash"}}
	var outcome: Dictionary = await rig["time"]._poll_and_capture(_two_draws(rig), 0, params)
	assert_eq(outcome["result"]["met"], true, "the wait is met")
	assert_eq(outcome["result"].get("shotHeld"), true, "its shot condition held")
	assert_eq(outcome["result"].get("screenshot"), {"path": "shot-1"}, "the first draw's frame")
	_assert_no_mid_shot_left(rig)
	_free(rig)


## A shot condition that never holds leaves the met wait without a screenshot or a warning.
func test_a_shot_condition_that_never_holds_leaves_the_wait_unshot() -> void:
	var rig: Dictionary = _rig()
	var params: Dictionary = {"screenshot": true, "shotWhen": {"kind": "exists", "node": "Never"}}
	var outcome: Dictionary = await rig["time"]._poll_and_capture(_two_draws(rig), 0, params)
	assert_eq(outcome["result"]["met"], true, "the wait is met")
	assert_eq(outcome["result"].get("shotHeld"), false, "its shot condition never held")
	assert_true(not outcome["result"].has("screenshot"), "no screenshot: %s" % outcome)
	assert_true(not outcome["result"].has("warning"), "no warning: %s" % outcome)
	_assert_no_mid_shot_left(rig)
	_free(rig)


## The shot condition changes neither the wait's window end nor the game ms its met check saw.
func test_a_shot_condition_leaves_the_waits_end_marker_and_game_ms_as_they_are() -> void:
	var ends: Array = []
	for shot_when: Variant in [{"kind": "exists", "node": "Flash"}, null]:
		var rig: Dictionary = _rig()
		var clock: Dictionary = {"seconds": 0.0, "seen": 0.0, "frames": 0}
		rig["clock"] = clock
		var params: Dictionary = {
			"screenshot": true, "endMarker": MARKER, "limitGameMs": 1000, "_gameClock": clock
		}
		if shot_when != null:
			params["shotWhen"] = shot_when
		else:
			# Without a shot condition the met frame is captured; a headless warning stands in.
			rig["frame"].headless = "headless"
		var outcome: Dictionary = await rig["time"]._poll_and_capture(_two_draws(rig), 0, params)
		var marks: Array = rig["bridge"]._logger.take_pending()[0]
		ends.append([outcome["result"].get("endMarked"), params.get("_metGameMs"), marks])
		_assert_no_mid_shot_left(rig)
		_free(rig)
	assert_eq(ends[0][0], true, "the window ends with a shot condition")
	assert_eq(ends[0], ends[1], "endMarked, the met check's game ms and the mark, either way")


## A shot condition the bridge cannot check is not held, and the reply says why.
func test_a_shot_condition_of_an_unknown_kind_is_not_held_and_names_its_refusal() -> void:
	var rig: Dictionary = _rig()
	var params: Dictionary = {"screenshot": true, "shotWhen": {"kind": "bogus"}}
	var outcome: Dictionary = await rig["time"]._poll_and_capture(_two_draws(rig), 0, params)
	assert_eq(outcome["result"]["met"], true, "the wait is met")
	assert_eq(outcome["result"].get("shotHeld"), false, "its shot condition did not hold")
	assert_eq(outcome["result"].get("shotError"), "unknown condition kind 'bogus'", "the refusal")
	assert_true(not outcome["result"].has("screenshot"), "no screenshot: %s" % outcome)
	_assert_no_mid_shot_left(rig)
	_free(rig)


## A plain screenshot wait that ends while another wait's shot check runs leaves that check alone:
## it captures as it would without one and gains no shotHeld, and the running check stays
## connected and later yields its own wait's shot.
func test_a_plain_screenshot_wait_leaves_another_waits_shot_check_running() -> void:
	var rig: Dictionary = _rig()
	var mid_shot: Node = rig["time"].get_node("MidShot")
	var scratch: Dictionary = {"screenshot": true, "shotWhen": {"kind": "exists", "node": "Flash"}}
	mid_shot.start(scratch)
	# The plain wait's met frame capture: a headless warning stands in for it.
	rig["frame"].headless = "headless"
	var plain: Dictionary = await rig["time"]._poll_and_capture(
		func() -> Array: return [true, 1], 0, {"screenshot": true}
	)
	assert_eq(plain["result"].get("warning"), "headless", "the plain wait captures its own frame")
	assert_true(not plain["result"].has("shotHeld"), "and gains no shotHeld: %s" % plain)
	assert_true(
		RenderingServer.frame_post_draw.is_connected(mid_shot._check_draw),
		"the other wait's shot check still runs"
	)
	rig["frame"].tag = 3
	rig["bridge"].nodes["Flash"] = rig["flash"]
	RenderingServer.frame_post_draw.emit()
	var outcome: Dictionary = {"result": {"met": true}}
	var shot: Variant = mid_shot.take(outcome, null, scratch)
	assert_eq(outcome["result"].get("shotHeld"), true, "the other wait's check held")
	assert_eq(shot.get_meta("tag") if shot is Image else null, 3, "on its own draw")
	_assert_no_mid_shot_left(rig)
	_free(rig)


## The wait's probe as a wait drawing two frames before its condition is met: Flash exists in the
## first draw (tag 1) and not in the second (tag 2), and the clock in rig, if any, moves 60 ms
## before each draw.
func _two_draws(rig: Dictionary) -> Callable:
	return func() -> Array:
		for tag: int in [1, 2]:
			if rig.has("clock"):
				_time_script.advance_game_clock(rig["clock"], false, 0.06)
			rig["frame"].tag = tag
			if tag == 1:
				rig["bridge"].nodes["Flash"] = rig["flash"]
			else:
				rig["bridge"].nodes.erase("Flash")
			RenderingServer.frame_post_draw.emit()
		return [true, 2]


func _assert_no_mid_shot_left(rig: Dictionary) -> void:
	var mid_shot: Node = rig["time"].get_node("MidShot")
	var left: Array = RenderingServer.frame_post_draw.get_connections().filter(
		func(connection: Dictionary) -> bool:
			return (connection["callable"] as Callable).get_object() == mid_shot
	)
	assert_eq(left.size(), 0, "no MidShot check is left connected to frame_post_draw")


## A clock whose bridge is a stand-in holding the conditions module, a frame module stand-in, an
## unregistered logger and a node map its _find_node reads; Flash is the node a shot condition
## looks for.
func _rig() -> Dictionary:
	var bridge: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var _conditions: Node",
					"var _frame: Node",
					"var _logger: Logger",
					"var nodes: Dictionary = {}",
					"",
					"",
					"func _find_node(element: String) -> Node:",
					"\treturn nodes.get(element)",
				]
			)
		)
	)
	var frame: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var tag: int = 0",
					'var headless: String = ""',
					"",
					"",
					"func headless_warning() -> String:",
					"\treturn headless",
					"",
					"",
					"func grab_frame() -> Image:",
					"\tvar image := Image.create(1, 1, false, Image.FORMAT_RGBA8)",
					'\timage.set_meta("tag", tag)',
					"\treturn image",
					"",
					"",
					"func save_screenshot(image: Image, _params: Dictionary) -> Variant:",
					'\treturn {"path": "shot-%d" % int(image.get_meta("tag"))}',
				]
			)
		)
	)
	var conditions: Node = load_bridge_script("godot_mcp_conditions.gd").new()
	conditions.bridge = bridge
	bridge._conditions = conditions
	bridge._frame = frame
	bridge._logger = load_bridge_script("godot_mcp_logger.gd").new()
	var flash := Node.new()
	flash.name = "Flash"
	var time: Node = _time_script.new()
	time.bridge = bridge
	return {"time": time, "bridge": bridge, "frame": frame, "flash": flash}


func _free(rig: Dictionary) -> void:
	rig["time"].free()
	rig["bridge"]._conditions.free()
	rig["frame"].free()
	rig["bridge"].free()
	rig["flash"].free()


## An instance of a script compiled from source.
func _compile(source: String) -> Node:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
