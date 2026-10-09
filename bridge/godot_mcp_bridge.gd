extends Node
## The godot-mcp bridge: an autoload injected into a game run through override.cfg.
##
## It dials the server at 127.0.0.1:GODOT_MCP_PORT, says hello with GODOT_MCP_TOKEN, the
## project path and its own process id, then answers the server's requests. A game run_project
## did not launch finds the port and token in the attach file attach_project writes instead;
## with neither, a game with a window in an armed folder waits dormant for a join
## (godot_mcp_dormant.gd), and any other game's bridge stays off. Frames are a 4-byte
## big-endian length followed by UTF-8 JSON. Requests are {id, command, params}; replies are
## {id, ok: true, result} or {id, ok: false, error}. The errors and warnings the game logs
## (godot_mcp_logger.gd) go out as {type: "errors", entries, dropped} frames without an id,
## each frame, and before every reply, so a command's errors reach the server before its reply.

const HOST := "127.0.0.1"
const HEADER_BYTES := 4
const MAX_FRAME_BYTES := 16 * 1024 * 1024
const GAMEPAD_SCRIPT := "godot_mcp_gamepad.gd"
const INPUT_SCRIPT := "godot_mcp_input.gd"
const RAW_EVENTS_SCRIPT := "godot_mcp_raw_events.gd"
const INSPECT_SCRIPT := "godot_mcp_inspect.gd"
const TIME_SCRIPT := "godot_mcp_time.gd"
const CONDITIONS_SCRIPT := "godot_mcp_conditions.gd"
const BASELINE_SCRIPT := "godot_mcp_baseline.gd"
const FRAME_SCRIPT := "godot_mcp_frame.gd"
const LOGGER_SCRIPT := "godot_mcp_logger.gd"
const JSON_SCRIPT := "godot_mcp_json.gd"
const PREVIEW_SCRIPT := "godot_mcp_preview.gd"
const UI_SNAPSHOT_SCRIPT := "godot_mcp_ui_snapshot.gd"
const SHOWN_TEXT_SCRIPT := "godot_mcp_shown_text.gd"
const ITEM_TARGETS_SCRIPT := "godot_mcp_item_targets.gd"
const CLASS_INFO_SCRIPT := "godot_mcp_class_info.gd"
const CAPTURE_SCRIPT := "godot_mcp_capture.gd"
const DOTNET_SCRIPT := "godot_mcp_dotnet.gd"
const STATE_SCRIPT := "godot_mcp_state.gd"
const DORMANT_SCRIPT := "godot_mcp_dormant.gd"
const WINDOW_SCRIPT := "godot_mcp_window.gd"
const AUDIO_SCRIPT := "godot_mcp_audio.gd"
const WATCH_SCRIPT := "godot_mcp_watch.gd"
## The commands a cancel request can end early, answering the request at once for a run_script it
## stops and a call_method it stops awaiting (_cancel); the server cancels one when its
## load-adjusted allowance passes before the request's backstopMs.
const CANCELLABLE: PackedStringArray = [
	"frame", "wait_for", "frames", "dotnet", "run_script", "call_method", "watch"
]
## A stopped run_script's answer, the restored clause (_restore) filled in.
const SCRIPT_STOPPED := (
	"stopped: its coroutine will not resume%s. A coroutine it awaited on another "
	+ "object, such as a node's own method, keeps running; restart_project stops everything."
)
## A cancelled call_method's answer, the restored clause (_restore) filled in.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const CALL_FORGOTTEN := "no longer awaited: the method keeps running on its node%s; restart_project stops it."
## The device id every injected mouse event carries, so _input can tell it from the real mouse
## (DEVICE_ID_MOUSE, 32) and from the engine's own ids: 0-15 joypads, 16-31 keyboards, -1
## emulation, -2 internal (core/input/input_event.h L64-67 in 4.7.2).
const INJECTED_DEVICE := 0x6D6370

