extends SceneTree
## Runs the bridge's GDScript unit tests without a window:
## godot --headless --path tests/bridge --script res://run_tests.gd
##
## Every res://test_*.gd extends gd_test.gd; each of its methods named test_* runs once, in name
## order, on one instance of the file's script. A test fails when an assertion in it fails or when
## the engine logs an error while it runs (a script error ends the test's function early without
## stopping the run). Each failure prints as "FAIL <file>::<test>: <message>", then a summary
## "gdtest: <passed> passed, <failed> failed", and the run quits with 1 when any test failed.

const GdTest := preload("res://gd_test.gd")
const TEST_PREFIX := "test_"


## Keeps the errors (not warnings) the engine logs, for the runner to read after each test.
class ErrorRecorder:
	extends Logger

	var _mutex := Mutex.new()
	var _errors: PackedStringArray = []

	func _log_error(
		function: String,
		file: String,
		line: int,
		code: String,
		rationale: String,
		_editor_notify: bool,
		error_type: int,
		_script_backtraces: Array[ScriptBacktrace]
	) -> void:
		if error_type == ERROR_TYPE_WARNING:
			return
		var message: String = rationale if not rationale.is_empty() else code
		_mutex.lock()
		_errors.append("engine error: %s (%s:%d in %s)" % [message, file, line, function])
		_mutex.unlock()

	## The errors logged since the last call; the list starts over empty.
	func take() -> PackedStringArray:
		_mutex.lock()
		var taken: PackedStringArray = _errors
		_errors = []
		_mutex.unlock()
		return taken


var _recorder := ErrorRecorder.new()
var _passed: int = 0
var _failed: int = 0


func _initialize() -> void:
	OS.add_logger(_recorder)
	var files: PackedStringArray = _test_files()
	if files.is_empty():
		_report_failure("run_tests.gd", "no res://test_*.gd files found")
	for file_name in files:
		_run_file(file_name)
	print("gdtest: %d passed, %d failed" % [_passed, _failed])
	quit(1 if _failed > 0 else 0)


func _test_files() -> PackedStringArray:
	var found: PackedStringArray = []
	for file_name in DirAccess.get_files_at("res://"):
		if file_name.begins_with(TEST_PREFIX) and file_name.ends_with(".gd"):
			found.append(file_name)
	found.sort()
	return found


func _run_file(file_name: String) -> void:
	var script: GDScript = load("res://" + file_name) as GDScript
	if script == null or not script.can_instantiate():
		_report_failure(file_name, "the script does not load")
		return
	var instance: Object = script.new()
	if not instance is GdTest:
		_report_failure(file_name, "the script does not extend gd_test.gd")
		return
	_recorder.take()
	for test_name in _test_names(script):
		_run_test(file_name, instance as GdTest, test_name)


func _test_names(script: GDScript) -> PackedStringArray:
	var names: PackedStringArray = []
	for method: Dictionary in script.get_script_method_list():
		var method_name: String = method["name"]
		if method_name.begins_with(TEST_PREFIX) and not names.has(method_name):
			names.append(method_name)
	names.sort()
	return names


func _run_test(file_name: String, instance: GdTest, test_name: String) -> void:
	instance.failures.clear()
	instance.call(test_name)
	var messages: PackedStringArray = instance.failures + _recorder.take()
	if messages.is_empty():
		_passed += 1
		return
	_failed += 1
	for message in messages:
		print("FAIL %s::%s: %s" % [file_name, test_name, message])


## A failure that belongs to a file rather than to one of its tests.
func _report_failure(file_name: String, message: String) -> void:
	_failed += 1
	print("FAIL %s: %s" % [file_name, message])
