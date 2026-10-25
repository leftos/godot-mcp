extends RefCounted
## Keeping a scene file's own text through a headless save. ResourceSaver.save writes the whole
## file in Godot's own form (no load_steps, unique_id= on every node, properties reordered,
## ext_resource ids renumbered), so splice(original, saved) keeps every section of the file the
## edit left alone byte for byte and takes the saved text only for the sections it added or
## changed. A changed section keeps its own unique_id state: its tag line is the saved one but for
## unique_id=, which it has only when the original had it, with the original's value; an added
## section keeps the unique_id the saver gave it.
##
## A section is a line opening [gd_scene, [ext_resource, [sub_resource, [node, [connection or
## [editable and every line up to the next such line, blank lines included. Sections are matched
## by key: an ext_resource by its path, a node by its parent and name (the root by having no
## parent), a connection by its signal, from, to and method, an editable by its path, and a
## sub_resource by its canonical form and its place among the sub_resources with that form, in
## file order, since its id is the saver's and equal resources are common. Two sections compare
## by their canonical form: the tag line without unique_id=, id= and uid=, the property lines
## sorted, and each ExtResource("<id>") or SubResource("<id>") replaced by what it names. An
## original node a move carried, and its connections and editable section, are keyed by the paths
## it has in saved (renames), so they match their saved sections; a carried node's changed section
## keeps its original lines in their order, only the tag line and the lines whose value changed
## taken from saved (_carried_lines).

## The tags that open a section.
const OPENERS: Array[String] = [
	"gd_scene", "ext_resource", "sub_resource", "node", "connection", "editable"
]
const HEADER_KIND := "gd_scene"
const EXT_KIND := "ext_resource"
const SUB_KIND := "sub_resource"
## A reference's function name, by the kind of the section it names.
const REFERENCE_OF := {"ext_resource": "ExtResource", "sub_resource": "SubResource"}

## ExtResource("<id>") or SubResource("<id>"): the function, then the id.
static var _reference := RegEx.create_from_string(
	'(ExtResource|SubResource)\\(\\s*"([^"]*)"\\s*\\)'
)
## The tag attributes the saver writes or renumbers on its own.
static var _saver_attribute := RegEx.create_from_string(
	' (?:unique_id=-?\\d+|id="[^"]*"|uid="[^"]*")'
)
## A node tag line's unique_id attribute, with the space before it.
static var _unique_id := RegEx.create_from_string(" unique_id=-?\\d+")
## The start of a property line: a name, then " = ".
static var _property_start := RegEx.create_from_string("^[A-Za-z_][^\\s=]*\\s*=")
static var _load_steps := RegEx.create_from_string(" load_steps=\\d+")


## original with the sections saved changed, added or deleted relative to it taken from saved, and
## every other section as original has it: {text}, or {fallback: <reason>} when a section cannot
## be mapped (a duplicate key, a reference to no section, a new id the output already uses) or the
## result would not read as saved does. Line endings follow original's. renames maps the path
## from the root a node a move carried has in original to the one it has in saved: original's
## sections are keyed by the paths renames gives (_parse), so a carried node's sections match its
## saved ones and a new node at a path a carried node left is new.
static func splice(original: String, saved: String, renames: Dictionary) -> Dictionary:
	if original.contains("\r\n"):
		saved = saved.replace("\r\n", "\n").replace("\n", "\r\n")
	var before: Dictionary = _parse(original, renames)
	var after: Dictionary = _parse(saved, {})
	for doc: Dictionary in [before, after]:
		if not doc["error"].is_empty():
			return {"fallback": doc["error"]}
	var built: Dictionary = _build(before, after)
	if built.has("fallback"):
		return built
	var failure: String = self_check(built["text"], saved)
	return {"fallback": failure} if not failure.is_empty() else built


