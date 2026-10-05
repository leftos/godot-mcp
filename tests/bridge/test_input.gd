extends "res://gd_test.gd"
## The input player's pure helpers (bridge/godot_mcp_input.gd), on an instance never added to
## the tree, and the bridge's pointer ownership it sets (bridge/godot_mcp_bridge.gd), on a bridge
## never added to the tree either.
# gdlint: disable=private-method-call

var _input_script: GDScript = load_bridge_script("godot_mcp_input.gd")
var _bridge_script: GDScript = load_bridge_script("godot_mcp_bridge.gd")
var _time_script: GDScript = load_bridge_script("godot_mcp_time.gd")
var _watch_script: GDScript = load_bridge_script("godot_mcp_watch.gd")


func test_parse_button_takes_names_and_numbers() -> void:
	var gestures: Node = _input_script.new()
	assert_eq(gestures.parse_button("Left"), MOUSE_BUTTON_LEFT, "Left, in any case")
	assert_eq(gestures.parse_button("middle"), MOUSE_BUTTON_MIDDLE, "middle")
	assert_eq(gestures.parse_button(2.0), 2, "a JSON float 2")
	assert_eq(gestures.parse_button(4), 0, "4 is not a button the tools take")
	assert_eq(gestures.parse_button("back"), 0, "an unknown name")
	assert_eq(gestures.parse_button(null), 0, "null")
	gestures.free()


func test_an_injected_motion_takes_the_pointer_and_a_real_one_hands_it_back() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	assert_eq(bridge._owns_pointer, false, "a new bridge owns no pointer")
	var injected: InputEventMouseMotion = gestures.motion_event(Vector2(40, 30), Vector2.ZERO, 0)
	assert_eq(bridge._owns_pointer, true, "an injected motion takes the pointer")
	gestures._on_window_input(injected)
	assert_eq(bridge._owns_pointer, true, "the injected motion reaching window_input keeps it")
	gestures._on_window_input(_real_motion())
	assert_eq(bridge._owns_pointer, false, "a real motion hands it back when nothing is in play")
	gestures.free()
	bridge.free()


func test_a_real_motion_keeps_the_pointer_while_injected_input_is_in_play() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	bridge._owns_pointer = true
	bridge._gesture_playing = true
	gestures._on_window_input(_real_motion())
	assert_eq(bridge._owns_pointer, true, "a real motion while a gesture plays")
	bridge._gesture_playing = false
	bridge._held_mask = MOUSE_BUTTON_MASK_LEFT
	gestures._on_window_input(_real_motion())
	assert_eq(bridge._owns_pointer, true, "a real motion while a button is held")
	gestures.free()
	bridge.free()


func test_a_lost_connection_hands_the_pointer_back() -> void:
	var bridge: Node = _bridge_script.new()
	bridge._time = _time_script.new()
	bridge._watch = _watch_script.new()
	bridge._owns_pointer = true
	bridge._end_connection()
	assert_eq(bridge._owns_pointer, false, "a run's game left idle owns no pointer")
	bridge._time.free()
	bridge._watch.free()
	bridge.free()


## A mouse motion without the injected mark, as the real mouse sends one.
func _real_motion() -> InputEventMouseMotion:
	var motion := InputEventMouseMotion.new()
	motion.position = Vector2(50, 30)
	return motion
