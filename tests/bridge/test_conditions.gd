# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## The conditions' pure logic (bridge/godot_mcp_conditions.gd): wait_for's condition kinds,
## expression parsing and its count of failed checks, and a text check's answer over entries
## built by hand. The node is never added to the tree, and the tests run before the root enters it
## (SceneTree::initialize calls _initialize first, scene/main/scene_tree.cpp L589-590 in 4.7.2),
## so only functions that do not need it are called; a text check's walk of a live tree (hidden
## Controls, ones being freed, under) is covered by TimeTests.

## The hint a refusal carries when the source reaches for GDScript syntax Expression has none of.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NOT_GDSCRIPT_HINT := "Godot's Expression is not GDScript: it has no lambdas (func), no if/else, no is or as, no not in (write not (a in b)), and no statements; it has calls, indexing, literals and operators such as and, or, not, in, ==, !=, <, <=, >, >=, +, -, *, / and %."
## A guard Expression does not short-circuit: and evaluates both sides, so get_child(0) runs on a
## node with no children and raises the engine's index error.
const GUARDED_EXPRESSION := "node.get_child_count() > 0 and node.get_child(0).modulate.r > 1.9"
## How the bridge pushes an expression's failure to the error feed.
const FAILURE_PREFIX := "godot-mcp wait_for: the expression failed: "
## The game script on the expression rig's Holder: shout() raises a game error and answers true.
# gdformat joins one string split over lines back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const HOLDER_SOURCE := "extends Node\n\n\nfunc shout() -> bool:\n\tpush_error('holder shout')\n\treturn true\n"

## The rect a text check's value carries for a Control _placing_targets places, as get_ui_elements
## writes a rect.
const SHOWN_RECT := {"x": 10.0, "y": 20.0, "width": 120.0, "height": 40.0}

## Why a settled check fails on Plain, a bare Node, named without a property.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const PLAIN_NO_PLACEMENT := "'Plain' is a Node, which has no rect or transform to watch; name a property with stable."

## The refusal _under_rig's resolver gives for an under several nodes have.
const SHARED_UNDER := "'Lobby' names 2 nodes"

## A target resolver placing every Control at 10,20,120,40, its rect_text the real one.
# gdlint: ignore=max-line-length
const PLACING_TARGETS_SOURCE := "extends Node\n\nvar bridge: Node\nvar resolver: GDScript\n\n\nfunc viewport_rect(_control: Control) -> Rect2:\n\treturn Rect2(10, 20, 120, 40)\n\n\nfunc rect_text(rect: Rect2) -> String:\n\treturn resolver.rect_text(rect)\n"

## A marked node's script whose _mcp_state returns its state variable.
const STATE_SOURCE := (
	"extends Node\n\nvar state: Variant = {}\n\n\n"
	+ "func _mcp_state() -> Variant:\n\treturn state\n"
)
## The state rig's stand-in bridge: the modules the state reader uses, and a node lookup by name
## under top.
const STATE_BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _logger: Logger\nvar _inspect: Node\n"
	+ "var _state: Node\nvar top: Node\n\n\nfunc _find_node(element: String) -> Node:\n"
	+ "\treturn top.find_child(element, true, false)\n"
)
## wait_for's refusal of a uiChanged: true condition no gesture has taken a baseline for.
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ 'uiChanged wait was met; send the input first, or wait for {uiChanged: "now"} '
	+ "in a parallel group beside an input in another session"
)
## The uiChanged rig's stand-in input module: its UI is the number ui, its baseline the ui it saw.
const UI_GESTURES_SOURCE := (
	"extends Node\n\nvar ui: int = 0\nvar _ui_baseline: Dictionary = {}\n\n\n"
	+ "func has_ui_baseline() -> bool:\n\treturn not _ui_baseline.is_empty()\n\n\n"
	+ "func take_ui_baseline() -> void:\n\tif _ui_baseline.is_empty():\n"
	+ "\t\t_ui_baseline = {'ui': ui}\n\n\n"
	+ "func ui_change() -> Dictionary:\n\tif _ui_baseline.is_empty() or _ui_baseline['ui'] == ui:\n"
	+ "\t\treturn {}\n\treturn {'before': _ui_baseline['ui'], 'after': ui}\n\n\n"
	+ "func use_up_ui_baseline() -> void:\n\t_ui_baseline = {}\n"
)
## A state the key path tests read.
const SEATED := {
	"turn": 2,
	"seats": [{"name": "alex", "hp": 7}, {"name": "sam", "hp": 9}],
	"grid": [[1, 2], [3, 4]],
}

var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")
var _text_targets: GDScript = load_bridge_script("godot_mcp_text_targets.gd")


func test_make_probe_builds_a_check_for_each_known_kind() -> void:
	var conditions: Node = _conditions_script.new()
	var exists: Variant = conditions.make_probe("exists", {"node": "Main", "exists": true}, {})
	var property: Variant = conditions.make_probe(
		"property", {"node": "Main", "property": "state", "equals": "done"}, {}
	)
	var expression: Variant = conditions.make_probe("expression", {"expression": "1 + 1 == 2"}, {})
	var text: Variant = conditions.make_probe("text", {"text": "Start"}, {})
	assert_true(exists is Callable, "exists")
	assert_true(property is Callable, "property")
	assert_true(expression is Callable, "expression")
	assert_true(text is Callable, "text")
	conditions.free()