## "" when spliced and saved hold the same sections, each by its key with an equal canonical form
## (the gd_scene header left out), their node sections in the same order, and no two nodes of
## spliced sharing a unique_id; else what differs. Both are texts after the edit, so their paths
## need no map.
static func self_check(spliced: String, saved: String) -> String:
	var mine: Dictionary = _parse(spliced, {})
	var theirs: Dictionary = _parse(saved, {})
	for doc: Dictionary in [mine, theirs]:
		if not doc["error"].is_empty():
			return doc["error"]
	var failure: String = _form_difference(_forms(mine), _forms(theirs))
	if failure.is_empty() and _node_keys(mine) != _node_keys(theirs):
		failure = "the spliced text put the nodes in another order than the save"
	if failure.is_empty():
		failure = _shared_unique_id(mine)
	return failure


## The first section ours lost, changed or kept relative to wanted (each {key: canonical form}),
## or "".
static func _form_difference(ours: Dictionary, wanted: Dictionary) -> String:
	for key: String in wanted:
		if ours.get(key, "") != wanted[key]:
			var lost: bool = not ours.has(key)
			var reason: String = "lost the section %s" if lost else "changed the section %s"
			return ("the spliced text " + reason) % key
	for key: String in ours:
		if not wanted.has(key):
			return "the spliced text kept the section %s the save does not have" % key
	return ""


## Each section's canonical form by its key, the gd_scene header left out.
static func _forms(doc: Dictionary) -> Dictionary:
	var forms: Dictionary = {}
	for key: String in doc["by_key"]:
		if key != HEADER_KIND:
			forms[key] = doc["by_key"][key]["canon"]
	return forms


## The sections of a scene's text in order, each {kind, content, sep}: content its lines up to its
## last non-blank one with that line's ending, sep the blank lines after it. kind is "" for a first
## section that opens with no known tag.
static func sections_of(text: String) -> Array[Dictionary]:
	var starts: Array[int] = [0]
	var at: int = text.find("\n")
	while at >= 0:
		if not _kind_of(text.substr(at + 1, 16)).is_empty():
			starts.append(at + 1)
		at = text.find("\n", at + 1)
	var sections: Array[Dictionary] = []
	for index in starts.size():
		var end: int = starts[index + 1] if index + 1 < starts.size() else text.length()
		sections.append(_section(text.substr(starts[index], end - starts[index])))
	return sections


## The section kind a line opens, or "".
static func _kind_of(line: String) -> String:
	if not line.begins_with("["):
		return ""
	for kind: String in OPENERS:
		var next: String = line.substr(kind.length() + 1, 1)
		if line.substr(1, kind.length()) == kind and (next == " " or next == "]"):
			return kind
	return ""


static func _section(raw: String) -> Dictionary:
	var last: int = raw.rstrip("\r\n").length()
	var cut: int = last + 2 if raw.substr(last, 2) == "\r\n" else mini(last + 1, raw.length())
	var content: String = raw.substr(0, cut)
	return {"kind": _kind_of(raw), "content": content, "sep": raw.substr(cut)}


## text parsed: {sections, by_key: {key: section}, ids: {kind: {id: section}}, canons: {id: canon},
## occurrences: {sub_resource key: count}, visiting, renames, sources (renames inverted, built once
## for every key, sources_of), error}; each section gains its head (tag line), id, canon, key and
## renamed. A node whose path renames holds is keyed by the path renames gives it, and is renamed;
## a connection's from and to and an editable's path go through renames too. error is "" or why
## the text cannot be spliced.
static func _parse(text: String, renames: Dictionary) -> Dictionary:
	var doc: Dictionary = {
		"sections": sections_of(text),
		"by_key": {},
		"ids": {EXT_KIND: {}, SUB_KIND: {}},
		"canons": {},
		"occurrences": {},
		"visiting": {},
		"renames": renames,
		"sources": sources_of(renames),
		"error": "",
	}
	if doc["sections"][0]["kind"] != HEADER_KIND:
		doc["error"] = "the file does not start with a [gd_scene] tag"
		return doc
	for section: Dictionary in doc["sections"]:
		_index(section, doc)
	for section: Dictionary in doc["sections"]:
		_key(section, doc)
	return doc


