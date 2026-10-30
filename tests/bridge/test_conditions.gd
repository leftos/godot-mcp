# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## The conditions' pure logic (bridge/godot_mcp_conditions.gd): wait_for's condition kinds as
## make_probe builds them, edge probes, and expression parsing and its count of failed checks;
## test_conditions_scene.gd covers the text, stable, state and uiChanged kinds. The node is never
## added to the tree, and the tests run before the root enters it (SceneTree::initialize calls
## _initialize first, scene/main/scene_tree.cpp L589-590 in 4.7.2), so only functions that do
## not need it are called.

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

var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")


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