var _stream: StreamPeerTCP
var _token: String = ""
var _buffer: PackedByteArray = PackedByteArray()
var _hello_sent: bool = false
## Whether the server accepted the hello (its welcome command); only a welcomed run quits on a loss.
var _welcomed: bool = false
var _connection_lost: bool = false
## The mouse buttons the injected input holds down, as a MouseButtonMask.
var _held_mask: int = 0
## Where the injected pointer last was, in window coordinates.
var _pointer: Vector2 = Vector2.ZERO
## Whether the hover belongs to the injected pointer: set by every injected mouse event, cleared by
## a real mouse motion while no injected input is in play (the cursor is the user's again) and when
## the connection ends.
var _owns_pointer: bool = false
## Whether an input gesture is playing, including the frames that settle it.
var _gesture_playing: bool = false
## Whether dispatch is delivering an injected event, so the touch twins Input makes of it pass.
var _dispatching: bool = false
## The gamepad (godot_mcp_gamepad.gd beside this script), a child once the bridge is on.
var _pads: Node
## The input player (godot_mcp_input.gd beside this script): the gestures.
var _gestures: Node
## The raw event player (godot_mcp_raw_events.gd beside this script): simulate_input's events,
## played through the input player's senders.
var _raw_events: Node
## The input capture (godot_mcp_capture.gd beside this script): capture_input's recording, which
## the input player feeds every event it dispatches.
var _capture: Node
## The C# helper module (godot_mcp_dotnet.gd beside this script): the dotnet command, which loads
## the helper extension once per process and passes it requests.
var _dotnet: Node
## The state reader (godot_mcp_state.gd beside this script): the state command, the marked nodes'
## own state read in one frame.
var _state: Node
## The inspector (godot_mcp_inspect.gd beside this script), a child once the bridge is on.
var _inspect: Node
## The conditions (godot_mcp_conditions.gd beside this script): the checks a wait polls and the
## condition Expression's parse.
var _conditions: Node
## The clock (godot_mcp_time.gd beside this script): pause, step, time scale and waits.
var _time: Node
## The watch (godot_mcp_watch.gd beside this script): the watch command's tracks, sampled each
## frame beside every other command.
var _watch: Node
## The screenshot comparison (godot_mcp_baseline.gd beside this script).
var _baseline: Node
## The scene preview (godot_mcp_preview.gd beside this script): preview_scene's framing and capture.
var _preview: Node
## The frame capture (godot_mcp_frame.gd beside this script): the root viewport's image with
## every native window pasted on it, and the PNGs a screenshot, a baseline and a preview save.
var _frame: Node
## The JSON conversion (godot_mcp_json.gd beside this script), static functions called on the
## script itself, by this script and by the Inspect and Time modules.
var _json: GDScript
## The UI snapshot (godot_mcp_ui_snapshot.gd beside this script), static functions called on the
## script itself by the input module, which owns wait_for {uiChanged}'s baseline.
var _ui_snapshot: GDScript
## The shown text (godot_mcp_shown_text.gd beside this script), static functions called on the
## script itself: the text get_ui_elements reports, the one a text target matches.
var _shown_text: GDScript
## The item targets (godot_mcp_item_targets.gd beside this script), static functions called on the
## script itself: the items get_ui_elements reports, read as an item target matches them.
var _item_targets: GDScript
## The class reader (godot_mcp_class_info.gd beside this script), static functions called on the
## script itself: describe_class.
var _class_info: GDScript
## Every command's handler, func(id, params), by command name (_command_handlers).
var _handlers: Dictionary = {}
## The cancellable requests still running: id -> the params Dictionary their handler holds, from
## the frame that read them until their reply.
var _running_requests: Dictionary = {}
## The run_script calls whose execute is suspended at an await: id -> {instance, state, before},
## the script's instance, the GDScriptFunctionState execute returned and the _snapshot from the
## call's start. They are the bridge's only references to the instance and its coroutine, so
## dropping an entry lets the coroutine go (_drop_script).
var _running_scripts: Dictionary = {}
## The call_method calls still awaiting their method: id -> the _snapshot from the call's start.
var _running_calls: Dictionary = {}
## The server to dial, found in _init; empty when the bridge is off.
var _endpoint: Dictionary = {}
## The logger (godot_mcp_logger.gd beside this script) collecting the game's errors, registered
## in _init when there is a server to send them to, else at a dormant game's first join. It is
## never removed: the engine removes script loggers at shutdown, and remove_logger is unsafe while
## other threads log.
var _logger: Logger
## The dormant mode's script (godot_mcp_dormant.gd beside this script), static functions.
var _dormant_script: GDScript
## The main window's script (godot_mcp_window.gd beside this script), static functions.
var _window: GDScript
## The globalised folder of the attach, armed, dormant and join files.
var _state_dir: String = ""
## The folder of this script and the modules beside it.
var _script_dir: String = ""
## The dormant waiter, a child made the first time the bridge goes dormant.
var _dormant: Node
## The mute (godot_mcp_audio.gd beside this script), a child made in _init.
var _audio: Node
## How the bridge runs, one of the dormant script's MODE_* values, decided in _init.
var _mode: String = ""
## Where _endpoint came from, one of the dormant script's SOURCE_* values; empty with no endpoint.
var _endpoint_source: String = ""
## When the game started, in ms since the Unix epoch, taken in _init.
var _started_unix_ms: int = 0


