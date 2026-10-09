extends Node
## The godot-mcp bridge's conditions, a child of the bridge: parses a condition Expression and
## builds the checks wait_for polls each frame (a node existing, a property's value, an
## expression, the UI changed since the last gesture).
## The watch parses its expression tracks through parse_condition too.
##
## A check returns [met, value], or [false, null, error] when its condition cannot be met.

## An expression's inputs besides node. Expression resolves only its inputs and its base
## instance's members, not singletons (core/math/expression.cpp L707-734 in 4.7.2).
const EXPRESSION_INPUTS: PackedStringArray = ["root", "tree", "Input", "Engine"]
## Why a source Expression cannot read: GDScript syntax it has no operator for.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NOT_GDSCRIPT_HINT := "Godot's Expression is not GDScript: it has no lambdas (func), no if/else, no is or as, no not in (write not (a in b)), and no statements; it has calls, indexing, literals and operators such as and, or, not, in, ==, !=, <, <=, >, >=, +, -, *, / and %."
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ "uiChanged wait was met; send the input first"
)

## The bridge these conditions belong to, for its node lookup, JSON conversion, input module and
## clock's JSON comparison.
var bridge: Node


## A Callable returning [met, value], or [false, null, error] when the wait cannot be met, for
## the condition; a String saying why there is none. failures counts an expression's failed checks.
## With params.edge the Callable is edge_probe's, met only on a rise.
func make_probe(kind: String, params: Dictionary, failures: Dictionary) -> Variant:
	var probe: Variant = _kind_probe(kind, params, failures)
	if probe is Callable and bool(params.get("edge", false)):
		return edge_probe(probe, failures)
	return probe


## probe as an edge check: met only on a check that finds it met after a check that found it not
## met, so a first check that finds it met is not; a failed check passes through as it is. A check
## whose expression failed to run (one more in failures' count) is no fall, while a missing node
## is one. The flag lives in the Callable, since a wait may check several times a frame (at a draw
## and at the frame), and any earlier check counts.
static func edge_probe(probe: Callable, failures: Dictionary) -> Callable:
	return _check_edge.bind(probe, failures, {"fell": false})


static func _check_edge(probe: Callable, failures: Dictionary, state: Dictionary) -> Array:
	var failed_before: int = int(failures.get("count", 0))
	var seen: Array = probe.call()
	if seen.size() > 2 or state["fell"]:
		return seen
	if not seen[0]:
		state["fell"] = int(failures.get("count", 0)) == failed_before
		return seen
	return [false, seen[1]]


func _kind_probe(kind: String, params: Dictionary, failures: Dictionary) -> Variant:
	var node_name: String = _text(params, "node")
	var probe: Variant = "unknown condition kind '%s'" % kind
	match kind:
		"exists":
			probe = _check_exists.bind(node_name, bool(params.get("exists", true)))
		"property":
			var property: String = _text(params, "property")
			probe = check_property.bind(node_name, property, params.get("equals"))
		"expression":
			probe = parse_expression(_text(params, "expression"), node_name, failures)
		"uiChanged":
			probe = _check_ui_changed if bridge._gestures.has_ui_baseline() else NO_UI_BASELINE
	return probe


## Met on the first check whose UI differs from the input module's baseline, with the change as
## its value; a met wait uses the baseline up, and one that times out leaves it.
func _check_ui_changed() -> Array:
	var change: Dictionary = bridge._gestures.ui_change()
	if change.is_empty():
		return [false, null]
	bridge._gestures.use_up_ui_baseline()
	return [true, change]


func _check_exists(node_name: String, wanted: bool) -> Array:
	var present: bool = bridge._find_node(node_name) != null
	var missing: Array = [] if present else _missing(node_name)
	if missing.size() > 2:
		return missing
	return [present == wanted, present]


## A probe's answer while its node is missing: not met, or failed when the name starts with a
## unique name more than one scene holds, which no wait can settle.
func _missing(node_name: String) -> Array:
	if not node_name.begins_with("%"):
		return [false, null]
	var ambiguity: String = bridge._inspect.unique_ambiguity(get_tree().root, node_name)
	return [false, null] if ambiguity.is_empty() else [false, null, ambiguity]


## The property (a path such as position:x) as run_script returns values, compared in JSON
## space; not met while the node is missing, and failed once it is found without the property.
func check_property(node_name: String, property: String, wanted: Variant) -> Array:
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return _missing(node_name)
	var first: String = property.get_slice(":", 0)
	if not bridge._json.has_property(node, first):
		return [false, null, "'%s' has no property '%s'." % [node.get_path(), first]]
	var value: Variant = bridge._json.to_json(node.get_indexed(NodePath(property)))
	return [bridge._time.json_equal(value, wanted), value]


