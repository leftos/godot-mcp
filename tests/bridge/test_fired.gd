extends "res://gd_test.gd"
## The fired-signal listener (bridge/godot_mcp_fired.gd): which nodes and signals a hit chain
## listens on, the kept names' cache, the raw entries it records, leftTree, the entry cap and the
## physics stamp. The nodes sit under the test run's root, which is in no tree yet and runs no
## frame, so every signal is emitted by hand, the paths are built from the nodes' names and the
## physics tick count comes from the test.

const BRIDGE_SOURCE := "extends Node\n\nvar _json: GDScript\n"
## A Control with a public and an underscored signal, and outer and inner, which a game handler
## connects so that outer emits inner.
const SIGNALS_SOURCE := (
	"extends Control\n\nsignal public_news(value: int)\nsignal _private_news\n"
	+ "signal outer\nsignal inner\n"
)

var _fired_script: GDScript = load_bridge_script("godot_mcp_fired.gd")


func test_noise_listed_signals_are_not_connected() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var button: Button = _add(Button.new(), _root(), "Pressable")
	fired.listen(button)
	for signal_name: String in _fired_script.NOISE:
		assert_true(not _connected(button, signal_name, fired), "%s is noise" % signal_name)
	assert_true(_connected(button, "pressed", fired), "pressed is listened on")
	assert_true(_connected(button, "focus_entered", fired), "focus_entered is kept")
	assert_true(not _connected(button, "tree_exited", fired), "a tree signal is not listed")
	_free(rig, [button])


func test_a_signal_whose_name_starts_with_an_underscore_is_left_out() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var node: Control = _add(_compile(SIGNALS_SOURCE).new(), _root(), "Newsy")
	var kept: PackedStringArray = fired.kept_names(node)
	assert_true(kept.has("public_news"), "a script signal is kept")
	assert_true(not kept.has("_private_news"), "an underscored one is not")
	fired.listen(node)
	assert_true(not _connected(node, "_private_news", fired), "nothing listens on _private_news")
	_free(rig, [node])


func test_kept_names_are_cached_per_class_and_script() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var first: Button = _add(Button.new(), _root(), "First")
	var second: Button = _add(Button.new(), _root(), "Second")
	var scripted: Button = _add(
		_compile("extends Button\n\nsignal chosen\n").new(), _root(), "Scripted"
	)
	fired.listen(first)
	fired.listen(second)
	assert_eq(fired._kept_names.size(), 1, "two plain Buttons share one entry")
	fired.listen(scripted)
	assert_eq(fired._kept_names.size(), 2, "a Button with a script has its own")
	assert_true(fired.kept_names(scripted).has("chosen"), "the scripted Button's entry has chosen")
	assert_true(not fired.kept_names(first).has("chosen"), "the plain Buttons' entry has not")
	_free(rig, [first, second, scripted])


func test_the_chain_stops_at_the_root_child_and_adds_the_root_for_gui_focus_changed() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var screen: Control = _add(Control.new(), _root(), "Screen")
	var panel: Panel = _add(Panel.new(), screen, "Panel")
	var button: Button = _add(Button.new(), panel, "Ready")
	assert_eq(
		fired.chain_of(button), [button, panel, screen], "bottom first, up to the root's child"
	)
	fired.listen(button)
	assert_true(_connected(screen, "visibility_changed", fired), "the root's child is listened on")
	assert_true(_connected(_root(), "gui_focus_changed", fired), "the root's gui_focus_changed")
	assert_true(
		not _connected(_root(), "visibility_changed", fired), "and nothing else of the root"
	)
	var report: Dictionary = fired.finish()
	assert_eq(report["listenedOn"], ["/root/Screen/Panel/Ready"], "the chain's bottom")
	assert_true(not _connected(_root(), "gui_focus_changed", fired), "finish lets go of the root")
	assert_true(not _connected(button, "pressed", fired), "and of the chain")
	_free(rig, [screen])


func test_bridge_nodes_are_not_listened_on() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var inner: Button = _add(Button.new(), rig["bridge"], "BridgeButton")
	fired.listen(inner)
	assert_true(not _connected(inner, "pressed", fired), "a bridge node's signals")
	assert_eq(fired.finish()["listenedOn"], [], "no chain was listened on")
	_free(rig, [])


