extends Node
## The godot-mcp bridge's dormant mode: a game started from an armed folder's override.cfg with no
## server to dial waits here until attach_project joins it. Its static functions also find how the
## bridge runs (choose) and the server it dials.
##
## Its files sit in the folder attach.json does, res://.godot/godot-mcp/, globalised: armed.json
## while arm_project keeps the folder armed, dormant/<pid>.json while this game waits, and
## join-<pid>.json when the server joins it, holding {port, token, shutOutRealGamepads, quiet} as
## attach.json does. While dormant it looks every POLL_MS: a join file is read and deleted, and a
## valid one ends the wait with joined; armed.json gone ends it for good with disarmed.

## A join file held a valid endpoint, {port, token, shutOutRealGamepads, quiet}, to dial.
signal joined(endpoint: Dictionary)
## The folder was disarmed while this game waited: nothing will join it now.
signal disarmed

const STATE_DIR := "res://.godot/godot-mcp"
const ARMED_FILE := "armed.json"
const DORMANT_DIR := "dormant"
const JOIN_FILE := "join-%d.json"
const POLL_MS := 500
## How the bridge runs, decided once in its _init (decide_mode): a run_project game, one
## attach_project waited for (attach.json), one with a window waiting in an armed folder, or none.
const MODE_RUN := "run"
const MODE_ATTACH := "attach"
const MODE_DORMANT := "dormant"
const MODE_OFF := "off"
## Where the bridge's endpoint came from: the environment run_project sets, attach.json or a join
## file.
const SOURCE_ENV := "env"
const SOURCE_ATTACH := "attach"
const SOURCE_JOIN := "join"
const QUIET_VARIABLE := "GODOT_MCP_QUIET"
const ATTACH_FILE := "attach.json"
## Set to "1" by the server for its headless runs, which load a live session's override.cfg: the
## bridge then stays off without looking for a server, and frees itself without a warning.
const OFF_VARIABLE := "GODOT_MCP_OFF"

## The globalised STATE_DIR the files are read and written in.
var state_dir: String = ProjectSettings.globalize_path(STATE_DIR)
var pid: int = OS.get_process_id()
## When the game started, in ms since the Unix epoch: the dormant file's startedUnixMs.
var started_unix_ms: int = 0

var _polling: bool = false
var _next_poll_ms: int = 0
## The dormant file this node wrote and has not deleted yet; empty when there is none.
var _written: String = ""


## How the bridge runs and whom it serves, {mode, endpoint, source}: the MODE_* decide_mode picks
## from what dir (the globalised STATE_DIR), the environment and whether the game is headless hold,
## and for a run or an attach the endpoint found and its SOURCE_*; endpoint and source are empty
## otherwise.
static func choose(dir: String) -> Dictionary:
	var off: bool = is_switched_off()
	var from_env: Dictionary = {} if off else env_endpoint()
	var from_attach: Dictionary = {} if off or not from_env.is_empty() else attach_endpoint(dir)
	var armed: bool = FileAccess.file_exists(armed_path(dir))
	var mode: String = decide_mode(
		not from_env.is_empty(), not from_attach.is_empty(), armed, off, is_headless()
	)
	if mode == MODE_RUN:
		return {"mode": mode, "endpoint": from_env, "source": SOURCE_ENV}
	if mode == MODE_ATTACH:
		return {"mode": mode, "endpoint": from_attach, "source": SOURCE_ATTACH}
	return {"mode": mode, "endpoint": {}, "source": ""}


## Whether this game runs without a window (a `--headless` run: a smoke or a test runner): such a
## game is never dormant, so an armed folder's bridge leaves it off rather than waiting for an
## attach_project that found no pid.
static func is_headless() -> bool:
	return DisplayServer.get_name() == "headless"


## Whether a bridge with no endpoint frees itself silently instead of warning that no server was
## found: the server switched it off (OFF_VARIABLE), or it is a headless game in an armed folder,
## which would have waited dormant had it a window.
static func frees_silently(dir: String) -> bool:
	return is_switched_off() or (is_headless() and FileAccess.file_exists(armed_path(dir)))


## Whether the server switched the bridge off for a headless run (OFF_VARIABLE).
static func is_switched_off() -> bool:
	return OS.get_environment(OFF_VARIABLE) == "1"


## The server to dial, whether to shut the real pads out and whether to park the window, {port,
## token, shutOutRealGamepads, quiet}: from the environment (env_endpoint), else the attach file
## under dir (attach_endpoint); empty when there is neither.
static func find_endpoint(dir: String) -> Dictionary:
	var found: Dictionary = env_endpoint()
	return found if not found.is_empty() else attach_endpoint(dir)


## GODOT_MCP_PORT, GODOT_MCP_TOKEN, GODOT_MCP_SHUT_OUT_REAL_GAMEPADS and GODOT_MCP_QUIET from
## run_project, as an endpoint; empty without a port and a token. override.cfg's joypad and window
## settings are written to match, but the bridge reads only these.
static func env_endpoint() -> Dictionary:
	var port_text: String = OS.get_environment("GODOT_MCP_PORT")
	var token: String = OS.get_environment("GODOT_MCP_TOKEN")
	if not port_text.is_valid_int() or token.is_empty():
		return {}
	return {
		"port": port_text.to_int(),
		"token": token,
		"shutOutRealGamepads": OS.get_environment("GODOT_MCP_SHUT_OUT_REAL_GAMEPADS") == "1",
		"quiet": OS.get_environment(QUIET_VARIABLE) == "1",
	}


