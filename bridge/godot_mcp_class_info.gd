extends RefCounted
## describe_class's reader, static functions only, shared by the bridge (in a running game) and
## the headless operations: an engine class's (ClassDB) or a project script class's properties
## with their defaults, methods with typed arguments, signals, constants and enums, and for a name
## that is neither, the closest class names. Script classes are those of
## ProjectSettings.get_global_class_list(). A script's own lists hold its base scripts' members
## too (4.7.2 modules/gdscript/gdscript.cpp L296-355, L1291-1312).

const Json := preload("godot_mcp_json.gd")
## How many methods a page holds when the request names no limit.
const DEFAULT_LIMIT := 100
## How many close names an unknown class name is answered with.
const SUGGESTION_COUNT := 5
## The most edits a close name may be away from the name asked for, unless it contains it.
const MAX_SUGGESTION_DISTANCE := 3
## Property-list entries that head a section of the inspector rather than hold a value.
const SECTION_USAGE := PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP
## Property-list entries never listed: section headings, and internal properties. Every other
## property is, PROPERTY_USAGE_NONE ones included (Node.name, Node2D.global_position): scripts
## reach them though the editor neither shows nor stores them.
const UNLISTED_USAGE := SECTION_USAGE | PROPERTY_USAGE_INTERNAL
## A property or argument whose class_name names an enum or a bitfield rather than a class.
const ENUM_USAGE := PROPERTY_USAGE_CLASS_IS_ENUM | PROPERTY_USAGE_CLASS_IS_BITFIELD
## The member lists whose entries are {name, …}.
const MEMBER_LISTS: Array[String] = ["properties", "methods", "signals"]
## The warning for a C# script class whose member lists are all empty, the class in place of %s.
const CSHARP_WARNING := (
	"%s's C# script lists no properties, methods or signals: the C# build may be red or "
	+ "stale, so its members may be missing."
)


## describe_class for params {className, inherited, offset, limit}: {result} with the class's
## header and members, its methods sorted by name and paged, or {error, suggestions} for a name
## that is neither an engine class nor a script class of the project.
static func describe(params: Dictionary) -> Dictionary:
	var title: String = str(params.get("className", "")).strip_edges()
	var inherited: bool = bool(params.get("inherited", false))
	var described: Dictionary
	if ClassDB.class_exists(title):
		described = _engine_class(title, inherited)
	else:
		var entry: Dictionary = _global_class_entry(title, ProjectSettings.get_global_class_list())
		if entry.is_empty():
			return unknown_class(title, candidate_names())
		described = _script_class(entry, inherited)
		if described.has("error"):
			return described
	var offset: int = int(params.get("offset", 0))
	return {"result": _paged(described, offset, int(params.get("limit", DEFAULT_LIMIT)))}


## The refusal of a name no class has: {error, suggestions}, the error naming the suggestions.
static func unknown_class(title: String, candidates: PackedStringArray) -> Dictionary:
	var close: PackedStringArray = suggestions(title, candidates)
	var hint: String = "No class has a close name."
	if not close.is_empty():
		hint = "Did you mean: %s?" % ", ".join(close)
	return {
		"error": "No engine class or project script class is named '%s'. %s" % [title, hint],
		"suggestions": Array(close),
	}


## Every engine class name and every script class name of the project.
static func candidate_names() -> PackedStringArray:
	var names: PackedStringArray = ClassDB.get_class_list()
	for entry: Dictionary in ProjectSettings.get_global_class_list():
		names.append(str(entry.get("class", "")))
	return names


## The SUGGESTION_COUNT candidates closest to query, compared without case: those that contain it
## first, then those at most MAX_SUGGESTION_DISTANCE edits away; each group by edit distance, then
## alphabetically.
static func suggestions(query: String, candidates: PackedStringArray) -> PackedStringArray:
	var wanted: String = query.to_lower()
	var ranked: Array = []
	for candidate: String in candidates:
		var lower: String = candidate.to_lower()
		var contains: bool = lower.contains(wanted)
		if not contains and absi(lower.length() - wanted.length()) > MAX_SUGGESTION_DISTANCE:
			continue
		var distance: int = levenshtein(wanted, lower)
		if contains or distance <= MAX_SUGGESTION_DISTANCE:
			ranked.append([0 if contains else 1, distance, candidate])
	ranked.sort_custom(func(a: Array, b: Array) -> bool: return a < b)
	var close: PackedStringArray = []
	for entry: Array in ranked.slice(0, SUGGESTION_COUNT):
		close.append(entry[2])
	return close


## The Levenshtein distance between a and b: the fewest single-character insertions, deletions
## and substitutions that turn one into the other.
static func levenshtein(a: String, b: String) -> int:
	var previous := PackedInt32Array()
	for column in b.length() + 1:
		previous.append(column)
	for row in a.length():
		var current := PackedInt32Array([row + 1])
		for column in b.length():
			var substitution: int = previous[column] + (0 if a[row] == b[column] else 1)
			current.append(mini(substitution, mini(previous[column + 1], current[column]) + 1))
		previous = current
	return previous[b.length()]


