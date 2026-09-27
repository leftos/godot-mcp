extends Node
## The godot-mcp bridge: an autoload injected into a game run through override.cfg.
##
## It dials the server at 127.0.0.1:GODOT_MCP_PORT, says hello with GODOT_MCP_TOKEN, the
## project path and its own process id, then answers the server's requests. A game run_project
## did not launch finds the port and token in the attach file attach_project writes instead;
## with neither, the bridge stays off. Frames are a 4-byte big-endian length
## followed by UTF-8 JSON. Requests are {id, command, params}; replies are
## {id, ok: true, result} or {id, ok: false, error}. The errors and warnings the game logs
## (godot_mcp_logger.gd) go out as {type: "errors", entries, dropped} frames without an id,
## each frame, and before every reply, so a command's errors reach the server before its reply.

const HOST := "127.0.0.1"
const HEADER_BYTES := 4
const MAX_FRAME_BYTES := 16 * 1024 * 1024
const SCREENSHOT_DIR := "res://.godot/godot-mcp/screenshots"
const ATTACH_FILE := "res://.godot/godot-mcp/attach.json"
const GAMEPAD_SCRIPT := "godot_mcp_gamepad.gd"
const INPUT_SCRIPT := "godot_mcp_input.gd"
const INSPECT_SCRIPT := "godot_mcp_inspect.gd"
const TIME_SCRIPT := "godot_mcp_time.gd"
const BASELINE_SCRIPT := "godot_mcp_baseline.gd"
const LOGGER_SCRIPT := "godot_mcp_logger.gd"
const JSON_SCRIPT := "godot_mcp_json.gd"
const PREVIEW_SCRIPT := "godot_mcp_preview.gd"
## A quiet run's frame-rate cap when the project sets none: its frames are never seen, so drawing
## at the monitor's refresh rate only burns the GPU.
const QUIET_MAX_FPS := 60
## The device id every injected mouse event carries, so _input can tell it from the real mouse
## (DEVICE_ID_MOUSE, 32) and from the engine's own ids: 0-15 joypads, 16-31 keyboards, -1
## emulation, -2 internal (core/input/input_event.h L64-67 in 4.7.2).
const INJECTED_DEVICE := 0x6D6370

var _stream: StreamPeerTCP
var _token: String = ""
var _buffer: PackedByteArray = PackedByteArray()
var _hello_sent: bool = false
var _connection_lost: bool = false
## The mouse buttons the injected input holds down, as a MouseButtonMask.
var _held_mask: int = 0
## Where the injected pointer last was, in window coordinates.
var _pointer: Vector2 = Vector2.ZERO
## Whether an input gesture is playing, including the frames that settle it.
var _gesture_playing: bool = false
## Whether _dispatch is delivering an injected event, so the touch twins Input makes of it pass.
var _dispatching: bool = false
## The gamepad (godot_mcp_gamepad.gd beside this script), a child once the bridge is on.
var _pads: Node
## The input player (godot_mcp_input.gd beside this script): gestures and raw events.
var _gestures: Node
## The inspector (godot_mcp_inspect.gd beside this script), a child once the bridge is on.
var _inspect: Node
## The clock (godot_mcp_time.gd beside this script): pause, step, time scale and waits.
var _time: Node
## The screenshot comparison (godot_mcp_baseline.gd beside this script).
var _baseline: Node
## The scene preview (godot_mcp_preview.gd beside this script): preview_scene's framing and capture.
var _preview: Node
## The JSON conversion (godot_mcp_json.gd beside this script), static functions called on the
## script itself, by this script and by the Inspect and Time modules.
var _json: GDScript
## Every command's handler, func(id, params), by command name (_command_handlers).
var _handlers: Dictionary = {}
## The server to dial, found in _init; empty when the bridge is off.
var _endpoint: Dictionary = {}
## The logger (godot_mcp_logger.gd beside this script) collecting the game's errors, registered
## in _init when there is a server to send them to. It is never removed: the engine removes
## script loggers at shutdown, and remove_logger is unsafe while other threads log.
var _logger: Logger