func test_a_popups_internal_nodes_are_not_listened_on_but_the_popup_is() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var option: OptionButton = _add(OptionButton.new(), _root(), "Choice")
	option.add_item("Alpha")
	var popup: PopupMenu = option.get_popup()
	var internal: Control = popup.get_child(0, true)
	fired.listen(internal)
	assert_true(not _connected(internal, "visibility_changed", fired), "the popup's internals")
	assert_true(_connected(popup, "index_pressed", fired), "the PopupMenu itself")
	assert_true(_connected(option, "item_selected", fired), "the OptionButton above it")
	var listened_on: Array = fired.finish()["listenedOn"]
	var popup_path: String = "/root/Choice/%s" % popup.name
	assert_eq(listened_on, [popup_path], "the chain's bottom is the PopupMenu")
	_free(rig, [option])


func test_a_button_groups_other_buttons_are_listened_on() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var group := ButtonGroup.new()
	var mine: Button = _add(_grouped(group), _root(), "Mine")
	var other: Button = _add(_grouped(group), _root(), "Other")
	fired.listen(mine)
	assert_true(_connected(other, "toggled", fired), "the group's other button")
	assert_eq(fired.finish()["listenedOn"], ["/root/Mine"], "which is no chain of its own")
	_free(rig, [mine, other])


func test_left_tree_keeps_the_topmost_node() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var screen: Control = _add(Control.new(), _root(), "Lobby")
	var panel: Panel = _add(Panel.new(), screen, "Panel")
	var button: Button = _add(Button.new(), panel, "Ready")
	fired.listen(button)
	# The run's root is in no tree, so the exit a screen swap makes is played by hand, children
	# first as Node._propagate_exit_tree goes.
	for node: Node in [button, panel, screen]:
		node.tree_exiting.emit()
	var report: Dictionary = fired.finish()
	assert_eq(report.get("leftTree"), ["/root/Lobby"], "the topmost node that left")
	assert_eq(report["fired"], [], "its tree signals are not listed")
	_free(rig, [screen])


func test_the_200_entry_cap_counts_the_rest() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var node: Control = _add(_compile(SIGNALS_SOURCE).new(), _root(), "Chatty")
	fired.listen(node)
	for index in 205:
		node.emit_signal("public_news", index)
	var report: Dictionary = fired.finish()
	assert_eq(report["fired"].size(), 200, "200 entries kept")
	assert_eq(report["fired"][199][3], [199], "the first 200, in order")
	assert_eq(report.get("firedOverflow"), 5, "the rest counted")
	_free(rig, [node])


func test_fired_overflow_appears_only_past_200_entries() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var node: Control = _add(_compile(SIGNALS_SOURCE).new(), _root(), "Exact")
	fired.listen(node)
	for index in 200:
		node.emit_signal("public_news", index)
	var report: Dictionary = fired.finish()
	assert_eq(report["fired"].size(), 200, "200 entries kept")
	assert_true(not report.has("firedOverflow"), "no overflow at exactly 200")
	_free(rig, [node])


func test_input_event_motion_is_left_out_and_a_button_event_kept() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var area: Area2D = _add(Area2D.new(), _root(), "Area")
	fired.listen(area)
	var press := InputEventMouseButton.new()
	press.button_index = MOUSE_BUTTON_LEFT
	press.pressed = true
	area.input_event.emit(_root(), InputEventMouseMotion.new(), 0)
	area.input_event.emit(_root(), press, 0)
	var entries: Array = fired.finish()["fired"]
	assert_eq(entries.size(), 1, "the button event alone")
	assert_eq(entries[0][2], "input_event", "as input_event")
	assert_eq(entries[0][3].size(), 3, "with its three arguments")
	_free(rig, [area])


func test_listeners_count_a_games_bound_callable_and_leave_out_the_bridges() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var button: Button = _add(Button.new(), _root(), "Counted")
	var label: Label = _add(Label.new(), _root(), "Shown")
	button.pressed.connect(func() -> void: pass)
	button.pressed.connect(label.hide)
	button.pressed.connect(label.set_visible.bind(true))
	button.pressed.connect(rig["bridge"].update_configuration_warnings)
	fired.listen(button)
	button.pressed.emit()
	var entries: Array = fired.finish()["fired"]
	assert_eq(entries[0][4], 3, "the game's lambda, plain callable and bound callable")
	_free(rig, [button, label])


