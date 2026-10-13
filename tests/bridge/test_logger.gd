extends "res://gd_test.gd"
## The error feed's logger (bridge/godot_mcp_logger.gd): how an error becomes an entry, where it
## is located, and the cap on pending entries. _log_error is called directly, as the engine would.
# gdlint: disable=private-method-call

## A script other than this test whose capture returns backtraces with its own frame on top.
const CAPTURER_SOURCE := (
	"extends RefCounted\n\n\n"
	+ "func capture() -> Array[ScriptBacktrace]:\n\treturn Engine.capture_script_backtraces()\n"
)

## This file as its own backtraces name it, the script a test's mute window is opened for.
const THIS_FILE := "res://test_logger.gd"

var _logger_script: GDScript = load_bridge_script("godot_mcp_logger.gd")


func test_warning_entry_takes_code_when_there_is_no_rationale() -> void:
	var logger: Logger = _logger_script.new()
	var none: Array[ScriptBacktrace] = []
	logger._log_error("f", "res://a.gd", 3, "the code", "", false, Logger.ERROR_TYPE_WARNING, none)
	var expected: Dictionary = {
		"type": "warning",
		"message": "the code",
		"function": "f",
		"file": "res://a.gd",
		"line": 3,
		"stack": [],
	}
	assert_eq(logger.take_pending(), [[expected], 0], "one warning entry, none dropped")


func test_error_entry_takes_rationale_over_code() -> void:
	var logger: Logger = _logger_script.new()
	var none: Array[ScriptBacktrace] = []
	logger._log_error("f", "res://a.gd", 3, "code", "why", false, Logger.ERROR_TYPE_ERROR, none)
	var entry: Dictionary = logger.take_pending()[0][0]
	assert_eq(entry["type"], "error", "an error's type")
	assert_eq(entry["message"], "why", "the rationale is the message")


func test_script_file_error_keeps_its_site_and_stack() -> void:
	var logger: Logger = _logger_script.new()
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	logger._log_error("f", "gdscript://7.gd", 9, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	var entry: Dictionary = logger.take_pending()[0][0]
	assert_eq(entry["file"], "gdscript://7.gd", "a script's own error keeps its file")
	assert_eq(entry["line"], 9, "and its line")
	assert_true(not entry.has("engine"), "and has no engine site")
	var stack: Array = entry["stack"]
	assert_true(not stack.is_empty(), "the stack holds this test's frames")
	if not stack.is_empty():
		var top: String = stack[0]
		assert_true(top.begins_with("res://test_logger.gd:"), "the top frame is this file: " + top)
		assert_true(top.ends_with(" in test_script_file_error_keeps_its_site_and_stack"), top)


func test_engine_error_moves_to_the_script_frame() -> void:
	var logger: Logger = _logger_script.new()
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	logger._log_error("run", "core/x.cpp", 12, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	var entry: Dictionary = logger.take_pending()[0][0]
	assert_eq(entry["engine"], "core/x.cpp:12", "the engine's site is kept")
	assert_eq(entry["file"], "res://test_logger.gd", "the error is located at this test")
	assert_eq(entry["function"], "test_engine_error_moves_to_the_script_frame", "in this function")
	assert_true(entry["line"] > 0, "at a line")


func test_locate_without_frames_leaves_the_entry() -> void:
	var logger: Logger = _logger_script.new()
	var entry: Dictionary = {"function": "run", "file": "core/x.cpp", "line": 12}
	var none: Array[ScriptBacktrace] = []
	logger._locate_in_script(entry, none)
	assert_eq(entry, {"function": "run", "file": "core/x.cpp", "line": 12}, "unchanged")


func test_pending_is_capped_and_counts_the_dropped() -> void:
	var logger: Logger = _logger_script.new()
	var none: Array[ScriptBacktrace] = []
	var cap: int = _logger_script.get_script_constant_map()["MAX_PENDING"]
	for index in cap + 3:
		logger._log_error("f", "res://a.gd", index, "c", "", false, Logger.ERROR_TYPE_ERROR, none)
	var taken: Array = logger.take_pending()
	assert_eq((taken[0] as Array).size(), cap, "entries stop at the cap")
	assert_eq(taken[1], 3, "the rest are counted as dropped")
	assert_eq(logger.take_pending(), [[], 0], "taking starts both over")


func test_a_muted_error_is_dropped_only_with_its_windows_script_on_top() -> void:
	var logger: Logger = _logger_script.new()
	var watch_path: String = load_bridge_script("godot_mcp_watch.gd").resource_path
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	var error: int = Logger.ERROR_TYPE_ERROR
	logger.mute(true, watch_path)
	logger._log_error("f", "core/x.cpp", 1, "game code on top", "", false, error, traces)
	logger.mute(false, watch_path)
	logger.mute(true, THIS_FILE)
	logger._log_error("f", "core/x.cpp", 1, "its script on top", "", false, error, traces)
	logger.mute(false, THIS_FILE)
	logger._log_error("f", "core/x.cpp", 1, "unmuted", "", false, error, traces)
	var entries: Array = logger.take_pending()[0]
	var messages: Array = entries.map(func(entry: Dictionary) -> String: return entry["message"])
	assert_eq(messages, ["game code on top", "unmuted"], "only the window's own call is dropped")


func test_a_muted_error_lands_when_one_backtrace_has_another_script_on_top() -> void:
	var logger: Logger = _logger_script.new()
	var theirs: Array[ScriptBacktrace] = _capture_elsewhere()
	var tops: Array = logger._latest_frame_files(theirs)
	assert_true(not tops.is_empty() and not tops.has(THIS_FILE), "%s" % [tops])
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	traces.append_array(theirs)
	logger.mute(true, THIS_FILE)
	logger._log_error("f", "core/x.cpp", 1, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	logger.mute(false, THIS_FILE)
	assert_eq(_files(logger), [THIS_FILE], "the window's script tops one only: it lands")


func test_a_muted_error_skips_a_backtrace_without_frames() -> void:
	var logger: Logger = _logger_script.new()
	var traces: Array[ScriptBacktrace] = [ScriptBacktrace.new()]
	traces.append_array(Engine.capture_script_backtraces())
	logger.mute(true, THIS_FILE)
	logger._log_error("f", "core/x.cpp", 1, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	logger.mute(false, THIS_FILE)
	assert_eq(_files(logger), [], "the empty one is skipped, the window's script tops the rest")


func test_a_mute_without_frames_drops_an_engine_entry_and_keeps_a_script_ones() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true, THIS_FILE)
	_log_at(logger, ["core/x.cpp", "res://a.gd", "gdscript://7.gd"])
	assert_eq(_files(logger), ["res://a.gd", "gdscript://7.gd"], "muted: the script files' only")
	logger.mute(false, THIS_FILE)
	_log_at(logger, ["core/x.cpp", "res://a.gd"])
	assert_eq(_files(logger), ["core/x.cpp", "res://a.gd"], "unmuted: both")


func test_mute_windows_nest_and_closing_an_extra_one_leaves_none_open() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true, THIS_FILE)
	logger.mute(true, THIS_FILE)
	logger.mute(false, THIS_FILE)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), [], "the outer window is still open")
	logger.mute(false, THIS_FILE)
	logger.mute(false, THIS_FILE)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), ["core/x.cpp"], "both closed, and an extra close is ignored")
	logger.mute(true, THIS_FILE)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), [], "one open again mutes: the depth never went below 0")


