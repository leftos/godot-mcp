extends Logger
## The godot-mcp bridge's error feed: every engine and script error and warning logged after
## the bridge registers it (OS.add_logger in the bridge's _init).
##
## Godot calls _log_error on whatever thread raised the error, with no engine lock held, and an
## error raised inside it never reaches a logger, so it only appends an entry under a mutex.
## The bridge drains the entries on the main thread with take_pending and sends them.

## Entries held until the bridge sends them; more are counted as dropped.
const MAX_PENDING := 200
## Script frames kept per entry, across every language's backtrace.
const MAX_FRAMES := 10
## Where a script's own errors are located: a project file, or a script compiled from source.
## Any other file is the engine's (push_error's variant_utility.cpp, Godot issue #119628, a
## C# error's glue, an ERR_FAIL in the node a script called), so the error is moved to the most
## recent script frame and the engine's site is kept as "engine".
const SCRIPT_PREFIXES: PackedStringArray = ["res://", "gdscript://"]

var _mutex := Mutex.new()
var _pending: Array = []
var _dropped: int = 0
## Entries logged since the logger was made, held or dropped: the sequence number the next entry
## takes. The held entries are numbered on from the first one take_pending has not sent, and the
## dropped ones come after them, since nothing is held once the cap is reached.
var _sequence: int = 0


func _log_error(
	function: String,
	file: String,
	line: int,
	code: String,
	rationale: String,
	_editor_notify: bool,
	error_type: int,
	script_backtraces: Array[ScriptBacktrace]
) -> void:
	var entry: Dictionary = {
		"type": "warning" if error_type == ERROR_TYPE_WARNING else "error",
		"message": rationale if not rationale.is_empty() else code,
		"function": function,
		"file": file,
		"line": line,
		"stack": _format_stack(script_backtraces),
	}
	if not _is_script_file(file):
		_locate_in_script(entry, script_backtraces)
	_mutex.lock()
	_sequence += 1
	if _pending.size() < MAX_PENDING:
		_pending.append(entry)
	else:
		_dropped += 1
	_mutex.unlock()


## The sequence number the next entry logged takes, the mark first_error_since reads from.
func sequence() -> int:
	_mutex.lock()
	var next: int = _sequence
	_mutex.unlock()
	return next


## The message of the first error (not a warning) logged at or after sequence number since that is
## still held, or "" when there is none: one take_pending has sent, or one dropped over the cap, is
## not seen.
func first_error_since(since: int) -> String:
	_mutex.lock()
	var first_held: int = _sequence - _dropped - _pending.size()
	var found: String = ""
	for index in range(maxi(0, since - first_held), _pending.size()):
		var entry: Dictionary = _pending[index]
		if entry["type"] == "error":
			found = entry["message"]
			break
	_mutex.unlock()
	return found


## The entries logged since the last call and how many were dropped over the cap, as
## [entries, dropped]; both start over empty.
## Whether an entry logged at or after sequence number since is no longer held: dropped over the
## cap, or already sent by take_pending. Held entries run from the first not yet taken, and an
## entry logged while the pending list is full is counted and dropped.
func lost_since(since: int) -> bool:
	_mutex.lock()
	var held: int = _pending.size()
	var first_held: int = _sequence - _dropped - held
	var logged: int = _sequence - since
	_mutex.unlock()
	return logged > maxi(0, first_held + held - maxi(since, first_held))


func take_pending() -> Array:
	_mutex.lock()
	var taken: Array = [_pending, _dropped]
	_pending = []
	_dropped = 0
	_mutex.unlock()
	return taken


## Up to MAX_FRAMES frames as "<file>:<line> in <function>", the most recent first.
static func _format_stack(backtraces: Array[ScriptBacktrace]) -> Array:
	var frames: Array = []
	for backtrace: ScriptBacktrace in backtraces:
		for index in backtrace.get_frame_count():
			if frames.size() == MAX_FRAMES:
				return frames
			var file: String = backtrace.get_frame_file(index)
			var line: int = backtrace.get_frame_line(index)
			frames.append("%s:%d in %s" % [file, line, backtrace.get_frame_function(index)])
	return frames


static func _is_script_file(file: String) -> bool:
	for prefix in SCRIPT_PREFIXES:
		if file.begins_with(prefix):
			return true
	return false


## Replaces the entry's function, file and line with the most recent script frame's, and keeps
## the engine's own site as "engine": "<file>:<line>". Without a frame the entry stays as it is.
static func _locate_in_script(entry: Dictionary, backtraces: Array[ScriptBacktrace]) -> void:
	for backtrace: ScriptBacktrace in backtraces:
		if backtrace.get_frame_count() > 0:
			entry["engine"] = "%s:%d" % [entry["file"], entry["line"]]
			entry["function"] = backtrace.get_frame_function(0)
			entry["file"] = backtrace.get_frame_file(0)
			entry["line"] = backtrace.get_frame_line(0)
			return