## Gives section its head and id, and lists a resource section under its id in doc.
static func _index(section: Dictionary, doc: Dictionary) -> void:
	section["head"] = _lines_of(section["content"])[0]
	section["id"] = _quoted(section["head"], "id")
	var kind: String = section["kind"]
	if not doc["ids"].has(kind):
		return
	if doc["ids"][kind].has(section["id"]):
		doc["error"] = 'two %s sections have the id "%s"' % [kind, section["id"]]
	doc["ids"][kind][section["id"]] = section


## Gives section its canonical form and key, and lists it under its key in doc. A sub_resource's
## key ends with its place among the sub_resources with an equal form, " #1" for the first.
static func _key(section: Dictionary, doc: Dictionary) -> void:
	if not doc["error"].is_empty():
		return
	var kind: String = section["kind"]
	section["canon"] = _sub_canon(section["id"], doc) if kind == SUB_KIND else _canon(section, doc)
	var keyed: Dictionary = _key_of(section, doc["renames"], doc["sources"])
	section["key"] = keyed["key"]
	section["renamed"] = keyed["renamed"]
	if kind == SUB_KIND:
		var place: int = doc["occurrences"].get(section["key"], 0) + 1
		doc["occurrences"][section["key"]] = place
		section["key"] += " #%d" % place
	if doc["by_key"].has(section["key"]):
		doc["error"] = "two sections have the key %s" % section["key"]
	doc["by_key"][section["key"]] = section


## {key, renamed}: section's key, its paths taken through renames (sources its inverse,
## sources_of), and whether it is a node section renames moved.
static func _key_of(section: Dictionary, renames: Dictionary, sources: Dictionary) -> Dictionary:
	var head: String = section["head"]
	match section["kind"]:
		EXT_KIND:
			return {"key": "ext_resource path=" + _quoted(head, "path"), "renamed": false}
		SUB_KIND:
			return {"key": "sub_resource " + section["canon"], "renamed": false}
		"node":
			return _node_key(head, renames, sources)
		"connection":
			return {"key": _connection_key(head, renames, sources), "renamed": false}
		"editable":
			return {"key": _editable_key(head, renames, sources), "renamed": false}
	return {"key": HEADER_KIND, "renamed": false}


## Whether the node at path, from the root, in original was displaced by a move: path is not one a
## move carried (a key of renames) but is, or lies under, the path a carried node has in saved (a
## key of sources, renames inverted by sources_of), so the node original had there is gone from
## saved. Its sections key as deleted (_node_key, _connection_key, _editable_key), and SceneEdit
## gives a node there no source. One lookup in sources for path and for each path above it.
static func is_displaced(path: String, renames: Dictionary, sources: Dictionary) -> bool:
	if renames.has(path):
		return false
	var above: String = path
	while not sources.has(above):
		var cut: int = above.rfind("/")
		if cut < 0:
			return false
		above = above.left(cut)
	return true


## renames ({path in original: path in saved}) inverted: {path in saved: path in original}.
static func sources_of(renames: Dictionary) -> Dictionary:
	var sources: Dictionary = {}
	for source: String in renames:
		sources[renames[source]] = source
	return sources


## {key, renamed} of the node section whose tag line is head: keyed by its parent and name, or, for
## a node whose path renames holds (renamed), by the parent and name of the path renames gives it;
## the root by having no parent; a displaced node (is_displaced) by a key no saved section has.
static func _node_key(head: String, renames: Dictionary, sources: Dictionary) -> Dictionary:
	var parent: String = _quoted(head, "parent")
	var name: String = _quoted(head, "name")
	if parent.is_empty():
		return {"key": "node (root) name=%s" % name, "renamed": false}
	var path: String = name if parent == "." else parent + "/" + name
	if is_displaced(path, renames, sources):
		return {"key": "node displaced path=%s" % path, "renamed": false}
	var renamed: bool = renames.has(path)
	if renamed:
		var moved: String = _moved_path(path, renames)
		parent = "." if moved.get_base_dir().is_empty() else moved.get_base_dir()
		name = moved.get_file()
	return {"key": "node parent=%s name=%s" % [parent, name], "renamed": renamed}


