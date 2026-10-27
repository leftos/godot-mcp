extends Node
## The godot-mcp bridge's conditions, a child of the bridge: parses a condition Expression and
## builds the checks wait_for polls each frame (a node existing, a property's value, an
## expression, the UI changed since the last gesture, a Control showing a text, a node's rect,
## transform or property holding still, a key of a marked node's state).
## The watch parses its expression tracks through parse_condition too.
##
## A check returns [met, value], or [false, null, error] when its condition cannot be met.

const TextTargets := preload("godot_mcp_text_targets.gd")
const Clock := preload("godot_mcp_time.gd")
const State := preload("godot_mcp_state.gd")

## An expression's inputs besides node. Expression resolves only its inputs and its base
## instance's members, not singletons (core/math/expression.cpp L707-734 in 4.7.2).
const EXPRESSION_INPUTS: PackedStringArray = ["root", "tree", "Input", "Engine"]
## Why a source Expression cannot read: GDScript syntax it has no operator for.
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NOT_GDSCRIPT_HINT := "Godot's Expression is not GDScript: it has no lambdas (func), no if/else, no is or as, no not in (write not (a in b)), and no statements; it has calls, indexing, literals and operators such as and, or, not, in, ==, !=, <, <=, >, >=, +, -, *, / and %."
const NO_UI_BASELINE := (
	"uiChanged has no baseline: no input gesture has started since launch or since the last "
	+ 'uiChanged wait was met; send the input first, or wait for {uiChanged: "now"} '
	+ "in a parallel group beside an input in another session"
)
# gdformat joins any split of this text back into one line past gdlint's 100 characters.
# gdlint: ignore=max-line-length
const NO_PLACEMENT := "'%s' is a %s, which has no rect or transform to watch; name a property with stable."
## A state check's node outside the mcp_state group, %s its path.
const NOT_MARKED := "'%s' is not in the mcp_state group; add it to the group to read its state."
## A state check's node whose state cannot be read: its path, then the read's error.
const NO_STATE := "'%s' has no state: %s"
## The state path naming the whole state, as the kept read's flattener keys it.
const WHOLE_STATE := "$"
## The most digits a state path's list index has: int.MaxValue's 10, the server's limit.
const MAX_INDEX_DIGITS := 10

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


## The probe kind's builder makes from params and failures, or a String refusing a kind with no
## builder. A builder returns its probe, or a String saying why there is none.
func _kind_probe(kind: String, params: Dictionary, failures: Dictionary) -> Variant:
	var builders: Dictionary = {
		"exists": _exists_probe,
		"property": _property_probe,
		"expression": _expression_probe,
		"uiChanged": _ui_changed_probe,
		"text": _text_probe,
		"settled": _settled_probe,
		"state": _state_probe,
	}
	if not builders.has(kind):
		return "unknown condition kind '%s'" % kind
	return (builders[kind] as Callable).call(params, failures)


func _exists_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	return _check_exists.bind(_text(params, "node"), bool(params.get("exists", true)))


func _property_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	var property: String = _text(params, "property")
	var mode: String = str(params.get("match", "exact"))
	return check_property.bind(_text(params, "node"), property, params.get("equals"), mode)


func _expression_probe(params: Dictionary, failures: Dictionary) -> Variant:
	return parse_expression(_text(params, "expression"), _text(params, "node"), failures)


## With params.uiChanged "now", takes a fresh baseline as the wait starts (make_probe runs once
## per wait, before its first check), replacing one a gesture left pending; with true, compares
## with the baseline a gesture took, and refuses (NO_UI_BASELINE) when none is pending.
func _ui_changed_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	if str(params.get("uiChanged")) == "now":
		bridge._gestures.use_up_ui_baseline()
		bridge._gestures.take_ui_baseline()
		return _check_ui_changed
	return _check_ui_changed if bridge._gestures.has_ui_baseline() else NO_UI_BASELINE


func _text_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	var text: String = _text(params, "text").strip_edges()
	var mode: String = str(params.get("match", "exact"))
	return _check_text.bind(text, _text(params, "under"), mode, bool(params.get("exists", true)))


func _settled_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	var node_name: String = _text(params, "node")
	var needed: int = int(params.get("stable", 1))
	var property: String = _text(params, "property")
	return _check_settled.bind(node_name, property, needed, settled_state())


