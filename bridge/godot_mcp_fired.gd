extends Node
## The godot-mcp bridge's fired-signal listener, the input player's Fired child: lists the signals
## a click or a mouse_button set off on its hit chain, and keeps the physics stamp every gesture's
## settle wait reads. A begun gesture connects one variadic lambda per (node, kept signal) on each
## chain listen is given, records [frame, node, signal, args, listeners] as they emit and lets go
## of them all at finish. An emission calls its slots in connection order, synchronously, so the
## list is in handler order: a signal a game handler emits comes before the one it answered
## (core/object/object.cpp L1214-1218, L1288-1296 in 4.7.2).

## Signals layout, drawing or the pointer's own motion emit on every gesture, never listened on.
const NOISE := [
	"draw",
	"item_rect_changed",
	"gui_input",
	"mouse_entered",
	"mouse_exited",
	"mouse_shape_entered",
	"mouse_shape_exited",
	"resized",
	"minimum_size_changed",
	"maximum_size_changed",
	"size_flags_changed",
	"theme_changed",
	"pre_sort_children",
	"sort_children",
	"child_order_changed",
	"property_list_changed",
	"script_changed",
	"renamed",
	"ready",
	"replacing_by",
	"editor_description_changed",
	"editor_state_changed",
	"window_input",
	"nonclient_window_input",
	"dpi_changed",
	"title_changed",
	"titlebar_changed",
	"size_changed",
	"output_max_linear_value_changed",
]
## The tree signals, never listed per emission: a chain node's tree_exiting feeds leftTree.
const TREE_SIGNALS := [
	"tree_entered", "tree_exiting", "tree_exited", "child_entered_tree", "child_exiting_tree"
]
## The raw entries a gesture keeps; the ones past it are counted as firedOverflow.
const CAP := 200
## What a C# [Signal]'s one connection reads as: its += handlers live on a C# delegate behind one
## EventSignalCallable (modules/mono/signal_awaiter_utils.cpp L154-160 in 4.7.2).
const CSHARP_SIGNAL := "::EventSignalMiddleman::"
const DISABLED_WARNING := "%s is disabled, so the press set off none of its own signals"
const PAUSED_WARNING := (
	"the game is paused and %s cannot process, so it received no input; "
	+ "resume, or step with frame_control"
)

## The bridge (godot_mcp_bridge.gd), set before this node enters the tree: its nodes are never
## listened on or counted, and its JSON module converts the arguments.
var bridge: Node
## The physics ticks run so far: Engine.get_physics_frames, or a test's stand-in.
var physics_frames: Callable = Callable(Engine, "get_physics_frames")

## Whether the playing gesture lists what it set off (begin), so listen connects.
var _begun: bool = false
## The physics tick of the gesture's last mouse button or wheel event; -1 before one.
var _physics_stamp: int = -1
## The process frame of the gesture's first mouse button or wheel event, which frame counts
## from; -1 before one.
var _first_event_frame: int = -1
## The raw entries, [frame, node, signal, args, listeners], in record order.
var _entries: Array = []
## The emissions past CAP.
var _overflow: int = 0
## The instance ids of the nodes listened on in this gesture (the root's for its
## gui_focus_changed).
var _listened: Dictionary = {}
## The path of each chain's bottom node, in listen order.
var _listened_on: Array = []
## The paths of the listened nodes whose tree_exiting fired.
var _left: Array = []
## Every connection made, [node, signal, callable], for finish to let go of.
var _connections: Array = []
## The kept signal names per [class, script].
var _kept_names: Dictionary = {}
## The Control the last press landed on (null over none), kept until the next press: a separate
## release listens on it.
var _pressed: Variant = null


## Starts a gesture: lets go of anything still connected, forgets the last gesture's button
## stamp and entries and, when lists (a click or a mouse_button), listens until finish.
func begin(lists: bool) -> void:
	_disconnect_all()
	_begun = lists
	_physics_stamp = -1
	_first_event_frame = -1
	_entries = []
	_overflow = 0
	_listened = {}
	_listened_on = []
	_left = []


