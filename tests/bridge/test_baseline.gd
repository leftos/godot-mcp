extends "res://gd_test.gd"
## The screenshot comparison's pure logic (bridge/godot_mcp_baseline.gd): the changed-pixel
## count against a tolerance, the bounding box, the diff image and the size check, on synthetic
## images. The node is never created; compare is static.

const GREY := Color8(100, 100, 100)

var _baseline_script: GDScript = load_bridge_script("godot_mcp_baseline.gd")


func test_identical_images_have_nothing_changed() -> void:
	var result: Variant = _baseline_script.compare(_image(8, 6, GREY), _image(8, 6, GREY), 2)
	assert_eq(result["changed"], 0, "changed pixels")
	assert_eq(result["total"], 48, "total pixels")
	assert_eq(result["bbox"], null, "no bbox")
	assert_eq(result["diff"], null, "no diff image")


func test_a_difference_within_the_tolerance_is_not_counted() -> void:
	var current: Image = _image(8, 6, GREY)
	current.set_pixel(3, 2, Color8(102, 100, 100))
	var result: Variant = _baseline_script.compare(current, _image(8, 6, GREY), 2)
	assert_eq(result["changed"], 0, "off by 2 at tolerance 2")
	assert_eq(result["bbox"], null, "no bbox")


func test_a_difference_past_the_tolerance_is_counted() -> void:
	var current: Image = _image(8, 6, GREY)
	current.set_pixel(3, 2, Color8(100, 103, 100))
	var result: Variant = _baseline_script.compare(current, _image(8, 6, GREY), 2)
	assert_eq(result["changed"], 1, "off by 3 at tolerance 2")
	assert_eq(result["bbox"], {"x": 3, "y": 2, "width": 1, "height": 1}, "the one pixel's box")


func test_tolerance_zero_counts_a_one_level_change() -> void:
	var current: Image = _image(8, 6, GREY)
	current.set_pixel(0, 0, Color8(100, 100, 101))
	var result: Variant = _baseline_script.compare(current, _image(8, 6, GREY), 0)
	assert_eq(result["changed"], 1, "off by 1 at tolerance 0")


func test_an_alpha_only_difference_is_counted() -> void:
	var current: Image = _image(8, 6, GREY)
	current.set_pixel(5, 5, Color8(100, 100, 100, 200))
	var result: Variant = _baseline_script.compare(current, _image(8, 6, GREY), 2)
	assert_eq(result["changed"], 1, "alpha 200 against 255")


func test_separated_changes_share_one_bounding_box() -> void:
	var current: Image = _image(10, 8, GREY)
	current.set_pixel(1, 6, Color.WHITE)
	current.set_pixel(7, 2, Color.WHITE)
	var result: Variant = _baseline_script.compare(current, _image(10, 8, GREY), 2)
	assert_eq(result["changed"], 2, "two changed pixels")
	assert_eq(result["bbox"], {"x": 1, "y": 2, "width": 7, "height": 5}, "the union of both")


func test_rgb8_content_matches_the_same_rgba8_content() -> void:
	var current: Image = Image.create_empty(8, 6, false, Image.FORMAT_RGB8)
	current.fill(GREY)
	var result: Variant = _baseline_script.compare(current, _image(8, 6, GREY), 0)
	assert_eq(result["changed"], 0, "RGB8 against RGBA8")


func test_a_size_mismatch_names_both_sizes() -> void:
	var result: Variant = _baseline_script.compare(_image(8, 6, GREY), _image(4, 3, GREY), 2)
	assert_eq(result, "the screenshot is 8x6 but baseline is 4x3", "the refusal")


func test_the_diff_marks_changes_red_on_a_dimmed_grey_copy() -> void:
	var current: Image = _image(8, 6, Color.WHITE)
	current.fill_rect(Rect2i(2, 2, 2, 1), Color.BLUE)
	var result: Variant = _baseline_script.compare(current, _image(8, 6, Color.WHITE), 2)
	var diff: Image = result["diff"]
	assert_eq(result["changed"], 2, "the blue pixels")
	assert_eq(diff.get_format(), Image.FORMAT_RGBA8, "RGBA8")
	assert_eq(diff.get_size(), Vector2i(8, 6), "the screenshot's size")
	assert_eq(diff.get_pixel(3, 2), Color.RED, "a changed pixel")
	var unchanged: Color = diff.get_pixel(0, 0)
	# White at the diff's brightness, 0.4: 102/255 once stored as RGBA8.
	assert_approx(unchanged.r, 0.4, "an unchanged pixel's red is white dimmed")
	assert_approx(unchanged.g, 0.4, "an unchanged pixel's green is white dimmed")
	assert_approx(unchanged.b, 0.4, "an unchanged pixel's blue is white dimmed")
	assert_approx(unchanged.a, 1.0, "an unchanged pixel stays opaque")


func _image(width: int, height: int, color: Color) -> Image:
	var image: Image = Image.create_empty(width, height, false, Image.FORMAT_RGBA8)
	image.fill(color)
	return image
