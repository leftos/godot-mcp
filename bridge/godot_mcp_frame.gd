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
## What a scaled capture answers when the root's stretch mode is not canvas_items, with the mode.
const SCALE_MODE_REFUSAL := (
	"scale needs the project's stretch mode canvas_items (it is %s): any other mode re-lays out "
	+ "the game or adds no detail; relaunch with --resolution instead"
)
## What a scaled capture answers on a maximized or fullscreen window.
const SCALE_WINDOW_REFUSAL := (
	"scale needs a windowed, not maximized or fullscreen, window: Windows does not resize it; "
	+ "relaunch with --resolution instead"
)
## What a scaled capture answers while Movie Maker records.
const SCALE_RECORDING_REFUSAL := (
	"scale is refused while the run records: " + "the resize would be in the clip"
)
## The warning of a scaled capture whose window grew less than asked.
const SCALE_CLAMPED_WARNING := (
	"the window could only grow to %dx%d, not %dx%d, " + "so the capture is %sx, not %sx"
)
## The warning of a scaled capture whose window did not return to its size.
const SCALE_UNRESTORED_WARNING := "the window did not return to %dx%d; it is %dx%d"
## The project setting's name of each Window.ContentScaleMode, as a refusal names it.
const STRETCH_MODE_NAMES := {
	Window.CONTENT_SCALE_MODE_DISABLED: "disabled",
	Window.CONTENT_SCALE_MODE_CANVAS_ITEMS: "canvas_items",
	Window.CONTENT_SCALE_MODE_VIEWPORT: "viewport",
}
## The window modes the OS does not resize (platform/windows/display_server_windows.cpp
## L2503-2505 in 4.7.2).
const UNRESIZABLE_MODES: Array[int] = [
	Window.MODE_MAXIMIZED, Window.MODE_FULLSCREEN, Window.MODE_EXCLUSIVE_FULLSCREEN
]
## The process frames a scaled capture waits for its window to return to its size.
const RESTORE_FRAMES := 30
## The most frame saves encoding off the main thread at once (save_screenshot_off_thread): an
## image of a 1920 x 1080 frame holds 8 MB, so past this a due frame is saved on the main thread.
## The server's MaxCaptureSavesInFlight (RuntimeTools.Frames.cs) holds the same value and sizes
## the reply's allowance by it.
const MAX_SAVES_IN_FLIGHT := 8

## The saves handed to the WorkerThreadPool and not yet waited, in the order they started, each
## {task, path, outcome: [Error]}. A finished task keeps its Callable, and so its image, until it
## is waited, so each is waited as soon as it is seen finished (saves_in_flight).
var _saves: Array[Dictionary] = []
## The message of the first save a waited task reports failed since finish_saves last answered,
## or "".
var _save_failure: String = ""


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
	var prepared: Variant = _prepare_screenshot(image, params, "")
	if prepared is String:
		return prepared
	return _write_screenshot(prepared, params)


## Saves image, the frame of this process frame, as save_screenshot does with no preview, its file
## named for the frame too (<stamp>-<pid>-<frame>.png), so two frames saved in one millisecond
## never share a path. While fewer than MAX_SAVES_IN_FLIGHT saves run, it only crops and names the
## file here and hands the PNG's encode and write to a WorkerThreadPool task, which finish_saves
## collects; at the cap it saves here. Returns {path, width, height}, the file still being written
## when a task has it, or a String as save_screenshot.
func save_screenshot_off_thread(image: Image, params: Dictionary) -> Variant:
	var tag: String = "-%d" % Engine.get_process_frames()
	var prepared: Variant = _prepare_screenshot(image, params, tag)
	if prepared is String:
		return prepared
	if saves_in_flight() >= MAX_SAVES_IN_FLIGHT:
		return _write_screenshot(prepared, params)
	var outcome: Array[Error] = [OK]
	var encode: Callable = _encode.bind(prepared["image"], prepared["path"], outcome)
	var task: int = WorkerThreadPool.add_task(encode, false, "godot-mcp frame save")
	_saves.append({"task": task, "path": prepared["path"], "outcome": outcome})
	return _saved(prepared)


