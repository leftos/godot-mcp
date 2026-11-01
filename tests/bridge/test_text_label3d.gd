# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## A Label3D's shown text (bridge/godot_mcp_shown_text.gd label3d_text) and a text check's value
## and ambiguity listing for a Label3D entry (bridge/godot_mcp_text_targets.gd shown_value, placed
## and ambiguity_refusal, through godot_mcp_conditions.gd text_answer), over entries built by hand:
## the walk of a live tree, with its Label3D visibility and under, is covered by TimeTests, since
## nothing here is in a scene tree and so no node has a path. A Label3D shows its text as a Label
## does, upper case included (scene/3d/label_3d.cpp L486 in 4.7.2), and its entry carries no rect
## but, when a camera sees it, the point it projects to.

## A resolver stand-in for hand-built entries: places every Control at 10,20,120,40 and frames
## rects and points as the real script does, and projects a Label3D to the point seeing puts on
## screen, (360, 180), or to NO_CAMERA's text when no camera sees it.
const TARGETS_SOURCE := (
	"extends Node\n\nvar resolver: GDScript\nvar bridge: Node\nvar seeing: bool = false\n\n\n"
	+ "func rect_text(rect: Rect2) -> String:\n\treturn resolver.rect_text(rect)\n\n\n"
	+ "func point_text(point: Vector2) -> String:\n\treturn resolver.point_text(point)\n\n\n"
	+ "func viewport_rect(_control: Control) -> Rect2:\n\treturn Rect2(10, 20, 120, 40)\n\n\n"
	+ "func _projected(_node: Node3D, _offset: Vector3) -> Variant:\n"
	+ '\treturn Vector2(360, 180) if seeing else "no camera"'
)
## The point a Label3D a camera sees projects to, as _json writes a Vector2.
const SEEN_POINT := {"x": 360.0, "y": 180.0}

var _shown_text: GDScript = load_bridge_script("godot_mcp_shown_text.gd")
var _text_targets: GDScript = load_bridge_script("godot_mcp_text_targets.gd")
var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")


func test_label3d_text_reads_and_upper_cases_as_a_label_does() -> void:
	var label := Label3D.new()
	label.text = "Gold: 12"
	assert_eq(_shown_text.label3d_text(label), "Gold: 12", "as written")
	label.text = "Straße"
	assert_eq(_shown_text.label3d_text(label), "Straße", "uppercase off: as written")
	label.uppercase = true
	assert_eq(
		_shown_text.label3d_text(label), "STRASSE", "uppercase on: the text server's upper case"
	)
	label.free()


func test_a_text_appear_is_met_by_a_label3d_entry_with_no_rect() -> void:
	var targets: Node = _targets()
	var entries: Array[Dictionary] = [_label3d_entry("/root/Room/Sign", "Sign", "Gold: 12")]
	var matches: Array[Dictionary] = _text_targets.matching(entries, "Gold: 12", "exact")
	assert_eq(
		_conditions_script.text_answer("Gold: 12", matches, true, targets),
		[true, {"path": "/root/Room/Sign", "shown": "Gold: 12"}],
		"met, its path and text, with no rect and no point while no camera sees it"
	)
	assert_eq(
		_conditions_script.text_answer("Gold: 12", matches, false, targets),
		[false, {"path": "/root/Room/Sign", "shown": "Gold: 12"}],
		"a leave holds while it shows, the same value"
	)
	_free_targets(targets)


func test_a_label3d_a_camera_sees_carries_the_point_it_projects_to() -> void:
	var targets: Node = _targets(true)
	var entries: Array[Dictionary] = [_label3d_entry("/root/Room/Sign", "Sign", "Gold: 12")]
	var matches: Array[Dictionary] = _text_targets.matching(entries, "Gold: 12", "exact")
	assert_eq(
		_conditions_script.text_answer("Gold: 12", matches, true, targets),
		[true, {"path": "/root/Room/Sign", "shown": "Gold: 12", "point": SEEN_POINT}],
		"met, with the point the camera projects it to"
	)
	_free_targets(targets)


func test_a_control_and_a_label3d_showing_a_text_are_listed_together() -> void:
	var targets: Node = _targets()
	var matches: Array[Dictionary] = [
		{"control": null, "path": "/root/Menu/Go", "name": "Go", "shown": "Ready"},
		_label3d_entry("/root/Room/Sign", "Sign", "Ready"),
	]
	assert_eq(
		_conditions_script.text_answer("Ready", matches, true, targets),
		[
			false,
			null,
			(
				"'Ready' is shown by 2 Controls and Label3Ds: /root/Menu/Go at 10,20,120,40, "
				+ "/root/Room/Sign; narrow it with under."
			)
		],
		"the Control at its rect, the Label3D no camera sees by its path alone"
	)
	_free_targets(targets)


func test_label3ds_a_camera_sees_are_listed_at_their_points() -> void:
	var targets: Node = _targets(true)
	var matches: Array[Dictionary] = [
		_label3d_entry("/root/Room/Sign", "Sign", "Ready"),
		_label3d_entry("/root/Room/Board", "Board", "Ready"),
	]
	assert_eq(
		_conditions_script.text_answer("Ready", matches, true, targets),
		[
			false,
			null,
			(
				"'Ready' is shown by 2 Controls and Label3Ds: /root/Room/Sign at (360, 180), "
				+ "/root/Room/Board at (360, 180); narrow it with under."
			)
		],
		"each at the point its camera projects it to"
	)
	_free_targets(targets)


func test_two_label3ds_alone_are_listed_as_label3ds() -> void:
	var targets: Node = _targets()
	var matches: Array[Dictionary] = [
		_label3d_entry("/root/Room/Sign", "Sign", "Ready"),
		_label3d_entry("/root/Room/Board", "Board", "Ready"),
	]
	assert_eq(
		_conditions_script.text_answer("Ready", matches, true, targets),
		[
			false,
			null,
			(
				"'Ready' is shown by 2 Controls and Label3Ds: /root/Room/Sign, "
				+ "/root/Room/Board; narrow it with under."
			)
		],
		"no Control among them, and no camera sees either: both by path, named Label3Ds"
	)
	_free_targets(targets)


## A resolver stand-in holding the real text targets script for its framing, and a bridge holding
## the JSON module; seeing makes its projection land on screen.
func _targets(seeing: bool = false) -> Node:
	var targets: Node = _compile(TARGETS_SOURCE)
	targets.resolver = load_bridge_script("godot_mcp_targets.gd")
	targets.seeing = seeing
	targets.bridge = _compile("extends Node\n\nvar _json: GDScript\n")
	targets.bridge._json = load_bridge_script("godot_mcp_json.gd")
	return targets


func _free_targets(targets: Node) -> void:
	targets.bridge.free()
	targets.free()


## A Label3D text entry at path showing shown, its node left out: a resolver stand-in projects it.
static func _label3d_entry(path: String, node_name: String, shown: String) -> Dictionary:
	return {"label3d": null, "path": path, "name": node_name, "shown": shown}


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
