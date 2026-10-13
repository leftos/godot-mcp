extends Logger
## The godot-mcp bridge's error feed: every engine and script error and warning logged after
## the bridge registers it (OS.add_logger in the bridge's _init).
##
## Godot calls _log_error on whatever thread raised the error, with no engine lock held, and an
## error raised inside it never reaches a logger, so it only appends an entry under a mutex.
## The bridge drains the entries on the main thread with take_pending and sends them.
##
## A mute window is opened for a script (mute, by the watch around an expression track and by the
## conditions around a wait_for or step-until expression). While one is open, an error the main
## thread raises directly in a native method the expression calls is dropped, such as get_child(0)
## on a node with no children: the most recent frame of every script backtrace that has frames is
## the script of an open window. An error from game code the expression calls (a push_error, a C#
## exception, an engine check failing inside a game method) has a game frame on top and still
## lands, as does every other thread's. Without script backtraces (a release export fills none) the
## error's file decides: a script file's lands, any other is dropped. Each dropped error (not a
## warning, which is dropped uncounted) is counted for the scripts whose windows dropped it, which
## take_muted reads. OS.get_thread_caller_id names
## the thread that raised the error, since the engine calls _log_error on it, and
## OS.get_main_thread_id the main thread (class reference, OS).

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
## The mute windows open, keyed by the path of the script they were opened for: how many
## mute(true, path) calls are not yet closed by a mute(false, path). Touched only on the main
## thread, and read only once _log_error knows it runs there.
var _mute_windows: Dictionary = {}
## The errors (not warnings) each script's windows dropped since its first window opened, keyed by
## its path: {count, error}, error the first dropped error's message. Main thread only, as
## _mute_windows.
var _muted_by: Dictionary = {}


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
	var message: String = rationale if not rationale.is_empty() else code
	var muting: Array = _muting_scripts(file, script_backtraces)
	if not muting.is_empty():
		if error_type != ERROR_TYPE_WARNING:
			_count_muted(muting, message)
		return
	var entry: Dictionary = {
		"type": "warning" if error_type == ERROR_TYPE_WARNING else "error",
		"message": message,
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


## Opens a mute window for the script at script_path (on) or closes one of its windows (off), on
## the main thread. A script's windows nest, and closing more than are open leaves none open for
## it; opening its first window starts its count of dropped errors over.
func mute(on: bool, script_path: String) -> void:
	var depth: int = int(_mute_windows.get(script_path, 0))
	if on:
		if depth == 0:
			_muted_by.erase(script_path)
		_mute_windows[script_path] = depth + 1
	elif depth > 1:
		_mute_windows[script_path] = depth - 1
	else:
		_mute_windows.erase(script_path)


## What the windows of the script at script_path dropped since its first window opened, as
## {count, error} with the first dropped error's message, or {} when they dropped none; the count
## starts over. On the main thread.
func take_muted(script_path: String) -> Dictionary:
	var muted: Dictionary = _muted_by.get(script_path, {})
	_muted_by.erase(script_path)
	return muted


## The scripts whose open windows drop an error at file, or none when it lands: raised on the main
## thread while a mute window is open, with an open window's script on top of every script
## backtrace that has frames (those scripts), or with no frames and outside a script file (every
## script with a window open).
## Another thread's error is never dropped, so it never reads the windows.
func _muting_scripts(file: String, backtraces: Array[ScriptBacktrace]) -> Array:
	if OS.get_thread_caller_id() != OS.get_main_thread_id() or _mute_windows.is_empty():
		return []
	var latest: Array = _latest_frame_files(backtraces)
	if latest.is_empty():
		return [] if _is_script_file(file) else _mute_windows.keys()
	if not latest.all(func(frame_file: String) -> bool: return _mute_windows.has(frame_file)):
		return []
	var scripts: Array = []
	for frame_file: String in latest:
		if not scripts.has(frame_file):
			scripts.append(frame_file)
	return scripts


## Counts a dropped error with message for each of scripts, keeping the first one's message.
func _count_muted(scripts: Array, message: String) -> void:
	for script_path: String in scripts:
		var muted: Dictionary = _muted_by.get_or_add(script_path, {"count": 0, "error": message})
		muted["count"] += 1


## The file of the most recent frame of each script backtrace that has frames.
static func _latest_frame_files(backtraces: Array[ScriptBacktrace]) -> Array:
	var files: Array = []
	for backtrace: ScriptBacktrace in backtraces:
		if backtrace.get_frame_count() > 0:
			files.append(backtrace.get_frame_file(0))
	return files


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