## Finds the server and registers the error logger as early as an autoload can: a logger sees
## only what is logged after OS.add_logger.
func _init() -> void:
	_endpoint = _find_endpoint()
	if _endpoint.is_empty():
		return
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_logger = (load(script_dir.path_join(LOGGER_SCRIPT)) as GDScript).new()
	OS.add_logger(_logger)


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	if _endpoint.is_empty():
		push_warning(
			(
				"godot-mcp bridge: GODOT_MCP_PORT and GODOT_MCP_TOKEN are not set and there is no "
				+ "attach file; the bridge is off."
			)
		)
		queue_free()
		return
	_token = _endpoint["token"]
	var port: int = _endpoint["port"]
	if OS.get_environment("GODOT_MCP_QUIET") == "1":
		_park_window()
		if Engine.max_fps == 0:
			Engine.max_fps = QUIET_MAX_FPS
	# A preview's scene enters after this autoload's _ready, so it never runs a frame unpaused.
	if OS.get_environment("GODOT_MCP_PREVIEW") == "1":
		get_tree().paused = true
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_json = load(script_dir.path_join(JSON_SCRIPT)) as GDScript
	_pads = (load(script_dir.path_join(GAMEPAD_SCRIPT)) as GDScript).new()
	_pads.name = "Gamepad"
	add_child(_pads)
	_gestures = (load(script_dir.path_join(INPUT_SCRIPT)) as GDScript).new()
	_gestures.name = "Gestures"
	_gestures.bridge = self
	add_child(_gestures)
	_inspect = (load(script_dir.path_join(INSPECT_SCRIPT)) as GDScript).new()
	_inspect.name = "Inspect"
	add_child(_inspect)
	if _endpoint["shutOutRealGamepads"]:
		_pads.shut_out_real_pads()
	_time = (load(script_dir.path_join(TIME_SCRIPT)) as GDScript).new()
	_time.name = "Time"
	_time.bridge = self
	add_child(_time)
	_baseline = (load(script_dir.path_join(BASELINE_SCRIPT)) as GDScript).new()
	_baseline.name = "Baseline"
	_baseline.bridge = self
	add_child(_baseline)
	_preview = (load(script_dir.path_join(PREVIEW_SCRIPT)) as GDScript).new()
	_preview.name = "Preview"
	_preview.bridge = self
	add_child(_preview)
	_handlers = _command_handlers()
	_stream = StreamPeerTCP.new()
	_stream.big_endian = true
	var error: Error = _stream.connect_to_host(HOST, port)
	if error != OK:
		push_error("godot-mcp bridge: cannot dial %s:%d (error %d)." % [HOST, port, error])
		_stream = null


## The server to dial and whether to shut the real pads out, {port, token,
## shutOutRealGamepads}: GODOT_MCP_PORT, GODOT_MCP_TOKEN and GODOT_MCP_SHUT_OUT_REAL_GAMEPADS
## from run_project, else the attach file attach_project writes; empty when there is neither.
## override.cfg's joypad setting is written to match, but the bridge reads only these.
func _find_endpoint() -> Dictionary:
	var port_text: String = OS.get_environment("GODOT_MCP_PORT")
	var token: String = OS.get_environment("GODOT_MCP_TOKEN")
	if port_text.is_valid_int() and not token.is_empty():
		var shut_out_real_gamepads: bool = (
			OS.get_environment("GODOT_MCP_SHUT_OUT_REAL_GAMEPADS") == "1"
		)
		return {
			"port": port_text.to_int(),
			"token": token,
			"shutOutRealGamepads": shut_out_real_gamepads
		}
	var path: String = ProjectSettings.globalize_path(ATTACH_FILE)
	if not FileAccess.file_exists(path):
		return {}
	var attach: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if (
		not attach is Dictionary
		or not (attach as Dictionary).has("port")
		or not (attach as Dictionary).has("token")
	):
		push_warning("godot-mcp bridge: %s holds no {port, token}; the bridge is off." % path)
		return {}
	return {
		"port": int(attach["port"]),
		"token": str(attach["token"]),
		"shutOutRealGamepads": bool(attach.get("shutOutRealGamepads", false)),
	}


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
				"pid": OS.get_process_id(),
			}
		)
	_flush_errors()
	_read_frames()


## Sends the errors logged since the last flush as one {type: "errors", entries, dropped}
## frame, once the hello is out. While the socket is not connected they stay queued (the
## logger caps them), so a failing send cannot feed its own errors back in a loop.
func _flush_errors() -> void:
	if _logger == null or not _hello_sent or _connection_lost:
		return
	if _stream.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		return
	var taken: Array = _logger.take_pending()
	var entries: Array = taken[0]
	var dropped: int = taken[1]
	if entries.is_empty() and dropped == 0:
		return
	_send({"type": "errors", "entries": entries, "dropped": dropped})