func test_an_edge_probe_is_not_met_by_a_first_check_that_finds_it_true() -> void:
	var rig: Dictionary = _edge_rig(true)
	var probe: Callable = rig["probe"]
	assert_eq(probe.call(), [false, true], "true at the first check is not met")
	assert_eq(probe.call(), [false, true], "nor at a second true check: true then true")
	_free_edge_rig(rig)


func test_an_edge_probe_is_met_by_a_true_check_after_a_false_one() -> void:
	var rig: Dictionary = _edge_rig(true)
	var probe: Callable = rig["probe"]
	assert_eq(probe.call(), [false, true], "true first, not met")
	rig["bridge"].present = false
	assert_eq(probe.call(), [false, false], "the false check")
	rig["bridge"].present = true
	assert_eq(probe.call(), [true, true], "the rise is met")
	_free_edge_rig(rig)


func test_an_edge_probe_meets_a_rise_from_a_false_first_check() -> void:
	var rig: Dictionary = _edge_rig(false)
	var probe: Callable = rig["probe"]
	assert_eq(probe.call(), [false, false], "false first")
	rig["bridge"].present = true
	assert_eq(probe.call(), [true, true], "then true is met")
	_free_edge_rig(rig)


func test_an_edge_probe_passes_a_failure_through() -> void:
	var failed: Callable = func() -> Array: return [false, null, "no such node"]
	var probe: Callable = _conditions_script.edge_probe(failed, {})
	assert_eq(probe.call(), [false, null, "no such node"], "the failure as the probe gave it")


func test_an_expression_that_fails_to_run_is_no_fall() -> void:
	var failures: Dictionary = {}
	var answers: Array = ["true", "failed", "true"]
	var expression: Callable = func() -> Array:
		var answer: String = answers.pop_front()
		if answer == "failed":
			_conditions_script.note_failure(failures, "Invalid index")
			return [false, null]
		return [true, true]
	var probe: Callable = _conditions_script.edge_probe(expression, failures)
	assert_eq(probe.call(), [false, true], "true first, not met")
	assert_eq(probe.call(), [false, null], "the failed run, not met")
	assert_eq(probe.call(), [false, true], "true after a failed run is no rise")


func test_two_edge_probes_keep_their_own_fall() -> void:
	var rig: Dictionary = _edge_rig(true)
	var first: Callable = rig["probe"]
	var params: Dictionary = {"node": "Main", "exists": true, "edge": true}
	var second: Callable = rig["conditions"].make_probe("exists", params, {})
	assert_eq(first.call(), [false, true], "the first, true first")
	assert_eq(second.call(), [false, true], "the second, true first")
	rig["bridge"].present = false
	assert_eq(first.call(), [false, false], "only the first sees the fall")
	rig["bridge"].present = true
	assert_eq(first.call(), [true, true], "the first meets the rise")
	assert_eq(second.call(), [false, true], "the second never fell")
	_free_edge_rig(rig)


func test_a_probe_without_edge_is_met_at_its_first_true_check() -> void:
	var rig: Dictionary = _edge_rig(true, false)
	var probe: Callable = rig["probe"]
	assert_eq(probe.call(), [true, true], "met at once")
	_free_edge_rig(rig)


func test_make_probe_refuses_an_unknown_kind() -> void:
	var conditions: Node = _conditions_script.new()
	assert_eq(
		conditions.make_probe("signal", {}, {}),
		"unknown condition kind 'signal'",
		"signal polls apart"
	)
	assert_eq(conditions.make_probe("", {}, {}), "unknown condition kind ''", "no kind")
	conditions.free()


func test_parse_expression_reports_a_parse_error_with_expressions_text() -> void:
	var conditions: Node = _conditions_script.new()
	var parsed: Variant = conditions.parse_expression("1 +", "", {})
	assert_true(parsed is String, "a String, not a Callable")
	assert_true(
		str(parsed).begins_with("the expression does not parse: "), "the prefix: %s" % parsed
	)
	assert_true(str(parsed).length() > "the expression does not parse: ".length(), "the text")
	conditions.free()


func test_parse_expression_accepts_its_inputs() -> void:
	var conditions: Node = _conditions_script.new()
	assert_true(conditions.parse_expression("node == null", "Main", {}) is Callable, "node named")
	assert_true(
		conditions.parse_expression("tree != root and Engine != Input", "", {}) is Callable,
		"inputs"
	)
	conditions.free()


func test_parse_expression_refuses_a_lambda_saying_expression_is_not_gdscript() -> void:
	var conditions: Node = _conditions_script.new()
	var parsed: Variant = conditions.parse_expression(
		'root.get_children().any(func(b): return b.visible and b.text == "Take it")', "", {}
	)
	assert_eq(
		parsed,
		"the expression does not parse: Expected ',' or ')'. " + NOT_GDSCRIPT_HINT,
		"a lambda names GDScript syntax Expression has no operator for"
	)
	conditions.free()


