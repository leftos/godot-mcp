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
	match command:
		"ping":
			_reply_ok(id, {"pong": true})
		"shutdown":
			_reply_ok(id, {})
			await get_tree().process_frame
			get_tree().quit()
		_:
			_reply_error(id, "unknown command '%s'" % command)


func _reply_ok(id: int, result: Variant) -> void:
	_send({"id": id, "ok": true, "result": result})


func _reply_error(id: int, message: String) -> void:
	_send({"id": id, "ok": false, "error": message})


func _send(message: Dictionary) -> void:
	var payload: PackedByteArray = JSON.stringify(message).to_utf8_buffer()
	_stream.put_u32(payload.size())
	_stream.put_data(payload)
