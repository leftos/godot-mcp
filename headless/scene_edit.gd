extends RefCounted
## What the headless scene edits share: opening a scene as the editor does, finding a node by its
## path relative to the scene root, refusing an edit a save would lose, and saving it through
## scene_files.gd, which puts back the uids a --script save leaves out.
##
## A scene opens with GEN_EDIT_STATE_MAIN, so instanced and inherited scenes stay references when
## it is packed again (GEN_EDIT_STATE_DISABLED flattens an inherited scene and doubles its
## inherited connections: 4.7.2 packed_scene.cpp L233-236, L682). Only text scenes are written:
## outside the editor a binary scene's uid cannot be read back (see SceneFiles.uid_of).

const SceneFiles := preload("scene_files.gd")
const Json := preload("../bridge/godot_mcp_json.gd")
const RES_PREFIX := "res://"
## A packed node record's fields before its property count: parent, owner, type, name, instance.
const RECORD_HEAD := 5
## The type of a packed node record an instanced or inherited scene holds (4.7.2 packed_scene.h).
const RECORD_TYPE_INSTANTIATED := 0x7FFFFFFF
## The bits of a stored property's name index that index names; bit 30 marks a deferred node path.
const PROPERTY_NAME_MASK := (1 << 30) - 1
## How many base scenes deep an inherited scene is followed.
const MAX_BASE_DEPTH := 64

## The engine log operations.gd keeps (its ErrorLog, with count() and since(start)), so an edit
## can quote what a load it refuses logged; null when nothing set it.
static var engine_log: Object = null


## {root} for the scene at path instantiated for editing, or {error}.
static func open(path: String) -> Dictionary:
	var scene := ResourceLoader.load(path) as PackedScene
	if scene == null:
		return {"error": "%s did not load as a scene" % path}
	var root: Node = instantiate_native(scene, PackedScene.GEN_EDIT_STATE_MAIN)
	if root == null:
		return {"error": "%s loaded but could not be instantiated" % path}
	return {"root": root}


## scene instantiated with edit_state, or null, with each engine property a script member of the
## same name hides holding the value the scenes store for it. An instance is built whole, script
## included, before the instancing scene's values are set (4.7.2 packed_scene.cpp L233, L266,
## L494), and Object.set gives the script instance first refusal (object.cpp L239-245), so an
## override of such a property reaches the member instead. A stored Object value is left as the
## instantiation set it, so a resource local to the scene is never shared.
static func instantiate_native(scene: PackedScene, edit_state: int) -> Node:
	var root: Node = scene.instantiate(edit_state)
	if root != null:
		_restore_hidden(root, scene.get_state(), 0)
	return root


## Sets the hidden engine properties of the nodes under root that state's records store, a
## record's instanced or base scene first, so an inner value lands before the outer one.
static func _restore_hidden(root: Node, state: SceneState, depth: int) -> void:
	if depth > MAX_BASE_DEPTH:
		return
	for index in state.get_node_count():
		var node: Node = root.get_node_or_null(state.get_node_path(index))
		if node == null:
			continue
		var inner: PackedScene = state.get_node_instance(index)
		if inner != null:
			_restore_hidden(node, inner.get_state(), depth + 1)
		_restore_values(node, state, index)


## Sets on node, through ClassDB, each hidden engine property the record at index of state stores,
## other than an Object value.
static func _restore_values(node: Node, state: SceneState, index: int) -> void:
	var names: PackedStringArray = _hidden_names(node)
	if names.is_empty():
		return
	for pair in state.get_node_property_count(index):
		var property: String = state.get_node_property_name(index, pair)
		var value: Variant = state.get_node_property_value(index, pair)
		if names.has(property) and typeof(value) != TYPE_OBJECT:
			ClassDB.class_set_property(node, property, value)


## The node at path, relative to root ("." is root itself), or null.
static func find(root: Node, path: String) -> Node:
	return root.get_node_or_null(NodePath(path))


## {node} for the node at path relative to root, or {error} saying the scene at scene_path has no
## such node.
static func node_or_error(root: Node, path: String, scene_path: String) -> Dictionary:
	var node: Node = find(root, path)
	if node == null:
		return {"error": "%s has no node %s." % [scene_path, path]}
	return {"node": node}