func test_parse_expression_refuses_text_after_a_complete_expression() -> void:
	var conditions: Node = _conditions_script.new()
	var texts: PackedStringArray = [
		"true if false else false", "root is Window", "func(b): return b.visible", "1)", "1]", "1 2"
	]
	for source in texts:
		assert_true(
			conditions.parse_expression(source, "Main", {}) is String, "%s is refused" % source
		)
	assert_true(
		conditions.parse_expression("str(1) + ')' == '1)'", "", {}) is Callable,
		"a quoted closing paren is no trailing text"
	)
	var delve: String = (
		"root.find_children('Code','Label',true,false).size() > 0 and "
		+ "root.find_children('Code','Label',true,false)[0].text != ''"
	)
	assert_true(
		conditions.parse_expression(delve, "", {}) is Callable, "a real wait expression parses"
	)
	conditions.free()


func test_every_refusal_is_the_whole_reason_expression_gives() -> void:
	var conditions: Node = _conditions_script.new()
	var names: PackedStringArray = PackedStringArray(["root", "tree", "Input", "Engine"])
	assert_eq(
		conditions.parse_expression("1)", "", {}),
		(
			"the expression does not parse: text follows a complete expression, and Expression "
			+ "would ignore it. "
			+ NOT_GDSCRIPT_HINT
		),
		"trailing text carries the hint"
	)
	for source in ["1 +", "node.is_inside_tree( +", "get('if') +"]:
		var probe := Expression.new()
		probe.parse(source, names)
		assert_eq(
			conditions.parse_expression(source, "", {}),
			"the expression does not parse: %s" % probe.get_error_text(),
			"%s carries Expression's own text and no hint" % source
		)
	var in_probe := Expression.new()
	in_probe.parse("'Armed' not in names", names)
	assert_eq(
		conditions.parse_expression("'Armed' not in names", "", {}),
		"the expression does not parse: %s. %s" % [in_probe.get_error_text(), NOT_GDSCRIPT_HINT],
		"'not in' carries the hint"
	)
	conditions.free()


func test_note_failure_counts_each_check_and_keeps_each_text_once() -> void:
	var conditions: Node = _conditions_script.new()
	var failures: Dictionary = {}
	var fresh: Array[bool] = []
	for failure in ["first", "second", "first"]:
		fresh.append(conditions.note_failure(failures, failure))
	assert_eq(failures["count"], 3, "every failed check counted")
	assert_eq(failures["error"], "first", "the last check's text")
	assert_eq(failures["seen"].size(), 2, "two distinct texts kept")
	assert_eq(fresh, [true, true, false], "reported once, on each text's first sighting")
	conditions.free()


func test_note_muted_adds_each_checks_count_and_keeps_the_first_text() -> void:
	var failures: Dictionary = {}
	_conditions_script.note_muted(failures, {})
	assert_true(not failures.has("muted"), "a check that muted nothing adds nothing")
	_conditions_script.note_muted(failures, {"count": 2, "error": "first"})
	_conditions_script.note_muted(failures, {})
	_conditions_script.note_muted(failures, {"count": 1, "error": "second"})
	assert_eq(
		failures["muted"], {"count": 3, "error": "first"}, "both counted, the first text kept"
	)


func test_an_engine_error_a_called_method_raises_stays_out_of_the_feed() -> void:
	var rig: Dictionary = _expression_rig()
	var failures: Dictionary = {}
	var probe: Callable = rig["conditions"].parse_expression(GUARDED_EXPRESSION, "Holder", failures)
	var logger: Logger = rig["bridge"]._logger
	OS.add_logger(logger)
	var checks: Array = [probe.call(), probe.call(), probe.call()]
	var kid := Node2D.new()
	kid.modulate = Color(2.0, 1.0, 1.0)
	rig["holder"].add_child(kid)
	var met: Array = probe.call()
	OS.remove_logger(logger)
	# The runner fails a test on any engine error; get_child's, one a check, and the failure's
	# push are expected, so they are taken here and checked.
	var raised: PackedStringArray = Engine.get_main_loop().get("_recorder").take()
	assert_eq(_containing(Array(raised), "out of bounds").size(), 3, "raised: %s" % [raised])
	assert_eq(raised.size(), 4, "and the failure was pushed once: %s" % [raised])
	var messages: Array = _messages(logger)
	assert_eq(_containing(messages, "out of bounds"), [], "the bridge's logger kept none of them")
	assert_eq(messages.size(), 1, "only the failure: %s" % [messages])
	if messages.size() == 1:
		assert_true(str(messages[0]).begins_with(FAILURE_PREFIX), messages[0])
	assert_eq(checks, [[false, null], [false, null], [false, null]], "not met while childless")
	assert_eq(failures["count"], 3, "each childless check failed")
	assert_eq(met, [true, true], "met once the child is there")
	var outcome: Dictionary = {"result": {"met": true, "value": true}}
	rig["conditions"].add_failed_checks(outcome, failures)
	var muted: Dictionary = outcome["result"].get("mutedErrors", {})
	assert_eq(muted.get("count"), 3, "the result counts the errors kept out: %s" % [outcome])
	assert_true(str(muted.get("error")).contains("out of bounds"), "the first's text: %s" % [muted])
	_free_expression_rig(rig)


