extends "res://gd_test.gd"
## The bridge's pure helpers (bridge/godot_mcp_bridge.gd), on an instance never added to the
## tree. Constructing it runs _init, whose endpoint lookup finds no GODOT_MCP_* variables and no
## attach file here, so it registers no logger; _ready, which frees a bridge that is off, never
## runs outside the tree.
# gdlint: disable=private-method-call

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")
var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")


func test_cancel_of_an_answered_request_replies_false() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._time = _time_script.new()
	var waiting: Dictionary = {"kind": "exists", "node": "Main", "timeoutMs": 10000}
	var answered: Dictionary = {"kind": "exists", "node": "Main", "timeoutMs": 10000}
	bridge._track(17, "wait_for", answered)
	bridge._track(18, "wait_for", waiting)
	bridge._track(19, "ping", {})
	bridge._running_requests.erase(17)
	assert_true(not bridge._cancel(17), "an answered request")
	assert_true(not answered.has("_cancelled"), "an answered request's params are left alone")
	assert_true(not bridge._cancel(19), "a request that is not cancellable")
	assert_true(not bridge._cancel(42), "an unknown request")
	assert_true(bridge._cancel(18), "a running wait")
	assert_eq(waiting.get("_cancelled"), true, "its params are marked, ending its poll")
	bridge._time.free()
	bridge.free()


func test_a_lost_connection_forgets_the_running_requests() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._track(18, "wait_for", {"kind": "exists", "node": "Main", "timeoutMs": 10000})
	bridge._end_connection()
	assert_true(bridge._connection_lost, "the connection is marked lost")
	assert_true(bridge._running_requests.is_empty(), "no running request is left to cancel")
	bridge.free()


func test_save_preview_leaves_a_narrow_image_alone() -> void:
	var bridge: Node = _bridge_script.new()
	var path: String = OS.get_temp_dir().path_join("godot_mcp_narrow_preview.png")
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)
	var result: Dictionary = {}
	var image: Image = Image.create_empty(8, 4, false, Image.FORMAT_RGBA8)
	assert_eq(bridge._save_preview(image, path, 16, result), OK, "a narrow image saves nothing")
	assert_true(not FileAccess.file_exists(path), "no file is written")
	assert_true(result.is_empty(), "no preview is reported")
	bridge.free()


func test_save_preview_scales_a_wide_image_down() -> void:
	var bridge: Node = _bridge_script.new()
	var path: String = OS.get_temp_dir().path_join("godot_mcp_wide_preview.png")
	var result: Dictionary = {}
	var image: Image = Image.create_empty(32, 16, false, Image.FORMAT_RGBA8)
	assert_eq(bridge._save_preview(image, path, 16, result), OK, "a wide image is scaled down")
	assert_eq(result.get("previewPath"), path, "the preview's path")
	assert_eq(result.get("previewWidth"), 16, "the preview's width")
	assert_eq(result.get("previewHeight"), 8, "the preview's height, kept in proportion")
	assert_true(FileAccess.file_exists(path), "the file is written")
	DirAccess.remove_absolute(path)
	bridge.free()
