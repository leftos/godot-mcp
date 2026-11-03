extends "res://gd_test.gd"
## The input player's pure helpers (bridge/godot_mcp_input.gd), on an instance never added to
## the tree, and the bridge's pointer ownership it sets (bridge/godot_mcp_bridge.gd), on a bridge
## never added to the tree either.
# gdlint: disable=private-method-call

## The refusal a click or a drag answers with while the button it would press is held.
const HELD_REFUSAL := (
	"the left button is held (a drag with release: false, or a mouse_button press); "
	+ "release it with mouse_button action release first."
)

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


## A motion's velocity over the last process frame's time, as the popup item probe needs: PopupMenu
## ignores a motion without velocity (scene/gui/popup_menu.cpp L750 in 4.7.2). An instance outside
## the tree has no process time (node.cpp L1015-1020), so seconds is the 0.001 floor here.
func test_a_motion_carries_the_velocity_of_its_relative() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	var relative := Vector2(4, -3)
	var motion: InputEventMouseMotion = gestures.motion_event(Vector2(40, 30), relative, 0)
	var seconds: float = maxf(gestures.get_process_delta_time(), 0.001)
	assert_approx(motion.velocity.x, relative.x / seconds, "the velocity follows the relative")
	assert_approx(motion.velocity.y, relative.y / seconds, "the velocity follows the relative")
	assert_eq(motion.screen_velocity, motion.velocity, "the screen velocity equals the velocity")
	gestures.free()
	bridge.free()


func test_a_motion_that_moves_nowhere_carries_no_velocity() -> void:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	var motion: InputEventMouseMotion = gestures.motion_event(Vector2(40, 30), Vector2.ZERO, 0)
	assert_eq(motion.velocity, Vector2.ZERO, "a motion with no relative has no velocity")
	assert_eq(motion.screen_velocity, Vector2.ZERO, "and no screen velocity")
	gestures.free()
	bridge.free()


func test_with_repick_hover_on_the_owned_pointer_re_picks_every_frame() -> void:
	var rig: Array = _repick_rig(true)
	var gestures: Node = rig[0]
	assert_eq(gestures._take_repick(), true, "a re-pick is due while the pointer is owned")
	assert_eq(gestures._take_repick(), true, "and is due again the next frame")
	_free_rig(rig)


## The scene_changed handler arms the one re-pick; the test runner runs outside the tree, so it
## calls the handler itself.
func test_with_repick_hover_off_a_scene_change_arms_one_re_pick() -> void:
	var rig: Array = _repick_rig(false)
	var gestures: Node = rig[0]
	assert_eq(gestures._take_repick(), false, "no re-pick each frame with repickHover off")
	gestures._arm_repick()
	assert_eq(gestures._take_repick(), true, "an armed re-pick is due")
	assert_eq(gestures._take_repick(), false, "the re-pick used it up")
	_free_rig(rig)


func test_an_armed_re_pick_waits_for_the_gesture_and_the_held_buttons() -> void:
	var rig: Array = _repick_rig(false)
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures._arm_repick()
	bridge._gesture_playing = true
	assert_eq(gestures._take_repick(), false, "not while a gesture plays")
	bridge._gesture_playing = false
	bridge._held_mask = MOUSE_BUTTON_MASK_LEFT
	assert_eq(gestures._take_repick(), false, "not while a button is held")
	bridge._held_mask = 0
	bridge._owns_pointer = false
	assert_eq(gestures._take_repick(), false, "not when the real mouse owns the pointer")
	bridge._owns_pointer = true
	assert_eq(gestures._take_repick(), true, "due once nothing holds it back")
	_free_rig(rig)


## Viewport.gui_perform_drop_at sends NOTIFICATION_DRAG_END to every node under the root at a GUI
## drop or cancel, then re-picks at the OS cursor (viewport.cpp L2457-2459 in 4.7.2).
func test_with_repick_hover_off_a_gui_drag_end_arms_one_re_pick_only_on_an_owned_pointer() -> void:
	var rig: Array = _repick_rig(false)
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures.notification(Node.NOTIFICATION_DRAG_END)
	assert_eq(gestures._take_repick(), true, "a drag's end arms one re-pick")
	assert_eq(gestures._take_repick(), false, "the re-pick used it up")
	bridge._owns_pointer = false
	gestures.notification(Node.NOTIFICATION_DRAG_END)
	bridge._owns_pointer = true
	assert_eq(gestures._take_repick(), false, "a drag's end arms none while the mouse owns it")
	_free_rig(rig)


func test_handing_the_pointer_back_drops_an_armed_re_pick() -> void:
	var rig: Array = _repick_rig(false)
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures._arm_repick()
	gestures._on_window_input(_real_motion())
	bridge._owns_pointer = true
	assert_eq(gestures._take_repick(), false, "the hand-back dropped the armed re-pick")
	_free_rig(rig)


