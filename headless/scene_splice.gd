extends RefCounted
## Keeping a scene file's own text through a headless save. ResourceSaver.save writes the whole
## file in Godot's own form (no load_steps, unique_id= on every node, properties reordered,
## ext_resource ids renumbered), so splice(original, saved) keeps every section of the file the
## edit left alone byte for byte and takes the saved text only for the sections it added or
## changed.
##
## A section is a line opening [gd_scene, [ext_resource, [sub_resource, [node, [connection or
## [editable and every line up to the next such line, blank lines included. Sections are matched
## by key: an ext_resource by its path, a node by its parent and name (the root by having no
## parent), a connection by its signal, from, to and method, an editable by its path, and a
## sub_resource by its canonical form and its place among the sub_resources with that form, in
## file order, since its id is the saver's and equal resources are common. Two sections compare
## by their canonical form: the tag line without unique_id=, id= and uid=, the property lines
## sorted, and each ExtResource("<id>") or SubResource("<id>") replaced by what it names.

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
## The start of a property line: a name, then " = ".
static var _property_start := RegEx.create_from_string("^[A-Za-z_][^\\s=]*\\s*=")
static var _load_steps := RegEx.create_from_string(" load_steps=\\d+")


## original with the sections saved changed, added or deleted relative to it taken from saved, and
## every other section as original has it: {text}, or {fallback: <reason>} when a section cannot
## be mapped (a duplicate key, a reference to no section, a new id the output already uses) or the
## result would not read as saved does. Line endings follow original's.
static func splice(original: String, saved: String) -> Dictionary:
	if original.contains("\r\n"):
		saved = saved.replace("\r\n", "\n").replace("\n", "\r\n")
	var before: Dictionary = _parse(original)
	var after: Dictionary = _parse(saved)
	for doc: Dictionary in [before, after]:
		if not doc["error"].is_empty():
			return {"fallback": doc["error"]}
	var built: Dictionary = _build(before, after)
	if built.has("fallback"):
		return built
	var failure: String = self_check(built["text"], saved)
	return {"fallback": failure} if not failure.is_empty() else built


## "" when spliced and saved hold the same sections, each by its key with an equal canonical form
## (the gd_scene header left out); else what differs.
static func self_check(spliced: String, saved: String) -> String:
	var mine: Dictionary = _parse(spliced)
	var theirs: Dictionary = _parse(saved)
	for doc: Dictionary in [mine, theirs]:
		if not doc["error"].is_empty():
			return doc["error"]
	var ours: Dictionary = _forms(mine)
	var wanted: Dictionary = _forms(theirs)
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
## occurrences: {sub_resource key: count}, visiting, error}; each section gains its head (tag
## line), id, canon and key. error is "" or why the text cannot be spliced.
static func _parse(text: String) -> Dictionary:
	var doc: Dictionary = {
		"sections": sections_of(text),
		"by_key": {},
		"ids": {EXT_KIND: {}, SUB_KIND: {}},
		"canons": {},
		"occurrences": {},
		"visiting": {},
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
	section["key"] = _key_of(section)
	if kind == SUB_KIND:
		var place: int = doc["occurrences"].get(section["key"], 0) + 1
		doc["occurrences"][section["key"]] = place
		section["key"] += " #%d" % place
	if doc["by_key"].has(section["key"]):
		doc["error"] = "two sections have the key %s" % section["key"]
	doc["by_key"][section["key"]] = section


static func _key_of(section: Dictionary) -> String:
	var head: String = section["head"]
	var key: String = HEADER_KIND
	match section["kind"]:
		EXT_KIND:
			key = "ext_resource path=" + _quoted(head, "path")
		SUB_KIND:
			key = "sub_resource " + section["canon"]
		"node":
			var parent: String = _quoted(head, "parent")
			var parent_key: String = "(root)" if parent.is_empty() else "parent=" + parent
			key = "node %s name=%s" % [parent_key, _quoted(head, "name")]
		"connection":
			var fields: PackedStringArray = []
			for field: String in ["signal", "from", "to", "method"]:
				fields.append("%s=%s" % [field, _quoted(head, field)])
			key = "connection " + " ".join(fields)
		"editable":
			key = "editable path=" + _quoted(head, "path")
	return key


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
## its key, and saved's new sections each after the section that precedes it in saved. {text} or
## {fallback}.
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
## the canonical forms match, else saved's with its ids mapped back. A dropped section's blank lines
## go to the section before it.
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
			entry["content"] = _mapped(saved, ids).replace(
				' id="%s"' % saved["id"], ' id="%s"' % section["id"]
			)
		entries.append(entry)
	return entries


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


## The output text: each entry, and after it the sections added after it in saved, each with the
## blank lines saved gives it and the last with the entry's own.
static func _joined(
	entries: Array[Dictionary], added: Dictionary, after: Dictionary, ids: Dictionary
) -> String:
	var parts: PackedStringArray = []
	for entry: Dictionary in entries:
		parts.append(entry["content"])
		var news: Array = added.get(entry["key"], [])
		if news.is_empty():
			parts.append(entry["sep"])
			continue
		parts.append(after["by_key"][entry["key"]]["sep"])
		for index in news.size():
			parts.append(_mapped(news[index], ids))
			parts.append(entry["sep"] if index == news.size() - 1 else news[index]["sep"])
	return "".join(parts)


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