## Why a change to node (found at path) would not be saved, or "". A node inherited from the base
## scene saves its overrides; a node inside an instance saves them only when the instance is an
## editable instance (its children shown in the editor); otherwise as instance_refusal says.
static func unsaved_edit_refusal(root: Node, node: Node, path: String) -> String:
	var instance: Node = node.owner
	if node != root and instance != null and instance != root:
		if root.is_editable_instance(instance):
			return ""
	return instance_refusal(root, node, path)


## Why an edit of node (found at path) would be lost, or "": a node whose owner is not the scene's
## root belongs to an instanced scene, whose file holds it. An instance's own root may be edited.
static func instance_refusal(root: Node, node: Node, path: String) -> String:
	if node == root or node.owner == root:
		return ""
	var instance: Node = node.owner
	if instance == null:
		return "%s is not saved with the scene: it has no owner." % path
	var file: String = instance.scene_file_path
	return (
		"%s is inside the instance of %s at %s; its changes would not be saved. Edit %s instead."
		% [path, file, String(root.get_path_to(instance)), file]
	)


## Why deleting node (found at path) from the scene at scene_path would be undone on reload, or
## "": a node the scene inherits from its base scene is recreated from that base. The editor
## refuses it too (4.7.2 editor/docks/scene_tree_dock.cpp ~L2307-2316). An inherited node's
## values may still be overridden, so this is the delete's refusal only.
static func inherited_delete_refusal(
	scene_path: String, root: Node, node: Node, path: String
) -> String:
	var base: String = inherited_from(scene_path, String(root.get_path_to(node)))
	if base.is_empty():
		return ""
	return (
		"%s is inherited from %s; its deletion would not be saved. Edit %s instead."
		% [path, base, base]
	)


## The deepest base scene of the scene at scene_path whose state lists node_path (a path
## relative to the root, without "./"), following a base that itself inherits; "" when none does.
static func inherited_from(scene_path: String, node_path: String) -> String:
	var found: String = ""
	var base: PackedScene = _base_of(ResourceLoader.load(scene_path) as PackedScene)
	var depth: int = 0
	while base != null and depth < MAX_BASE_DEPTH:
		if _state_lists(base.get_state(), node_path):
			found = base.resource_path
		base = _base_of(base)
		depth += 1
	return found


## Why a save of the scene at scene_path is refused when it uses C# scripts, or "": prep holds the
## run's build state (build), the configuration built (buildConfiguration) and a failed build's
## quoted compiler errors (buildErrors), which the refusal quotes.
static func csharp_refusal(scene_path: String, uses_csharp: bool, prep: Dictionary) -> String:
	if str(prep.get("build", "")) != "failed" or not uses_csharp:
		return ""
	var configuration: String = str(prep.get("buildConfiguration", ""))
	var built: String = "C# build" if configuration.is_empty() else "%s C# build" % configuration
	var errors: String = str(prep.get("buildErrors", ""))
	var refusal: String = (
		"%s uses C# scripts and the project's %s failed; fix it first" % [scene_path, built]
	)
	if errors.is_empty():
		return refusal + " (validate lists the errors)."
	return "%s:\n%s" % [refusal, errors]


## Whether the scene file at path uses a C# script, directly or through a scene it instances or
## inherits (a base scene is among its dependencies), however deep.
static func file_uses_csharp(path: String) -> bool:
	return _scene_uses_csharp(path, {})


## Whether root or a node under it, instanced ones included, has a C# script, or is the root of an
## instanced scene whose file uses one: a C# script that cannot load while the build is red leaves
## its node with no script, so the instance's file is read instead.
static func tree_uses_csharp(root: Node) -> bool:
	var nodes: Array[Node] = root.find_children("*", "", true, false)
	nodes.append(root)
	var visited: Dictionary = {}
	for node in nodes:
		if _node_uses_csharp(node, visited):
			return true
	return false


## The res:// paths of the C# scripts among ResourceLoader.get_dependencies entries, which read
## "<path>" or, for a dependency saved with its UID, "<uid>::::<fallback path>", the second
## section always empty (4.7.2 doc/classes/ResourceLoader.xml).
static func csharp_dependencies(dependencies: PackedStringArray) -> PackedStringArray:
	return _dependency_paths(dependencies, [".cs"])


static func _node_uses_csharp(node: Node, visited: Dictionary) -> bool:
	var script := node.get_script() as Script
	if script != null and script.resource_path.ends_with(".cs"):
		return true
	return not node.scene_file_path.is_empty() and _scene_uses_csharp(node.scene_file_path, visited)