func test_listeners_leave_out_engine_wiring_on_a_scripted_node() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var option: OptionButton = _add(
		_compile("extends OptionButton\n").new(), _root(), "ScriptedChoice"
	)
	option.add_item("Alpha")
	var popup: PopupMenu = option.get_popup()
	fired.listen(popup)
	popup.index_pressed.emit(0)
	var entries: Array = fired.finish()["fired"]
	var pressed: Array = entries.filter(
		func(entry: Array) -> bool: return entry[2] == "index_pressed"
	)
	assert_eq(pressed.size(), 1, "index_pressed is listed")
	assert_eq(pressed[0][4], 0, "the OptionButton's callable_mp is engine wiring, script or not")
	_free(rig, [option])


func test_a_bridge_script_lambda_is_not_counted() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var maker := GDScript.new()
	maker.source_code = (
		"extends RefCounted\n\n\nstatic func make() -> Callable:\n"
		+ "\treturn func() -> void: pass\n"
	)
	maker.resource_path = _fired_script.resource_path.get_base_dir().path_join("lambda_maker.gd")
	maker.reload()
	var button: Button = _add(Button.new(), _root(), "Probed")
	button.pressed.connect(maker.make())
	button.pressed.connect(func() -> void: pass)
	fired.listen(button)
	button.pressed.emit()
	var entries: Array = fired.finish()["fired"]
	assert_eq(entries[0][4], 1, "the game's lambda, not the one a bridge script made")
	_free(rig, [button])


func test_a_nested_emission_is_listed_before_its_outer_signal() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var node: Control = _add(_compile(SIGNALS_SOURCE).new(), _root(), "Nested")
	node.connect("outer", func() -> void: node.emit_signal("inner"))
	fired.listen(node)
	node.emit_signal("outer")
	var names: Array = fired.finish()["fired"].map(func(entry: Array) -> String: return entry[2])
	assert_eq(names, ["inner", "outer"], "inner, emitted inside outer's game handler, first")
	_free(rig, [node])


func test_physics_wait_is_due_only_when_no_tick_ran_since_the_last_button_event() -> void:
	var fired: Node = _fired_script.new()
	var ticks: Array = [10]
	fired.physics_frames = func() -> int: return ticks[0]
	fired.begin(false)
	assert_true(not fired.physics_wait_due(), "no button event sent")
	fired.stamp_button()
	assert_true(fired.physics_wait_due(), "no tick since the button event")
	ticks[0] = 11
	assert_true(not fired.physics_wait_due(), "a tick ran since")
	fired.stamp_button()
	assert_true(fired.physics_wait_due(), "a later button event, no tick since it")
	fired.begin(false)
	assert_true(not fired.physics_wait_due(), "a new gesture forgets the stamp")
	fired.free()


func test_a_raw_entry_is_frame_node_signal_args_and_listeners() -> void:
	var rig: Dictionary = _rig()
	var fired: Node = rig["fired"]
	var button: Button = _add(Button.new(), _root(), "Toggle")
	fired.listen(button)
	button.toggled.emit(true)
	var report: Dictionary = fired.finish()
	assert_eq(report["fired"], [[0, "/root/Toggle", "toggled", [true], 0]], "the five elements")
	_free(rig, [button])


## A fake bridge under the root holding the JSON module, and a listener begun on a gesture that
## lists.
func _rig() -> Dictionary:
	var bridge: Node = _add(_compile(BRIDGE_SOURCE).new(), _root(), "FakeBridge")
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	var fired: Node = _fired_script.new()
	fired.bridge = bridge
	fired.begin(true)
	return {"bridge": bridge, "fired": fired}


## Ends the rig's gesture and frees it with the nodes the test added under the root.
func _free(rig: Dictionary, nodes: Array) -> void:
	rig["fired"].finish()
	for node: Node in nodes:
		node.free()
	rig["fired"].free()
	rig["bridge"].free()


func _root() -> Window:
	return (Engine.get_main_loop() as SceneTree).root


func _add(node: Node, parent: Node, node_name: String) -> Node:
	node.name = node_name
	parent.add_child(node)
	return node


func _grouped(group: ButtonGroup) -> Button:
	var button := Button.new()
	button.toggle_mode = true
	button.button_group = group
	return button


func _compile(source: String) -> GDScript:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script


## Whether listener has a connection on node's signal.
static func _connected(node: Object, signal_name: String, listener: Object) -> bool:
	for connection: Dictionary in node.get_signal_connection_list(signal_name):
		if (connection["callable"] as Callable).get_object() == listener:
			return true
	return false
