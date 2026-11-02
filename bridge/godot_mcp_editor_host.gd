@tool
extends SceneTree
## The godot-mcp editor host: run_editor starts the Godot editor with this script as its main
## loop (godot --path <p> --editor --windowed --script <this file>), and the editor is built
## inside it. The host reads every GODOT_MCP_* variable the server set and removes it from its
## environment at once, so no game the editor starts inherits the session's token; once the editor
## has finished its first scan, it adds the bridge under root in editor mode with those values.
## It never quits the tree itself: the editor's own close request does (godot_mcp_editor_mode.gd).

const BRIDGE_SCRIPT := "godot_mcp_bridge.gd"
## Every variable the server sets for a launch: Godot lists no environment, so each is named.
const VARIABLES: PackedStringArray = [
	"GODOT_MCP_PORT",
	"GODOT_MCP_TOKEN",
	"GODOT_MCP_QUIET",
	"GODOT_MCP_MUTE",
	"GODOT_MCP_SHUT_OUT_REAL_GAMEPADS",
	"GODOT_MCP_PREVIEW",
	"GODOT_MCP_NO_REPICK_HOVER",
	"GODOT_MCP_HIDDEN_DESKTOP",
	"GODOT_MCP_MOVIE_FPS",
	"GODOT_MCP_WINDOW_SIZE",
	"GODOT_MCP_OFF",
	"GODOT_MCP_DOTNET_ERROR",
]
## How many frames in a row the editor must be ready before the bridge is added, so a scan that
## starts again right after the first is seen.
const READY_FRAMES := 10

## The variables read at start, by name.
var _values: Dictionary = {}
var _bridge: Node = null
var _scan_seen: bool = false
var _filesystem_changed: bool = false
var _watching: bool = false
var _ready_frames: int = 0
var _in_process: bool = false


func _initialize() -> void:
	for name: String in VARIABLES:
		if OS.has_environment(name):
			_values[name] = OS.get_environment(name)
			OS.unset_environment(name)


## A progress dialog runs a whole frame inside the call that shows it (4.7.2
## editor/gui/progress_dialog.cpp L148-152), so this can run inside itself: the inner frame does
## nothing.
func _process(_delta: float) -> bool:
	if _in_process or _bridge != null:
		return false
	_in_process = true
	_wait_for_editor()
	_in_process = false
	return false


func _wait_for_editor() -> void:
	if not _is_ready():
		_ready_frames = 0
		return
	_ready_frames += 1
	if _ready_frames >= READY_FRAMES:
		_add_bridge()


## Whether the editor is up and idle: EditorInterface and its base control exist, and the file
## system is neither scanning nor importing, after a scan was seen or filesystem_changed fired.
func _is_ready() -> bool:
	if not Engine.has_singleton("EditorInterface"):
		return false
	var editor: Object = Engine.get_singleton("EditorInterface")
	var filesystem: EditorFileSystem = editor.get_resource_filesystem()
	if editor.get_base_control() == null or filesystem == null:
		return false
	_watch(filesystem)
	if filesystem.is_scanning() or filesystem.is_importing():
		return false
	return _scan_seen or _filesystem_changed


## Notes a scan in progress, and the first time connects to filesystem_changed.
func _watch(filesystem: EditorFileSystem) -> void:
	if filesystem.is_scanning():
		_scan_seen = true
	if not _watching:
		_watching = true
		filesystem.filesystem_changed.connect(func() -> void: _filesystem_changed = true)


## Adds the bridge (godot_mcp_bridge.gd beside this script) under root in editor mode, handing it
## the variables read at start. A node under root stays out of the edited scene and is never saved.
func _add_bridge() -> void:
	var script_dir: String = (get_script() as Script).resource_path.get_base_dir()
	_bridge = (load(script_dir.path_join(BRIDGE_SCRIPT)) as GDScript).new()
	_bridge.name = "GodotMcpBridge"
	_bridge.call("use_editor", _values)
	root.add_child(_bridge)
