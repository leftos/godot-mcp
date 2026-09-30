extends Node
## The godot-mcp bridge's screenshot comparison, a child of the bridge: captures the next drawn
## frame and counts the pixels that differ from a baseline PNG the server saved earlier.
##
## A baseline is read from its absolute path: Image.load_from_file warns on a res:// path and
## logs "Failed to load image" when the file cannot be read (core/io/image.cpp L2768-2788 in
## 4.7.2), so the file's presence is checked first.

## The share of a channel's range an unchanged pixel may drift by, on top of the tolerance's
## whole levels: a level is 1/255, and the half level keeps float rounding off the boundary.
const HALF_LEVEL := 0.5
## How much of its brightness the diff image keeps for unchanged pixels, shown grey.
const DIFF_BRIGHTNESS := 0.4

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node


## Compares current with baseline, both converted to RGBA8: a pixel is changed when one of its
## channels, alpha included, differs by more than tolerance levels (0-255). Returns {changed,
## total, bbox, diff}: bbox {x, y, width, height} around the changed pixels and diff a dimmed
## grey copy of current with the changed pixels red, both null when nothing changed; or a String
## when the sizes differ.
static func compare(current: Image, baseline: Image, tolerance: int) -> Variant:
	var now: Image = current.duplicate() as Image
	now.convert(Image.FORMAT_RGBA8)
	var then: Image = baseline.duplicate() as Image
	then.convert(Image.FORMAT_RGBA8)
	# compute_image_metrics silently compares only the smaller size (image.cpp L4729-4730).
	if now.get_size() != then.get_size():
		return (
			"the screenshot is %dx%d but baseline is %dx%d"
			% [now.get_width(), now.get_height(), then.get_width(), then.get_height()]
		)
	if now.compute_image_metrics(then, false)["max"] <= tolerance:
		return {
			"changed": 0, "total": now.get_width() * now.get_height(), "bbox": null, "diff": null
		}
	return _diff(now, then, (tolerance + HALF_LEVEL) / 255.0)


## Captures the next drawn frame (cropped to params.crop), saves it as a screenshot does, and
## compares it with the PNG at params.baselinePath within params.tolerance. Returns {path,
## width, height, changedPixels, totalPixels, bbox[, diffPath, diffPreviewPath]}, or a String
## saying why it could not.
func compare_screenshot(params: Dictionary) -> Variant:
	var baseline_path: String = str(params.get("baselinePath", ""))
	if not FileAccess.file_exists(baseline_path):
		return "there is no baseline file at %s" % baseline_path
	await bridge._frame.wait_for_drawn_frame()
	var capture: Variant = _capture(params.get("crop"))
	if capture is String:
		return capture
	var baseline: Image = Image.load_from_file(baseline_path)
	if baseline == null:
		return "the baseline %s could not be read as an image" % baseline_path
	var compared: Variant = compare(capture, baseline, int(params.get("tolerance", 0)))
	if compared is String:
		return compared
	var saved: Variant = bridge._frame.save_screenshot(capture, {})
	if saved is String:
		return saved
	return _reply(saved, compared, int(params.get("previewMaxWidth", 0)))


## The changed pixels of two RGBA8 images of one size, counted pixel by pixel: get_pixel pairs
## measured faster than a loop over get_data's bytes (62 ms against 112 ms at 1280x720).
static func _diff(now: Image, then: Image, threshold: float) -> Dictionary:
	var diff: Image = now.duplicate() as Image
	diff.adjust_bcs(DIFF_BRIGHTNESS, 1.0, 0.0)
	var changed: int = 0
	var low := Vector2i(now.get_width(), now.get_height())
	var high := Vector2i(-1, -1)
	for y in now.get_height():
		for x in now.get_width():
			var a: Color = now.get_pixel(x, y)
			var b: Color = then.get_pixel(x, y)
			var rgb_gap: float = maxf(absf(a.r - b.r), maxf(absf(a.g - b.g), absf(a.b - b.b)))
			if maxf(rgb_gap, absf(a.a - b.a)) > threshold:
				changed += 1
				diff.set_pixel(x, y, Color.RED)
				low = Vector2i(mini(low.x, x), mini(low.y, y))
				high = Vector2i(maxi(high.x, x), maxi(high.y, y))
	var total: int = now.get_width() * now.get_height()
	if changed == 0:
		return {"changed": 0, "total": total, "bbox": null, "diff": null}
	var bbox: Dictionary = {
		"x": low.x, "y": low.y, "width": high.x - low.x + 1, "height": high.y - low.y + 1
	}
	return {"changed": changed, "total": total, "bbox": bbox, "diff": diff}


## The root viewport's image, cropped to crop when it is a Dictionary; a String when there is
## no image or the crop misses it.
func _capture(crop: Variant) -> Variant:
	var image: Image = bridge._frame.grab_frame()
	if image == null:
		return "the viewport returned no image"
	if crop is Dictionary:
		return bridge._frame.crop(image, crop)
	return image


## The reply for a saved capture and its comparison; when pixels changed, the diff image is
## saved beside the capture, with a preview when it is wider than preview_max_width (0: none).
func _reply(saved: Dictionary, compared: Dictionary, preview_max_width: int) -> Variant:
	var reply: Dictionary = {
		"path": saved["path"],
		"width": saved["width"],
		"height": saved["height"],
		"changedPixels": compared["changed"],
		"totalPixels": compared["total"],
		"bbox": compared["bbox"],
	}
	if compared["diff"] == null:
		return reply
	var diff: Image = compared["diff"]
	var diff_path: String = str(saved["path"]).get_basename() + "_diff.png"
	var error: Error = diff.save_png(diff_path)
	if error != OK:
		return "saving %s failed: %s" % [diff_path, error_string(error)]
	reply["diffPath"] = diff_path
	var preview: Dictionary = {}
	var preview_path: String = diff_path.get_basename() + "_preview.png"
	error = bridge._frame.save_preview(diff, preview_path, preview_max_width, preview)
	if error != OK:
		return "saving %s failed: %s" % [preview_path, error_string(error)]
	if preview.has("previewPath"):
		reply["diffPreviewPath"] = preview["previewPath"]
	return reply
