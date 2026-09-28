extends "res://gd_test.gd"
## The bridge's paste of window images onto a captured frame (paste_windows in
## bridge/godot_mcp_bridge.gd), on images made here, with no window open.

const BLACK := Color(0, 0, 0, 1)

var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")


func test_paste_blends_a_window_at_its_rect() -> void:
	var canvas: Image = _filled(8, 8, BLACK)
	var windows: Array[Dictionary] = [
		{"image": _filled(2, 3, Color(1, 0, 0, 0.5)), "rect": Rect2i(3, 2, 2, 3)}
	]
	var pasted: Image = _bridge_script.paste_windows(canvas, windows)
	# An 8-bit alpha of 0.5 is 128 / 255, so the blend lands within a step of half red.
	var top_left: Color = pasted.get_pixel(3, 2)
	var bottom_right: Color = pasted.get_pixel(4, 4)
	assert_true(absf(top_left.r - 0.5) < 0.01, "the top-left corner is blended half over black")
	assert_true(
		absf(bottom_right.r - 0.5) < 0.01, "the bottom-right corner is blended half over black"
	)
	assert_eq([top_left.g, top_left.b, top_left.a], [0.0, 0.0, 1.0], "over an opaque canvas")
	assert_eq(pasted.get_pixel(2, 2), BLACK, "left of the rect is untouched")
	assert_eq(pasted.get_pixel(5, 2), BLACK, "right of the rect is untouched")
	assert_eq(pasted.get_pixel(3, 5), BLACK, "below the rect is untouched")
	assert_eq(pasted.get_size(), Vector2i(8, 8), "the canvas keeps its size")


func test_paste_clips_a_window_outside_the_canvas() -> void:
	var canvas: Image = _filled(8, 8, BLACK)
	var green := Color(0, 1, 0, 1)
	var windows: Array[Dictionary] = [
		{"image": _filled(4, 4, green), "rect": Rect2i(-2, 6, 4, 4)},
		{"image": _filled(2, 2, green), "rect": Rect2i(20, 20, 2, 2)},
	]
	var pasted: Image = _bridge_script.paste_windows(canvas, windows)
	assert_eq(pasted.get_pixel(0, 6), green, "the part inside the canvas is pasted")
	assert_eq(pasted.get_pixel(1, 7), green, "up to the canvas's corner")
	assert_eq(pasted.get_pixel(2, 6), BLACK, "right of the window is untouched")
	assert_eq(pasted.get_pixel(0, 5), BLACK, "above the window is untouched")
	assert_eq(pasted.get_size(), Vector2i(8, 8), "the canvas keeps its size")


func test_paste_scales_a_window_to_its_rect() -> void:
	var canvas: Image = _filled(8, 8, BLACK)
	var blue := Color(0, 0, 1, 1)
	var window: Image = Image.create(1, 1, false, Image.FORMAT_RGB8)
	window.fill(blue)
	var windows: Array[Dictionary] = [{"image": window, "rect": Rect2i(1, 1, 3, 2)}]
	var pasted: Image = _bridge_script.paste_windows(canvas, windows)
	assert_eq(pasted.get_pixel(1, 1), blue, "the rect's top-left is covered")
	assert_eq(pasted.get_pixel(3, 2), blue, "the rect's bottom-right is covered")
	assert_eq(pasted.get_pixel(4, 1), BLACK, "right of the rect is untouched")
	assert_eq(pasted.get_pixel(1, 3), BLACK, "below the rect is untouched")
	assert_eq(pasted.get_format(), Image.FORMAT_RGBA8, "the canvas keeps its format")
	assert_eq(window.get_size(), Vector2i(1, 1), "the window's own image is left alone")


func test_paste_with_no_windows_returns_the_canvas_unchanged() -> void:
	var canvas: Image = _filled(4, 4, Color(0.2, 0.4, 0.6, 1))
	var before: PackedByteArray = canvas.get_data()
	var windows: Array[Dictionary] = []
	var pasted: Image = _bridge_script.paste_windows(canvas, windows)
	assert_eq(pasted.get_data(), before, "the pixels are unchanged")
	assert_eq(pasted.get_size(), Vector2i(4, 4), "the size is unchanged")


static func _filled(width: int, height: int, color: Color) -> Image:
	var image: Image = Image.create(width, height, false, Image.FORMAT_RGBA8)
	image.fill(color)
	return image