## The path a node had at path, from the root, has in saved: the one renames ({path in original:
## path in saved}) gives it, else path itself.
static func _moved_path(path: String, renames: Dictionary) -> String:
	return renames.get(path, path)


## The key of the connection section whose tag line is head: its signal, from, to and method, from
## and to taken through renames; when from or to is displaced (is_displaced), one no saved section
## has, with from and to as head writes them, so two such connections keep keys of their own.
static func _connection_key(head: String, renames: Dictionary, sources: Dictionary) -> String:
	var from: String = _quoted(head, "from")
	var to: String = _quoted(head, "to")
	var kind: String = "connection"
	if is_displaced(from, renames, sources) or is_displaced(to, renames, sources):
		kind = "connection displaced"
	else:
		from = _moved_path(from, renames)
		to = _moved_path(to, renames)
	var fields: Array = [kind, _quoted(head, "signal"), from, to, _quoted(head, "method")]
	return "%s signal=%s from=%s to=%s method=%s" % fields


## The key of the editable section whose tag line is head: its path taken through renames, or one
## no saved section has when the path is displaced (is_displaced).
static func _editable_key(head: String, renames: Dictionary, sources: Dictionary) -> String:
	var path: String = _quoted(head, "path")
	if is_displaced(path, renames, sources):
		return "editable displaced path=" + path
	return "editable path=" + _moved_path(path, renames)


## section's canonical form: its tag line without the saver's attributes, then its property lines
## sorted, each reference replaced by what it names.
static func _canon(section: Dictionary, doc: Dictionary) -> String:
	var lines: PackedStringArray = _lines_of(section["content"])
	var head: String = _saver_attribute.sub(lines[0], "", true)
	var entries: PackedStringArray = _entries(lines.slice(1))
	entries.sort()
	return _resolve(head + "\n" + "\n".join(entries), doc)


## The canonical form of the sub_resource with id id, once per id; "" with doc.error set for an id
## that names no sub_resource or a sub_resource that reaches itself.
static func _sub_canon(id: String, doc: Dictionary) -> String:
	if doc["canons"].has(id):
		return doc["canons"][id]
	if not doc["ids"][SUB_KIND].has(id):
		doc["error"] = 'SubResource("%s") names no sub_resource' % id
		return ""
	if doc["visiting"].has(id):
		doc["error"] = 'the sub_resource "%s" refers to itself' % id
		return ""
	doc["visiting"][id] = true
	var canon: String = _canon(doc["ids"][SUB_KIND][id], doc)
	doc["visiting"].erase(id)
	doc["canons"][id] = canon
	return canon


## text with each ExtResource("<id>") as the path it names and each SubResource("<id>") as the
## canonical form of the sub_resource it names; doc.error set for an id that names nothing.
static func _resolve(text: String, doc: Dictionary) -> String:
	var parts: PackedStringArray = []
	var at: int = 0
	for found: RegExMatch in _reference.search_all(text):
		parts.append(text.substr(at, found.get_start() - at))
		parts.append(_named(found.get_string(1), found.get_string(2), doc))
		at = found.get_end()
	parts.append(text.substr(at))
	return "".join(parts)


static func _named(function: String, id: String, doc: Dictionary) -> String:
	if function == "SubResource":
		return "SubResource{%s}" % _sub_canon(id, doc)
	var ext: Dictionary = doc["ids"][EXT_KIND].get(id, {})
	if ext.is_empty():
		doc["error"] = 'ExtResource("%s") names no ext_resource' % id
		return ""
	return "ExtResource{%s}" % _quoted(ext["head"], "path")


## lines grouped into properties: a line that starts "<name> =" opens one, and every line up to the
## next such line (a value written over several lines) belongs to it.
static func _entries(lines: PackedStringArray) -> PackedStringArray:
	var entries: PackedStringArray = []
	for line in lines:
		if entries.is_empty() or _property_start.search(line) != null:
			entries.append(line)
		else:
			entries[entries.size() - 1] += "\n" + line
	return entries