## Stamps a mouse button or wheel event about to be sent: the physics tick it falls in, and for
## the gesture's first, the process frame entries count from.
func stamp_button() -> void:
	_physics_stamp = physics_frames.call()
	if _first_event_frame < 0:
		_first_event_frame = Engine.get_process_frames()


## Whether the gesture sent a mouse button or wheel event and no physics tick has run since:
## physics picking delivers it to a world node's input_event on the next tick, right after
## physics_frame is emitted (scene/main/scene_tree.cpp L649-652 in 4.7.2), so the gesture awaits
## one before its settle frames.
func physics_wait_due() -> bool:
	return _physics_stamp >= 0 and physics_frames.call() == _physics_stamp


## Before a mouse button event is sent: a release listens on the chain of the Control the last
## press landed on, which Godot sends it to wherever the pointer is (scene/main/viewport.cpp
## L2015-2040 in 4.7.2).
func before_button(pressed: bool) -> void:
	if not pressed:
		listen(_pressed)


## After a mouse button event: listens on the Control now under the pointer (a popup the press
## opened joins), and a press keeps it as the Control the next release goes to.
func after_button(hovered: Control, pressed: bool) -> void:
	listen(hovered)
	if pressed:
		_pressed = hovered


## Listens on node's hit chain while a gesture is begun (chain_of): each kept signal and
## tree_exiting of every chain node not yet listened on, and the root's gui_focus_changed, which a
## press's focus grab emits (scene/main/viewport.cpp L1984, L2003 in 4.7.2); notes the chain's
## bottom in listenedOn. Null or a freed node adds nothing.
func listen(node: Variant) -> void:
	if not _begun or not is_instance_valid(node):
		return
	var chain: Array[Node] = chain_of(node)
	if chain.is_empty():
		return
	for link: Node in chain:
		_listen_node(link)
	_listen_root(chain[-1])
	var bottom: String = path_of(chain[0])
	if not _listened_on.has(bottom):
		_listened_on.append(bottom)


## node and its ancestors up to the root's child (the child of the parentless top, /root in the
## game), bottom first, less the bridge's own nodes and a PopupMenu's internal Controls
## (scene/gui/popup_menu.cpp L3785-3818 in 4.7.2); the menu itself stays, its index_pressed and
## id_pressed are the item's.
func chain_of(node: Node) -> Array[Node]:
	var chain: Array[Node] = []
	var link: Node = node
	while link != null and link.get_parent() != null:
		if not _is_bridges(link) and not _in_popup_menu(link):
			chain.append(link)
		link = link.get_parent()
	return chain


## node's path in the tree; out of it (a test's nodes), its ancestors' names joined the same way.
static func path_of(node: Node) -> String:
	if node.is_inside_tree():
		return str(node.get_path())
	var names := PackedStringArray()
	var link: Node = node
	while link != null:
		names.insert(0, str(link.name))
		link = link.get_parent()
	return "/" + "/".join(names)


## The signals listened on for node's class and script: every one get_signal_list gives (script
## signals included) less NOISE, TREE_SIGNALS and the names that start with _. Cached per
## [class, script], since reading the list is most of a listen's cost.
func kept_names(node: Node) -> PackedStringArray:
	var key: Array = [node.get_class(), node.get_script()]
	if not _kept_names.has(key):
		var names := PackedStringArray()
		for entry: Dictionary in node.get_signal_list():
			var signal_name: String = entry["name"]
			if _kept(signal_name) and not names.has(signal_name):
				names.append(signal_name)
		_kept_names[key] = names
	return _kept_names[key]


## The Godot connections node's signal has besides the bridge's own and the engine's wiring (a
## custom callable, such as a callable_mp, whose object has no script), or null for a C# [Signal]
## (CSHARP_SIGNAL), whose handlers Godot cannot see.
func listeners_of(node: Object, signal_name: String) -> Variant:
	var count: int = 0
	for connection: Dictionary in node.get_signal_connection_list(signal_name):
		var callable: Callable = connection["callable"]
		if str(callable).contains(CSHARP_SIGNAL):
			return null
		if not _ignored(callable):
			count += 1
	return count