## The endpoint in the attach file attach_project writes under dir (GODOT_MCP_QUIET still makes an
## attached game quiet); empty, with a warning when the file holds no {port, token}, when there is
## none.
static func attach_endpoint(dir: String) -> Dictionary:
	var path: String = dir.path_join(ATTACH_FILE)
	if not FileAccess.file_exists(path):
		return {}
	var quiet_variable: bool = OS.get_environment(QUIET_VARIABLE) == "1"
	var found: Dictionary = parse_endpoint(FileAccess.get_file_as_string(path), quiet_variable)
	if found.is_empty():
		push_warning("godot-mcp bridge: %s holds no {port, token}; it is not used." % path)
	return found


## How the bridge runs: off when the server switched it off, else a run's environment, then
## attach.json, then an armed folder for a game with a window, each found (a malformed attach.json
## is not found). A headless game is never dormant, so an armed folder alone leaves it off.
static func decide_mode(
	env_found: bool, attach_found: bool, armed: bool, off: bool, headless: bool
) -> String:
	if off:
		return MODE_OFF
	if env_found:
		return MODE_RUN
	if attach_found:
		return MODE_ATTACH
	if armed and not headless:
		return MODE_DORMANT
	return MODE_OFF


## The endpoint an attach or join file's text holds, {port, token, shutOutRealGamepads, quiet},
## quiet also when quiet_variable (GODOT_MCP_QUIET=1) is; empty when the text is not a JSON object
## with a port and a token.
static func parse_endpoint(text: String, quiet_variable: bool) -> Dictionary:
	var json := JSON.new()
	if json.parse(text) != OK or not json.data is Dictionary:
		return {}
	var found: Dictionary = json.data
	if not found.has("port") or not found.has("token"):
		return {}
	return {
		"port": int(found["port"]),
		"token": str(found["token"]),
		"shutOutRealGamepads": bool(found.get("shutOutRealGamepads", false)),
		"quiet": quiet_variable or bool(found.get("quiet", false)),
	}


## Whether a connection that ended leaves the game dormant again: only an endpoint from attach.json
## or a join file, only while the folder is still armed, and never for a headless game, which is
## never dormant. A run's game never goes dormant.
static func goes_dormant_again(source: String, armed: bool, headless: bool) -> bool:
	return armed and not headless and (source == SOURCE_ATTACH or source == SOURCE_JOIN)


static func armed_path(dir: String) -> String:
	return dir.path_join(ARMED_FILE)


static func dormant_path(dir: String, game_pid: int) -> String:
	return dir.path_join(DORMANT_DIR).path_join("%d.json" % game_pid)


static func join_path(dir: String, game_pid: int) -> String:
	return dir.path_join(JOIN_FILE % game_pid)


## What a dormant file holds: {pid, startedUnixMs}.
static func dormant_content(game_pid: int, started_ms: int) -> Dictionary:
	return {"pid": game_pid, "startedUnixMs": started_ms}


## Writes the dormant file of game_pid under dir, making its folder; returns its path, or "" with a
## warning when it cannot be written.
static func write_dormant_file(dir: String, game_pid: int, started_ms: int) -> String:
	var path: String = dormant_path(dir, game_pid)
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	var file: FileAccess = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		var reason: String = error_string(FileAccess.get_open_error())
		push_warning(
			"godot-mcp bridge: cannot write %s (%s); nothing can join it." % [path, reason]
		)
		return ""
	file.store_string(JSON.stringify(dormant_content(game_pid, started_ms)))
	file.close()
	return path


## Whether armed.json under dir asks for quiet sessions; false when it is missing or malformed.
static func armed_quiet(dir: String) -> bool:
	var path: String = armed_path(dir)
	var json := JSON.new()
	if not FileAccess.file_exists(path) or json.parse(FileAccess.get_file_as_string(path)) != OK:
		return false
	return json.data is Dictionary and bool((json.data as Dictionary).get("quiet", false))


static func remove_file(path: String) -> void:
	if not path.is_empty() and FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


## Writes this game's dormant file and starts looking for a join, at the next frame.
func enter() -> void:
	_written = write_dormant_file(state_dir, pid, started_unix_ms)
	_polling = true
	_next_poll_ms = 0


## Stops looking and deletes the dormant file.
func leave() -> void:
	_polling = false
	remove_file(_written)
	_written = ""


func is_polling() -> bool:
	return _polling


func _process(_delta: float) -> void:
	if not _polling or Time.get_ticks_msec() < _next_poll_ms:
		return
	_next_poll_ms = Time.get_ticks_msec() + POLL_MS
	poll()


## One look: a join file is read and deleted, and a valid one ends the wait (joined); a malformed
## one is warned about and the wait goes on. Else armed.json gone ends the wait (disarmed).
func poll() -> void:
	var join_file: String = join_path(state_dir, pid)
	if FileAccess.file_exists(join_file):
		var quiet_variable: bool = OS.get_environment(QUIET_VARIABLE) == "1"
		var endpoint: Dictionary = parse_endpoint(
			FileAccess.get_file_as_string(join_file), quiet_variable
		)
		remove_file(join_file)
		if endpoint.is_empty():
			push_warning("godot-mcp bridge: %s holds no {port, token}; still dormant." % join_file)
			return
		leave()
		joined.emit(endpoint)
	elif not FileAccess.file_exists(armed_path(state_dir)):
		leave()
		disarmed.emit()


func _exit_tree() -> void:
	remove_file(_written)
	_written = ""
