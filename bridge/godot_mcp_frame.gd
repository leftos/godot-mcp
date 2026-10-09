extends Node
## The godot-mcp bridge's frame capture, a child of the bridge: the root viewport's image with
## every native window pasted on it, and the PNGs a screenshot, a baseline and a preview save.
##
## A child, as the Baseline child is, so get_viewport and get_tree reach the bridge's frame. A
## screenshot's pixels are the render target's: the window less its letterbox bars under
## canvas_items, the base size under viewport.

## The folder a screenshot is saved under, under the project's .godot/, which projects keep out of
## git.
const SCREENSHOT_DIR := "res://.godot/godot-mcp/screenshots"
## What a capture answers when a drawing game's viewport has no image.
const NO_IMAGE := "the viewport returned no image"
## What a frame-reading tool answers on a headless game (headless_refusal).
const HEADLESS_REFUSAL := (
	"a headless game draws no frames, so there is nothing to capture; read state with "
	+ "get_game_state, get_ui_elements or run_script, or run the scene windowed."
)
## What a wait's screenshot warns on a headless game (headless_warning).
const HEADLESS_WARNING := (
	"a headless game draws no frames, so no screenshot was taken; read state with "
	+ "get_game_state, get_ui_elements or run_script, or run the scene windowed."
)


## Whether the game runs on the headless display server, which draws no frames: it reports that
## no window can draw (servers/display/display_server_headless.h L139-141 in 4.7.2), and the dummy
## renderer's viewport texture is no texture, so reading its image logs 'Parameter "t" is null'
## (servers/rendering/dummy/storage/texture_storage.h L108-110, L229).
static func is_headless() -> bool:
	return DisplayServer.get_name() == "headless"


## HEADLESS_REFUSAL on a headless game, else empty: every frame-reading tool asks it before it
## grabs or waits for a frame.
static func headless_refusal() -> String:
	return HEADLESS_REFUSAL if is_headless() else ""


## HEADLESS_WARNING on a headless game, else empty.
static func headless_warning() -> String:
	return HEADLESS_WARNING if is_headless() else ""


## The root viewport's image with every visible window that is not embedded (a popup or tooltip
## of a project that turns embed_subwindows off, an OS window of its own) pasted on it at its place
## in the frame, in the order the display server lists them; null when the viewport has no
## image, and on a headless game, whose viewport is never read. Embedded windows are already in
## the root viewport's image.
func grab_frame() -> Image:
	if is_headless():
		return null
	var canvas: Image = get_viewport().get_texture().get_image()
	if canvas == null:
		return null
	return paste_windows(canvas, _native_window_images())


## canvas with each {image: Image, rect: Rect2i} of windows alpha-blended on it in order: the image
## scaled to rect's size and converted to canvas's format when they differ (on a copy, never the
## caller's image), and clipped to the canvas. A rect with no area pastes nothing.
static func paste_windows(canvas: Image, windows: Array[Dictionary]) -> Image:
	for window: Dictionary in windows:
		var rect: Rect2i = window["rect"]
		if not rect.has_area():
			continue
		var image: Image = window["image"]
		if image.get_size() != rect.size or image.get_format() != canvas.get_format():
			image = image.duplicate() as Image
			image.resize(rect.size.x, rect.size.y)
			image.convert(canvas.get_format())
		canvas.blend_rect(image, Rect2i(Vector2i.ZERO, image.get_size()), rect.position)
	return canvas


## Every visible window of this process other than the root that is not embedded, as
## {image, rect} with rect in frame pixels, in the display server's order.
func _native_window_images() -> Array[Dictionary]:
	var found: Array[Dictionary] = []
	var root: Window = get_tree().root
	for window_id: int in DisplayServer.get_window_list():
		var instance_id: int = DisplayServer.window_get_attached_instance_id(window_id)
		var window := instance_from_id(instance_id) as Window
		if window == null or window == root or window.is_embedded() or not window.visible:
			continue
		var image: Image = window.get_texture().get_image()
		if image != null:
			found.append({"image": image, "rect": _window_rect_in_frame(window)})
	return found


## A window's screen rect in frame pixels: its offset from the root window, through the
## viewport-to-frame transform with the inverse of the viewport's screen transform on the right,
## which divides the window transform back out.
func _window_rect_in_frame(window: Window) -> Rect2i:
	var to_frame: Transform2D = (
		_viewport_to_frame() * get_viewport().get_screen_transform().affine_inverse()
	)
	var origin := Vector2(window.position - get_tree().root.position)
	return _rect_through(to_frame, Rect2(origin, Vector2(window.size)))


## The transform from viewport coordinates (the ones get_ui_elements, hover and the input tools
## use) to frame pixels (the render target's pixels: the window less its letterbox bars under
## canvas_items, the base size under viewport): the root's stretch transform and global canvas
## transform, without the window transform that places the frame between the bars
## (scene/main/window.cpp L1400-1426, L3233-3236 in 4.7.2).
func _viewport_to_frame() -> Transform2D:
	var viewport := get_viewport()
	return viewport.get_stretch_transform() * viewport.get_global_canvas_transform()