func test_an_error_game_code_an_expression_calls_raises_still_reaches_the_feed() -> void:
	var rig: Dictionary = _expression_rig()
	var probe: Callable = rig["conditions"].parse_expression("node.shout()", "Holder", {})
	var logger: Logger = rig["bridge"]._logger
	OS.add_logger(logger)
	var met: Array = probe.call()
	OS.remove_logger(logger)
	# The game's push reaches the runner, which would fail the test on it; it is expected.
	var raised: PackedStringArray = Engine.get_main_loop().get("_recorder").take()
	assert_eq(raised.size(), 1, "the game's error: %s" % [raised])
	assert_eq(_messages(logger), ["holder shout"], "the game's error lands in the feed")
	assert_eq(met, [true, true], "and the check reads the method's answer")
	_free_expression_rig(rig)


func test_an_expression_failure_still_reaches_the_feed_once() -> void:
	var rig: Dictionary = _expression_rig()
	var failures: Dictionary = {}
	var source: String = "node.no_such_property > 0"
	var probe: Callable = rig["conditions"].parse_expression(source, "Holder", failures)
	var logger: Logger = rig["bridge"]._logger
	OS.add_logger(logger)
	var checks: Array = [probe.call(), probe.call()]
	OS.remove_logger(logger)
	# The failure's push reaches the runner, which would fail the test on it; it is expected.
	var raised: PackedStringArray = Engine.get_main_loop().get("_recorder").take()
	assert_eq(raised.size(), 1, "pushed once for two failed checks: %s" % [raised])
	var messages: Array = _messages(logger)
	assert_eq(messages.size(), 1, "the feed holds the failure once: %s" % [messages])
	if messages.size() == 1:
		var message: String = messages[0]
		assert_true(message.begins_with(FAILURE_PREFIX), message)
		assert_true(message.contains("no_such_property"), "naming the index: " + message)
	assert_eq(checks, [[false, null], [false, null]], "neither check is met")
	assert_eq(failures["count"], 2, "both checks failed")
	var outcome: Dictionary = {"result": {"met": false, "last": null}}
	rig["conditions"].add_failed_checks(outcome, failures)
	assert_true(outcome["result"].has("failedChecks"), "the failed checks: %s" % [outcome])
	assert_true(not outcome["result"].has("mutedErrors"), "and nothing muted: %s" % [outcome])
	_free_expression_rig(rig)


func test_a_met_result_carries_its_failed_checks_and_a_clean_one_carries_none() -> void:
	var conditions: Node = _conditions_script.new()
	var failed: Dictionary = {"count": 2, "error": "Invalid index of type int for base type Array"}
	var met: Dictionary = {"result": {"met": true, "value": true}}
	conditions.add_failed_checks(met, failed)
	assert_eq(
		met["result"]["failedChecks"], failed, "each failed check counted, the last error kept"
	)
	var clean: Dictionary = {"result": {"met": true, "value": true}}
	conditions.add_failed_checks(clean, {})
	assert_true(not clean["result"].has("failedChecks"), "a wait with no failed check carries none")
	conditions.free()


func test_a_text_appear_is_met_by_the_one_control_showing_it_with_its_value() -> void:
	var targets: Node = _placing_targets()
	var entries: Array[Dictionary] = [
		_shown("/root/Menu/Go", "Start"), _shown("/root/Menu/X", "Quit")
	]
	var matches: Array[Dictionary] = _text_targets.matching(entries, "Start")
	var none: Array[Dictionary] = []
	assert_eq(
		_conditions_script.text_answer("Start", matches, true, targets),
		[true, {"path": "/root/Menu/Go", "shown": "Start", "rect": SHOWN_RECT}],
		"met, with the Control's path, text and rect"
	)
	assert_eq(
		_conditions_script.text_answer("Start", none, true, targets), [false, null], "none: not met"
	)
	_free_placing_targets(targets)


func test_a_text_leave_holds_while_it_shows_and_is_met_once_none_does() -> void:
	var targets: Node = _placing_targets()
	var matches: Array[Dictionary] = [_shown("/root/Menu/Go", "Start")]
	var none: Array[Dictionary] = []
	assert_eq(
		_conditions_script.text_answer("Start", matches, false, targets),
		[false, {"path": "/root/Menu/Go", "shown": "Start", "rect": SHOWN_RECT}],
		"held while it shows, the Control showing it as its value"
	)
	assert_eq(
		_conditions_script.text_answer("Start", none, false, targets),
		[true, null],
		"met once none shows it"
	)
	_free_placing_targets(targets)


func test_a_text_match_keeps_case_and_trims() -> void:
	var targets: Node = _placing_targets()
	var entries: Array[Dictionary] = [
		_shown("/root/Menu/Lower", "start"), _shown("/root/Menu/Spaced", " Resume ")
	]
	var lower: Array[Dictionary] = _text_targets.matching(entries, "Start")
	var spaced: Array[Dictionary] = _text_targets.matching(entries, "Resume")
	assert_eq(
		_conditions_script.text_answer("Start", lower, true, targets),
		[false, null],
		"start does not meet Start"
	)
	assert_eq(
		_conditions_script.text_answer("Resume", spaced, true, targets),
		[true, {"path": "/root/Menu/Spaced", "shown": " Resume ", "rect": SHOWN_RECT}],
		"a padded text meets, its text as shown"
	)
	_free_placing_targets(targets)


