extends "res://gd_test.gd"
## The error feed's logger (bridge/godot_mcp_logger.gd): how an error becomes an entry, where it
## is located, and the cap on pending entries. _log_error is called directly, as the engine would.
# gdlint: disable=private-method-call

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