## Ends the gesture: lets go of every connection and, when it was begun, answers {fired,
## listenedOn}, with firedOverflow (the entries past CAP), leftTree (the topmost listened nodes
## that left the tree) and warning when they apply; {} when it was not.
func finish() -> Dictionary:
	_disconnect_all()
	if not _begun:
		return {}
	_begun = false
	var report: Dictionary = {"fired": _entries, "listenedOn": _listened_on}
	if _overflow > 0:
		report["firedOverflow"] = _overflow
	var left: Array = topmost(_left)
	if not left.is_empty():
		report["leftTree"] = left
	var warning: String = _warning()
	if not warning.is_empty():
		report["warning"] = warning
	return report


## The paths in paths no other one is under, in their first order, each once.
static func topmost(paths: Array) -> Array:
	var kept: Array = []
	for path: String in paths:
		if not kept.has(path) and not _under_any(path, paths):
			kept.append(path)
	return kept


static func _under_any(path: String, paths: Array) -> bool:
	for other: String in paths:
		if path.begins_with(other + "/"):
			return true
	return false


static func _kept(signal_name: String) -> bool:
	return not (
		NOISE.has(signal_name) or TREE_SIGNALS.has(signal_name) or signal_name.begins_with("_")
	)


## Whether node is no Viewport and the nearest Viewport above it is a PopupMenu, whose Controls
## are all its own internals.
static func _in_popup_menu(node: Node) -> bool:
	if node is Viewport:
		return false
	var link: Node = node.get_parent()
	while link != null and not (link is Viewport):
		link = link.get_parent()
	return link is PopupMenu


## Whether object is the bridge or one of its nodes.
func _is_bridges(object: Object) -> bool:
	var node := object as Node
	return node != null and bridge != null and (node == bridge or bridge.is_ancestor_of(node))


## Whether a connection is the bridge's own (a bridge node's, or a bridge script's lambda that
## uses no self, which names its Script as its object, modules/gdscript/gdscript_lambda_callable.cpp
## L75-77 in 4.7.2) or the engine's wiring (engine_wiring), which listeners leaves out.
func _ignored(callable: Callable) -> bool:
	var target: Object = callable.get_object()
	if target == self or _is_bridges(target) or _is_bridge_script(target):
		return true
	return callable.is_custom() and engine_wiring(str(callable))


## Whether a custom callable's text names engine wiring: a callable_mp reads Class::method
## (core/object/callable_mp.h L127 in 4.7.2), here an engine class whose method is not bound, so
## no script could have connected it; a bound callable reads as the one it wraps
## (core/variant/callable_bind.cpp L38-40), so a game's .bind() of a bound method counts. A build
## without DEBUG_ENABLED gives a callable_mp no text (callable_mp.h L71-74), read as wiring too.
static func engine_wiring(text: String) -> bool:
	if text.is_empty():
		return true
	var parts: PackedStringArray = text.split("::")
	return (
		parts.size() == 2
		and ClassDB.class_exists(parts[0])
		and not ClassDB.class_has_method(parts[0], parts[1])
	)


## Whether target is a script in the bridge's own folder, which this script shares.
func _is_bridge_script(target: Object) -> bool:
	var script := target as Script
	if script == null:
		return false
	var bridge_dir: String = (get_script() as Script).resource_path.get_base_dir()
	return script.resource_path.get_base_dir() == bridge_dir


## Connects a recording lambda to each of node's kept signals and a leftTree note to its
## tree_exiting, once per gesture; a BaseButton brings its ButtonGroup's other buttons.
func _listen_node(node: Node) -> void:
	var id: int = node.get_instance_id()
	if _listened.has(id):
		return
	_listened[id] = true
	var path: String = path_of(node)
	for signal_name: String in kept_names(node):
		var on_signal := func(...args: Array) -> void: _record(node, path, signal_name, args)
		_connect(node, signal_name, on_signal)
	_connect(node, "tree_exiting", func() -> void: _note_left(path))
	if node is BaseButton:
		_listen_group(node as BaseButton)


