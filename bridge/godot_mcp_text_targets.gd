extends RefCounted
## Text targets for the target resolver (godot_mcp_targets.gd): the one visible Control at or
## under a node that shows a text, or a refusal listing the near misses, the nodes of that name, or
## the several Controls showing it. Static functions called on the script itself; all but find
## work on entries, {control, path, name, shown}, one per visible Control that shows a text.

const ShownText := preload("godot_mcp_shown_text.gd")

## No visible Control shows the text: the text as given, trimmed.
const NO_MATCH := "no visible Control shows '%s'"
## Appended to NO_MATCH: the near misses, each NEAR_MISS (a path and its text as quoted gives it),
## joined with ", ".
const NEAR_MISSES := "; near misses: %s"
const NEAR_MISS := "%s shows '%s'"
## Appended to NO_MATCH for each Control named as the text: its path, its name and its text as
## quoted gives it.
const NAME_HINT := "; %s is named '%s' and shows '%s'"
## Several Controls show the text: the text, the count, then each MATCH_AT, joined with ", ".
const AMBIGUOUS_TEXT := "'%s' is shown by %d Controls: %s; narrow it with under."
const MATCH_AT := "%s at %s"
const MAX_NEAR_MISSES := 5
const MAX_NAME_HINTS := 5
const MAX_LISTED_MATCHES := 10
## How many characters of a shown text NEAR_MISS and NAME_HINT quote.
const MAX_QUOTED := 60


## The entry of the one visible Control at or under under whose shown text, trimmed, is text
## trimmed, case kept; or a String saying why there is none. targets is the resolver: its bridge
## gathers the Controls get_ui_elements lists (the bridge's own and tooltips left out), and its
## viewport_rect places each of several matches as the element refusals do.
static func find(targets: Node, under: Node, text: String) -> Variant:
	var wanted: String = text.strip_edges()
	var entries: Array[Dictionary] = _entries(targets.bridge, under)
	var matches: Array[Dictionary] = matching(entries, wanted)
	if matches.is_empty():
		return miss_refusal(wanted, entries)
	if matches.size() == 1:
		return matches[0]
	for entry: Dictionary in matches.slice(0, MAX_LISTED_MATCHES):
		entry["rect"] = targets.rect_text(targets.viewport_rect(entry["control"]))
	return ambiguity_refusal(wanted, matches)


## An entry for each visible Control at or under under that shows a text, in tree order, leaving
## out every subtree pruned says so.
static func _entries(bridge: Node, under: Node) -> Array[Dictionary]:
	var snapshot: GDScript = bridge._ui_snapshot
	var skip := func(node: Node) -> bool: return pruned(node, bridge, snapshot)
	var controls: Array[Control] = []
	bridge._gather_controls(under, true, skip, controls)
	var entries: Array[Dictionary] = []
	for control: Control in controls:
		var shown: Variant = ShownText.shown_text(control)
		if shown == null:
			continue
		(
			entries
			. append(
				{
					"control": control,
					"path": str(control.get_path()),
					"name": str(control.name),
					"shown": shown,
				}
			)
		)
	return entries


## Whether the scan leaves out node and everything under it: the bridge itself, a node being
## freed (an old menu queued for deletion beside its replacement), or a tooltip (snapshot is the
## UI snapshot script, which tells one).
static func pruned(node: Node, bridge: Node, snapshot: GDScript) -> bool:
	return node == bridge or node.is_queued_for_deletion() or snapshot.is_tooltip(node)


## A shown text as a refusal quotes it: trimmed, and cut to MAX_QUOTED characters with "…".
static func quoted(shown: String) -> String:
	var trimmed: String = shown.strip_edges()
	if trimmed.length() <= MAX_QUOTED:
		return trimmed
	return trimmed.left(MAX_QUOTED) + "…"


## The entries whose shown text, trimmed, is wanted.
static func matching(entries: Array[Dictionary], wanted: String) -> Array[Dictionary]:
	var matches: Array[Dictionary] = []
	for entry: Dictionary in entries:
		if str(entry["shown"]).strip_edges() == wanted:
			matches.append(entry)
	return matches


## NO_MATCH for wanted, with the near misses and the name hints among entries.
static func miss_refusal(wanted: String, entries: Array[Dictionary]) -> String:
	var refusal: String = NO_MATCH % wanted
	var near := PackedStringArray()
	for entry: Dictionary in near_misses(entries, wanted):
		near.append(NEAR_MISS % [entry["path"], quoted(entry["shown"])])
	if not near.is_empty():
		refusal += NEAR_MISSES % ", ".join(near)
	for entry: Dictionary in name_hints(entries, wanted):
		refusal += NAME_HINT % [entry["path"], entry["name"], quoted(entry["shown"])]
	return refusal + "."


## At most MAX_NEAR_MISSES entries whose shown text, trimmed, is wanted in another case, then
## those that contain wanted, then those wanted contains, all regardless of case, each group in
## tree order. An entry showing only whitespace is never a near miss.
static func near_misses(entries: Array[Dictionary], wanted: String) -> Array[Dictionary]:
	var lowered: String = wanted.to_lower()
	var near: Array[Dictionary] = []
	for rank in 3:
		for entry: Dictionary in entries:
			if nearness(str(entry["shown"]).strip_edges().to_lower(), lowered) == rank:
				near.append(entry)
	return near.slice(0, MAX_NEAR_MISSES)


## How near a shown text is to the wanted one, both trimmed and lowered: 0 equal, 1 containing it,
## 2 contained in it, -1 neither or either empty.
static func nearness(shown: String, wanted: String) -> int:
	if shown.is_empty() or wanted.is_empty():
		return -1
	if shown == wanted:
		return 0
	if shown.contains(wanted):
		return 1
	return 2 if wanted.contains(shown) else -1


## At most MAX_NAME_HINTS entries whose node is named wanted, in tree order.
static func name_hints(entries: Array[Dictionary], wanted: String) -> Array[Dictionary]:
	var named: Array[Dictionary] = []
	for entry: Dictionary in entries:
		if entry["name"] == wanted:
			named.append(entry)
	return named.slice(0, MAX_NAME_HINTS)


## AMBIGUOUS_TEXT for wanted, listing at most MAX_LISTED_MATCHES matches with their rects, and …
## when there are more.
static func ambiguity_refusal(wanted: String, matches: Array[Dictionary]) -> String:
	var listed := PackedStringArray()
	for entry: Dictionary in matches.slice(0, MAX_LISTED_MATCHES):
		listed.append(MATCH_AT % [entry["path"], entry["rect"]])
	if matches.size() > MAX_LISTED_MATCHES:
		listed.append("…")
	return AMBIGUOUS_TEXT % [wanted, matches.size(), ", ".join(listed)]