## content's lines without their endings, the empty one after the last line ending left out.
static func _lines_of(content: String) -> PackedStringArray:
	var lines: PackedStringArray = []
	for line in content.split("\n"):
		lines.append(line.trim_suffix("\r"))
	if lines.size() > 1 and lines[lines.size() - 1].is_empty():
		lines.remove_at(lines.size() - 1)
	return lines


## The output: original's header with load_steps recomputed, then original's sections in order,
## each kept, replaced by saved's text where it changed, or dropped where saved has no section with
## its key, and saved's new sections each after the section that precedes it in saved; then the
## node sections put in saved's order, in the slots node sections fill. {text} or {fallback}.
static func _build(before: Dictionary, after: Dictionary) -> Dictionary:
	var ids: Dictionary = _id_map(before, after)
	var entries: Array[Dictionary] = _kept_entries(before, after, ids)
	var added: Dictionary = _added(before, after)
	var failure: String = _collision(entries, added)
	if not failure.is_empty():
		return {"fallback": failure}
	var resources: int = 0
	for entry: Dictionary in entries:
		resources += 1 if REFERENCE_OF.has(entry["kind"]) else 0
	for news: Array in added.values():
		for section: Dictionary in news:
			resources += 1 if REFERENCE_OF.has(section["kind"]) else 0
	entries[0]["content"] = _header(before["sections"][0], after["sections"][0], resources)
	return {"text": _joined(entries, added, after, ids)}


## For each reference function, the saved id of each resource original has too, to original's id.
static func _id_map(before: Dictionary, after: Dictionary) -> Dictionary:
	var ids: Dictionary = {}
	for kind: String in REFERENCE_OF:
		var mapped: Dictionary = {}
		for id: String in after["ids"][kind]:
			var key: String = after["ids"][kind][id]["key"]
			if before["by_key"].has(key):
				mapped[id] = before["by_key"][key]["id"]
		ids[REFERENCE_OF[kind]] = mapped
	return ids


## original's sections that saved still has, as {kind, key, content, sep}: the original's text when
## the canonical forms match, else as _changed_content gives it. A dropped section's blank lines go
## to the section before it.
static func _kept_entries(
	before: Dictionary, after: Dictionary, ids: Dictionary
) -> Array[Dictionary]:
	var entries: Array[Dictionary] = []
	for section: Dictionary in before["sections"]:
		var key: String = section["key"]
		if not after["by_key"].has(key):
			entries[entries.size() - 1]["sep"] = section["sep"]
			continue
		var entry: Dictionary = {"kind": section["kind"], "key": key, "sep": section["sep"]}
		var saved: Dictionary = after["by_key"][key]
		entry["content"] = section["content"]
		if saved["canon"] != section["canon"]:
			entry["content"] = _changed_content(section, saved, ids)
		entries.append(entry)
	return entries


## The text of original's section whose saved form differs: saved's with its ids mapped back and
## the original's unique_id state; for a node a move carried (renamed), its lines as
## _carried_lines keeps them.
static func _changed_content(section: Dictionary, saved: Dictionary, ids: Dictionary) -> String:
	var content: String = _mapped(saved, ids).replace(
		' id="%s"' % saved["id"], ' id="%s"' % section["id"]
	)
	if section["renamed"]:
		content = _carried_lines(section["content"], content)
	return _with_unique_id_of(section["head"], content)