## Parses source once with its inputs (node too when node_name is set, also the base instance)
## through parse_condition and returns the Callable that runs it with failures; a String with the
## reason and wait_for's prefix when it cannot be a condition.
func parse_expression(source: String, node_name: String, failures: Dictionary) -> Variant:
	var names: PackedStringArray = EXPRESSION_INPUTS.duplicate()
	if not node_name.is_empty():
		names.append("node")
	var parsed: Variant = parse_condition(source, names)
	if parsed is String:
		return "the expression does not parse: %s" % parsed
	return _run_expression.bind(parsed, node_name, failures)


## Parses source with names as a condition Expression: the parsed Expression, or a String saying why
## it cannot be one, with no prefix. Text Expression would parse and then ignore is refused: at the
## top level the operator loop stops at the first non-operator token and rewinds and parse() never
## checks the whole source was used (core/math/expression.cpp L1043-1046, L1480-1491 in 4.7.2), so
## '(%s)' and '[%s, 0]' both wrap it; a source reaching for GDScript syntax adds NOT_GDSCRIPT_HINT.
static func parse_condition(source: String, names: PackedStringArray) -> Variant:
	var expression := Expression.new()
	if expression.parse(source, names) != OK:
		return _condition_reason(expression.get_error_text(), source, false)
	if _parses_condition("(%s)" % source, names) and _parses_condition("[%s, 0]" % source, names):
		return expression
	var trailing: String = "text follows a complete expression, and Expression would ignore it"
	return _condition_reason(trailing, source, true)


static func _parses_condition(code: String, names: PackedStringArray) -> bool:
	return Expression.new().parse(code, names) == OK


## reason, with NOT_GDSCRIPT_HINT appended for a GDScript word or ignored trailing text.
static func _condition_reason(reason: String, source: String, whole_source: bool) -> String:
	if whole_source or _names_non_expression_syntax(source):
		return reason + ". " + NOT_GDSCRIPT_HINT
	return reason


## Whether source names GDScript syntax Expression has no operator for (func, if, else, is, as,
## return, var, await, or 'not in'), read with string literals removed so a quoted 'else' is out.
static func _names_non_expression_syntax(source: String) -> bool:
	var literal := RegEx.new()
	literal.compile("\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'")
	var word := RegEx.new()
	word.compile("(?<![\\w.])(func|if|else|is|as|return|var|await)\\b|\\bnot\\s+in\\b")
	return word.search(literal.sub(source, "", true)) != null


## Met when the expression returns true; not run while its node is missing. A failed run counts as
## not met in failures, its distinct texts going to the error feed once each (L1494-1508).
func _run_expression(expression: Expression, node_name: String, failures: Dictionary) -> Array:
	var tree: SceneTree = get_tree()
	var inputs: Array = [tree.root, tree, Input, Engine]
	var node: Node = null
	if not node_name.is_empty():
		node = bridge._find_node(node_name)
		if node == null:
			return _missing(node_name)
		inputs.append(node)
	var value: Variant = expression.execute(inputs, node, false)
	if expression.has_execute_failed():
		var failure: String = expression.get_error_text()
		if note_failure(failures, failure):
			push_error("godot-mcp wait_for: the expression failed: %s" % failure)
		return [false, null]
	return [value is bool and value == true, bridge._json.to_json(value)]


## Counts a failed check in failures, naming its text; true the first time that text is seen.
static func note_failure(failures: Dictionary, failure: String) -> bool:
	failures["count"] = int(failures.get("count", 0)) + 1
	failures["error"] = failure
	var seen: Dictionary = failures.get_or_add("seen", {})
	var fresh: bool = not seen.has(failure)
	seen[failure] = true
	return fresh


## Adds failedChecks {count, error} to an outcome's result when checks of it failed: how many failed
## and the last failure's text (a screenshot wait may check twice in a frame, so it counts checks)
static func add_failed_checks(outcome: Dictionary, failures: Dictionary) -> void:
	if int(failures.get("count", 0)) == 0 or not outcome.has("result"):
		return
	outcome["result"]["failedChecks"] = {"count": failures["count"], "error": failures["error"]}


## params[key] when it is a String, else empty: the server leaves out fields it has no value for.
func _text(params: Dictionary, key: String) -> String:
	return params[key] if params.get(key) is String else ""
