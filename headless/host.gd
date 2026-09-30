extends SceneTree
## godot-mcp's warm headless host, which the server runs as
## godot --headless --path <project> --script <this file>
## with GODOT_MCP_HOST_PORT and GODOT_MCP_HOST_TOKEN set.
##
## _initialize frees the project's autoloads as operations.gd does, notes the error log's start
## slice and dials the server on 127.0.0.1:<port>; once connected it says hello, {type: "hello",
## token, projectPath, pid, host: "headless"}, and then answers requests on the bridge's wire
## (a 4-byte big-endian length, then UTF-8 JSON), one at a time on the main thread:
## headless {op, params} runs operations.gd's run_request and answers {id, ok: true, result}, the
## result being what a cold run writes to its result file; ping answers {id, ok: true}; shutdown
## answers {id, ok: true} and quits. Before each headless request it prints
## "[godot-mcp] request <id> <op>", so a failure can quote the log from its own request on. The
## host quits once the connection is gone, or when it cannot dial.

const OperationsScript := preload("operations.gd")
const SceneEdit := preload("scene_edit.gd")
const HOST := "127.0.0.1"
const HEADER_BYTES := 4
const MAX_FRAME_BYTES := 16 * 1024 * 1024
const PORT_VARIABLE := "GODOT_MCP_HOST_PORT"
const TOKEN_VARIABLE := "GODOT_MCP_HOST_TOKEN"

var _log := OperationsScript.ErrorLog.new()
var _start: int = 0
var _stream: StreamPeerTCP
var _buffer: PackedByteArray = PackedByteArray()
var _token: String = ""
var _hello_sent := false
var _quitting := false


## The logger goes in first, before Godot creates the project's autoloads, as in operations.gd.
func _init() -> void:
	OS.add_logger(_log)
	SceneEdit.engine_log = _log


func _initialize() -> void:
	for autoload in root.get_children():
		autoload.free()
	_start = _log.count()
	_token = OS.get_environment(TOKEN_VARIABLE)
	var port: int = int(OS.get_environment(PORT_VARIABLE))
	_stream = StreamPeerTCP.new()
	_stream.big_endian = true
	if _token.is_empty() or port <= 0 or _stream.connect_to_host(HOST, port) != OK:
		var names: String = "%s and %s" % [PORT_VARIABLE, TOKEN_VARIABLE]
		printerr("godot-mcp host: cannot dial %s:%d; the server sets %s" % [HOST, port, names])
		_stream = null


## Returns false while the connection is being made or lives, true (quit) once it is gone.
func _process(_delta: float) -> bool:
	if _stream == null or _quitting:
		return true
	_stream.poll()
	var status: StreamPeerTCP.Status = _stream.get_status()
	if status == StreamPeerTCP.STATUS_CONNECTING:
		return false
	if status != StreamPeerTCP.STATUS_CONNECTED:
		return true
	if not _hello_sent:
		_hello_sent = true
		_stream.set_no_delay(true)
		_send(
			{
				"type": "hello",
				"token": _token,
				"projectPath": ProjectSettings.globalize_path("res://"),
				"pid": OS.get_process_id(),
				"host": "headless",
			}
		)
	_serve()
	return _quitting


## Takes every whole frame off the front of buffer: {frames, rest, error}, frames the payloads
## as text in order, rest the bytes of a frame still incomplete, and error, when not empty, why
## reading stopped: a frame over MAX_FRAME_BYTES, after which nothing more is read.
static func take_frames(buffer: PackedByteArray) -> Dictionary:
	var frames: Array = []
	var rest: PackedByteArray = buffer
	while rest.size() >= HEADER_BYTES:
		var length: int = (rest[0] << 24) | (rest[1] << 16) | (rest[2] << 8) | rest[3]
		if length > MAX_FRAME_BYTES:
			var error: String = "a frame of %d bytes is over the limit" % length
			return {"frames": frames, "rest": PackedByteArray(), "error": error}
		if rest.size() < HEADER_BYTES + length:
			break
		frames.append(rest.slice(HEADER_BYTES, HEADER_BYTES + length).get_string_from_utf8())
		rest = rest.slice(HEADER_BYTES + length)
	return {"frames": frames, "rest": rest, "error": ""}


## The answer to one frame: {reply, quit}, reply the frame to send back (empty for a frame that
## is not a request) and quit whether the host quits after sending it. The id is echoed as an int,
## since GDScript's JSON reads every number as a float.
static func answer(text: String, log: OperationsScript.ErrorLog, start: int) -> Dictionary:
	var request: Variant = JSON.parse_string(text)
	if not request is Dictionary or not request.has("id"):
		push_warning("godot-mcp host: dropped a frame that is not a request: %s" % text.left(200))
		return {"reply": {}, "quit": false}
	var id: int = int(request["id"])
	var command: String = str(request.get("command", ""))
	var params: Dictionary = request["params"] if request.get("params") is Dictionary else {}
	match command:
		"headless":
			return {"reply": _headless(id, params, log, start), "quit": false}
		"ping":
			return {"reply": {"id": id, "ok": true}, "quit": false}
		"shutdown":
			return {"reply": {"id": id, "ok": true}, "quit": true}
	var refused: Dictionary = {"id": id, "ok": false, "error": "unknown command '%s'" % command}
	return {"reply": refused, "quit": false}


## Runs one headless request, {op, params}, marking its start in the log.
static func _headless(
	id: int, params: Dictionary, log: OperationsScript.ErrorLog, start: int
) -> Dictionary:
	var op: String = str(params.get("op", ""))
	print("[godot-mcp] request %d %s" % [id, op])
	var op_params: Dictionary = params["params"] if params.get("params") is Dictionary else {}
	return {"id": id, "ok": true, "result": OperationsScript.run_request(op, op_params, log, start)}


## Reads what has arrived and answers every whole frame; a frame over the limit closes the host.
func _serve() -> void:
	var available: int = _stream.get_available_bytes()
	if available > 0:
		var chunk: Array = _stream.get_partial_data(available)
		if chunk[0] == OK:
			_buffer.append_array(chunk[1])
	var taken: Dictionary = take_frames(_buffer)
	_buffer = taken["rest"]
	for text: String in taken["frames"]:
		var answered: Dictionary = answer(text, _log, _start)
		if not (answered["reply"] as Dictionary).is_empty():
			_send(answered["reply"])
		if answered["quit"]:
			_quitting = true
			return
	if not (taken["error"] as String).is_empty():
		push_error("godot-mcp host: %s; closing." % taken["error"])
		_stream.disconnect_from_host()
		_quitting = true


## Writes one frame; keys stay in the order they were set, as the cold run's result file keeps them.
func _send(message: Dictionary) -> void:
	var payload: PackedByteArray = JSON.stringify(message, "", false).to_utf8_buffer()
	_stream.put_u32(payload.size())
	_stream.put_data(payload)