## Finds the server, none when the server switched the bridge off (OFF_VARIABLE), and registers
## the error logger as early as an autoload can: a logger sees only what is logged after
## OS.add_logger. With no server but an armed folder, the bridge is dormant and registers none.
func _init() -> void:
	_started_unix_ms = int(Time.get_unix_time_from_system() * 1000)
	# A subclass compiled from source (the unit tests') has no path; this script, its base, has.
	var script: Script = get_script()
	while script.resource_path.is_empty() and script.get_base_script() != null:
		script = script.get_base_script()
	_script_dir = script.resource_path.get_base_dir()
	_dormant_script = load(_script_dir.path_join(DORMANT_SCRIPT)) as GDScript
	_window = load(_script_dir.path_join(WINDOW_SCRIPT)) as GDScript
	_audio = (load(_script_dir.path_join(AUDIO_SCRIPT)) as GDScript).new()
	add_child(_audio)
	_state_dir = ProjectSettings.globalize_path(_dormant_script.STATE_DIR)
	var chosen: Dictionary = _dormant_script.choose(_state_dir)
	_mode = chosen["mode"]
	_endpoint = chosen["endpoint"]
	_endpoint_source = chosen["source"]
	if not _endpoint.is_empty():
		_add_logger()


## Registers the error logger, once; the engine removes it at shutdown.
func _add_logger() -> void:
	if _logger != null:
		return
	_logger = (load(_script_dir.path_join(LOGGER_SCRIPT)) as GDScript).new()
	OS.add_logger(_logger)


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	if _mode == _dormant_script.MODE_DORMANT:
		if _dormant_script.armed_quiet(_state_dir):
			_window.park_window()
		_go_dormant()
		return
	if _endpoint.is_empty():
		if _dormant_script.frees_silently(_state_dir):
			queue_free()
			return
		push_warning(
			(
				"godot-mcp bridge: GODOT_MCP_PORT and GODOT_MCP_TOKEN are not set and there is no "
				+ "attach file; the bridge is off. It was loaded by an override.cfg a godot-mcp "
				+ "server wrote, most likely one a stopped server left behind; delete override.cfg "
				+ "if no godot-mcp session uses this project."
			)
		)
		_window.restore_parked_window()
		queue_free()
		return
	# A preview's scene enters after this autoload's _ready, so it never runs a frame unpaused.
	if OS.get_environment("GODOT_MCP_PREVIEW") == "1":
		get_tree().paused = true
	_join(_endpoint, _endpoint_source)


## Makes the children, the static modules and the command handlers, the first time only.
func _build_once() -> void:
	if _pads != null:
		return
	_json = load(_script_dir.path_join(JSON_SCRIPT)) as GDScript
	_ui_snapshot = load(_script_dir.path_join(UI_SNAPSHOT_SCRIPT)) as GDScript
	_shown_text = load(_script_dir.path_join(SHOWN_TEXT_SCRIPT)) as GDScript
	_item_targets = load(_script_dir.path_join(ITEM_TARGETS_SCRIPT)) as GDScript
	_class_info = load(_script_dir.path_join(CLASS_INFO_SCRIPT)) as GDScript
	_pads = (load(_script_dir.path_join(GAMEPAD_SCRIPT)) as GDScript).new()
	_pads.name = "Gamepad"
	_pads.bridge = self
	add_child(_pads)
	_gestures = (load(_script_dir.path_join(INPUT_SCRIPT)) as GDScript).new()
	_gestures.name = "Gestures"
	_gestures.bridge = self
	add_child(_gestures)
	_raw_events = (load(_script_dir.path_join(RAW_EVENTS_SCRIPT)) as GDScript).new()
	_raw_events.name = "RawEvents"
	_raw_events.bridge = self
	add_child(_raw_events)
	_capture = (load(_script_dir.path_join(CAPTURE_SCRIPT)) as GDScript).new()
	_capture.name = "Capture"
	_capture.bridge = self
	_capture.send_frame = _send_captured
	add_child(_capture)
	_dotnet = (load(_script_dir.path_join(DOTNET_SCRIPT)) as GDScript).new()
	_dotnet.name = "Dotnet"
	_dotnet.bridge = self
	add_child(_dotnet)
	_state = (load(_script_dir.path_join(STATE_SCRIPT)) as GDScript).new()
	_state.name = "State"
	_state.bridge = self
	add_child(_state)
	_inspect = (load(_script_dir.path_join(INSPECT_SCRIPT)) as GDScript).new()
	_inspect.name = "Inspect"
	add_child(_inspect)
	_conditions = (load(_script_dir.path_join(CONDITIONS_SCRIPT)) as GDScript).new()
	_conditions.name = "Conditions"
	_conditions.bridge = self
	add_child(_conditions)
	_time = (load(_script_dir.path_join(TIME_SCRIPT)) as GDScript).new()
	_time.name = "Time"
	_time.bridge = self
	add_child(_time)
	_watch = (load(_script_dir.path_join(WATCH_SCRIPT)) as GDScript).new()
	_watch.name = "Watch"
	_watch.bridge = self
	add_child(_watch)
	_baseline = (load(_script_dir.path_join(BASELINE_SCRIPT)) as GDScript).new()
	_baseline.name = "Baseline"
	_baseline.bridge = self
	add_child(_baseline)
	_preview = (load(_script_dir.path_join(PREVIEW_SCRIPT)) as GDScript).new()
	_preview.name = "Preview"
	_preview.bridge = self
	add_child(_preview)
	_frame = (load(_script_dir.path_join(FRAME_SCRIPT)) as GDScript).new()
	_frame.name = "Frame"
	add_child(_frame)
	_handlers = _command_handlers()