func test_two_controls_showing_a_text_fail_an_appear_and_hold_a_leave() -> void:
	var targets: Node = _placing_targets()
	var matches: Array[Dictionary] = [
		_shown("/root/Menu/First", "Start"), _shown("/root/Menu/Second", "Start")
	]
	assert_eq(
		_conditions_script.text_answer("Start", matches, true, targets),
		[
			false,
			null,
			(
				"'Start' is shown by 2 Controls: /root/Menu/First at 10,20,120,40, "
				+ "/root/Menu/Second at 10,20,120,40; narrow it with under."
			)
		],
		"an appear fails with the ambiguity listing"
	)
	assert_eq(
		_conditions_script.text_answer("Start", matches, false, targets),
		[false, {"path": "/root/Menu/First", "shown": "Start", "rect": SHOWN_RECT}],
		"a leave holds while both show, the first as its value"
	)
	assert_eq(
		_conditions_script.text_answer("Start", matches.slice(1), false, targets),
		[false, {"path": "/root/Menu/Second", "shown": "Start", "rect": SHOWN_RECT}],
		"and while one still shows"
	)
	_free_placing_targets(targets)


func test_a_missing_under_shows_nothing() -> void:
	var rig: Dictionary = _under_rig(
		"func _nodes_named(_node_name: String) -> Array[Node]:\n\treturn []\n"
	)
	var conditions: Node = rig["conditions"]
	for under: String in ["TextRigLobby", "Menu/TextRigLobby"]:
		var appear: Dictionary = {"text": "Start", "under": under}
		var leave: Dictionary = {"text": "Start", "under": under, "exists": false}
		var appeared: Array = conditions.make_probe("text", appear, {}).call()
		var left: Array = conditions.make_probe("text", leave, {}).call()
		assert_eq(appeared, [false, null], "%s missing: an appear is not met" % under)
		assert_eq(left, [true, null], "%s missing: a leave is met" % under)
	_free_under_rig(rig)


func test_an_under_several_nodes_have_fails_the_check() -> void:
	var rig: Dictionary = _under_rig(
		(
			"func _nodes_named(_node_name: String) -> Array[Node]:\n\treturn [self, self]\n\n\n"
			+ "func _ambiguous(_node_name: String, _named: Array[Node]) -> String:\n"
			+ '\treturn "%s"\n' % SHARED_UNDER
		)
	)
	var conditions: Node = rig["conditions"]
	var appear: Dictionary = {"text": "Start", "under": "Lobby"}
	var leave: Dictionary = {"text": "Start", "under": "Lobby", "exists": false}
	var appeared: Array = conditions.make_probe("text", appear, {}).call()
	var left: Array = conditions.make_probe("text", leave, {}).call()
	assert_eq(appeared, [false, null, SHARED_UNDER], "an appear fails with the resolver's refusal")
	assert_eq(left, [false, null, SHARED_UNDER], "and so does a leave")
	_free_under_rig(rig)


func test_a_still_value_is_settled_after_stable_counted_frames_and_not_before() -> void:
	var state: Dictionary = _conditions_script.settled_state()
	var rect: Dictionary = {"x": 10.0, "y": 20.0, "width": 120.0, "height": 40.0}
	assert_eq(
		_conditions_script.settled_answer(state, [true, rect], 50, 2),
		[false, {"frames": 0, "value": rect}],
		"the first reading counts no frame"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [true, rect.duplicate()], 51, 2),
		[false, {"frames": 1, "value": rect}],
		"one frame held of two is not settled"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [true, rect.duplicate()], 52, 2),
		[true, {"frames": 2, "value": rect}],
		"two frames held is settled"
	)


func test_a_changed_value_resets_the_settled_count() -> void:
	var state: Dictionary = _conditions_script.settled_state()
	_conditions_script.settled_answer(state, [true, 4.0], 50, 3)
	_conditions_script.settled_answer(state, [true, 4.0], 51, 3)
	assert_eq(
		_conditions_script.settled_answer(state, [true, 9.0], 52, 3),
		[false, {"frames": 0, "value": 9.0}],
		"a new value starts the count again"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [true, 9.0 + 1e-9], 53, 3),
		[false, {"frames": 1, "value": 9.0 + 1e-9}],
		"a number within 1e-6 is the same value"
	)


func test_two_settled_checks_in_one_frame_count_it_once() -> void:
	var state: Dictionary = _conditions_script.settled_state()
	_conditions_script.settled_answer(state, [true, 4.0], 50, 2)
	_conditions_script.settled_answer(state, [true, 4.0], 51, 2)
	assert_eq(
		_conditions_script.settled_answer(state, [true, 4.0], 51, 2),
		[false, {"frames": 1, "value": 4.0}],
		"a second check in frame 51 counts nothing"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [true, 4.0], 52, 2),
		[true, {"frames": 2, "value": 4.0}],
		"the next frame counts"
	)