# This file stands in for the conditions script and the capturer for the watch's: the logger
# knows a window's script only by the path its opener names.
func test_nested_windows_of_two_scripts_each_drop_only_their_own_scripts_errors() -> void:
	var logger: Logger = _logger_script.new()
	var watched: Array[ScriptBacktrace] = _capture_elsewhere()
	var watch_path: String = logger._latest_frame_files(watched)[0]
	var own: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	assert_true(watch_path != THIS_FILE, "two scripts: %s" % watch_path)
	logger.mute(true, THIS_FILE)
	logger.mute(true, watch_path)
	_log_with(logger, "conditions, both open", own)
	_log_with(logger, "watch, both open", watched)
	logger.mute(false, watch_path)
	_log_with(logger, "conditions, the watch's closed", own)
	_log_with(logger, "watch, the watch's closed", watched)
	logger.mute(false, THIS_FILE)
	_log_with(logger, "conditions, both closed", own)
	logger.mute(true, watch_path)
	_log_with(logger, "conditions, the watch's alone open", own)
	_log_with(logger, "watch, the watch's alone open", watched)
	logger.mute(false, watch_path)
	assert_eq(
		_messages(logger),
		[
			"watch, the watch's closed",
			"conditions, both closed",
			"conditions, the watch's alone open",
		],
		"each script's error is dropped while a window of its own is open, and only then"
	)


func test_a_windows_script_takes_the_count_and_first_text_its_windows_dropped() -> void:
	var logger: Logger = _logger_script.new()
	var watched: Array[ScriptBacktrace] = _capture_elsewhere()
	var watch_path: String = logger._latest_frame_files(watched)[0]
	var own: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	assert_eq(logger.take_muted(THIS_FILE), {}, "nothing dropped yet")
	logger.mute(true, THIS_FILE)
	logger.mute(true, watch_path)
	_log_with(logger, "first", own)
	_log_with(logger, "the watch's", watched)
	logger.mute(false, watch_path)
	_log_with(logger, "second", own)
	logger.mute(false, THIS_FILE)
	_log_with(logger, "unmuted", own)
	assert_eq(logger.take_muted(THIS_FILE), {"count": 2, "error": "first"}, "two, the first's text")
	assert_eq(logger.take_muted(THIS_FILE), {}, "taking starts it over")
	assert_eq(
		logger.take_muted(watch_path), {"count": 1, "error": "the watch's"}, "the other's own"
	)
	logger.mute(true, THIS_FILE)
	_log_at(logger, ["core/x.cpp"])
	logger.mute(false, THIS_FILE)
	assert_eq(logger.take_muted(THIS_FILE), {"count": 1, "error": "code"}, "a frameless one counts")
	assert_eq(_messages(logger), ["unmuted"], "only the unmuted error is held")


