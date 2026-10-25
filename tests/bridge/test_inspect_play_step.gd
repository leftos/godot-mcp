extends "res://gd_test.gd"
## The inspector's PlayStep by name (bridge/godot_mcp_inspect.gd): a call to a scratch root's
## PlayStep with one String argument plays the step whose GetStepName is that String, read from
## the root at the call; a name matching no step or several is refused, and every other call goes
## through as it came. Each call is made through call_now, as the options.call of capture_frames,
## watch and wait_for makes it, on a root built here from source.

const BRIDGE_SOURCE := (
	"extends Node\n\nvar _json: GDScript\nvar _logger: Object\nvar target: Node\n\n\n"
	+ "func _find_node(_node_name: String) -> Node:\n\treturn target\n"
)
## A logger standing in for the error feed: each sequence() is a new mark, the first raise() logs
## its error at the latest mark, and first_error_since answers it for any mark taken at or before
## that one.
const LOGGER_SOURCE := (
	'extends RefCounted\n\nvar count: int = 0\nvar error: String = ""\nvar raised_at: int = -1\n\n\n'
	+ "func sequence() -> int:\n\tcount += 1\n\treturn count\n\n\n"
	+ "func raise(message: String) -> void:\n\tif error.is_empty():\n"
	+ "\t\terror = message\n\t\traised_at = count\n\n\n"
	+ 'func first_error_since(mark: int) -> String:\n\treturn error if raised_at >= mark else ""\n'
)
## A scratch root whose GetStepCount answers null.
const NO_COUNT_SOURCE := (
	"extends Node\n\nvar played: Array = []\n\n\n"
	+ "func GetStepCount() -> Variant:\n\treturn null\n\n\n"
	+ "func GetStepName(_index: int) -> String:\n\treturn 'open'\n\n\n"
	+ "func PlayStep(index: int) -> void:\n\tplayed.append(index)\n"
)
## A scratch root of three steps whose GetStepName answers null for index 1.
const NO_NAME_SOURCE := (
	"extends Node\n\nvar played: Array = []\n\n\n"
	+ "func GetStepCount() -> int:\n\treturn 3\n\n\n"
	+ "func GetStepName(index: int) -> Variant:\n\treturn null if index == 1 else 'open'\n\n\n"
	+ "func PlayStep(index: int) -> void:\n\tplayed.append(index)\n"
)
## A scratch root of two steps whose GetStepName logs an error to logger and still answers a name.
const RAISING_SOURCE := (
	"extends Node\n\nvar played: Array = []\nvar logger: Object\n\n\n"
	+ "func GetStepCount() -> int:\n\treturn 2\n\n\n"
	+ "func GetStepName(index: int) -> String:\n\tlogger.raise('names[%d] is gone' % index)\n"
	+ "\treturn 'open'\n\n\n"
	+ "func PlayStep(index: int) -> void:\n\tplayed.append(index)\n"
)
## A scratch root whose steps are its names; PlayStep records the index it played and returns it.
const SCRATCH_SOURCE := (
	"extends Node\n\nvar names: Array = []\nvar played: Array = []\n\n\n"
	+ "func GetStepCount() -> int:\n\treturn names.size()\n\n\n"
	+ "func GetStepName(index: int) -> String:\n\treturn names[index]\n\n\n"
	+ "func PlayStep(index: int) -> int:\n\tplayed.append(index)\n\treturn index\n"
)
## A node with GetStepCount and a PlayStep of its own taking a String, but no GetStepName.
const UNNAMED_SOURCE := (
	"extends Node\n\nvar played: Array = []\n\n\n"
	+ "func GetStepCount() -> int:\n\treturn 3\n\n\n"
	+ "func PlayStep(label: String) -> String:\n\tplayed.append(label)\n\treturn label\n"
)
const STEPS := ["open", "move", "close"]

var _inspect_script: GDScript = load_bridge_script("godot_mcp_inspect.gd")


func test_a_name_plays_the_step_it_names() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, STEPS)
	var outcome: Variant = rig["inspect"].call_now(_play("move"))
	assert_eq(outcome, {"value": 1}, "the step named move is index 1")
	assert_eq(rig["target"].played, [1], "PlayStep ran once, with the index")
	_free(rig)


func test_a_number_is_an_index_as_before() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, STEPS)
	var outcome: Variant = rig["inspect"].call_now(_play(2.0))
	assert_eq(outcome, {"value": 2}, "a JSON number plays that index")
	assert_eq(rig["target"].played, [2], "PlayStep ran with the index given")
	_free(rig)