## A carried node's section from original, its text before the move, and saved, its saved text
## with ids mapped back: saved's tag line, then original's properties in their order, each as saved
## writes it (so one whose value changed takes the saved line in place) and one saved does not
## have dropped, then each property only saved has placed by _with_saved_only. Line endings follow
## original's.
static func _carried_lines(original: String, saved: String) -> String:
	var theirs: PackedStringArray = _lines_of(saved)
	var saved_entries: PackedStringArray = _entries(theirs.slice(1))
	var wanted: Dictionary = {}
	for entry: String in saved_entries:
		wanted[_entry_name(entry)] = entry
	var lines: PackedStringArray = [theirs[0]]
	for entry: String in _entries(_lines_of(original).slice(1)):
		var name: String = _entry_name(entry)
		if wanted.has(name):
			lines.append(wanted[name])
			wanted.erase(name)
	lines = _with_saved_only(lines, saved_entries, wanted)
	var text: String = "\n".join(lines) + ("\n" if original.ends_with("\n") else "")
	return text.replace("\n", "\r\n") if original.contains("\r\n") else text


## lines (a tag line, then entries) with each entry of saved_entries that only (by name) holds put
## right after the latest line of lines that an entry before it in saved_entries is, or right
## after the tag line when none is. saved_entries being in Godot's order, a new engine property
## lands before script = and a new script member after it, as Godot writes them.
static func _with_saved_only(
	lines: PackedStringArray, saved_entries: PackedStringArray, only: Dictionary
) -> PackedStringArray:
	var anchor: int = 0
	for entry: String in saved_entries:
		if only.has(_entry_name(entry)):
			anchor += 1
			lines.insert(anchor, entry)
		else:
			anchor = maxi(anchor, lines.find(entry))
	return lines


## The property name a property entry (_entries) opens with, the text before its "=".
static func _entry_name(entry: String) -> String:
	return entry.get_slice("=", 0).strip_edges()


## content with its tag line's unique_id as head has it: none when head has none, else head's,
## in the place saved gave its own (before the closing "]" when it gave none). The rest of the tag
## line is content's.
static func _with_unique_id_of(head: String, content: String) -> String:
	var own: RegExMatch = _unique_id.search(head)
	var token: String = "" if own == null else own.get_string()
	var end: int = content.find("\n")
	var tag: String = content if end < 0 else content.substr(0, end)
	var rest: String = "" if end < 0 else content.substr(end)
	if _unique_id.search(tag) != null:
		tag = _unique_id.sub(tag, token)
	elif own != null:
		tag = tag.insert(tag.rfind("]"), token)
	return tag + rest


## saved's sections original has no key for, in saved's order, by the key of the section before
## them that original has.
static func _added(before: Dictionary, after: Dictionary) -> Dictionary:
	var added: Dictionary = {}
	var anchor: String = HEADER_KIND
	for section: Dictionary in after["sections"]:
		if before["by_key"].has(section["key"]):
			anchor = section["key"]
			continue
		if not added.has(anchor):
			added[anchor] = []
		added[anchor].append(section)
	return added


## Why a new resource cannot keep its saved id (the output already has a resource of its kind with
## that id), or "".
static func _collision(entries: Array[Dictionary], added: Dictionary) -> String:
	var taken: Dictionary = {}
	for entry: Dictionary in entries:
		if REFERENCE_OF.has(entry["kind"]):
			taken["%s %s" % [entry["kind"], _quoted(_lines_of(entry["content"])[0], "id")]] = true
	for news: Array in added.values():
		for section: Dictionary in news:
			var id_key: String = "%s %s" % [section["kind"], section["id"]]
			if taken.has(id_key):
				return (
					'the new %s\'s id "%s" is already used in the file'
					% [section["kind"], section["id"]]
				)
	return ""


## The output text: the flattened sections, the nodes put in saved's order.
static func _joined(
	entries: Array[Dictionary], added: Dictionary, after: Dictionary, ids: Dictionary
) -> String:
	var items: Array[Dictionary] = _flattened(entries, added, after, ids)
	_in_saved_order(items, after)
	var parts: PackedStringArray = []
	for item: Dictionary in items:
		parts.append(item["content"])
		parts.append(item["sep"])
	return "".join(parts)