## An engine class's header and members: its own unless inherited.
static func _engine_class(title: String, inherited: bool) -> Dictionary:
	var parent: String = String(ClassDB.get_parent_class(title))
	var described: Dictionary = {
		"className": title,
		"inherits": parent,
		"inheritsChain": _native_chain(parent),
		"canInstantiate": ClassDB.can_instantiate(title),
		"isScript": false,
	}
	described.merge(_native_members(title, not inherited))
	return described


## A script class's header and its script's members, with its engine base's appended when
## inherited, and a warning for a C# class that lists no members; {error} when its script does not
## load.
static func _script_class(entry: Dictionary, inherited: bool) -> Dictionary:
	var title: String = str(entry.get("class", ""))
	var path: String = str(entry.get("path", ""))
	var script: Script = load(path) as Script
	if script == null:
		return {
			"error": "%s is a script class of the project, but %s does not load." % [title, path]
		}
	var base: String = str(entry.get("base", ""))
	var described: Dictionary = {
		"className": title,
		"inherits": base,
		"inheritsChain": _script_chain(base),
		"canInstantiate": script.can_instantiate(),
		"isScript": true,
		"scriptPath": path,
		"language": str(entry.get("language", "")),
	}
	described.merge(_script_members(script))
	var warning: String = _csharp_warning(described)
	if inherited:
		_append_members(described, _native_members(String(script.get_instance_base_type()), false))
	if not warning.is_empty():
		described["warning"] = warning
	return described


## A ClassDB class's properties, methods, signals, constants and enums: only those it declares
## itself when own.
static func _native_members(title: String, own: bool) -> Dictionary:
	var properties: Array = []
	for info: Dictionary in ClassDB.class_get_property_list(title, own):
		if _is_listed(info):
			properties.append(_native_property(title, info))
	var methods: Array = []
	for info: Dictionary in ClassDB.class_get_method_list(title, own):
		methods.append(_method(info))
	var signals: Array = []
	for info: Dictionary in ClassDB.class_get_signal_list(title, own):
		signals.append(_signal(info))
	return {
		"properties": properties,
		"methods": methods,
		"signals": signals,
		"constants": _native_constants(title, own),
		"enums": _native_enums(title, own),
	}


## A class's integer constants that belong to no enum, {name: value}.
static func _native_constants(title: String, own: bool) -> Dictionary:
	var constants: Dictionary = {}
	for key: String in ClassDB.class_get_integer_constant_list(title, own):
		if String(ClassDB.class_get_integer_constant_enum(title, key)).is_empty():
			constants[key] = ClassDB.class_get_integer_constant(title, key)
	return constants


## A class's enums, {enumName: {key: value}}.
static func _native_enums(title: String, own: bool) -> Dictionary:
	var enums: Dictionary = {}
	for enum_name: String in ClassDB.class_get_enum_list(title, own):
		var values: Dictionary = {}
		for key: String in ClassDB.class_get_enum_constants(title, enum_name, own):
			values[key] = ClassDB.class_get_integer_constant(title, key)
		enums[enum_name] = values
	return enums


## A script's properties, methods, signals, constants and enums, its base scripts' included; a
## method a script overrides is listed once, as the nearest script declares it.
static func _script_members(script: Script) -> Dictionary:
	var properties: Array = []
	for info: Dictionary in script.get_script_property_list():
		if _is_listed(info):
			properties.append(_property(info, script.get_property_default_value(info["name"])))
	var methods: Array = []
	for info: Dictionary in script.get_script_method_list():
		_add_named(methods, [_method(info)])
	var signals: Array = []
	for info: Dictionary in script.get_script_signal_list():
		_add_named(signals, [_signal(info)])
	var described: Dictionary = {"properties": properties, "methods": methods, "signals": signals}
	described.merge(_split_constants(script.get_script_constant_map()))
	return described


## A script's constant map as {constants, enums}: a Dictionary of int values is a named enum, and
## anything else a constant, as JSON.
static func _split_constants(constant_map: Dictionary) -> Dictionary:
	var constants: Dictionary = {}
	var enums: Dictionary = {}
	for key: Variant in constant_map:
		var value: Variant = constant_map[key]
		if _is_enum(value):
			enums[str(key)] = value
		else:
			constants[str(key)] = Json.to_json(value)
	return {"constants": constants, "enums": enums}


static func _is_enum(value: Variant) -> bool:
	if not value is Dictionary or (value as Dictionary).is_empty():
		return false
	return (value as Dictionary).values().all(func(entry: Variant) -> bool: return entry is int)


## Appends extra's members to described's, leaving out a property, method or signal of a name
## described already has, and a constant or enum likewise.
static func _append_members(described: Dictionary, extra: Dictionary) -> void:
	for key: String in MEMBER_LISTS:
		_add_named(described[key], extra[key])
	for key: String in ["constants", "enums"]:
		(described[key] as Dictionary).merge(extra[key])


## Appends each of entries whose name listed does not hold yet.
static func _add_named(listed: Array, entries: Array) -> void:
	var names: Dictionary = {}
	for entry: Dictionary in listed:
		names[entry["name"]] = true
	for entry: Dictionary in entries:
		if not names.has(entry["name"]):
			names[entry["name"]] = true
			listed.append(entry)


