extends "res://gd_test.gd"
## The error feed's logger (bridge/godot_mcp_logger.gd): how an error becomes an entry, where it
## is located, and the cap on pending entries. _log_error is called directly, as the engine would.
# gdlint: disable=private-method-call

## A script other than this test whose capture returns backtraces with its own frame on top.
const CAPTURER_SOURCE := (
	"extends RefCounted\n\n\n"
	+ "func capture() -> Array[ScriptBacktrace]:\n\treturn Engine.capture_script_backtraces()\n"
)

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


func test_a_muted_error_is_dropped_only_with_the_watchs_frame_on_top() -> void:
	var logger: Logger = _logger_script.new()
	var watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")
	assert_eq(logger.watch_path, watch_script.resource_path, "the watch beside the logger")
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	var error: int = Logger.ERROR_TYPE_ERROR
	logger.mute(true)
	logger._log_error("f", "core/x.cpp", 1, "game code on top", "", false, error, traces)
	logger.watch_path = "res://test_logger.gd"
	logger._log_error("f", "core/x.cpp", 1, "the watch on top", "", false, error, traces)
	logger.mute(false)
	logger._log_error("f", "core/x.cpp", 1, "unmuted", "", false, error, traces)
	var entries: Array = logger.take_pending()[0]
	var messages: Array = entries.map(func(entry: Dictionary) -> String: return entry["message"])
	assert_eq(messages, ["game code on top", "unmuted"], "only the watch's own call is dropped")


func test_a_muted_error_lands_when_one_backtrace_has_another_script_on_top() -> void:
	var logger: Logger = _logger_script.new()
	logger.watch_path = "res://test_logger.gd"
	var capturer := GDScript.new()
	capturer.source_code = CAPTURER_SOURCE
	capturer.reload()
	var theirs: Array[ScriptBacktrace] = capturer.new().capture()
	var tops: Array = logger._latest_frame_files(theirs)
	assert_true(not tops.is_empty() and not tops.has(logger.watch_path), "%s" % [tops])
	var traces: Array[ScriptBacktrace] = Engine.capture_script_backtraces()
	traces.append_array(theirs)
	logger.mute(true)
	logger._log_error("f", "core/x.cpp", 1, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	logger.mute(false)
	assert_eq(_files(logger), ["res://test_logger.gd"], "the watch is on top of one only: it lands")


func test_a_muted_error_skips_a_backtrace_without_frames() -> void:
	var logger: Logger = _logger_script.new()
	logger.watch_path = "res://test_logger.gd"
	var traces: Array[ScriptBacktrace] = [ScriptBacktrace.new()]
	traces.append_array(Engine.capture_script_backtraces())
	logger.mute(true)
	logger._log_error("f", "core/x.cpp", 1, "code", "", false, Logger.ERROR_TYPE_ERROR, traces)
	logger.mute(false)
	assert_eq(_files(logger), [], "the empty one is skipped, the watch tops the rest")


func test_a_mute_without_frames_drops_an_engine_entry_and_keeps_a_script_ones() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true)
	_log_at(logger, ["core/x.cpp", "res://a.gd", "gdscript://7.gd"])
	assert_eq(_files(logger), ["res://a.gd", "gdscript://7.gd"], "muted: the script files' only")
	logger.mute(false)
	_log_at(logger, ["core/x.cpp", "res://a.gd"])
	assert_eq(_files(logger), ["core/x.cpp", "res://a.gd"], "unmuted: both")


func test_mute_windows_nest_and_closing_an_extra_one_leaves_none_open() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true)
	logger.mute(true)
	logger.mute(false)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), [], "the outer window is still open")
	logger.mute(false)
	logger.mute(false)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), ["core/x.cpp"], "both closed, and an extra close is ignored")
	logger.mute(true)
	_log_at(logger, ["core/x.cpp"])
	assert_eq(_files(logger), [], "one open again mutes: the depth never went below 0")


func test_a_mute_keeps_an_engine_entry_another_thread_raises() -> void:
	var logger: Logger = _logger_script.new()
	logger.mute(true)
	var thread := Thread.new()
	thread.start(_log_at.bind(logger, ["core/x.cpp"]))
	thread.wait_to_finish()
	_log_at(logger, ["core/y.cpp"])
	logger.mute(false)
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


## The files of the entries logger holds, taking them.
static func _files(logger: Logger) -> Array:
	var entries: Array = logger.take_pending()[0]
	return entries.map(func(entry: Dictionary) -> String: return entry["file"])