## The output sections as {kind, key, content, sep}: each entry, and after it the sections added
## after it in saved, each with the blank lines saved gives it and the last with the entry's own.
static func _flattened(
	entries: Array[Dictionary], added: Dictionary, after: Dictionary, ids: Dictionary
) -> Array[Dictionary]:
	var items: Array[Dictionary] = []
	for entry: Dictionary in entries:
		items.append(entry)
		var news: Array = added.get(entry["key"], [])
		if news.is_empty():
			continue
		var own_sep: String = entry["sep"]
		entry["sep"] = after["by_key"][entry["key"]]["sep"]
		for index in news.size():
			var section: Dictionary = news[index]
			(
				items
				. append(
					{
						"kind": section["kind"],
						"key": section["key"],
						"content": _mapped(section, ids),
						"sep": own_sep if index == news.size() - 1 else section["sep"],
					}
				)
			)
	return items


## Refills the slots items' node sections fill with those sections in saved's node order, each
## slot keeping its blank lines, so every other section stays where it is. items stays as it is
## when its node keys are not saved's (self_check then says which section differs).
static func _in_saved_order(items: Array[Dictionary], after: Dictionary) -> void:
	var slots: Array[int] = []
	var by_key: Dictionary = {}
	for index in items.size():
		if items[index]["kind"] == "node":
			slots.append(index)
			by_key[items[index]["key"]] = items[index]
	var order: PackedStringArray = _node_keys(after)
	if order.size() != slots.size() or not by_key.has_all(Array(order)):
		return
	var placed: Array[Dictionary] = []
	for place in slots.size():
		var item: Dictionary = by_key[order[place]]
		placed.append({"kind": "node", "key": item["key"], "content": item["content"]})
	for place in slots.size():
		placed[place]["sep"] = items[slots[place]]["sep"]
		items[slots[place]] = placed[place]


## The keys of doc's node sections, in file order.
static func _node_keys(doc: Dictionary) -> PackedStringArray:
	var keys: PackedStringArray = []
	for section: Dictionary in doc["sections"]:
		if section["kind"] == "node":
			keys.append(section["key"])
	return keys


## Why two of doc's node sections carry one unique_id, or "": a node without one is never counted.
static func _shared_unique_id(doc: Dictionary) -> String:
	var seen: Dictionary = {}
	for section: Dictionary in doc["sections"]:
		if section["kind"] != "node":
			continue
		var found: RegExMatch = _unique_id.search(section["head"])
		if found == null:
			continue
		var value: int = int(found.get_string().trim_prefix(" unique_id="))
		if seen.has(value):
			return "the spliced text gave two nodes the unique_id %d" % value
		seen[value] = true
	return ""


## original's header line with load_steps=<resources + 1> where it had load_steps, and saved's uid
## where it had none.
static func _header(original: Dictionary, saved: Dictionary, resources: int) -> String:
	var content: String = original["content"]
	var steps: String = " load_steps=%d" % (resources + 1)
	content = _load_steps.sub(content, steps)
	var uid: String = _quoted(saved["head"], "uid")
	if _quoted(original["head"], "uid").is_empty() and not uid.is_empty():
		var close: int = content.rfind("]")
		content = content.insert(close, ' uid="%s"' % uid)
	return content


## section's content with each reference to a resource original has rewritten to original's id.
static func _mapped(section: Dictionary, ids: Dictionary) -> String:
	var content: String = section["content"]
	var parts: PackedStringArray = []
	var at: int = 0
	for found: RegExMatch in _reference.search_all(content):
		var function: String = found.get_string(1)
		var id: String = ids[function].get(found.get_string(2), found.get_string(2))
		parts.append(content.substr(at, found.get_start() - at))
		parts.append('%s("%s")' % [function, id])
		at = found.get_end()
	parts.append(content.substr(at))
	return "".join(parts)


## The value of key="..." in a tag line, where key follows a space; "" when absent.
static func _quoted(line: String, key: String) -> String:
	var marker: String = ' %s="' % key
	var start: int = line.find(marker)
	if start < 0:
		return ""
	start += marker.length()
	var end: int = line.find('"', start)
	return line.substr(start, end - start) if end > start else ""