## Listens on the other buttons of button's ButtonGroup, which a toggle unpresses, each emitting
## toggled(false) (scene/gui/base_button.cpp L50-56 in 4.7.2).
func _listen_group(button: BaseButton) -> void:
	if button.button_group == null:
		return
	for other: BaseButton in button.button_group.get_buttons():
		if not _is_bridges(other):
			_listen_node(other)


## Connects to the gui_focus_changed of the root above a chain's top alone, once per gesture.
func _listen_root(top: Node) -> void:
	var root: Node = top.get_parent()
	while root.get_parent() != null:
		root = root.get_parent()
	var id: int = root.get_instance_id()
	if _listened.has(id):
		return
	_listened[id] = true
	var path: String = path_of(root)
	var on_focus := func(...args: Array) -> void: _record(root, path, "gui_focus_changed", args)
	_connect(root, "gui_focus_changed", on_focus)


## Connects callable to node's signal when node has it (a user signal another node of the class
## added is not on this one) and keeps the connection for finish.
func _connect(node: Node, signal_name: String, callable: Callable) -> void:
	if node.has_signal(signal_name):
		node.connect(signal_name, callable)
		_connections.append([node, signal_name, callable])


## Records an emission while the gesture is begun: [frame, node, signal, args, listeners], the
## arguments converted to JSON now, up to CAP entries, then only counted. An input_event of a
## mouse motion is left out: the aim's own motion reaches a world node too.
func _record(node: Object, path: String, signal_name: String, args: Array) -> void:
	if not _begun or (signal_name == "input_event" and _holds_motion(args)):
		return
	if _entries.size() >= CAP:
		_overflow += 1
		return
	var frame: int = (
		maxi(0, Engine.get_process_frames() - _first_event_frame) if _first_event_frame >= 0 else 0
	)
	var json_args: Variant = bridge._json.to_json(args)
	_entries.append([frame, path, signal_name, json_args, listeners_of(node, signal_name)])


static func _holds_motion(args: Array) -> bool:
	for arg: Variant in args:
		if arg is InputEventMouseMotion:
			return true
	return false


func _note_left(path: String) -> void:
	if _begun:
		_left.append(path)


## The warning a press explains its fired list with, whatever fired holds: the pressed node a
## disabled BaseButton, which ignores all input (scene/gui/base_button.cpp L62 in 4.7.2), or, in
## a paused tree, one that cannot process, so it was sent neither the press nor the release
## (scene/main/viewport.cpp L2002-2003, L2015-2040), though the press still grabbed its focus
## (focus_entered, the root's gui_focus_changed). A node that cannot process for another reason
## (PROCESS_MODE_DISABLED, a suspended tree, scene/main/node.cpp L907-928) gets no paused
## warning. "" when neither applies.
func _warning() -> String:
	var pressed: Node = _pressed_node()
	if pressed == null:
		return ""
	var path: String = str(pressed.get_path())
	var parts := PackedStringArray()
	if pressed is BaseButton and (pressed as BaseButton).disabled:
		parts.append(DISABLED_WARNING % path)
	if pressed.get_tree().paused and not pressed.can_process():
		parts.append(PAUSED_WARNING % path)
	return " ".join(parts)


## The node the press landed on, a PopupMenu's internal Control answering as the menu itself, when
## the gesture sent a button event and it is still in the tree; else null.
func _pressed_node() -> Node:
	if _first_event_frame < 0 or not is_instance_valid(_pressed):
		return null
	var node: Node = _pressed
	while _in_popup_menu(node):
		node = node.get_parent()
	return node if node.is_inside_tree() else null


## Disconnects every connection whose node is still alive (a freed emitter's slots went with it).
func _disconnect_all() -> void:
	for connection: Array in _connections:
		var node: Variant = connection[0]
		if is_instance_valid(node) and (node as Node).is_connected(connection[1], connection[2]):
			(node as Node).disconnect(connection[1], connection[2])
	_connections = []