## Keeps the real mouse out of injected input: while a gesture plays or injected input holds a
## button, a mouse button or motion event without the injected mark is marked handled here.
## The root viewport runs every _input before its GUI (Viewport::push_input), so the GUI never
## sees it; hover still follows the real pointer, since push_input updates it before _input.
## With emulate_touch_from_mouse on, Input sends each left-button event's touch twin (device
## DEVICE_ID_EMULATION) just before the event itself, the injected ones' included
## (input.cpp L850-861, L876-891 in 4.7.2); a twin that arrives outside _dispatch is a real one's.
func _input(event: InputEvent) -> void:
	if not (_gesture_playing or _held_mask != 0):
		return
	if _is_real_pointer_event(event):
		get_viewport().set_input_as_handled()


## Whether event is a mouse button or motion without the injected mark, or a touch twin raised
## outside _dispatch: the real input _input swallows while injected input is in play.
func _is_real_pointer_event(event: InputEvent) -> bool:
	if event is InputEventMouseButton or event is InputEventMouseMotion:
		return event.device != INJECTED_DEVICE
	if event is InputEventScreenTouch or event is InputEventScreenDrag:
		return event.device == InputEvent.DEVICE_ID_EMULATION and not _dispatching
	return false


## A quiet run's window: its override.cfg created it unfocused, and asked for an off-screen
## position that Windows clamps onto the primary screen at creation
## (platform/windows/display_server_windows.cpp L7180-7183, L7206-7211 in 4.7.2), so it is moved
## off-screen here, where window_set_position does not clamp, and made click-through.
func _park_window() -> void:
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_MOUSE_PASSTHROUGH, true)
	DisplayServer.window_set_position(Vector2i(-9999, -9999))


func _read_frames() -> void:
	var available: int = _stream.get_available_bytes()
	if available > 0:
		var chunk: Array = _stream.get_partial_data(available)
		if chunk[0] == OK:
			_buffer.append_array(chunk[1])
	while _buffer.size() >= HEADER_BYTES:
		var length: int = (_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3]
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
	if not _handlers.has(command):
		_reply_error(id, "unknown command '%s'" % command)
		return
	(_handlers[command] as Callable).call(id, params)


## Every command's handler, func(id, params), by command name; built once in _ready.
func _command_handlers() -> Dictionary:
	return {
		"ping": _handle_ping,
		"screenshot": _handle_screenshot,
		"ui_elements": _handle_ui_elements,
		"run_script": _handle_run_script,
		"input": _handle_input,
		"scene_tree": _handle_inspect.bind("scene_tree"),
		"inspect_node": _handle_inspect.bind("inspect_node"),
		"set_property": _handle_inspect.bind("set_property"),
		"call_method": _handle_inspect.bind("call_method"),
		"frame": _handle_time.bind("frame"),
		"wait_for": _handle_time.bind("wait_for"),
		"compare_screenshot": _handle_compare,
		"preview": _handle_preview,
		"movie_frame": _handle_movie_frame,
		"shutdown": _handle_shutdown,
	}


func _handle_ping(id: int, _params: Dictionary) -> void:
	_reply_ok(id, {"pong": true})


## Replies with the number of frames Movie Maker has written so far, the index of the next one.
## The writer begins before the first iteration (4.7.2 main.cpp L4855) and adds one frame at
## the end of every iteration, after the process frame count is raised (L5119, L5151), so a
## command handled during an iteration sees exactly the frames written before it.
func _handle_movie_frame(id: int, _params: Dictionary) -> void:
	_reply_ok(id, {"frame": Engine.get_process_frames()})


func _handle_ui_elements(id: int, params: Dictionary) -> void:
	var elements: Array = []
	var visible_only: bool = bool(params.get("visibleOnly", true))
	_collect_controls(get_tree().root, visible_only, str(params.get("classFilter", "")), elements)
	_reply_ok(id, {"elements": elements})


## Runs a scene_tree, inspect_node, set_property or call_method request on the Inspect child,
## which answers a Dictionary or a String saying why it could not.
func _handle_inspect(id: int, params: Dictionary, command: String) -> void:
	var result: Variant = await _inspect.handle(command, params)
	if result is String:
		_reply_error(id, result)
	else:
		_reply_ok(id, result)


## Replies, then quits once the reply has had a frame to go out.
func _handle_shutdown(id: int, _params: Dictionary) -> void:
	_reply_ok(id, {})
	await get_tree().process_frame
	get_tree().quit()