## How many saves save_screenshot_off_thread started are still encoding or writing. Each one
## found finished is waited here and dropped, freeing its image, its failure kept for
## finish_saves.
func saves_in_flight() -> int:
	var running: Array[Dictionary] = []
	for save: Dictionary in _saves:
		if WorkerThreadPool.is_task_completed(save["task"]):
			_collect(save)
		else:
			running.append(save)
	_saves = running
	return _saves.size()


## Waits for every save save_screenshot_off_thread started, the main thread running a frame
## between checks while this node is in the tree (blocking outside it), and collects each task.
## Returns the message of the first save that failed, in the order they started, or "".
func finish_saves() -> String:
	while is_inside_tree() and saves_in_flight() > 0:
		await get_tree().process_frame
	wait_saves()
	var failed: String = _save_failure
	_save_failure = ""
	return failed


## Blocks until every save task still running has ended and collects each, keeping the first
## failure for finish_saves: every task must be waited (doc/classes/WorkerThreadPool.xml L72 in
## 4.7.2), and one still running when the game quits can crash its exit.
func wait_saves() -> void:
	for save: Dictionary in _saves:
		_collect(save)
	_saves.clear()


## What a failed save of path answers.
static func save_failure(path: String, error: Error) -> String:
	return "saving %s failed: %s" % [path, error_string(error)]


## No save task outlives this node: leaving the tree, as the game's quit makes it, or being freed
## waits for each.
func _notification(what: int) -> void:
	if what == NOTIFICATION_EXIT_TREE or what == NOTIFICATION_PREDELETE:
		wait_saves()


## Waits a save's task, which frees it and its image, and keeps its failure when it is the first.
func _collect(save: Dictionary) -> void:
	WorkerThreadPool.wait_for_task_completion(save["task"])
	var error: Error = save["outcome"][0]
	if error != OK and _save_failure.is_empty():
		_save_failure = save_failure(save["path"], error)


## image cropped as params.crop asks and the path its PNG goes to, its folder made, the file
## named <stamp>-<pid><tag>.png: {image, path}, or a String saying why not, as save_screenshot
## answers it.
func _prepare_screenshot(image: Image, params: Dictionary, tag: String) -> Variant:
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
	var file_name: String = "%s-%d%s.png" % [_utc_stamp(), OS.get_process_id(), tag]
	return {"image": image, "path": directory.path_join(file_name)}


## Encodes and writes prepared's image as a PNG at its path, and its preview when params asks one,
## as save_screenshot does.
func _write_screenshot(prepared: Dictionary, params: Dictionary) -> Variant:
	var image: Image = prepared["image"]
	var path: String = prepared["path"]
	var error: Error = image.save_png(path)
	if error != OK:
		return save_failure(path, error)
	var result: Dictionary = _saved(prepared)
	var preview_max_width: int = int(params.get("previewMaxWidth", 0))
	error = save_preview(image, path.get_basename() + "_preview.png", preview_max_width, result)
	if error != OK:
		return "saving the preview of %s failed: %s" % [path, error_string(error)]
	return result


## A save's {path, width, height} from _prepare_screenshot's result.
static func _saved(prepared: Dictionary) -> Dictionary:
	var image: Image = prepared["image"]
	return {"path": prepared["path"], "width": image.get_width(), "height": image.get_height()}


## A save task's work, on a worker thread: image encoded and written as a PNG at path, its Error
## put in outcome[0], which the main thread reads only once the task is waited.
static func _encode(image: Image, path: String, outcome: Array[Error]) -> void:
	outcome[0] = image.save_png(path)


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
	var inside: Rect2i = crop_rect(_viewport_to_frame(), wanted, image.get_size())
	if not inside.has_area():
		var viewport_size := Vector2i(get_viewport().get_visible_rect().size)
		return "the crop %s lies outside the %s viewport" % [wanted, viewport_size]
	return image.get_region(inside)


## The part of a frame of frame_size inside wanted, a rect in viewport coordinates mapped to frame
## pixels through to_frame, never less than a pixel a side; no area when none of it is inside.
static func crop_rect(to_frame: Transform2D, wanted: Rect2i, frame_size: Vector2i) -> Rect2i:
	var mapped: Rect2i = _rect_through(to_frame, Rect2(wanted))
	mapped.size = mapped.size.maxi(1)
	return mapped.intersection(Rect2i(Vector2i.ZERO, frame_size))