## Serves endpoint, from source (a SOURCE_*; a join drops the errors logged before it): builds the
## children once, sizes the window, parks it and caps the frame rate when quiet, mutes as asked,
## shuts the real pads out if asked, and dials; a dial refused at once when armed waits dormant.
func _join(endpoint: Dictionary, source: String) -> void:
	_build_once()
	_endpoint = endpoint
	_endpoint_source = source
	_token = endpoint["token"]
	_add_logger()
	if source == _dormant_script.SOURCE_JOIN:
		_logger.take_pending()
	_window.apply_window_size(get_tree().root)
	if endpoint["quiet"]:
		_window.park_window()
		if Engine.max_fps == 0:
			Engine.max_fps = _window.QUIET_MAX_FPS
	_audio.set_muted(endpoint["mute"])
	if endpoint["shutOutRealGamepads"]:
		_pads.shut_out_real_pads()
	var port: int = endpoint["port"]
	_stream = StreamPeerTCP.new()
	_stream.big_endian = true
	var error: Error = _stream.connect_to_host(HOST, port)
	if error != OK:
		push_error("godot-mcp bridge: cannot dial %s:%d (error %d)." % [HOST, port, error])
		_stream = null
		_dormant_if_armed()


## Waits for attach_project to join, muted as the arm asks, polling for a join file even paused.
func _go_dormant() -> void:
	if _dormant == null:
		_dormant = _dormant_script.new()
		_dormant.name = "Dormant"
		_dormant.state_dir = _state_dir
		_dormant.started_unix_ms = _started_unix_ms
		_dormant.joined.connect(_join.bind(_dormant_script.SOURCE_JOIN))
		_dormant.disarmed.connect(_on_disarmed)
		add_child(_dormant)
	_audio.set_muted(_dormant_script.armed_mute(_state_dir))
	_dormant.enter()


## The folder was disarmed while the game waited: the bridge goes, as with no server.
func _on_disarmed() -> void:
	_window.restore_window()
	_audio.set_muted(false)
	queue_free()


## Goes dormant again when the endpoint came from attach.json or a join file and the folder is
## still armed; a run's game, a headless one, or one in a disarmed folder stays idle, unmuted.
func _dormant_if_armed() -> void:
	var armed: bool = FileAccess.file_exists(_dormant_script.armed_path(_state_dir))
	var headless: bool = _dormant_script.is_headless()
	if _dormant_script.goes_dormant_again(_endpoint_source, armed, headless):
		_go_dormant_again()
	else:
		_watch.drop()
		_audio.set_muted(false)


## Forgets the connection that ended and a capture and a watch it ran, lets go of the injected
## input still held, keeps the window parked when the arm is quiet or gives back one parked, and
## waits.
func _go_dormant_again() -> void:
	_capture.stop()
	_watch.drop()
	_raw_events.release_all()
	# After release_all, whose button releases take the pointer again.
	_owns_pointer = false
	_stream = null
	_buffer = PackedByteArray()
	_hello_sent = false
	_welcomed = false
	_connection_lost = false
	_token = ""
	_endpoint = {}
	_endpoint_source = ""
	_gesture_playing = false
	if _dormant_script.armed_quiet(_state_dir):
		_window.park_window()
	else:
		_window.restore_window()
	_go_dormant()