## Saves the next drawn frame of the root viewport as _save_screenshot does.
func _handle_screenshot(id: int, params: Dictionary) -> void:
	await _wait_for_drawn_frame()
	var saved: Variant = _save_screenshot(get_viewport().get_texture().get_image(), params)
	if saved is String:
		_reply_error(id, saved)
		return
	_reply_ok(id, saved)


## Frames and saves the scene a preview_scene run shows, on the Preview child.
func _handle_preview(id: int, params: Dictionary) -> void:
	var result: Variant = await _preview.capture(params)
	if result is String:
		_reply_error(id, result)
	else:
		_reply_ok(id, result)


## Saves image (cropped when params.crop is set) as a PNG under the project's .godot/ folder,
## which projects keep out of git, and a scaled-down copy when the image is wider than
## params.previewMaxWidth. Returns {path, width, height[, previewPath, previewWidth,
## previewHeight]}, or a String saying why it could not.
func _save_screenshot(image: Image, params: Dictionary) -> Variant:
	if image == null:
		return "the viewport returned no image"
	if params.get("crop") is Dictionary:
		var cropped: Variant = _crop(image, params["crop"])
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
	error = _save_preview(image, path.get_basename() + "_preview.png", preview_max_width, result)
	if error != OK:
		return "saving the preview of %s failed: %s" % [path, error_string(error)]
	return result


## The part of image inside crop {x, y, width, height}, or a String when none of it is.
func _crop(image: Image, crop: Dictionary) -> Variant:
	var wanted := Rect2i(
		int(crop.get("x", 0)),
		int(crop.get("y", 0)),
		int(crop.get("width", 0)),
		int(crop.get("height", 0))
	)
	var inside: Rect2i = wanted.intersection(Rect2i(Vector2i.ZERO, image.get_size()))
	if not inside.has_area():
		return "the crop %s lies outside the %s screenshot" % [wanted, image.get_size()]
	return image.get_region(inside)


## Writes image scaled to max_width as a PNG at path, adding previewPath, previewWidth and
## previewHeight to result. Returns OK writing nothing and leaving result alone when max_width is
## zero or less, or image is no wider than it.
func _save_preview(image: Image, path: String, max_width: int, result: Dictionary) -> Error:
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
func _wait_for_drawn_frame() -> void:
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
		"rect": _json.to_json(rect),
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
## execute ends the call with null; the error itself reaches the server through the logger,
## flushed before the reply.
func _handle_run_script(id: int, params: Dictionary) -> void:
	var script := GDScript.new()
	script.source_code = str(params.get("source", ""))
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
	_reply_ok(id, {"value": _json.to_json(value)})


## Runs a frame or wait_for request on the clock child, which answers {result} or {error}.
func _handle_time(id: int, params: Dictionary, command: String) -> void:
	var outcome: Dictionary
	if command == "frame":
		outcome = await _time.frame_control(params)
	else:
		outcome = await _time.wait_for(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## Runs a compare_screenshot request on the Baseline child, which answers a Dictionary or a
## String saying why it could not.
func _handle_compare(id: int, params: Dictionary) -> void:
	var outcome: Variant = await _baseline.compare_screenshot(params)
	if outcome is String:
		_reply_error(id, outcome)
		return
	_reply_ok(id, outcome)


## Runs an input request on the Gestures child (godot_mcp_input.gd), which plays it and
## answers {result} or {error}.
func _handle_input(id: int, params: Dictionary) -> void:
	var outcome: Dictionary = await _gestures.play(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## An absolute path (/root/Main/Button), a path under the root (Main/Button), or else the
## first node of that name, breadth first from the root.
func _find_node(element: String) -> Node:
	var root: Window = get_tree().root
	if element.contains("/"):
		return root.get_node_or_null(NodePath(element))
	var queue: Array[Node] = [root]
	while not queue.is_empty():
		var node: Node = queue.pop_front()
		if str(node.name) == element:
			return node
		queue.append_array(node.get_children())
	return null


func _free_unless_counted(instance: Variant) -> void:
	if instance is Object and not instance is RefCounted and is_instance_valid(instance):
		(instance as Object).free()


func _reply_ok(id: int, result: Variant) -> void:
	_flush_errors()
	_send({"id": id, "ok": true, "result": result})


func _reply_error(id: int, message: String) -> void:
	_flush_errors()
	_send({"id": id, "ok": false, "error": message})


func _send(message: Dictionary) -> void:
	var payload: PackedByteArray = JSON.stringify(message).to_utf8_buffer()
	_stream.put_u32(payload.size())
	_stream.put_data(payload)