## Saves the next drawn frame as save_screenshot does; with params.scale above 1, the frame
## grab_scaled takes, its reply adding scale and any warning.
func capture_screenshot(params: Dictionary) -> Variant:
	var scale: float = float(params.get("scale", 1.0))
	if scale <= 1.0:
		await wait_for_drawn_frame()
		return save_screenshot(grab_frame(), params)
	var shot: Variant = await grab_scaled(scale, params.get("crop"))
	if shot is String:
		return shot
	var uncropped: Dictionary = params.duplicate()
	uncropped.erase("crop")
	var saved: Variant = save_screenshot(shot["image"], uncropped)
	if saved is Dictionary:
		shot.erase("image")
		saved.merge(shot)
	return saved


## Why a scaled capture is refused, or empty: the root's stretch mode must be canvas_items, the only
## one that renders a bigger window at more pixels without re-laying the game out
## (scene/main/window.cpp L1328-1331, L1407-1424 in 4.7.2); its window mode one the OS resizes; and
## Movie Maker must not record (movie_path empty).
static func scale_refusal(stretch_mode: int, window_mode: int, movie_path: String) -> String:
	if stretch_mode != Window.CONTENT_SCALE_MODE_CANVAS_ITEMS:
		return SCALE_MODE_REFUSAL % STRETCH_MODE_NAMES.get(stretch_mode, str(stretch_mode))
	if window_mode in UNRESIZABLE_MODES:
		return SCALE_WINDOW_REFUSAL
	if not movie_path.is_empty():
		return SCALE_RECORDING_REFUSAL
	return ""


## The root window grown to scale times its size for one drawn frame, that frame grabbed (cropped to
## region, a viewport rect, through the grown frame's own transform when region is a Dictionary),
## and the window restored: {image, scale[, warning]} as scaled_shot gives it, or a String saying
## why not. The refusals come before any resize.
func grab_scaled(scale: float, region: Variant) -> Variant:
	var root: Window = get_tree().root
	var refusal: String = scale_refusal(
		root.content_scale_mode, root.mode, Engine.get_write_movie_path()
	)
	if not refusal.is_empty():
		return refusal
	var original: Vector2i = root.size
	var wanted := Vector2i((Vector2(original) * scale).round())
	root.size = wanted
	await get_tree().process_frame
	await wait_for_drawn_frame()
	var got: Vector2i = root.size
	var image: Image = grab_frame()
	var shot: Variant = crop(image, region) if image != null and region is Dictionary else image
	var unrestored: String = await _restore_size(root, original)
	if shot == null:
		return NO_IMAGE
	if shot is String:
		return shot
	var result: Dictionary = scaled_shot(shot, original, wanted, got, scale)
	_add_warning(result, unrestored)
	return result


## result with warning added to its warning, joined by "; " when it has one; unchanged when warning
## is empty.
static func _add_warning(result: Dictionary, warning: String) -> void:
	if warning.is_empty():
		return
	var earlier: String = result.get("warning", "")
	result["warning"] = warning if earlier.is_empty() else earlier + "; " + warning


## {image, scale}, scale the window's growth reached (its smaller side's, to two decimals), plus a
## warning when got, the size the window reached, is smaller than wanted.
static func scaled_shot(
	image: Image, original: Vector2i, wanted: Vector2i, got: Vector2i, scale: float
) -> Dictionary:
	var reached: float = minf(float(got.x) / original.x, float(got.y) / original.y)
	var shot: Dictionary = {"image": image, "scale": roundf(reached * 100.0) / 100.0}
	if got.x < wanted.x or got.y < wanted.y:
		var sizes: Array = [got.x, got.y, wanted.x, wanted.y]
		shot["warning"] = SCALE_CLAMPED_WARNING % (sizes + [_decimals(reached), _decimals(scale)])
	return shot


## value with at most two decimals and no trailing zeros: 2, 1.5, 2.34.
static func _decimals(value: float) -> String:
	return ("%.2f" % value).rstrip("0").rstrip(".")


## Sets root back to original and waits, up to RESTORE_FRAMES process frames, until its size reads
## back as original; returns SCALE_UNRESTORED_WARNING when it never does, else empty.
func _restore_size(root: Window, original: Vector2i) -> String:
	root.size = original
	for _waited: int in RESTORE_FRAMES:
		await get_tree().process_frame
		if root.size == original:
			return ""
	return SCALE_UNRESTORED_WARNING % [original.x, original.y, root.size.x, root.size.y]


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