func _process(_delta: float) -> void:
	if _stream == null or _connection_lost:
		return
	_stream.poll()
	var status: StreamPeerTCP.Status = _stream.get_status()
	if status == StreamPeerTCP.STATUS_CONNECTING:
		return
	if status != StreamPeerTCP.STATUS_CONNECTED:
		_end_connection()
		push_warning("godot-mcp bridge: the connection to the server is gone (status %d)." % status)
		return
	if not _hello_sent:
		_hello_sent = true
		_stream.set_no_delay(true)
		var window_size: Vector2i = DisplayServer.window_get_size()
		_send(
			{
				"type": "hello",
				"token": _token,
				"projectPath": ProjectSettings.globalize_path("res://"),
				"pid": OS.get_process_id(),
				"window": {"width": window_size.x, "height": window_size.y},
				"hwnd": _window.native_handle(),
			}
		)
	_flush_errors()
	_read_frames()


## Marks the connection gone and cancels each running request before forgetting it: no cancel can
## reach them from the server now, and a wait_for or dotnet call would otherwise poll on
## until its backstopMs for a reply nobody reads. A suspended run_script is dropped as a cancel
## stops it, and a call_method is left to end unanswered, neither restoring the time scale or the
## pause: no server is left to be told. A game the server launched quits a frame later, since the
## bridge never dials again and a server killed outright never asks it to; an attached or joined
## game is the user's and runs on.
func _end_connection() -> void:
	# A refused hello is never welcomed: a child game that inherited the run's token runs on.
	var launched: bool = _endpoint_source == _dormant_script.SOURCE_ENV and _welcomed
	_connection_lost = true
	for request: int in _running_scripts.keys():
		_drop_script(request)
	_running_calls.clear()
	for request: int in _running_requests.keys():
		_cancel(request)
	_running_requests.clear()
	_dormant_if_armed()
	_owns_pointer = false
	if launched:
		var tree: SceneTree = Engine.get_main_loop() as SceneTree
		await tree.process_frame
		_quit(tree)


## Ends the game as its own quit does, running its exit work; the unit tests replace it.
func _quit(tree: SceneTree) -> void:
	tree.quit()


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
## button, a mouse button (a wheel notch included), motion or pan gesture event without the
## injected mark is marked handled here.
## The root viewport runs every _input before its GUI (Viewport::push_input), so the GUI never
## sees it; hover still follows the real pointer, since push_input updates it before _input.
## With emulate_touch_from_mouse on, Input sends each left-button event's touch twin (device
## DEVICE_ID_EMULATION) just before the event itself, the injected ones' included
## (input.cpp L850-861, L876-891 in 4.7.2); a twin that arrives outside dispatch is a real one's.
func _input(event: InputEvent) -> void:
	if not (_gesture_playing or _held_mask != 0):
		return
	if _is_real_pointer_event(event):
		get_viewport().set_input_as_handled()


## Whether event is a mouse button (a wheel notch included) or motion or a pan gesture without the
## injected mark, or a touch twin raised outside dispatch: the real input _input swallows while
## injected input is in play.
func _is_real_pointer_event(event: InputEvent) -> bool:
	if (
		event is InputEventMouseButton
		or event is InputEventMouseMotion
		or event is InputEventPanGesture
	):
		return event.device != INJECTED_DEVICE
	if event is InputEventScreenTouch or event is InputEventScreenDrag:
		return event.device == InputEvent.DEVICE_ID_EMULATION and not _dispatching
	return false


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
			_end_connection()
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
	_track(id, command, params)
	(_handlers[command] as Callable).call(id, params)


## Registers a cancellable request as running until its reply.
func _track(id: int, command: String, params: Dictionary) -> void:
	if command in CANCELLABLE:
		_running_requests[id] = params


## Replies {cancelled} to a cancel of params.request.
func _handle_cancel(id: int, params: Dictionary) -> void:
	_reply_ok(id, {"cancelled": _cancel(int(params.get("request", -1)))})


