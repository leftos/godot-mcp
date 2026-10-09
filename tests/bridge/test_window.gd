extends "res://gd_test.gd"
## The bridge's main-window helpers (bridge/godot_mcp_window.gd) that run without a window.

var _window_script: GDScript = load_bridge_script("godot_mcp_window.gd")


func test_native_handle_is_zero_without_a_display() -> void:
	var handle: Variant = _window_script.native_handle()
	assert_eq(typeof(handle), TYPE_INT, "the handle is an int, which the hello writes as a number")
	assert_eq(handle, 0, "the headless display server has no window, so its handle is 0")