## rect through transform, its corners rounded to whole pixels.
static func _rect_through(transform: Transform2D, rect: Rect2) -> Rect2i:
	var top_left: Vector2 = transform * rect.position
	var bottom_right: Vector2 = transform * rect.end
	return Rect2i(Vector2i(top_left.round()), Vector2i((bottom_right - top_left).round()))


## Saves image (cropped when params.crop is set) as a PNG under the project's .godot/ folder,
## which projects keep out of git, and a scaled-down copy when the image is wider than
## params.previewMaxWidth. Returns {path, width, height[, previewPath, previewWidth,
## previewHeight]}, or a String saying why it could not: on a headless game, with no image,
## HEADLESS_REFUSAL.
func save_screenshot(image: Image, params: Dictionary) -> Variant:
	if image == null:
		return HEADLESS_REFUSAL if is_headless() else NO_IMAGE
	if params.get("crop") is Dictionary:
		var cropped: Variant = crop(image, params["crop"])
		if cropped is String:
			return cropped
		image = cropped
	var directory: String = ProjectSettings.globalize_path(SCREENSHOT_DIR)
	DirAccess.make_dir_recursive_absolute(directory)
	# The process id keeps two games on one project from writing one file in the same millisecond.
	var file_name: String = "%s-%d.png" % [_utc_stamp(), OS.get_process_id()]
	var path: String = directory.path_join(file_name)
	var error: Error = image.save_png(path)
	if error != OK:
		return "saving %s failed: %s" % [path, error_string(error)]
	var result: Dictionary = {
		"path": path, "width": image.get_width(), "height": image.get_height()
	}
	var preview_max_width: int = int(params.get("previewMaxWidth", 0))
	error = save_preview(image, path.get_basename() + "_preview.png", preview_max_width, result)
	if error != OK:
		return "saving the preview of %s failed: %s" % [path, error_string(error)]
	return result


## The part of image, a frame of the root viewport, inside the crop {x, y, width, height}, or a
## String when none of it is. The crop is in viewport coordinates and is mapped to the frame's
## pixels through _viewport_to_frame, never less than a pixel a side, so a stretched window's
## crop keeps the same content at the frame's scale.
func crop(image: Image, region: Dictionary) -> Variant:
	var wanted := Rect2i(
		int(region.get("x", 0)),
		int(region.get("y", 0)),
		int(region.get("width", 0)),
		int(region.get("height", 0))
	)
	var mapped: Rect2i = _rect_through(_viewport_to_frame(), Rect2(wanted))
	mapped.size = mapped.size.maxi(1)
	var inside: Rect2i = mapped.intersection(Rect2i(Vector2i.ZERO, image.get_size()))
	if not inside.has_area():
		var viewport_size := Vector2i(get_viewport().get_visible_rect().size)
		return "the crop %s lies outside the %s viewport" % [wanted, viewport_size]
	return image.get_region(inside)


## Writes image scaled to max_width as a PNG at path, adding previewPath, previewWidth and
## previewHeight to result. Returns OK writing nothing and leaving result alone when max_width is
## zero or less, or image is no wider than it.
func save_preview(image: Image, path: String, max_width: int, result: Dictionary) -> Error:
	if max_width <= 0 or image.get_width() <= max_width:
		return OK
	var height: int = maxi(1, int(round(float(image.get_height()) * max_width / image.get_width())))
	var preview: Image = image.duplicate()
	preview.resize(max_width, height, Image.INTERPOLATE_LANCZOS)
	var error: Error = preview.save_png(path)
	result["previewPath"] = path
	result["previewWidth"] = max_width
	result["previewHeight"] = height
	return error


## Returns once a frame has been drawn. A window the OS reports as undrawable (occluded), or a
## game in low-processor mode with nothing changed, never emits frame_post_draw on its own
## (main/main.cpp L5071-5086 in 4.7.2), so one draw is forced; force_draw emits the signal
## synchronously with single-threaded rendering, hence the connection made before it.
func wait_for_drawn_frame() -> void:
	if DisplayServer.window_can_draw() and not OS.low_processor_usage_mode:
		await RenderingServer.frame_post_draw
		return
	var drawn: Array[bool] = [false]
	var on_drawn := func() -> void: drawn[0] = true
	RenderingServer.frame_post_draw.connect(on_drawn, CONNECT_ONE_SHOT)
	RenderingServer.force_draw(false, 0.0)
	if not drawn[0]:
		await RenderingServer.frame_post_draw
	elif RenderingServer.frame_post_draw.is_connected(on_drawn):
		RenderingServer.frame_post_draw.disconnect(on_drawn)


## A UTC timestamp safe in a file name, to the millisecond: 20260925T123456_789Z.
func _utc_stamp() -> String:
	var now: float = Time.get_unix_time_from_system()
	var stamp: String = Time.get_datetime_string_from_unix_time(int(now))
	stamp = stamp.replace("-", "").replace(":", "")
	return "%s_%03dZ" % [stamp, int(fmod(now, 1.0) * 1000.0)]