## Marks the running request's params _cancelled, which ends a wait_for's poll and a dotnet call's
## at their next frame, and ends a running step or capture as its deadline would. A suspended
## run_script is stopped (_stop_script) and a call_method no longer awaited (_forget_call), each
## answered here. Returns false when the request has been answered, was never cancellable, or is
## unknown.
func _cancel(request: int) -> bool:
	if not _running_requests.has(request):
		return false
	var params: Dictionary = _running_requests[request]
	params["_cancelled"] = true
	_time.cancel(params)
	_watch.cancel(params)
	if _running_scripts.has(request):
		_stop_script(request)
	elif _running_calls.has(request):
		_forget_call(request)
	return true


## Stops a suspended run_script and answers it with SCRIPT_STOPPED, once its time scale and pause
## are restored.
func _stop_script(request: int) -> void:
	var before: Dictionary = _drop_script(request)
	_reply_error(request, SCRIPT_STOPPED % _restore(before))


## Drops the bridge's references to a suspended run_script's instance and coroutine, freeing an
## instance that is not RefCounted; returns the _snapshot from its start. The instance's
## destructor clears each coroutine still pending on it, which then never resumes and raises no
## error: an await keeps only a raw pointer to the instance (modules/gdscript/gdscript_vm.cpp,
## OPCODE_AWAIT), and GDScriptInstance::~GDScriptInstance clears the pending states' connections
## and stacks (modules/gdscript/gdscript.cpp in 4.7.2).
func _drop_script(request: int) -> Dictionary:
	var running: Dictionary = _running_scripts[request]
	_running_scripts.erase(request)
	var before: Dictionary = running["before"]
	_free_unless_counted(running["instance"])
	running.clear()
	return before


## Stops awaiting a call_method whose method is still running, which the bridge cannot stop, and
## answers it with CALL_FORGOTTEN once its time scale and pause are restored; its handler sends
## nothing when the method ends later (_handle_inspect).
func _forget_call(request: int) -> void:
	var before: Dictionary = _running_calls[request]
	_running_calls.erase(request)
	_reply_error(request, CALL_FORGOTTEN % _restore(before))


## The game's SceneTree: the main loop, the tree this autoload is in. Read from the engine rather
## than get_tree, which the bridge's unit tests cannot give a tree before the runner's root enters.
func _scene_tree() -> SceneTree:
	return Engine.get_main_loop() as SceneTree


## Engine.time_scale and SceneTree.paused now, {time_scale, paused}, for _restore.
func _snapshot() -> Dictionary:
	return {"time_scale": Engine.time_scale, "paused": _scene_tree().paused}


## Puts Engine.time_scale and SceneTree.paused back to before's values and says which it changed:
## empty when neither, else "; restored " and each changed one, joined by " and ".
func _restore(before: Dictionary) -> String:
	var restored: PackedStringArray = []
	var time_scale: float = before["time_scale"]
	if Engine.time_scale != time_scale:
		Engine.time_scale = time_scale
		restored.append("Engine.time_scale to %s" % time_scale)
	var paused: bool = before["paused"]
	if _scene_tree().paused != paused:
		_scene_tree().paused = paused
		restored.append("SceneTree.paused to %s" % ("true" if paused else "false"))
	if restored.is_empty():
		return ""
	return "; restored " + " and ".join(restored)


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
		"snapshot": _handle_inspect.bind("snapshot"),
		"frame": _handle_time.bind("frame"),
		"wait_for": _handle_time.bind("wait_for"),
		"frames": _handle_time.bind("frames"),
		"compare_screenshot": _handle_compare,
		"preview": _handle_preview,
		"movie_frame": _handle_movie_frame,
		"describe_class": _handle_describe_class,
		"capture": _handle_capture,
		"watch": _handle_watch,
		"dotnet": _handle_dotnet,
		"state": _handle_state,
		"shutdown": _handle_shutdown,
		"cancel": _handle_cancel,
		"welcome": _handle_welcome,
	}


func _handle_ping(id: int, _params: Dictionary) -> void:
	_reply_ok(id, {"pong": true})


## The server accepted this bridge's hello for a run it launched: from here a lost connection
## means the server is gone, and the game quits.
func _handle_welcome(id: int, _params: Dictionary) -> void:
	_welcomed = true
	_reply_ok(id, {})


## Replies with the number of frames Movie Maker has written so far, the index of the next one.
## The writer begins before the first iteration (4.7.2 main.cpp L4855) and adds one frame at
## the end of every iteration, after the process frame count is raised (L5119, L5151), so a
## command handled during an iteration sees exactly the frames written before it.
func _handle_movie_frame(id: int, _params: Dictionary) -> void:
	_reply_ok(id, {"frame": Engine.get_process_frames()})


