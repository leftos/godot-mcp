extends Node
## The godot-mcp bridge: an autoload injected into a game run through override.cfg.
##
## It dials the server at 127.0.0.1:GODOT_MCP_PORT, says hello with GODOT_MCP_TOKEN and the
## project path, then answers the server's requests. Frames are a 4-byte big-endian length
## followed by UTF-8 JSON. Requests are {id, command, params}; replies are
## {id, ok: true, result} or {id, ok: false, error}.

const HOST := "127.0.0.1"
const HEADER_BYTES := 4
const MAX_FRAME_BYTES := 16 * 1024 * 1024
const SCREENSHOT_DIR := "res://.godot/godot-mcp/screenshots"

var _stream: StreamPeerTCP
var _token: String = ""
var _buffer: PackedByteArray = PackedByteArray()
var _hello_sent: bool = false
var _connection_lost: bool = false


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	var port_text: String = OS.get_environment("GODOT_MCP_PORT")
	_token = OS.get_environment("GODOT_MCP_TOKEN")
	if not port_text.is_valid_int() or _token.is_empty():
		push_warning("godot-mcp bridge: GODOT_MCP_PORT or GODOT_MCP_TOKEN is not set; the bridge is off.")
		queue_free()
		return
	if OS.get_environment("GODOT_MCP_BACKGROUND") == "1":
		_enter_background()
	_stream = StreamPeerTCP.new()
	_stream.big_endian = true
	var error: Error = _stream.connect_to_host(HOST, port_text.to_int())
	if error != OK:
		push_error("godot-mcp bridge: cannot dial %s:%s (error %d)." % [HOST, port_text, error])
		_stream = null


func _process(_delta: float) -> void:
	if _stream == null or _connection_lost:
		return
	_stream.poll()
	var status: StreamPeerTCP.Status = _stream.get_status()
	if status == StreamPeerTCP.STATUS_CONNECTING:
		return
	if status != StreamPeerTCP.STATUS_CONNECTED:
		_connection_lost = true
		push_warning("godot-mcp bridge: the connection to the server is gone (status %d)." % status)
		return
	if not _hello_sent:
		_hello_sent = true
		_stream.set_no_delay(true)
		_send(
			{
				"type": "hello",
				"token": _token,
				"projectPath": ProjectSettings.globalize_path("res://"),
			}
		)
	_read_frames()


func _enter_background() -> void:
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_MOUSE_PASSTHROUGH, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_position(Vector2i(-9999, -9999))


func _read_frames() -> void:
	var available: int = _stream.get_available_bytes()
	if available > 0:
		var chunk: Array = _stream.get_partial_data(available)
		if chunk[0] == OK:
			_buffer.append_array(chunk[1])
	while _buffer.size() >= HEADER_BYTES:
		var length: int = (
			(_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3]
		)
		if length > MAX_FRAME_BYTES:
			push_error("godot-mcp bridge: a frame of %d bytes is over the limit; closing." % length)
			_stream.disconnect_from_host()
			_connection_lost = true
			return
		if _buffer.size() < HEADER_BYTES + length:
			return
		var payload: PackedByteArray = _buffer.slice(HEADER_BYTES, HEADER_BYTES + length)
		_buffer = _buffer.slice(HEADER_BYTES + length)
		_handle_frame(payload.get_string_from_utf8())


func _handle_frame(text: String) -> void:
	var request: Variant = JSON.parse_string(text)
	if not request is Dictionary or not request.has("id"):
		push_warning("godot-mcp bridge: dropped a frame that is not a request: %s" % text.left(200))
		return
	var id: int = int(request["id"])
	var command: String = str(request.get("command", ""))
	var params: Dictionary = {}
	if request.get("params") is Dictionary:
		params = request["params"]
	match command:
		"ping":
			_reply_ok(id, {"pong": true})
		"screenshot":
			_handle_screenshot(id, params)
		"ui_elements":
			var elements: Array = []
			var visible_only: bool = bool(params.get("visibleOnly", true))
			_collect_controls(get_tree().root, visible_only, str(params.get("classFilter", "")), elements)
			_reply_ok(id, {"elements": elements})
		"run_script":
			_handle_run_script(id, str(params.get("source", "")))
		"shutdown":
			_reply_ok(id, {})
			await get_tree().process_frame
			get_tree().quit()
		_:
			_reply_error(id, "unknown command '%s'" % command)


## Saves the next drawn frame of the root viewport (cropped when params.crop is set) as a PNG
## under the project's .godot/ folder, which projects keep out of git, and a scaled-down copy
## when the image is wider than params.previewMaxWidth.
func _handle_screenshot(id: int, params: Dictionary) -> void:
	await _wait_for_drawn_frame()
	var image: Image = get_viewport().get_texture().get_image()
	if image == null:
		_reply_error(id, "the viewport returned no image")
		return
	if params.get("crop") is Dictionary:
		var crop: Dictionary = params["crop"]
		var wanted := Rect2i(
			int(crop.get("x", 0)),
			int(crop.get("y", 0)),
			int(crop.get("width", 0)),
			int(crop.get("height", 0))
		)
		var inside: Rect2i = wanted.intersection(Rect2i(Vector2i.ZERO, image.get_size()))
		if not inside.has_area():
			_reply_error(id, "the crop %s lies outside the %s screenshot" % [wanted, image.get_size()])
			return
		image = image.get_region(inside)
	var directory: String = ProjectSettings.globalize_path(SCREENSHOT_DIR)
	DirAccess.make_dir_recursive_absolute(directory)
	var path: String = directory.path_join(_utc_stamp() + ".png")
	var error: Error = image.save_png(path)
	if error != OK:
		_reply_error(id, "saving %s failed: %s" % [path, error_string(error)])
		return
	var result: Dictionary = {"path": path, "width": image.get_width(), "height": image.get_height()}
	var preview_max_width: int = int(params.get("previewMaxWidth", 0))
	if preview_max_width > 0 and image.get_width() > preview_max_width:
		error = _save_preview(image, path.get_basename() + "_preview.png", preview_max_width, result)
		if error != OK:
			_reply_error(id, "saving the preview of %s failed: %s" % [path, error_string(error)])
			return
	_reply_ok(id, result)


