extends RefCounted
## The values of a scene opened headless, to and from JSON: the bridge's JSON module
## (bridge/godot_mcp_json.gd, beside this folder in the checkout and the published layout alike),
## with a node read and written as its path from the scene's root, since an opened scene is in no
## tree.

const Json := preload("../bridge/godot_mcp_json.gd")
const REFUSED: Array = [false, null]


## The JSON module's to_json, a node reading as its path from root ("." for root itself, null for
## a node outside it). The module's node_root is set for the call alone.
static func to_json(value: Variant, root: Node) -> Variant:
	Json.node_root = root
	var json: Variant = Json.to_json(value)
	Json.node_root = null
	return json


## [true, value] with a JSON value converted to the type a property entry declares, or
## [false, null]. A Node-typed export (PROPERTY_HINT_NODE_TYPE) takes a path from root, or null to
## clear it; a missing node, or one not of the class the hint names, is refused. Anything else
## converts as the JSON module's from_json says.
static func from_json(value: Variant, info: Dictionary, root: Node) -> Array:
	if _is_node_export(info):
		return _node_from_json(value, str(info.get("hint_string", "")), root)
	return Json.from_json(value, info)


static func _is_node_export(info: Dictionary) -> bool:
	var hint: int = int(info.get("hint", PROPERTY_HINT_NONE))
	return int(info.get("type", TYPE_NIL)) == TYPE_OBJECT and hint == PROPERTY_HINT_NODE_TYPE


static func _node_from_json(value: Variant, hint_string: String, root: Node) -> Array:
	if typeof(value) == TYPE_NIL:
		return [true, null]
	if not value is String or NodePath(value).is_absolute():
		return REFUSED
	var node: Node = root.get_node_or_null(NodePath(value))
	if node == null or not _fits_hint(node, hint_string):
		return REFUSED
	return [true, node]


## Whether node is of one of the classes hint_string lists ("A,B"), a native class or the
## class_name of its script or of one of that script's bases, or hint_string is empty.
static func _fits_hint(node: Node, hint_string: String) -> bool:
	if hint_string.is_empty():
		return true
	var script_classes: PackedStringArray = []
	var script: Script = node.get_script() as Script
	while script != null:
		script_classes.append(str(script.get_global_name()))
		script = script.get_base_script()
	for class_title: String in hint_string.split(","):
		var wanted: String = class_title.strip_edges()
		if node.is_class(wanted) or wanted in script_classes:
			return true
	return false