## The result a drag that ends without releasing records: released false, guiDragStarted as the
## motions found it, and no releasedOn or dropAccepted, nothing having been released.
func test_a_drag_that_ends_without_a_release_records_released_false() -> void:
	var rig: Array = _drag_end_rig()
	var gestures: Node = rig[0]
	gestures._end_drag(Vector2(40, 30), MOUSE_BUTTON_LEFT, false, true)
	assert_eq(
		gestures._hits,
		{"guiDragStarted": true, "released": false},
		"released false, and no releasedOn or dropAccepted"
	)
	_free_drag_end_rig(rig)


## A drag that ends without releasing sends no release, and leaves its button in the held mask.
func test_a_drag_that_ends_without_a_release_leaves_the_button_held() -> void:
	var rig: Array = _drag_end_rig()
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures._end_drag(Vector2(40, 30), MOUSE_BUTTON_LEFT, false, true)
	assert_eq(bridge._held_mask, MOUSE_BUTTON_MASK_LEFT, "the button is still held")
	assert_eq(bridge._capture.sent_events, [], "no release was sent")
	_free_drag_end_rig(rig)


## A click while the button it would press is held is refused before anything is sent: its press
## would drop the payload a drag with release false left held.
func test_a_click_while_its_button_is_held_is_refused() -> void:
	var rig: Array = _drag_end_rig()
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	var refusal: String = gestures.click({"target": {"path": "Main/DragSource"}})
	assert_eq(refusal, HELD_REFUSAL, "the click is refused with the held button's name")
	assert_eq(bridge._capture.sent_events, [], "nothing was sent")
	_free_drag_end_rig(rig)


## A drag while the button it would press is held is refused before anything is sent.
func test_a_drag_while_its_button_is_held_is_refused() -> void:
	var rig: Array = _drag_end_rig()
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	var refusal: String = gestures._play_drag(
		{"from": {"path": "Main/DragSource"}, "to": {"path": "Main/DropTarget"}}
	)
	assert_eq(refusal, HELD_REFUSAL, "the drag is refused with the held button's name")
	assert_eq(bridge._capture.sent_events, [], "nothing was sent")
	_free_drag_end_rig(rig)


## A mouse_button release after a drag that did not release lets the button go, the release
## carrying no mask.
func test_a_mouse_button_release_after_a_drag_that_did_not_release_clears_the_mask() -> void:
	var rig: Array = _drag_end_rig()
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures._end_drag(Vector2(40, 30), MOUSE_BUTTON_LEFT, false, true)
	gestures.send_button(Vector2(40, 30), MOUSE_BUTTON_LEFT, false, false)
	assert_eq(bridge._held_mask, 0, "the button is let go")
	assert_eq(bridge._capture.sent_events.size(), 1, "one release was sent")
	var release: InputEventMouseButton = bridge._capture.sent_events[0]
	assert_eq(release.button_mask, 0, "the release carries no mask")
	_free_drag_end_rig(rig)


## An input player re-picking every frame as repick_hover says, with a bridge that owns the
## pointer: [gestures, bridge].
func _repick_rig(repick_hover: bool) -> Array:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	gestures.repick_hover = repick_hover
	bridge._owns_pointer = true
	return [gestures, bridge]


func _free_rig(rig: Array) -> void:
	for node: Node in rig:
		node.free()


## A mouse motion without the injected mark, as the real mouse sends one.
func _real_motion() -> InputEventMouseMotion:
	var motion := InputEventMouseMotion.new()
	motion.position = Vector2(50, 30)
	return motion


## An input player with a bridge, a real fired listener and a capture recording every event it
## dispatches, its held mask carrying LEFT as a drag that ends without releasing leaves it:
## [gestures, bridge].
func _drag_end_rig() -> Array:
	var bridge: Node = _bridge_script.new()
	var gestures: Node = _input_script.new()
	gestures.bridge = bridge
	gestures._fired = load_bridge_script("godot_mcp_fired.gd").new()
	gestures._fired.bridge = bridge
	bridge._capture = _recording_capture()
	bridge._held_mask = MOUSE_BUTTON_MASK_LEFT
	return [gestures, bridge]


func _free_drag_end_rig(rig: Array) -> void:
	var gestures: Node = rig[0]
	var bridge: Node = rig[1]
	gestures._fired.free()
	bridge._capture.free()
	_free_rig(rig)


## A capture recording every event dispatch sends in sent_events.
func _recording_capture() -> Node:
	var script := GDScript.new()
	script.source_code = (
		"extends Node\n\nvar sent_events: Array = []\n\n\n"
		+ "func sent(event: InputEvent) -> void:\n\tsent_events.append(event)\n"
	)
	script.reload()
	return script.new()