func _save_preview(image: Image, path: String, max_width: int, result: Dictionary) -> Error:
	var height: int = maxi(1, int(round(float(image.get_height()) * max_width / image.get_width())))
	var preview: Image = image.duplicate()
	preview.resize(max_width, height, Image.INTERPOLATE_LANCZOS)
	var error: Error = preview.save_png(path)
	result["previewPath"] = path
	result["previewWidth"] = max_width
	result["previewHeight"] = height
	return error


## Returns once a frame has been drawn. A window the OS reports as undrawable (occluded) never
## emits frame_post_draw on its own, so one draw is forced; force_draw emits the signal
## synchronously with single-threaded rendering, hence the connection made before it.
func _wait_for_drawn_frame() -> void:
	if DisplayServer.window_can_draw():
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


## Appends every Control under node, depth first. An invisible Control hides its subtree when
## visible_only is set; class_filter keeps Controls of that engine class or a subclass of it.
func _collect_controls(node: Node, visible_only: bool, class_filter: String, into: Array) -> void:
	var control := node as Control
	if control != null:
		if visible_only and not control.is_visible_in_tree():
			return
		if class_filter.is_empty() or control.is_class(class_filter):
			into.append(_describe_control(control))
	for child in node.get_children():
		_collect_controls(child, visible_only, class_filter, into)


func _describe_control(control: Control) -> Dictionary:
	var rect: Rect2 = control.get_global_rect()
	var element: Dictionary = {
		"path": str(control.get_path()),
		"name": str(control.name),
		"class": control.get_class(),
		"rect": _to_json(rect),
		"visible": control.is_visible_in_tree(),
	}
	if control is Label or control is Button or control is LineEdit or control is RichTextLabel:
		element["text"] = str(control.get("text"))
	if control is BaseButton:
		element["disabled"] = (control as BaseButton).disabled
	if not control.tooltip_text.is_empty():
		element["tooltip"] = control.tooltip_text
	return element


## Compiles source, runs its execute(scene_tree) and replies {value}. A runtime error inside
## execute ends the call with null; its SCRIPT ERROR lines go to stderr, where the server
## reads them.
func _handle_run_script(id: int, source: String) -> void:
	var script := GDScript.new()
	script.source_code = source
	var error: Error = script.reload()
	if error != OK:
		_reply_error(id, "the script did not compile (%s, error %d)" % [error_string(error), error])
		return
	var instance: Variant = script.new()
	if not instance is Object or not (instance as Object).has_method("execute"):
		_free_unless_counted(instance)
		_reply_error(id, "the script defines no func execute(scene_tree: SceneTree) -> Variant")
		return
	var value: Variant = await instance.execute(get_tree())
	_free_unless_counted(instance)
	_reply_ok(id, {"value": _to_json(value)})


func _free_unless_counted(instance: Variant) -> void:
	if instance is Object and not instance is RefCounted and is_instance_valid(instance):
		(instance as Object).free()


## A JSON-safe copy of value: vectors, colours and rects become objects, a Node its path, any
## other Object its class and to_string(), containers recursively, anything else its str().
func _to_json(value: Variant) -> Variant:
	var json: Variant
	match typeof(value):
		TYPE_NIL, TYPE_BOOL, TYPE_INT, TYPE_STRING:
			json = value
		TYPE_FLOAT:
			json = value if is_finite(value) else str(value)
		TYPE_VECTOR2, TYPE_VECTOR2I:
			json = {"x": value.x, "y": value.y}
		TYPE_VECTOR3, TYPE_VECTOR3I:
			json = {"x": value.x, "y": value.y, "z": value.z}
		TYPE_COLOR:
			json = {"r": value.r, "g": value.g, "b": value.b, "a": value.a}
		TYPE_RECT2, TYPE_RECT2I:
			json = {
				"x": value.position.x,
				"y": value.position.y,
				"width": value.size.x,
				"height": value.size.y,
			}
		TYPE_DICTIONARY:
			json = {}
			for key: Variant in value:
				json[str(key)] = _to_json(value[key])
		TYPE_OBJECT:
			json = _object_to_json(value)
		_:
			json = _array_to_json(value) if typeof(value) >= TYPE_ARRAY else str(value)
	return json


func _array_to_json(values: Variant) -> Array:
	var json: Array = []
	for item: Variant in values:
		json.append(_to_json(item))
	return json


func _object_to_json(value: Variant) -> Variant:
	if not is_instance_valid(value):
		return "<freed object>"
	if value is Node:
		return str((value as Node).get_path())
	return {"class": (value as Object).get_class(), "string": (value as Object).to_string()}


func _reply_ok(id: int, result: Variant) -> void:
	_send({"id": id, "ok": true, "result": result})


func _reply_error(id: int, message: String) -> void:
	_send({"id": id, "ok": false, "error": message})


func _send(message: Dictionary) -> void:
	var payload: PackedByteArray = JSON.stringify(message).to_utf8_buffer()
	_stream.put_u32(payload.size())
	_stream.put_data(payload)