func _state_probe(params: Dictionary, _failures: Dictionary) -> Variant:
	var helper: Dictionary = {
		"extension": _text(params, "extension"), "csharpError": _text(params, "csharpError")
	}
	var key: String = _text(params, "state")
	var mode: String = str(params.get("match", "exact"))
	return _check_state.bind(_text(params, "node"), key, params.get("equals"), mode, helper)


## The value at key in the node's state as get_game_state reads it (State.read_one at its default
## depth, then state_at), compared as check_property compares (equals_met); not met while the node
## is missing or its state lacks key, and failed for a node outside the mcp_state group or one
## whose state cannot be read. helper carries the C# helper's extension, or csharpError.
func _check_state(
	node_name: String, key: String, wanted: Variant, mode: String, helper: Dictionary
) -> Array:
	var looked: Array = _lookup(node_name)
	var node: Node = looked[0]
	if node == null:
		return looked[1]
	var reader: Node = bridge._state
	if not node.is_in_group(State.GROUP):
		return [false, null, NOT_MARKED % reader.path_of.call(node)]
	var entry: Dictionary = reader.read_one(
		node, State.DEFAULT_MAX_DEPTH, helper["extension"], helper["csharpError"]
	)
	if entry.has("error"):
		return [false, null, NO_STATE % [entry["path"], entry["error"]]]
	var found: Array = state_at(entry.get("state"), key)
	if not found[0]:
		return found
	return [equals_met(found[1], wanted, mode), found[1]]


## Whether a value read meets a condition's equals: with mode prefix or contains and a String
## wanted, the value must be a String that starts with or holds it, case-sensitive and untrimmed (a
## value of another type never meets it); otherwise json_equal, so null meets only null.
static func equals_met(actual: Variant, wanted: Variant, mode: String) -> bool:
	if mode == "exact" or not wanted is String:
		return Clock.json_equal(actual, wanted)
	if not actual is String:
		return false
	if mode == "prefix":
		return (actual as String).begins_with(wanted)
	return (actual as String).contains(wanted)


## The value at path in state as get_game_state's keys find it (the server's StateMerge.TryFind):
## [true, value], or [false, null] when a step of it is missing. path is WHOLE_STATE, or names
## split on dots, each a member name followed by any [n] list indexes (an empty name only before
## an index); a cut marker the read wrote ("<depth limit: ...>", "<size limit>") reached on the way
## ends the walk with the marker as the value.
static func state_at(state: Variant, path: String) -> Array:
	if path == WHOLE_STATE:
		return [true, state]
	var at: Variant = state
	for part: String in path.split("."):
		if _is_cut_marker(at):
			return [true, at]
		var bracket: int = part.find("[")
		var name_part: String = part if bracket < 0 else part.substr(0, bracket)
		var stepped: Array = _state_member(at, name_part, bracket >= 0)
		if stepped[0]:
			stepped = _state_indexes(stepped[1], "" if bracket < 0 else part.substr(bracket))
		if not stepped[0]:
			return [false, null]
		at = stepped[1]
	return [true, at]


## Whether value is a cut marker the state reader writes for a value it did not write.
static func _is_cut_marker(value: Variant) -> bool:
	if not value is String:
		return false
	var text: String = value
	return text.begins_with("<depth limit: ") or text == State.SIZE_LIMIT


## [true, at's member name], or [true, at] for an empty name when allow_empty (one before an
## index); [false, null] when at is no Dictionary or lacks it.
static func _state_member(at: Variant, name_part: String, allow_empty: bool) -> Array:
	if name_part.is_empty():
		return [allow_empty, at if allow_empty else null]
	if at is Dictionary and (at as Dictionary).has(name_part):
		return [true, at[name_part]]
	return [false, null]


## [true, the item] after stepping through each [n] of indexes into a list; [false, null] on a
## malformed index (digits only, as the server parses it) or one past its list's end.
static func _state_indexes(at: Variant, indexes: String) -> Array:
	var item: Variant = at
	var rest: String = indexes
	while not rest.is_empty():
		var close: int = rest.find("]")
		var digits: String = (
			rest.substr(1, close - 1) if rest.begins_with("[") and close > 0 else ""
		)
		if not _is_index(digits):
			return [false, null]
		if not item is Array or int(digits) >= (item as Array).size():
			return [false, null]
		item = item[int(digits)]
		rest = rest.substr(close + 1)
	return [true, item]