func test_a_missing_reading_resets_the_settled_count_and_passes_through() -> void:
	var state: Dictionary = _conditions_script.settled_state()
	_conditions_script.settled_answer(state, [true, 4.0], 50, 2)
	_conditions_script.settled_answer(state, [true, 4.0], 51, 2)
	assert_eq(
		_conditions_script.settled_answer(state, [false, null], 52, 2),
		[false, null],
		"a missing node is not met"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [false, null, "'%Box' names 2 nodes"], 53, 2),
		[false, null, "'%Box' names 2 nodes"],
		"a failed reading fails the check"
	)
	assert_eq(
		_conditions_script.settled_answer(state, [true, 4.0], 54, 2),
		[false, {"frames": 0, "value": 4.0}],
		"the same value found again counts from its first reading"
	)


func test_a_property_path_names_the_first_sub_name_its_value_lacks() -> void:
	var rect := Rect2(1, 2, 3, 4)
	assert_eq(
		_conditions_script.missing_subname(Vector2(3, 4), "position:q".split(":")),
		"position:q",
		"a Vector2 has no q"
	)
	assert_eq(
		_conditions_script.missing_subname(Vector2(3, 4), "position:x".split(":")),
		"",
		"a Vector2 has x"
	)
	assert_eq(
		_conditions_script.missing_subname(rect, "rect:size:q".split(":")),
		"rect:size:q",
		"the lacking part found deeper down"
	)
	assert_eq(
		_conditions_script.missing_subname(rect, "rect:sise:x".split(":")),
		"rect:sise",
		"the path stops at the first part lacking"
	)
	assert_eq(_conditions_script.missing_subname(rect, "rect".split(":")), "", "a path of one part")


func test_a_settled_reading_of_a_node_with_no_rect_or_transform_fails_naming_it() -> void:
	var conditions: Node = _conditions_script.new()
	var plain := Node.new()
	assert_eq(
		conditions._settled_reading(plain, "Plain", ""),
		[false, null, PLAIN_NO_PLACEMENT],
		"a plain Node without property fails the check"
	)
	plain.free()
	conditions.free()


func test_a_state_path_reads_keys_dotted_paths_and_indexes_as_get_game_states_keys() -> void:
	assert_eq(_conditions_script.state_at(SEATED, "turn"), [true, 2], "a key")
	assert_eq(_conditions_script.state_at(SEATED, "seats[1].hp"), [true, 9], "a dotted path")
	assert_eq(_conditions_script.state_at(SEATED, "grid[1][0]"), [true, 3], "two indexes")
	assert_eq(
		_conditions_script.state_at(SEATED, "seats[0]"),
		[true, {"name": "alex", "hp": 7}],
		"a list item whole"
	)
	assert_eq(_conditions_script.state_at(SEATED, "$"), [true, SEATED], "$ is the whole state")
	assert_eq(_conditions_script.state_at(5, "$"), [true, 5], "and a state that is no Dictionary")


func test_a_state_path_the_state_lacks_is_not_found() -> void:
	for path: String in [
		"nope",
		"seats[2].hp",
		"seats[0].mp",
		"turn.x",
		"turn[0]",
		"seats[x]",
		"seats[-1]",
		"seats[+1]",
		"seats[0",
		"seats.",
		"",
	]:
		assert_eq(_conditions_script.state_at(SEATED, path), [false, null], path)


func test_an_index_past_any_list_length_is_not_found_without_parsing_it() -> void:
	for path: String in ["seats[99999999999999999999]", "seats[12345678901]"]:
		assert_eq(_conditions_script.state_at(SEATED, path), [false, null], path)


func test_a_state_path_reaching_a_cut_marker_reads_the_marker() -> void:
	var state: Dictionary = {"deep": "<depth limit: Dictionary>", "big": "<size limit>"}
	assert_eq(
		_conditions_script.state_at(state, "deep.a[0].b"),
		[true, "<depth limit: Dictionary>"],
		"a depth cut"
	)
	assert_eq(_conditions_script.state_at(state, "big.a"), [true, "<size limit>"], "a size cut")


func test_a_state_wait_is_met_when_its_key_equals_the_value_and_shows_last_otherwise() -> void:
	var rig: Dictionary = _state_rig()
	var board: Node = _add_state_node(rig["game"], "Board", STATE_SOURCE, true)
	board.state = SEATED.duplicate(true)
	var params: Dictionary = {"node": "Board", "state": "seats[0].hp", "equals": 7.0}
	var probe: Callable = rig["conditions"].make_probe("state", params, {})
	assert_eq(probe.call(), [true, 7], "met, the key's value as its value")
	board.state["seats"][0]["hp"] = 6
	assert_eq(probe.call(), [false, 6], "not met, its value as last")
	_free_state_rig(rig)


func test_a_state_wait_on_a_path_the_state_lacks_or_a_missing_node_is_not_met() -> void:
	var rig: Dictionary = _state_rig()
	var board: Node = _add_state_node(rig["game"], "Board", STATE_SOURCE, true)
	board.state = SEATED.duplicate(true)
	var lacking: Dictionary = {"node": "Board", "state": "seats[2].hp", "equals": 7.0}
	var nowhere: Dictionary = {"node": "Nowhere", "state": "turn", "equals": 2.0}
	assert_eq(rig["conditions"].make_probe("state", lacking, {}).call(), [false, null], "no seat 2")
	assert_eq(rig["conditions"].make_probe("state", nowhere, {}).call(), [false, null], "no node")
	_free_state_rig(rig)