## Whether a property-list entry is a property to list: neither a section heading nor internal.
static func _is_listed(info: Dictionary) -> bool:
	return (int(info.get("usage", 0)) & UNLISTED_USAGE) == 0


static func _property(info: Dictionary, default_value: Variant) -> Dictionary:
	return {"name": info["name"], "type": _value_type(info), "default": Json.to_json(default_value)}


## An engine class's property as {name, type, default?}. ClassDB keeps defaults only for editor
## and stored properties (4.7.2 core/object/class_db.cpp L2193) and reads null for the rest
## (Node.name, Node2D.global_position), which is not their default, so theirs is left out.
static func _native_property(title: String, info: Dictionary) -> Dictionary:
	if (int(info.get("usage", 0)) & (PROPERTY_USAGE_EDITOR | PROPERTY_USAGE_STORAGE)) == 0:
		return {"name": info["name"], "type": _value_type(info)}
	return _property(info, ClassDB.class_get_property_default_value(title, info["name"]))


## A method-list entry as {name, args: [{name, type, default?}], returnType, isVirtual, isStatic}.
static func _method(info: Dictionary) -> Dictionary:
	var flags: int = info.get("flags", 0)
	return {
		"name": info["name"],
		"args": _arguments(info.get("args", []), info.get("default_args", [])),
		"returnType": _return_type(info.get("return", {})),
		"isVirtual": (flags & METHOD_FLAG_VIRTUAL) != 0,
		"isStatic": (flags & METHOD_FLAG_STATIC) != 0,
	}


## A method's arguments, the last of them carrying the defaults (default_args holds the defaults of
## the last arguments, in order).
static func _arguments(args: Array, defaults: Array) -> Array:
	var listed: Array = []
	var first_default: int = args.size() - defaults.size()
	for index in args.size():
		var arg: Dictionary = args[index]
		var entry: Dictionary = {"name": arg["name"], "type": _value_type(arg)}
		if index >= first_default:
			entry["default"] = Json.to_json(defaults[index - first_default])
		listed.append(entry)
	return listed


## A signal-list entry as {name, args: [{name, type}]}.
static func _signal(info: Dictionary) -> Dictionary:
	var args: Array = []
	for arg: Dictionary in info.get("args", []):
		args.append({"name": arg["name"], "type": _value_type(arg)})
	return {"name": info["name"], "args": args}


## The type a property or argument declares: the enum's name for an enum, Variant for an untyped
## one, else as the JSON module names it.
static func _value_type(info: Dictionary) -> String:
	var class_title: String = str(info.get("class_name", ""))
	if (int(info.get("usage", 0)) & ENUM_USAGE) != 0 and not class_title.is_empty():
		return class_title
	if int(info.get("type", TYPE_NIL)) == TYPE_NIL:
		return "Variant"
	return Json.type_name(info)


## A method's return type: void when it returns nothing, Variant when it may return anything.
static func _return_type(info: Dictionary) -> String:
	var untyped: bool = int(info.get("type", TYPE_NIL)) == TYPE_NIL
	if untyped and (int(info.get("usage", 0)) & PROPERTY_USAGE_NIL_IS_VARIANT) == 0:
		return "void"
	return _value_type(info)


## The warning for a C# script class whose property, method and signal lists are all empty.
static func _csharp_warning(described: Dictionary) -> String:
	if described["language"] != "C#":
		return ""
	for key: String in MEMBER_LISTS:
		if not (described[key] as Array).is_empty():
			return ""
	return CSHARP_WARNING % described["className"]


## Sorts described's methods by name and keeps the page of limit from offset, adding methodCount
## (how many there are), offset and limit.
static func _paged(described: Dictionary, offset: int, limit: int) -> Dictionary:
	var methods: Array = described["methods"]
	methods.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return a["name"] < b["name"])
	described["methods"] = methods.slice(offset, offset + limit)
	described["methodCount"] = methods.size()
	described["offset"] = offset
	described["limit"] = limit
	return described


## start and the ClassDB classes above it, nearest first; empty for an empty start.
static func _native_chain(start: String) -> Array:
	var chain: Array = []
	var current: String = start
	while not current.is_empty():
		chain.append(current)
		current = String(ClassDB.get_parent_class(current))
	return chain


## A script class's bases from base up: script classes through the global class list, then the
## engine classes above the first ClassDB class; a cycle among script classes ends the walk.
static func _script_chain(base: String) -> Array:
	var classes: Array = ProjectSettings.get_global_class_list()
	var chain: Array = []
	var current: String = base
	for _step in classes.size() + 1:
		if current.is_empty() or ClassDB.class_exists(current):
			break
		chain.append(current)
		current = str(_global_class_entry(current, classes).get("base", ""))
	chain.append_array(_native_chain(current))
	return chain


## The entry {class, base, path, language, …} of classes for the script class title, or {}.
static func _global_class_entry(title: String, classes: Array) -> Dictionary:
	for entry: Dictionary in classes:
		if str(entry.get("class", "")) == title:
			return entry
	return {}