## Whether the scene file at path, or a scene among its dependencies followed recursively, has a C#
## script among its dependencies. visited holds the scenes already read, so a cycle ends.
static func _scene_uses_csharp(path: String, visited: Dictionary) -> bool:
	if visited.has(path) or not FileAccess.file_exists(path):
		return false
	visited[path] = true
	var dependencies: PackedStringArray = ResourceLoader.get_dependencies(path)
	if not csharp_dependencies(dependencies).is_empty():
		return true
	for scene in _dependency_paths(dependencies, [".tscn", ".scn"]):
		if _scene_uses_csharp(scene, visited):
			return true
	return false


## The res:// paths among ResourceLoader.get_dependencies entries that end in one of extensions.
static func _dependency_paths(
	dependencies: PackedStringArray, extensions: Array
) -> PackedStringArray:
	var found: PackedStringArray = []
	for dependency in dependencies:
		for part in dependency.split("::"):
			if (
				part.begins_with(RES_PREFIX)
				and _ends_with_any(part, extensions)
				and not found.has(part)
			):
				found.append(part)
	return found


static func _ends_with_any(text: String, suffixes: Array) -> bool:
	for suffix: String in suffixes:
		if text.ends_with(suffix):
			return true
	return false


## Packs root and saves it to the text scene at path with the uid uid, as SceneFiles.save_resource
## does: known holds the source's own ext_resource uids, and keep_layout keeps the text of every
## section the edit left alone. {uid, warning?} with uid as uid:// text, or {error}.
static func save(
	root: Node, path: String, uid: int, known: Dictionary, keep_layout: bool
) -> Dictionary:
	var packing: Dictionary = pack_native(root)
	if packing.has("error"):
		return {"error": "%s could not be packed: %s" % [path, packing["error"]]}
	var saved: Dictionary = SceneFiles.save_resource(
		packing["packed"], path, uid, known, keep_layout
	)
	var clashes: PackedStringArray = packing["clashes"]
	if saved.has("error") or clashes.is_empty():
		return saved
	var warning: String = "; ".join(clashes)
	if saved.has("warning"):
		warning += " " + str(saved["warning"])
	saved["warning"] = warning
	return saved


## {packed, clashes} for root packed as PackedScene.pack packs it, or {error} with the pack's
## error text. Headless, a C# script is a real instance, and Object.get asks it before the engine
## class (4.7.2 object.cpp L319-326), so pack stores a C# field named like an engine property
## (packed_scene.cpp L883) in the engine property's place. The packed state is rewritten to hold
## the engine's value there, as an editor save does; clashes holds one clause per distinct
## (script, name) saying so.
static func pack_native(root: Node) -> Dictionary:
	var packed := PackedScene.new()
	var error: int = packed.pack(root)
	if error != OK:
		return {"error": error_string(error)}
	var state: SceneState = packed.get_state()
	var fixes: Dictionary = {}
	var clashes: PackedStringArray = []
	for index in state.get_node_count():
		var node: Node = root.get_node_or_null(state.get_node_path(index))
		var names: PackedStringArray = _hidden_names(node)
		if names.is_empty():
			continue
		fixes[index] = {
			"node": node, "names": names, "bases": _base_values(state, index, node, names)
		}
		for property in names:
			var clause: String = _clash_clause(node, property)
			if not clashes.has(clause):
				clashes.append(clause)
	if not fixes.is_empty():
		packed.set("_bundled", _with_native_values(packed.get("_bundled"), fixes))
	return {"packed": packed, "clashes": clashes}


## The root name for a new scene at path: its file name in PascalCase (player_ship.tscn and
## player-ship.tscn give PlayerShip; Level1.tscn stays Level1), or Root when that is empty.
static func root_name_for(path: String) -> String:
	var words: PackedStringArray = []
	var base: String = path.get_file().get_basename().replace("-", "_").replace(" ", "_")
	for word in base.split("_", false):
		words.append(word.left(1).to_upper() + word.substr(1))
	return "Root" if words.is_empty() else "".join(words)


## A new node of type, a Node class or a script's class_name whose base is a Node class; null
## for any other name.
static func new_root(type: String) -> Node:
	if ClassDB.class_exists(type):
		if ClassDB.is_parent_class(type, "Node") and ClassDB.can_instantiate(type):
			return ClassDB.instantiate(type) as Node
		return null
	for entry: Dictionary in ProjectSettings.get_global_class_list():
		if entry["class"] == type:
			return script_root(entry["path"])
	return null