func test_a_state_key_past_the_reads_depth_reads_the_cut_marker_and_is_not_met() -> void:
	var rig: Dictionary = _state_rig()
	var deep: Node = _add_state_node(rig["game"], "Deep", STATE_SOURCE, true)
	deep.state = {"a": {"b": {"c": {"d": {"e": 1}}}}}
	var params: Dictionary = {"node": "Deep", "state": "a.b.c.d.e", "equals": 1.0}
	assert_eq(
		rig["conditions"].make_probe("state", params, {}).call(),
		[false, "<depth limit: Dictionary>"],
		"d sits at the read's depth of 4, so e is out of reach"
	)
	_free_state_rig(rig)


func test_a_state_wait_on_the_whole_state_compares_it() -> void:
	var rig: Dictionary = _state_rig()
	var counter: Node = _add_state_node(rig["game"], "Counter", STATE_SOURCE, true)
	counter.state = 5
	var params: Dictionary = {"node": "Counter", "state": "$", "equals": 5.0}
	assert_eq(rig["conditions"].make_probe("state", params, {}).call(), [true, 5], "$ reads 5")
	_free_state_rig(rig)


func test_a_state_wait_on_a_node_outside_the_group_fails_naming_the_group() -> void:
	var rig: Dictionary = _state_rig()
	_add_state_node(rig["game"], "Loose", STATE_SOURCE, false)
	var params: Dictionary = {"node": "Loose", "state": "turn", "equals": 2.0}
	assert_eq(
		rig["conditions"].make_probe("state", params, {}).call(),
		[
			false,
			null,
			(
				"'/root/Game/Loose' is not in the mcp_state group; "
				+ "add it to the group to read its state."
			)
		],
		"a node outside the group fails the check"
	)
	_free_state_rig(rig)


func test_a_state_wait_on_a_node_with_no_state_fails_with_the_reads_error() -> void:
	var rig: Dictionary = _state_rig()
	_add_state_node(rig["game"], "Plain", "", true)
	var params: Dictionary = {"node": "Plain", "state": "turn", "equals": 2.0}
	assert_eq(
		rig["conditions"].make_probe("state", params, {}).call(),
		[
			false,
			null,
			"'/root/Game/Plain' has no state: in the mcp_state group but has no _mcp_state method"
		],
		"a marked node without _mcp_state fails the check"
	)
	_free_state_rig(rig)


func test_a_now_ui_wait_with_no_baseline_takes_one_and_is_met_by_a_later_change() -> void:
	var rig: Dictionary = _ui_rig()
	var gestures: Node = rig["bridge"]._gestures
	var probe: Variant = rig["conditions"].make_probe("uiChanged", {"uiChanged": "now"}, {})
	assert_true(probe is Callable, "a probe, not a refusal: %s" % [probe])
	assert_true(gestures.has_ui_baseline(), "the probe took a baseline")
	if probe is Callable:
		assert_eq(probe.call(), [false, null], "nothing changed yet")
		gestures.ui = 1
		assert_eq(probe.call(), [true, {"before": 0, "after": 1}], "the later change meets it")
	assert_true(not gestures.has_ui_baseline(), "the met wait used its baseline up")
	_free_ui_rig(rig)


func test_a_now_ui_wait_replaces_a_baseline_a_gesture_left() -> void:
	var rig: Dictionary = _ui_rig()
	var gestures: Node = rig["bridge"]._gestures
	gestures.take_ui_baseline()
	gestures.ui = 1
	var probe: Variant = rig["conditions"].make_probe("uiChanged", {"uiChanged": "now"}, {})
	assert_true(probe is Callable, "a probe, not a refusal: %s" % [probe])
	if probe is Callable:
		assert_eq(
			probe.call(), [false, null], "a change from before the wait started meets nothing"
		)
		gestures.ui = 2
		assert_eq(probe.call(), [true, {"before": 1, "after": 2}], "a change after it does")
	_free_ui_rig(rig)


func test_a_true_ui_wait_with_no_baseline_is_refused() -> void:
	var rig: Dictionary = _ui_rig()
	assert_eq(
		rig["conditions"].make_probe("uiChanged", {"uiChanged": true}, {}),
		NO_UI_BASELINE,
		"no gesture took a baseline"
	)
	assert_true(not rig["bridge"]._gestures.has_ui_baseline(), "and the refusal took none")
	_free_ui_rig(rig)


## Conditions whose bridge is a stand-in holding UI_GESTURES_SOURCE's input module.
func _ui_rig() -> Dictionary:
	var bridge: Node = _compile("extends Node\n\nvar _gestures: Node\n")
	bridge._gestures = _compile(UI_GESTURES_SOURCE)
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	return {"conditions": conditions, "bridge": bridge}


func _free_ui_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"]._gestures.free()
	rig["bridge"].free()


