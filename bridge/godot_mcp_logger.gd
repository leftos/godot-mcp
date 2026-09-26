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
	if _pending.size() < MAX_PENDING:
		_pending.append(entry)
	else:
		_dropped += 1
	_mutex.unlock()


## The entries logged since the last call and how many were dropped over the cap, as
## [entries, dropped]; both start over empty.
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
			frames.append(
				(
					"%s:%d in %s"
					% [
						backtrace.get_frame_file(index),
						backtrace.get_frame_line(index),
						backtrace.get_frame_function(index),
					]
				)
			)
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
