extends Control
## The InputProbe's networked probe: two games on one folder, one listening and one joining over loopback.
## With --net-host=<port> it listens on 127.0.0.1 and sends "press:<n>" to its peer each time Send is
## pressed; with --net-join=<port> it dials until connected, prints "[net] joined", and prints and
## shows every count it receives.

## Whether this game listens for the other, set by --net-host=<port>.
var is_host: bool = false

## Whether this game has a peer: taken on the host, dialled on the guest.
var connected: bool = false

## How many times Send was pressed, the count the host sends.
var press_count: int = 0

## The last count the guest received.
var shown_count: int = 0

var _server: TCPServer
var _peer: StreamPeerTCP
var _join_port: int = 0
var _buffer: String = ""
var _redial_frame: int = 0

@onready var _send: Button = $Send
@onready var _mirror: Label = $Mirror


func _ready() -> void:
	_send.pressed.connect(_on_send)
	for arg: String in OS.get_cmdline_user_args():
		if arg.begins_with("--net-host="):
			is_host = true
			_server = TCPServer.new()
			var port: int = int(arg.get_slice("=", 1))
			var err: Error = _server.listen(port, "127.0.0.1")
			if err != OK:
				push_error("net_probe: could not listen on 127.0.0.1:%d (error %d)" % [port, err])
		elif arg.begins_with("--net-join="):
			_join_port = int(arg.get_slice("=", 1))
			_dial()


func _dial() -> void:
	_peer = StreamPeerTCP.new()
	_peer.connect_to_host("127.0.0.1", _join_port)


func _on_send() -> void:
	press_count += 1
	if _peer != null and connected:
		_peer.put_data(("press:%d\n" % press_count).to_utf8_buffer())


func _process(_delta: float) -> void:
	if is_host:
		_host_poll()
	elif _peer != null:
		_guest_poll()


func _host_poll() -> void:
	if _server.is_connection_available():
		_peer = _server.take_connection()
		_peer.set_no_delay(true)
		connected = true
	if _peer != null:
		_peer.poll()


func _guest_poll() -> void:
	_peer.poll()
	var status: StreamPeerTCP.Status = _peer.get_status()
	if not connected:
		if status == StreamPeerTCP.STATUS_CONNECTED:
			connected = true
			_peer.set_no_delay(true)
			print("[net] joined")
		elif (
			status != StreamPeerTCP.STATUS_CONNECTING
			and Engine.get_process_frames() >= _redial_frame
		):
			_redial_frame = Engine.get_process_frames() + 15
			_dial()
		return
	for line: String in _read_lines():
		if line.begins_with("press:"):
			_show(int(line.get_slice(":", 1)))


func _show(n: int) -> void:
	shown_count = n
	_mirror.text = "presses: %d" % n
	print("presses: %d" % n)


func _read_lines() -> PackedStringArray:
	var available: int = _peer.get_available_bytes()
	if available > 0:
		var chunk: Array = _peer.get_partial_data(available)
		if chunk[0] == OK:
			_buffer += (chunk[1] as PackedByteArray).get_string_from_utf8()
	var lines: PackedStringArray = []
	while _buffer.contains("\n"):
		lines.append(_buffer.get_slice("\n", 0))
		_buffer = _buffer.substr(_buffer.find("\n") + 1)
	return lines
