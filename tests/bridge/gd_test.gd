extends RefCounted
## The base of every tests/bridge/test_*.gd: the assertions, and the bridge's scripts to test.
##
## An assertion that fails records its message and the test goes on; run_tests.gd reads
## failures after each test and clears it before the next.

## The bridge's folder, from this project's own: tests/bridge is two levels under the repo root.
## Computed rather than passed in, so the Godot command runs the tests without run.ps1.
const BRIDGE_DIR := "../../bridge"

## What the running test's failed assertions said, in order.
var failures: PackedStringArray = []


## A script from the repo's bridge/ folder, by file name.
static func load_bridge_script(file_name: String) -> GDScript:
	var root: String = ProjectSettings.globalize_path("res://")
	return load(root.path_join(BRIDGE_DIR).path_join(file_name).simplify_path()) as GDScript


## Fails unless actual equals expected: of the same type (an int and a float compare as
## numbers), and equal as == compares them, containers by their contents.
func assert_eq(actual: Variant, expected: Variant, message: String) -> void:
	if not _equal(actual, expected):
		_fail(message, "expected %s, got %s" % [_show(expected), _show(actual)])


func assert_true(condition: bool, message: String) -> void:
	if not condition:
		_fail(message, "expected true")


## Fails unless actual is within is_equal_approx of expected.
func assert_approx(actual: float, expected: float, message: String) -> void:
	if not is_equal_approx(actual, expected):
		_fail(message, "expected about %s, got %s" % [expected, actual])


func _fail(message: String, detail: String) -> void:
	failures.append("%s: %s" % [message, detail])


static func _equal(actual: Variant, expected: Variant) -> bool:
	if _is_number(actual) and _is_number(expected):
		return actual == expected
	return typeof(actual) == typeof(expected) and actual == expected


static func _is_number(value: Variant) -> bool:
	return value is int or value is float


static func _show(value: Variant) -> String:
	if value is String:
		return '"%s"' % value
	return "%s (%s)" % [str(value), type_string(typeof(value))]
