extends "res://gd_test.gd"
## The bridge's pure helpers (bridge/godot_mcp_bridge.gd), on an instance never added to the
## tree. Constructing it runs _init, whose endpoint lookup finds no GODOT_MCP_* variables and no
## attach file here, so it registers no logger; _ready, which frees a bridge that is off, never
## runs outside the tree.
# gdlint: disable=private-method-call

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")


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