## Replies with every Control (visible ones only unless visibleOnly is false), each described;
## classFilter keeps Controls of that engine class or a subclass of it.
func _handle_ui_elements(id: int, params: Dictionary) -> void:
	var controls: Array[Control] = []
	_gather_controls(get_tree().root, bool(params.get("visibleOnly", true)), Callable(), controls)
	var class_filter: String = str(params.get("classFilter", ""))
	var elements: Array = []
	for control in controls:
		if class_filter.is_empty() or control.is_class(class_filter):
			elements.append(_describe_control(control))
	_reply_ok(id, {"elements": elements})


## Runs a scene_tree, inspect_node, set_property or call_method request on the Inspect child,
## which answers a Dictionary or a String saying why it could not. A call_method is kept in
## _running_calls while its method runs; one cancelled meanwhile (_forget_call) is not answered
## again when the method ends.
func _handle_inspect(id: int, params: Dictionary, command: String) -> void:
	if command == "call_method":
		_running_calls[id] = _snapshot()
	var result: Variant = await _inspect.handle(command, params)
	_running_calls.erase(id)
	if params.get("_cancelled", false):
		return
	if result is String:
		_reply_error(id, result)
	else:
		_reply_ok(id, result)


## Describes an engine class or a script class of the game (godot_mcp_class_info.gd), or refuses
## a name no class has with the closest class names.
func _handle_describe_class(id: int, params: Dictionary) -> void:
	var described: Dictionary = _class_info.describe(params)
	if described.has("error"):
		_reply_error(id, described["error"])
	else:
		_reply_ok(id, described["result"])