## Whether digits is a list index the server's int.TryParse takes: digits only, and at most
## MAX_INDEX_DIGITS of them, so a longer one is not found without int() logging an engine error.
static func _is_index(digits: String) -> bool:
	return (
		not digits.is_empty()
		and digits.length() <= MAX_INDEX_DIGITS
		and digits.lstrip("0123456789").is_empty()
	)


## A settled check's count before its first reading: held -1 until a value has been read, then
## the frames that kept last since it was first read, frame the process frame of the last check.
static func settled_state() -> Dictionary:
	return {"last": null, "frame": -1, "held": -1}


## Reads the node's watched value (_settled_reading) and counts it into state (settled_answer) at
## this process frame; while the node is missing, _lookup's answer resets the count.
func _check_settled(node_name: String, property: String, needed: int, state: Dictionary) -> Array:
	var found: Array = _lookup(node_name)
	var reading: Array = found[1]
	if found[0] != null:
		reading = _settled_reading(found[0], node_name, property)
	return settled_answer(state, reading, Engine.get_process_frames(), needed)


## A settled check's answer from reading, [true, value] for the watched value's JSON form, or the
## probe answer that stood for it ([false, null] while the node is missing, or a failure), which
## resets the count and is passed on as it is. A value equal to the last one (Clock.json_equal)
## counts one frame when frame differs from the last check's, so two checks in one frame count it
## once; a different one starts the count again from 0. Met once needed frames have held, its value
## {frames, value}, frames the frames held so far.
static func settled_answer(state: Dictionary, reading: Array, frame: int, needed: int) -> Array:
	if not reading[0]:
		state["last"] = null
		state["held"] = -1
		return reading
	var value: Variant = reading[1]
	if int(state["held"]) < 0 or not Clock.json_equal(value, state["last"]):
		state["last"] = value
		state["held"] = 0
	elif frame != int(state["frame"]):
		state["held"] = int(state["held"]) + 1
	state["frame"] = frame
	var held: int = state["held"]
	return [held >= needed, {"frames": held, "value": value}]


## The value a settled check watches on node, as [true, its JSON form]: property when given, read
## as check_property reads it; else a Control's global rect or a Node2D's or Node3D's
## global_transform. Any other node without property fails the check, which ends the wait with
## {error} as a property the node lacks does.
func _settled_reading(node: Node, node_name: String, property: String) -> Array:
	if not property.is_empty():
		return _read_property(node, property)
	var placement: Variant = null
	if node is Control:
		placement = (node as Control).get_global_rect()
	elif node is Node2D:
		placement = (node as Node2D).global_transform
	elif node is Node3D:
		placement = (node as Node3D).global_transform
	if placement == null:
		return [false, null, NO_PLACEMENT % [node_name, node.get_class()]]
	return [true, bridge._json.to_json(placement)]


## Whether text (trimmed) shows on the visible Controls at or under under, the root when empty,
## matched as mode says, as a {text} target is (TextTargets.matching), answered by
## text_answer. Nothing shows
## under an under no node has, or one hidden or being freed; one that several nodes have fails the
## check. Each check walks the Controls afresh.
func _check_text(text: String, under: String, mode: String, wanted: bool) -> Array:
	var targets: Node = bridge._gestures._targets
	var scope: Variant = _text_scope(targets, under)
	if scope is String:
		return [false, null, scope]
	var entries: Array[Dictionary] = []
	if scope != null:
		entries = TextTargets.entries_under(bridge, scope)
	return text_answer(text, TextTargets.matching(entries, text, mode), wanted, targets)