func test_a_muted_warning_is_dropped_but_not_counted() -> void:
	var logger: Logger = _logger_script.new()
	var own: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	var warning: int = Logger.ERROR_TYPE_WARNING
	logger.mute(true, THIS_FILE)
	logger._log_error("f", "core/x.cpp", 1, "a warning", "", false, warning, own)
	_log_with(logger, "an error", own)
	logger.mute(false, THIS_FILE)
	assert_eq(logger.take_muted(THIS_FILE), {"count": 1, "error": "an error"}, "the error alone")
	assert_eq(_messages(logger), [], "and neither is held")


func test_a_frameless_engine_error_counts_for_every_open_window() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true, THIS_FILE)
	logger.mute(true, "res://other.gd")
	_log_at(logger, ["core/x.cpp"])
	logger.mute(false, "res://other.gd")
	logger.mute(false, THIS_FILE)
	assert_eq(logger.take_muted(THIS_FILE), {"count": 1, "error": "code"}, "counted for this one")
	assert_eq(logger.take_muted("res://other.gd"), {"count": 1, "error": "code"}, "and the other")
	assert_eq(_messages(logger), [], "and not held")


func test_a_new_window_starts_its_scripts_count_over() -> void:
	var logger: Logger = _logger_script.new()
	var own: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	logger.mute(true, THIS_FILE)
	_log_with(logger, "not taken", own)
	logger.mute(false, THIS_FILE)
	logger.mute(true, THIS_FILE)
	_log_with(logger, "next window", own)
	logger.mute(false, THIS_FILE)
	assert_eq(
		logger.take_muted(THIS_FILE), {"count": 1, "error": "next window"}, "the last window's"
	)


func test_a_mute_keeps_an_engine_entry_another_thread_raises() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true, THIS_FILE)
	var thread := Thread.new()
	thread.start(_log_at.bind(logger, ["core/x.cpp"]))
	thread.wait_to_finish()
	_log_at(logger, ["core/y.cpp"])
	logger.mute(false, THIS_FILE)
	assert_eq(_files(logger), ["core/x.cpp"], "the other thread's kept, the main thread's dropped")


func test_lost_since_is_true_only_for_an_entry_dropped_or_sent() -> void:
	var logger: Logger = _logger_script.new()
	var none: Array[ScriptBacktrace] = []
	var cap: int = _logger_script.get_script_constant_map()["MAX_PENDING"]
	var start: int = logger.sequence()
	assert_true(not logger.lost_since(start), "nothing logged, nothing lost")
	var warn: int = Logger.ERROR_TYPE_WARNING
	logger._log_error("f", "res://a.gd", 1, "a warning", "", false, warn, none)
	assert_true(not logger.lost_since(start), "a held warning is not lost")
	for index in cap - 1:
		logger._log_error("f", "res://a.gd", index, "c", "", false, warn, none)
	var full: int = logger.sequence()
	assert_true(not logger.lost_since(start), "the full list still holds every entry")
	logger._log_error("f", "res://a.gd", 1, "e", "", false, Logger.ERROR_TYPE_ERROR, none)
	assert_true(logger.lost_since(full), "the entry past the cap was dropped")
	assert_true(logger.lost_since(start), "and a mark before it sees the drop too")
	logger.take_pending()
	var after: int = logger.sequence()
	logger._log_error("f", "res://a.gd", 1, "w", "", false, warn, none)
	assert_true(logger.lost_since(start), "entries already sent are no longer held")
	assert_true(not logger.lost_since(after), "a warning held after the take is not lost")


## Logs an error at each of files, as the engine would.
static func _log_at(logger: Logger, files: Array) -> void:
	var none: Array[ScriptBacktrace] = []
	for file: String in files:
		logger._log_error("f", file, 1, "code", "", false, Logger.ERROR_TYPE_ERROR, none)


## Logs an engine error with message and traces, as the engine would.
static func _log_with(logger: Logger, message: String, traces: Array[ScriptBacktrace]) -> void:
	logger._log_error("f", "core/x.cpp", 1, message, "", false, Logger.ERROR_TYPE_ERROR, traces)


## Backtraces captured in a script compiled from CAPTURER_SOURCE, its frame on top.
static func _capture_elsewhere() -> Array[ScriptBacktrace]:
	var capturer := GDScript.new()
	capturer.source_code = CAPTURER_SOURCE
	capturer.reload()
	return capturer.new().capture()


## The files of the entries logger holds, taking them.
static func _files(logger: Logger) -> Array:
	var entries: Array = logger.take_pending()[0]
	return entries.map(func(entry: Dictionary) -> String: return entry["file"])


## The messages of the entries logger holds, taking them.
static func _messages(logger: Logger) -> Array:
	var entries: Array = logger.take_pending()[0]
	return entries.map(func(entry: Dictionary) -> String: return entry["message"])