## Starts or stops capture_input's capture on the Capture child; a stop's last captured frame goes
## out before the reply.
func _handle_capture(id: int, params: Dictionary) -> void:
	var outcome: Dictionary = _capture.handle(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## Runs a watch request (start, run or stop) on the Watch child, which answers {result} or {error}.
func _handle_watch(id: int, params: Dictionary) -> void:
	var outcome: Dictionary = await _watch.handle(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## Passes a helper request to the C# helper on the Dotnet child, loading it on the first call;
## replies {reply, loadedNow} or the reason the helper could not be reached.
func _handle_dotnet(id: int, params: Dictionary) -> void:
	var outcome: Dictionary = await _dotnet.handle(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## Reads the state of the nodes in the mcp_state group on the State child, all in this frame;
## replies {frame, nodes, total, omitted?} or why params.node cannot be read from.
func _handle_state(id: int, params: Dictionary) -> void:
	var outcome: Dictionary = _state.handle(params)
	if outcome.has("error"):
		_reply_error(id, str(outcome["error"]))
		return
	_reply_ok(id, outcome["result"])


## Sends a {type: "captured", events, truncated?} frame from the Capture child, once the hello is
## out and while the connection holds.
func _send_captured(frame: Dictionary) -> void:
	if _stream == null or not _hello_sent or _connection_lost:
		return
	_send(frame)


## Replies, then quits once the reply has had a frame to go out. The captured input not yet sent
## goes out first, so a capture keeps what the game recorded before it quit.
func _handle_shutdown(id: int, _params: Dictionary) -> void:
	_capture.flush()
	_reply_ok(id, {})
	await get_tree().process_frame
	get_tree().quit()


## Saves the next drawn frame of the root viewport as the frame module's save_screenshot does;
## on a headless game, replies the frame module's refusal before waiting for any frame.
func _handle_screenshot(id: int, params: Dictionary) -> void:
	var refusal: String = _frame.headless_refusal()
	if not refusal.is_empty():
		_reply_error(id, refusal)
		return
	await _frame.wait_for_drawn_frame()
	var saved: Variant = _frame.save_screenshot(_frame.grab_frame(), params)
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


## Appends every Control under node, depth first: the Controls get_ui_elements lists, and the UI
## snapshot wait_for {uiChanged} compares. An invisible Control hides its subtree when
## visible_only is set, and so does any node skip (when valid) returns true for.
func _gather_controls(node: Node, visible_only: bool, skip: Callable, into: Array[Control]) -> void:
	if skip.is_valid() and skip.call(node):
		return
	var control := node as Control
	if control != null:
		if visible_only and not control.is_visible_in_tree():
			return
		into.append(control)
	for child in node.get_children():
		_gather_controls(child, visible_only, skip, into)


func _describe_control(control: Control) -> Dictionary:
	var rect: Rect2 = control.get_global_rect()
	var element: Dictionary = {
		"path": str(control.get_path()),
		"name": str(control.name),
		"class": control.get_class(),
		"rect": _json.to_json(rect),
		"visible": control.is_visible_in_tree(),
	}
	var shown: Variant = _shown_text.shown_text(control)
	if shown != null:
		element["text"] = shown
	var listed: Variant = _item_targets.listed_items(control)
	if listed != null:
		element.merge(listed)
	if control is BaseButton:
		element["disabled"] = (control as BaseButton).disabled
	if not control.tooltip_text.is_empty():
		element["tooltip"] = control.tooltip_text
	return element


## Compiles source, runs its execute(scene_tree) and replies {value}. An execute that returns at
## once is answered now; one suspended at an await is kept in _running_scripts and answered when
## it completes (_on_script_completed), unless a cancel stops it first (_stop_script). It is never
## awaited here: a coroutine of the bridge's own suspended on it would hold the instance, which no
## cancel could then drop. A runtime error inside execute ends the call with null; the error itself
## reaches the server through the logger, flushed before the reply.
func _handle_run_script(id: int, params: Dictionary) -> void:
	var instance: Variant = _compile_script(id, str(params.get("source", "")))
	if instance == null:
		return
	var before: Dictionary = _snapshot()
	# Object.call runs execute to its first await and returns the GDScriptFunctionState that await
	# made (modules/gdscript/gdscript_vm.cpp, OPCODE_AWAIT, in 4.7.2), whose completed signal
	# carries execute's return value. A direct instance.execute() without await would not: a debug
	# build ends the caller with "Trying to call an async function without "await"."
	var value: Variant = (instance as Object).call("execute", _scene_tree())
	if value is Object and is_instance_valid(value):
		if (value as Object).is_class("GDScriptFunctionState"):
			_running_scripts[id] = {"instance": instance, "state": value, "before": before}
			(value as Object).connect("completed", _on_script_completed.bind(id))
			return
	_free_unless_counted(instance)
	_reply_ok(id, {"value": _json.to_json(value)})


## The run_script instance compiled from source, or null once the request is answered with why it
## cannot run: a compile error, or no execute method.
func _compile_script(id: int, source: String) -> Variant:
	var script := GDScript.new()
	script.source_code = source
	var error: Error = script.reload()
	if error != OK:
		_reply_error(id, "the script did not compile (%s, error %d)" % [error_string(error), error])
		return null
	var instance: Variant = script.new()
	if not instance is Object or not (instance as Object).has_method("execute"):
		_free_unless_counted(instance)
		_reply_error(id, "the script defines no func execute(scene_tree: SceneTree) -> Variant")
		return null
	return instance


## Answers a suspended run_script whose execute has returned, with its value, and frees its
## instance; a script already stopped is no longer in _running_scripts and is left alone.
func _on_script_completed(value: Variant, id: int) -> void:
	if not _running_scripts.has(id):
		return
	var running: Dictionary = _running_scripts[id]
	_running_scripts.erase(id)
	_free_unless_counted(running["instance"])
	_reply_ok(id, {"value": _json.to_json(value)})


## Runs a frame, wait_for or frames (capture_frames) request on the clock child, which answers
## {result} or {error}.
func _handle_time(id: int, params: Dictionary, command: String) -> void:
	var outcome: Dictionary
	match command:
		"frame":
			outcome = await _time.frame_control(params)
		"frames":
			outcome = await _time.capture_frames(params)
		_:
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


## An absolute path (/root/Main/Button), a path under the root (Main/Button), a path starting at a
## unique name (%Rows, looked up in every scene: the inspector's find_unique), or else the first
## node of that name, breadth first from the root.
func _find_node(element: String) -> Node:
	var root: Window = get_tree().root
	if element.begins_with("%"):
		return _inspect.find_unique(root, element)
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
	_running_requests.erase(id)
	_flush_errors()
	_send({"id": id, "ok": true, "result": result})


func _reply_error(id: int, message: String) -> void:
	_running_requests.erase(id)
	_flush_errors()
	_send({"id": id, "ok": false, "error": message})


## Writes one frame; none without a stream or before the hello (a late reply to a lost one).
func _send(message: Dictionary) -> void:
	if _stream == null or not _hello_sent:
		return
	var payload: PackedByteArray = JSON.stringify(message).to_utf8_buffer()
	_stream.put_u32(payload.size())
	_stream.put_data(payload)