## A text check's answer from the entries showing text, placing through targets, the resolver,
## only the entries it reports. An appear (wanted true) is met by exactly one, its value {path,
## shown, rect}, rect its viewport rect as get_ui_elements writes a rect; several showing it fail
## the check with TextTargets' ambiguity listing, the listed ones placed. A leave (wanted false)
## is met when none does and holds while any does, the first one's value as its value.
static func text_answer(
	text: String, matches: Array[Dictionary], wanted: bool, targets: Node
) -> Array:
	if matches.is_empty():
		return [not wanted, null]
	if wanted and matches.size() > 1:
		var listed: Array[Dictionary] = TextTargets.placed(targets, matches)
		return [false, null, TextTargets.ambiguity_refusal(text, listed)]
	var entry: Dictionary = matches[0]
	var rect: Variant = targets.bridge._json.to_json(targets.viewport_rect(entry["control"]))
	return [wanted, {"path": entry["path"], "shown": entry["shown"], "rect": rect}]


## The node a text check scans: the root when under is empty, else the one node under names as an
## element target names it (_node_at_path for a path or a unique name); null when no node has it,
## or a String when several nodes have that bare name.
func _text_scope(targets: Node, under: String) -> Variant:
	if under.is_empty():
		return targets.get_tree().root
	if under.contains("/") or under.begins_with("%"):
		return _node_at_path(under)
	var named: Array[Node] = targets._nodes_named(under)
	if named.size() > 1:
		return targets._ambiguous(under, named)
	return named[0] if named.size() == 1 else null


## The node a path or a unique name names; null while it is missing, or the failure text of a
## path starting with a unique name more than one scene holds.
func _node_at_path(node_name: String) -> Variant:
	var found: Array = _lookup(node_name)
	if found[0] != null:
		return found[0]
	var missing: Array = found[1]
	return missing[2] if missing.size() > 2 else null


## Met on the first check whose UI differs from the input module's baseline, with the change as
## its value; a met wait uses the baseline up, and one that times out leaves it.
func _check_ui_changed() -> Array:
	var change: Dictionary = bridge._gestures.ui_change()
	if change.is_empty():
		return [false, null]
	bridge._gestures.use_up_ui_baseline()
	return [true, change]


func _check_exists(node_name: String, wanted: bool) -> Array:
	var found: Array = _lookup(node_name)
	var present: bool = found[0] != null
	var missing: Array = found[1]
	if missing.size() > 2:
		return missing
	return [present == wanted, present]


## The root a unique name is looked up from: get_tree().root, read only here, so a test can stand
## in for it with no live tree. A path or a bare name goes through the bridge's _find_node, and an
## expression's inputs come from the main loop.
func _tree_root() -> Node:
	return get_tree().root


## The node a check names, and the answer it gives while the node is missing: [the node or null,
## [false, null], or [false, null, why no wait can settle a unique name more than one scene
## holds]]. A unique name walks the scenes once (the inspector's unique_lookup), any other name
## through the bridge's lookup.
func _lookup(node_name: String) -> Array:
	if not node_name.begins_with("%"):
		return [bridge._find_node(node_name), [false, null]]
	var found: Array = bridge._inspect.unique_lookup(_tree_root(), node_name)
	var ambiguity: String = found[1]
	var missing: Array = [false, null] if ambiguity.is_empty() else [false, null, ambiguity]
	return [found[0], missing]


## The property (a path such as position:x) as run_script returns values, compared in JSON
## space, or with mode prefix or contains as equals_met compares a string; not met while the node
## is missing, and failed once it is found without the property.
func check_property(node_name: String, property: String, wanted: Variant, mode: String) -> Array:
	var found: Array = _lookup(node_name)
	var node: Node = found[0]
	if node == null:
		return found[1]
	var read: Array = _read_property(node, property)
	if read.size() > 2:
		return read
	return [equals_met(read[1], wanted, mode), read[1]]


## node's property (a path such as position:x) as run_script returns values: [true, value], or
## [false, null, error] naming the path up to the first part node or its value lacks. get_indexed
## reads a missing sub-name as null without an error (core/object/object.cpp L451-473 in 4.7.2),
## so a null read is walked again by missing_subname.
func _read_property(node: Node, property: String) -> Array:
	var parts: PackedStringArray = property.split(":")
	if not bridge._json.has_property(node, parts[0]):
		return _no_property(node, parts[0])
	var value: Variant = node.get_indexed(NodePath(property))
	if typeof(value) == TYPE_NIL:
		var lacking: String = missing_subname(node.get(parts[0]), parts)
		if not lacking.is_empty():
			return _no_property(node, lacking)
	return [true, bridge._json.to_json(value)]


