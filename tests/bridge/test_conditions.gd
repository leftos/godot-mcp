extends "res://gd_test.gd"
## The conditions' pure logic (bridge/godot_mcp_conditions.gd): wait_for's condition kinds,
## expression parsing and its count of failed checks. The node is never added to the tree;
## only functions that do not need it are called.

## The hint a refusal carries when the source reaches for GDScript syntax Expression has none of.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NOT_GDSCRIPT_HINT := "Godot's Expression is not GDScript: it has no lambdas (func), no if/else, no is or as, no not in (write not (a in b)), and no statements; it has calls, indexing, literals and operators such as and, or, not, in, ==, !=, <, <=, >, >=, +, -, *, / and %."

var _conditions_script: GDScript = load_bridge_script("godot_mcp_conditions.gd")


func test_make_probe_builds_a_check_for_each_known_kind() -> void:
	var conditions: Node = _conditions_script.new()
	var exists: Variant = conditions.make_probe("exists", {"node": "Main", "exists": true}, {})
	var property: Variant = conditions.make_probe(
		"property", {"node": "Main", "property": "state", "equals": "done"}, {}
	)
	var expression: Variant = conditions.make_probe("expression", {"expression": "1 + 1 == 2"}, {})
	assert_true(exists is Callable, "exists")
	assert_true(property is Callable, "property")
	assert_true(expression is Callable, "expression")
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


## An instance of a script compiled from source.
func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
