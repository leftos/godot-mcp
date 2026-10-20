extends Control
## The InputProbe's networked probe: two games on one folder, one listening and one joining over loopback.
## With --net-host=<port> it listens on 127.0.0.1 and sends "press:<n>" to its peer each time Send is
## pressed; with --net-join=<port> it dials until connected, prints "[net] joined", and prints and
## shows every count it receives. On the guest, Flash shows "press <n>" for one frame as each count
## arrives: shown in the frame the count is read, hidden and cleared in the next frame's _process.
## With --net-fault beside --net-host, each Send sends "fault:<n>" instead: the guest raises it with
## push_error, shows the count, and a frame later sends "ack:<n>" back, which the host keeps.

## Whether this game listens for the other, set by --net-host=<port>.
var is_host: bool = false

## Whether this game has a peer: taken on the host, dialled on the guest.
var connected: bool = false

## How many times Send was pressed, the count the host sends.
var press_count: int = 0

## The last count the guest received.
var shown_count: int = 0

## Whether each Send sends a fault for the guest to raise, set by --net-fault on the host.
var sends_faults: bool = false

## The last fault count the guest acknowledged, on the host.
var acked_count: int = 0

var _server: TCPServer
var _peer: StreamPeerTCP
var _join_port: int = 0
var _buffer: String = ""
var _redial_frame: int = 0
var _flash_frame: int = 0
var _ack_count: int = 0
var _ack_frame: int = -1

@onready var _send: Button = $Send
@onready var _mirror: Label = $Mirror
@onready var _flash: Label = $Flash


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
		elif arg == "--net-fault":
			sends_faults = true
		elif arg.begins_with("--net-join="):
			_join_port = int(arg.get_slice("=", 1))
			_dial()


func _dial() -> void:
	_peer = StreamPeerTCP.new()
	_peer.connect_to_host("127.0.0.1", _join_port)


func _on_send() -> void:
	press_count += 1
	if _peer != null and connected:
		var kind: String = "fault" if sends_faults else "press"
		_peer.put_data(("%s:%d\n" % [kind, press_count]).to_utf8_buffer())


func _process(_delta: float) -> void:
	if _flash.visible and Engine.get_process_frames() > _flash_frame:
		_flash.visible = false
		_flash.text = ""
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
		for line: String in _read_lines():
			if line.begins_with("ack:"):
				acked_count = int(line.get_slice(":", 1))


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
	_send_ack()
	for line: String in _read_lines():
		_guest_line(line)


func _guest_line(line: String) -> void:
	if line.begins_with("press:"):
		_show(int(line.get_slice(":", 1)))
	elif line.begins_with("fault:"):
		var n: int = int(line.get_slice(":", 1))
		push_error("net_probe: fault %d" % n)
		_show(n)
		_ack_count = n
		_ack_frame = Engine.get_process_frames()


## Acknowledges a fault a frame after it was raised: the bridge, an autoload processed before
## this scene, has by then written the error to its socket, before this writes the ack to the
## host's.
func _send_ack() -> void:
	if _ack_frame >= 0 and Engine.get_process_frames() > _ack_frame:
		_peer.put_data(("ack:%d\n" % _ack_count).to_utf8_buffer())
		_ack_frame = -1


func _show(n: int) -> void:
	shown_count = n
	_mirror.text = "presses: %d" % n
	_flash.text = "press %d" % n
	_flash.visible = true
	_flash_frame = Engine.get_process_frames()
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