static func _no_property(node: Node, path: String) -> Array:
	return [false, null, "'%s' has no property '%s'." % [node.get_path(), path]]


## The path through parts up to the first part after the first that its value lacks, value being
## the first part's; empty when it has them all. Each part is read with Variant.get as
## Expression's indexing reads it, which for a String index reads a named member, a Dictionary's
## key or an Object's property as get_indexed does (core/variant/variant_setget.cpp L1235-1268).
static func missing_subname(value: Variant, parts: PackedStringArray) -> String:
	var member := Expression.new()
	member.parse("base[key]", ["base", "key"])
	var current: Variant = value
	for index: int in range(1, parts.size()):
		current = member.execute([current, parts[index]], null, false)
		if member.has_execute_failed():
			return ":".join(parts.slice(0, index + 1))
	return ""


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
## not met in failures, its distinct texts going to the error feed once each (L1494-1508), outside
## the mute window _execute runs it in.
func _run_expression(expression: Expression, node_name: String, failures: Dictionary) -> Array:
	var tree := Engine.get_main_loop() as SceneTree
	var inputs: Array = [tree.root, tree, Input, Engine]
	var node: Node = null
	if not node_name.is_empty():
		var found: Array = _lookup(node_name)
		node = found[0]
		if node == null:
			return found[1]
		inputs.append(node)
	var value: Variant = _execute(expression, inputs, node, failures)
	if expression.has_execute_failed():
		var failure: String = expression.get_error_text()
		if note_failure(failures, failure):
			push_error("godot-mcp wait_for: the expression failed: %s" % failure)
		return [false, null]
	return [value is bool and value == true, bridge._json.to_json(value)]


## Runs expression with a mute window for this script open on the bridge's logger, as the watch
## runs its tracks: Expression evaluates both sides of and/or (core/math/expression.cpp L1261-1286
## in 4.7.2), so a guard such as get_child_count() > 0 does not stop get_child(0) from raising the
## engine's index error, which stays out of the error feed and is counted in failures instead
## (note_muted). The logger is read here, since a dormant bridge has none until its first join. The
## logger's windows are main-thread state, so off the main thread the expression runs unmuted.
func _execute(expression: Expression, inputs: Array, node: Node, failures: Dictionary) -> Variant:
	var logger: Logger = bridge._logger
	if logger == null or OS.get_thread_caller_id() != OS.get_main_thread_id():
		return expression.execute(inputs, node, false)
	var own: String = (get_script() as Script).resource_path
	logger.mute(true, own)
	var value: Variant = expression.execute(inputs, node, false)
	logger.mute(false, own)
	note_muted(failures, logger.take_muted(own))
	return value


## Adds the engine errors a check's mute window dropped ({count, error}, or {} for none) to
## failures' muted, which keeps the first one's text.
static func note_muted(failures: Dictionary, muted: Dictionary) -> void:
	if muted.is_empty():
		return
	var total: Dictionary = failures.get_or_add("muted", {"count": 0, "error": muted["error"]})
	total["count"] += int(muted["count"])


## Counts a failed check in failures, naming its text; true the first time that text is seen.
static func note_failure(failures: Dictionary, failure: String) -> bool:
	failures["count"] = int(failures.get("count", 0)) + 1
	failures["error"] = failure
	var seen: Dictionary = failures.get_or_add("seen", {})
	var fresh: bool = not seen.has(failure)
	seen[failure] = true
	return fresh


## Adds failedChecks {count, error} to an outcome's result when checks of it failed: how many failed
## and the last failure's text (a screenshot wait may check twice in a frame, so it counts checks);
## and mutedErrors {count, error} when its checks raised engine errors kept out of the error feed:
## how many, and the first one's text.
static func add_failed_checks(outcome: Dictionary, failures: Dictionary) -> void:
	if not outcome.has("result"):
		return
	if int(failures.get("count", 0)) > 0:
		outcome["result"]["failedChecks"] = {"count": failures["count"], "error": failures["error"]}
	if failures.has("muted"):
		outcome["result"]["mutedErrors"] = (failures["muted"] as Dictionary).duplicate()


## params[key] when it is a String, else empty: the server leaves out fields it has no value for.
func _text(params: Dictionary, key: String) -> String:
	return params[key] if params.get(key) is String else ""
