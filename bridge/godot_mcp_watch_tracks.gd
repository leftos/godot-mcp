extends RefCounted
## The godot-mcp bridge's watch tracks, a static helper of the watch (godot_mcp_watch.gd): builds
## its property and expression tracks from their specs, refusing a missing node or property, an
## expression that does not parse, or a minDelta on a track that does not read a number or a
## vector, and reads them once (read_values), keyed as the server keys a track (track_key). The
## watch samples the tracks it builds here, and capture_frames resolves and reads its tracks here
## too, through the watch.


## The tracks of params.properties and params.expressions (_property_track, _expression_track),
## or the first refusal; watch resolves them (its _resolve) and gives the bridge.
static func value_tracks(watch: Node, params: Dictionary) -> Variant:
	var tracks: Array = []
	var properties := func(spec: Dictionary) -> Variant: return _property_track(watch, spec)
	var expressions := func(spec: Dictionary) -> Variant: return _expression_track(watch, spec)
	var refusal: String = watch._resolve(params.get("properties"), properties, tracks)
	if refusal.is_empty():
		refusal = watch._resolve(params.get("expressions"), expressions, tracks)
	return tracks if refusal.is_empty() else refusal


## Reads tracks now, as watch samples them (its _read): {values: {<key>: JSON value}}, keyed as the
## server keys a track (track_key), plus trackErrors: {<key>: text} for each read that failed,
## whose value is then null; {} for no tracks.
static func read_values(watch: Node, tracks: Array) -> Dictionary:
	if tracks.is_empty():
		return {}
	var values: Dictionary = {}
	var errors: Dictionary = {}
	for track: Dictionary in tracks:
		var value: Variant = watch._read(track)
		var failure: Variant = _read_failure(track, value)
		values[track_key(track)] = watch.bridge._json.to_json(value) if failure == null else null
		if failure != null:
			errors[track_key(track)] = failure
	return {"values": values} if errors.is_empty() else {"values": values, "trackErrors": errors}


## A track's key: its name, else its node and property as the request gave them, node:property.
static func track_key(track: Dictionary) -> String:
	var spec: Dictionary = track["spec"]
	if spec.get("name") is String:
		return spec["name"]
	return "%s:%s" % [spec.get("node", ""), spec.get("property", "")]


## Why watch's _read value is not the track's: a freed node's or a failed expression's text, else
## null.
static func _read_failure(track: Dictionary, value: Variant) -> Variant:
	if _is_error(value):
		return value["$error"]
	if track.has("path") and value is Dictionary and value.has("$freed"):
		return "its node %s was freed" % track["node_path"]
	return null


## A property track {node, property, name?, minDelta?}: refused when the node or the property is
## missing, or when minDelta is set and the first read is not numeric.
static func _property_track(watch: Node, spec: Dictionary) -> Variant:
	var bridge: Node = watch.bridge
	var node_name: String = str(spec.get("node", ""))
	var property: String = str(spec.get("property", ""))
	var node: Node = bridge._find_node(node_name)
	if node == null:
		return bridge._inspect.not_found(node_name, "get_scene_tree lists the nodes' paths")
	var first: String = property.get_slice(":", 0)
	if not bridge._json.has_property(node, first):
		return "'%s' has no property '%s'." % [node.get_path(), first]
	var track: Dictionary = _new_track(watch, spec, node)
	track["path"] = NodePath(property)
	return _checked_min_delta(watch, track)


## An expression track {name, expression, node?, minDelta?}, parsed once with wait_for's inputs
## (node too, also the base instance, when given) through the conditions' parse_condition:
## refused when its node is missing, when it does not parse (Expression ignores trailing text and
## GDScript syntax it has no operator for) or when minDelta is set and its first read is not
## numeric.
static func _expression_track(watch: Node, spec: Dictionary) -> Variant:
	var bridge: Node = watch.bridge
	var conditions_script: GDScript = watch._conditions_script
	var name: String = str(spec.get("name", ""))
	var node: Node = null
	if spec.get("node") is String:
		node = bridge._find_node(spec["node"])
		if node == null:
			return bridge._inspect.not_found(spec["node"], "get_scene_tree lists the nodes' paths")
	var names: PackedStringArray = conditions_script.EXPRESSION_INPUTS.duplicate()
	if node != null:
		names.append("node")
	var parsed: Variant = conditions_script.parse_condition(str(spec.get("expression", "")), names)
	if parsed is String:
		return "The expression of track '%s' does not parse: %s" % [name, parsed]
	var track: Dictionary = _new_track(watch, spec, node)
	track["expression"] = parsed
	return _checked_min_delta(watch, track)


## A track's state: its spec, its node (null for an expression without one), minDelta (0 for the
## default rule), the samples' summary and the kept points (watch's _keep).
static func _new_track(watch: Node, spec: Dictionary, node: Node) -> Dictionary:
	return {
		"spec": spec,
		"node": node,
		"has_node": node != null,
		"node_path": watch._path_of(node),
		"min_delta": float(spec.get("minDelta", 0.0)),
		"done": false,
		"samples": 0,
		"last": null,
		"has_kept": false,
		"last_kept": null,
		"kept": 0,
		"first_points": [],
		"ring": [],
		"ring_head": 0,
		"range_kind": -1,
		"min": [],
		"max": [],
		"min_at": [],
		"max_at": [],
	}


## track, or the refusal of its minDelta when a read now is neither a number nor a vector; an
## expression whose read fails is accepted. A track without minDelta is not read here, so an
## expression runs only in the frames it samples.
static func _checked_min_delta(watch: Node, track: Dictionary) -> Variant:
	if float(track["min_delta"]) <= 0.0:
		return track
	var first: Variant = watch._read(track)
	if watch._range_kind(first) >= 0 or _is_error(first):
		return track
	return watch.MIN_DELTA_REFUSAL % [_label(track), type_string(typeof(first))]


## The track's name for a message: its name, else its node and property.
static func _label(track: Dictionary) -> String:
	var spec: Dictionary = track["spec"]
	if spec.get("name") is String:
		return spec["name"]
	return "%s:%s" % [track["node_path"], spec.get("property", "")]


static func _is_error(value: Variant) -> bool:
	return value is Dictionary and value.size() == 1 and value.has("$error")