func test_a_name_that_is_a_number_is_still_a_name() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, ["a", "7"])
	var outcome: Variant = rig["inspect"].call_now(_play("7"))
	assert_eq(outcome, {"value": 1}, "the step named 7 is index 1, not index 7")
	assert_eq(rig["target"].played, [1], "PlayStep ran with the named step's index")
	_free(rig)


func test_an_unknown_name_is_refused_with_the_step_list() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, STEPS)
	var outcome: Variant = rig["inspect"].call_now(_play("jump"))
	assert_eq(
		outcome,
		'PlayStep "jump" matches no step; its steps are 0: open, 1: move, 2: close',
		"the refusal lists every step by index and name"
	)
	assert_eq(rig["target"].played, [], "no step ran")
	_free(rig)


func test_a_name_on_a_root_without_steps_is_refused() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, [])
	var outcome: Variant = rig["inspect"].call_now(_play("jump"))
	assert_eq(
		outcome, 'PlayStep "jump" matches no step; it has no steps', "the refusal says it has none"
	)
	_free(rig)


func test_a_name_several_steps_share_is_refused_with_their_indices() -> void:
	var rig: Dictionary = _rig(SCRATCH_SOURCE, ["open", "move", "open"])
	var outcome: Variant = rig["inspect"].call_now(_play("open"))
	assert_eq(
		outcome,
		'PlayStep "open" names 2 steps (0, 2); call it by index',
		"no first match wins: the refusal names both indices"
	)
	assert_eq(rig["target"].played, [], "no step ran")
	_free(rig)


func test_a_node_without_get_step_name_takes_the_string_as_it_came() -> void:
	var rig: Dictionary = _rig(UNNAMED_SOURCE, [])
	var outcome: Variant = rig["inspect"].call_now(_play("move"))
	assert_eq(outcome, {"value": "move"}, "the String reaches the node's own PlayStep")
	assert_eq(rig["target"].played, ["move"], "PlayStep ran with the String")
	_free(rig)


func test_a_step_count_that_is_not_an_int_is_refused() -> void:
	var rig: Dictionary = _rig(NO_COUNT_SOURCE, [])
	var outcome: Variant = rig["inspect"].call_now(_play("open"))
	assert_eq(
		outcome,
		'PlayStep "open": GetStepCount returned null, not a step count',
		"a null count refuses the call"
	)
	assert_eq(rig["target"].played, [], "no step ran")
	_free(rig)


func test_a_step_name_that_is_not_a_string_is_refused() -> void:
	var rig: Dictionary = _rig(NO_NAME_SOURCE, [])
	var outcome: Variant = rig["inspect"].call_now(_play("open"))
	assert_eq(
		outcome,
		'PlayStep "open": GetStepName(1) returned null, not a name',
		"a null name refuses the call, naming its index"
	)
	assert_eq(rig["target"].played, [], "no step ran")
	_free(rig)


func test_an_error_raised_reading_the_names_is_refused() -> void:
	var rig: Dictionary = _rig(RAISING_SOURCE, [])
	rig["target"].logger = rig["bridge"]._logger
	var outcome: Variant = rig["inspect"].call_now(_play("open"))
	assert_eq(
		outcome,
		'PlayStep "open": reading its step names raised: names[0] is gone',
		"the feed's first error since the lookup began"
	)
	assert_eq(rig["target"].played, [], "no step ran")
	_free(rig)


## {inspect, bridge, target}: an inspector whose stand-in bridge finds target, a node compiled from
## source, for any name; a scratch root's names are steps.
func _rig(source: String, steps: Array) -> Dictionary:
	var target: Node = _compile(source)
	if "names" in target:
		target.names = steps
	var bridge: Node = _compile(BRIDGE_SOURCE)
	bridge._json = load_bridge_script("godot_mcp_json.gd")
	bridge._logger = _compile(LOGGER_SOURCE)
	bridge.target = target
	var inspect: Node = _inspect_script.new()
	inspect._bridge = bridge
	return {"inspect": inspect, "bridge": bridge, "target": target}


func _free(rig: Dictionary) -> void:
	rig["inspect"].free()
	rig["bridge"].free()
	rig["target"].free()


## A PlayStep call's params as the server sends them, with one argument.
static func _play(argument: Variant) -> Dictionary:
	return {"node": "/root/ScratchScene", "method": "PlayStep", "args": [argument]}


## An instance of a script compiled from source.
static func _compile(source: String) -> Object:
	var script := GDScript.new()
	script.source_code = source
	script.reload()
	return script.new()