## Conditions whose bridge is a stand-in finding no node, its input player's target resolver a
## stand-in whose bare-name lookup is resolver_body.
func _under_rig(resolver_body: String) -> Dictionary:
	var bridge: Node = _compile(
		(
			"extends Node\n\nvar _gestures: Node\n\n\n"
			+ "func _find_node(_element: String) -> Node:\n\treturn null\n"
		)
	)
	var gestures: Node = _compile("extends Node\n\nvar _targets: Node\n")
	gestures._targets = _compile("extends Node\n\n\n" + resolver_body)
	bridge._gestures = gestures
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	return {"conditions": conditions, "bridge": bridge}


func _free_under_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"]._gestures._targets.free()
	rig["bridge"]._gestures.free()
	rig["bridge"].free()


## A resolver stand-in (PLACING_TARGETS_SOURCE) whose bridge holds the JSON module.
func _placing_targets() -> Node:
	var targets: Node = _compile(PLACING_TARGETS_SOURCE)
	targets.resolver = load_bridge_script("godot_mcp_targets.gd")
	targets.bridge = _compile("extends Node\n\nvar _json: GDScript\n")
	targets.bridge._json = load_bridge_script("godot_mcp_json.gd")
	return targets


func _free_placing_targets(targets: Node) -> void:
	targets.bridge.free()
	targets.free()


## A text target entry at path showing shown, its Control left out: a resolver stand-in places it.
static func _shown(path: String, shown: String) -> Dictionary:
	return {"control": null, "path": path, "name": path.get_file(), "shown": shown}


## An exists probe on Main (edge unless edge is false) whose bridge is a stand-in finding Main
## while its present is true.
func _edge_rig(present: bool, edge: bool = true) -> Dictionary:
	var bridge: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var present: bool = false",
					"",
					"",
					"func _find_node(_element: String) -> Node:",
					"\treturn self if present else null",
				]
			)
		)
	)
	bridge.present = present
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	var params: Dictionary = {"node": "Main", "exists": true, "edge": edge}
	var probe: Callable = conditions.make_probe("exists", params, {})
	return {"probe": probe, "bridge": bridge, "conditions": conditions}


func _free_edge_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"].free()


## Conditions whose bridge is a stand-in holding the JSON module and an unregistered logger, and
## finding Holder, a node outside any tree with no children and HOLDER_SOURCE's script.
func _expression_rig() -> Dictionary:
	var bridge: Node = _compile(
		(
			"\n"
			. join(
				[
					"extends Node",
					"",
					"var _json: GDScript",
					"var _logger: Logger",
					"var holder: Node",
					"",
					"",
					"func _find_node(element: String) -> Node:",
					"\treturn holder if element == str(holder.name) else null",
				]
			)
		)
	)
	var holder: Node = _compile(HOLDER_SOURCE)
	holder.name = "Holder"
	bridge.holder = holder
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = load_bridge_script("godot_mcp_logger.gd").new()
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	return {"conditions": conditions, "bridge": bridge, "holder": holder}


func _free_expression_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["holder"].free()
	rig["bridge"].free()


## {top, game, bridge, conditions}: a stand-in root named root holding Game, and conditions whose
## stand-in bridge (STATE_BRIDGE_SOURCE) finds nodes under it by name and holds a state reader
## naming a node by its path from that root, the JSON module, an unregistered logger and the
## inspector. The nodes are in no tree, as the state reader's own tests read them.
func _state_rig() -> Dictionary:
	var top := Node.new()
	top.name = "root"
	var game := Node.new()
	game.name = "Game"
	top.add_child(game)
	var bridge: Node = _compile(STATE_BRIDGE_SOURCE)
	bridge.top = top
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = load_bridge_script("godot_mcp_logger.gd").new()
	var inspect: Node = load_bridge_script("godot_mcp_inspect.gd").new()
	inspect._bridge = bridge
	bridge._inspect = inspect
	var state: Node = load_bridge_script("godot_mcp_state.gd").new()
	state.bridge = bridge
	state.path_of = func(node: Node) -> String: return "/root/%s" % top.get_path_to(node)
	bridge._state = state
	var conditions: Node = _conditions_script.new()
	conditions.bridge = bridge
	return {"top": top, "game": game, "bridge": bridge, "conditions": conditions}


func _free_state_rig(rig: Dictionary) -> void:
	rig["conditions"].free()
	rig["bridge"]._state.free()
	rig["bridge"]._inspect.free()
	rig["bridge"].free()
	rig["top"].free()


## A node named node_name under parent, with a script compiled from source when it is not empty,
## and in the mcp_state group when marked.
func _add_state_node(parent: Node, node_name: String, source: String, marked: bool) -> Node:
	var node: Node = Node.new() if source.is_empty() else _compile(source)
	node.name = node_name
	if marked:
		node.add_to_group("mcp_state")
	parent.add_child(node)
	return node


## The messages of the entries logger holds, taking them.
static func _messages(logger: Logger) -> Array:
	var entries: Array = logger.take_pending()[0]
	return entries.map(func(entry: Dictionary) -> String: return entry["message"])


## The items of texts that contain text.
static func _containing(texts: Array, text: String) -> Array:
	return texts.filter(func(item: String) -> bool: return item.contains(text))


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