## A new node of the script at path, a node of the class it extends; null when it does not load,
## cannot be instantiated or extends a class that is not a Node.
static func script_root(path: String) -> Node:
	var script := load(path) as Script
	if script == null or not script.can_instantiate():
		return null
	if not ClassDB.is_parent_class(script.get_instance_base_type(), "Node"):
		return null
	return script.new() as Node


## The engine properties node stores whose value a script member of the same name hides: the
## storage properties of its native class that node.get and ClassDB.class_get_property read
## differently. Empty for null and for a node with no script.
static func _hidden_names(node: Node) -> PackedStringArray:
	var names: PackedStringArray = []
	if node == null or node.get_script() == null:
		return names
	for info: Dictionary in ClassDB.class_get_property_list(node.get_class()):
		var property: String = info["name"]
		if (int(info["usage"]) & PROPERTY_USAGE_STORAGE) == 0 or property == "script":
			continue
		if not Json.same(node.get(property), ClassDB.class_get_property(node, property)):
			names.append(property)
	return names


## The warning clause for the script member of node that hides its engine property.
static func _clash_clause(node: Node, property: String) -> String:
	var script: String = (node.get_script() as Script).resource_path.get_file().get_basename()
	return (
		"%s.%s (a C# field) hides %s.%s; the file stores the engine's value"
		% [script, property, node.get_class(), property]
	)


## bundled, a PackedScene's _bundled state, with each record of fixes ({record index: {node,
## names}}) storing the engine's value of its names. A record holds parent, owner, type, name,
## instance, the property count and as many (name index, value index) pairs into names and
## variants, then the group count and the groups (4.7.2 packed_scene.cpp get_bundled_scene).
static func _with_native_values(bundled: Dictionary, fixes: Dictionary) -> Dictionary:
	var nodes: PackedInt32Array = bundled["nodes"]
	var rewritten: PackedInt32Array = []
	var at: int = 0
	for record in int(bundled["node_count"]):
		var head: PackedInt32Array = nodes.slice(at, at + RECORD_HEAD)
		var pairs_start: int = at + RECORD_HEAD + 1
		var groups_start: int = pairs_start + nodes[at + RECORD_HEAD] * 2
		var groups_end: int = groups_start + 1 + nodes[groups_start]
		var pairs: PackedInt32Array = nodes.slice(pairs_start, groups_start)
		if fixes.has(record):
			var plain: bool = head[2] != RECORD_TYPE_INSTANTIATED and head[4] == -1
			pairs = _native_pairs(bundled, pairs, fixes[record], plain)
		rewritten.append_array(head)
		rewritten.append(int(pairs.size() / 2.0))
		rewritten.append_array(pairs)
		rewritten.append_array(nodes.slice(groups_start, groups_end))
		at = groups_end
	bundled["nodes"] = rewritten
	return bundled


## pairs, a node record's (name index, value index) pairs, storing the engine's value of each of
## fix.names on fix.node. A stored pair points at that value, or is dropped when the value is the
## base one: the class default for a plain record (neither instanced nor inherited, so no other
## scene supplies a value), else fix.bases, what the instanced or base scenes give the node; a
## missing pair is added when the value is not the base one.
static func _native_pairs(
	bundled: Dictionary, pairs: PackedInt32Array, fix: Dictionary, plain: bool
) -> PackedInt32Array:
	var node: Node = fix["node"]
	var bases: Dictionary = fix["bases"]
	for property: String in fix["names"]:
		var value: Variant = ClassDB.class_get_property(node, property)
		var base: Variant = ClassDB.class_get_property_default_value(node.get_class(), property)
		if not plain:
			base = bases[property]
		var is_base: bool = Json.same(value, base)
		var at: int = _pair_at(bundled["names"], pairs, property)
		if at >= 0 and is_base:
			pairs = pairs.slice(0, at) + pairs.slice(at + 2)
		elif at >= 0:
			pairs[at + 1] = _variant_index(bundled, value)
		elif not is_base:
			pairs = _with_pair(bundled, pairs, property, value)
	return pairs


## {property: value} for each of names: the value the scenes the record at index of state
## instances or inherits give node, as PropertyUtils finds a stored property's default: the value
## stored for the node in the nearest of those scenes that stores one, else the class default.
static func _base_values(
	state: SceneState, index: int, node: Node, names: PackedStringArray
) -> Dictionary:
	var bases: Dictionary = {}
	var path: String = _plain_path(state.get_node_path(index))
	for property in names:
		var supplied: Array = _supplied_value(state, path, property, false, 0)
		if supplied.is_empty():
			bases[property] = ClassDB.class_get_property_default_value(node.get_class(), property)
		else:
			bases[property] = supplied[0]
	return bases


## [value] of property for the node at path (relative to state's root): state's own record for
## path when own and it stores one, else what the instanced or base scene that holds the node
## gives it; [] when no scene stores it.
static func _supplied_value(
	state: SceneState, path: String, property: String, own: bool, depth: int
) -> Array:
	if depth > MAX_BASE_DEPTH:
		return []
	if own:
		var stored: Array = _stored_value(state, path, property)
		if not stored.is_empty():
			return stored
	var supplier: int = _supplier(state, path)
	if supplier < 0:
		return []
	var inner: String = _relative_path(path, _plain_path(state.get_node_path(supplier)))
	var scene: PackedScene = state.get_node_instance(supplier)
	return _supplied_value(scene.get_state(), inner, property, true, depth + 1)


## [value] of property in state's record for the node at path, or [] when it stores none.
static func _stored_value(state: SceneState, path: String, property: String) -> Array:
	for index in state.get_node_count():
		if _plain_path(state.get_node_path(index)) != path:
			continue
		for pair in state.get_node_property_count(index):
			if state.get_node_property_name(index, pair) == property:
				return [state.get_node_property_value(index, pair)]
		return []
	return []


## The record of state that instances or inherits the scene holding the node at path: the
## deepest record at path or above it with an instance, or -1. Records come in tree order, so the
## last such record is the deepest.
static func _supplier(state: SceneState, path: String) -> int:
	var supplier: int = -1
	for index in state.get_node_count():
		if state.get_node_instance(index) == null:
			continue
		if _relative_path(path, _plain_path(state.get_node_path(index))) != "":
			supplier = index
	return supplier


## path relative to at, both relative to one root ("." for the root itself), or "" when path is
## not at or below at.
static func _relative_path(path: String, at: String) -> String:
	if at == ".":
		return path
	if path == at:
		return "."
	if path.begins_with(at + "/"):
		return path.substr(at.length() + 1)
	return ""


## path as text without a leading "./", "." for the root.
static func _plain_path(path: NodePath) -> String:
	var text: String = str(path)
	if text.begins_with("./"):
		text = text.substr(2)
	return "." if text.is_empty() else text


## pairs with a pair for property holding value, placed before the script's pair: instantiation
## sets the pairs in order, and an engine property set once the script is attached reaches the
## script's member instead.
static func _with_pair(
	bundled: Dictionary, pairs: PackedInt32Array, property: String, value: Variant
) -> PackedInt32Array:
	var pair: PackedInt32Array = [_name_index(bundled, property), _variant_index(bundled, value)]
	var at: int = _pair_at(bundled["names"], pairs, "script")
	if at < 0:
		return pairs + pair
	return pairs.slice(0, at) + pair + pairs.slice(at)


## The offset in pairs of the pair whose name is property, or -1.
static func _pair_at(names: PackedStringArray, pairs: PackedInt32Array, property: String) -> int:
	for at in range(0, pairs.size(), 2):
		if names[pairs[at] & PROPERTY_NAME_MASK] == property:
			return at
	return -1


## The index of property in bundled's names, appended when absent.
static func _name_index(bundled: Dictionary, property: String) -> int:
	var names: PackedStringArray = bundled["names"]
	var index: int = names.find(property)
	if index < 0:
		index = names.size()
		names.append(property)
		bundled["names"] = names
	return index


## The index of value appended to bundled's variants.
static func _variant_index(bundled: Dictionary, value: Variant) -> int:
	var variants: Array = bundled["variants"]
	variants.append(value)
	return variants.size() - 1


## The scene an inherited scene's root instances, or null for a scene that inherits nothing.
static func _base_of(scene: PackedScene) -> PackedScene:
	if scene == null or scene.get_state().get_node_count() == 0:
		return null
	return scene.get_state().get_node_instance(0)


## Whether state has a node at node_path; a state's paths read "." or "./A/B".
static func _state_lists(state: SceneState, node_path: String) -> bool:
	for index in state.get_node_count():
		var listed: String = String(state.get_node_path(index))
		if listed == node_path or listed.trim_prefix("./") == node_path:
			return true
	return false
