extends RefCounted
## Saving a resource headless with the uids a --script save leaves out put back, and reading uids
## from files. A --script save writes neither the header uid nor any ext_resource's uid=
## (4.7.2 core/io/resource_saver.cpp L285-294), so save_resource sets the one and, in a text
## file, writes the others back. Outside the editor a file's uid is read from the file itself (see
## uid_of).

const SceneSplice := preload("scene_splice.gd")
const EXT_TAG := "[ext_resource "
## The warning a save that keeps the layout returns when it cannot, with the reason.
const LAYOUT_WARNING := (
	"the scene was saved in Godot's own form, " + "not only the parts the edit changed: %s"
)
const PATH_ATTRIBUTE := ' path="'
const UID_ATTRIBUTE := ' uid="'
const UID_PREFIX := "uid://"
## The extensions of the text formats whose ext_resource tags are written back; a binary .res or
## .scn is never rewritten as text.
const TEXT_EXTENSIONS: Array[String] = ["tscn", "tres"]


## Saves resource to path with the uid uid, creating missing folders, and, for a text scene or
## resource, gives each ext_resource its uid back: the one in known (res:// path to uid:// text)
## first, else the one its file records. With keep_layout, a file that existed before the save
## keeps the text of every section the save left alone (SceneSplice.splice); when a section
## cannot be mapped the file stays as saved and the result carries a warning. {uid, warning?} with
## uid as uid:// text, or {error}.
static func save_resource(
	resource: Resource, path: String, uid: int, known: Dictionary, keep_layout: bool
) -> Dictionary:
	var error: int = DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	if error != OK and error != ERR_ALREADY_EXISTS:
		return {"error": "cannot create the folder of %s: %s" % [path, error_string(error)]}
	var original: String = _layout_source(path, keep_layout)
	error = ResourceSaver.save(resource, path)
	if error != OK:
		return {"error": "%s could not be saved: %s" % [path, error_string(error)]}
	error = _write_uids(path, uid, known)
	if error != OK:
		var reason: String = error_string(error)
		return {
			"error":
			(
				"%s was saved, but its uids could not be written back (%s): it has none now; save it again."
				% [path, reason]
			)
		}
	var saved: Dictionary = {"uid": ResourceUID.id_to_text(uid)}
	if not original.is_empty():
		var warning: String = _splice_into(path, original)
		if not warning.is_empty():
			saved["warning"] = warning
	return saved


## Sets the uid of the file at path and, in a text file, writes each ext_resource's uid back; an
## error code.
static func _write_uids(path: String, uid: int, known: Dictionary) -> int:
	var error: int = ResourceSaver.set_uid(path, uid)
	if error == OK and path.get_extension().to_lower() in TEXT_EXTENSIONS:
		error = _restore_ext_uids(path, known)
	return error


## The text of the file at path before a save that keeps its layout; "" when the layout is not
## kept or there is no file.
static func _layout_source(path: String, keep_layout: bool) -> String:
	if not keep_layout or not FileAccess.file_exists(path):
		return ""
	return FileAccess.get_file_as_string(path)


## Splices the file at path, as just saved, into original and writes the result back: "", or the
## warning that the file stays in Godot's own form and why.
static func _splice_into(path: String, original: String) -> String:
	var spliced: Dictionary = SceneSplice.splice(original, FileAccess.get_file_as_string(path))
	var reason: String = spliced.get("fallback", "")
	if reason.is_empty():
		var file := FileAccess.open(path, FileAccess.WRITE)
		if file != null:
			file.store_string(spliced["text"])
			file.close()
			return ""
		reason = (
			"the spliced text could not be written (%s)" % error_string(FileAccess.get_open_error())
		)
	return LAYOUT_WARNING % reason


## The uid to save path with: the one the file at path has, else a new one.
static func uid_for(path: String) -> int:
	var id: int = uid_of(path) if FileAccess.file_exists(path) else ResourceUID.INVALID_ID
	return id if id != ResourceUID.INVALID_ID else ResourceUID.create_id()


## The uid of the file at path, or INVALID_ID. Outside the editor ResourceLoader.get_resource_uid
## only looks the path up in ResourceUID's cache (4.7.2 core/io/resource_loader.cpp L1412-1416),
## which is empty in a project with no .godot/uid_cache.bin, for text and binary files alike
## (measured); so the uid is read from the file too: a text scene's or resource's header, a
## script's <path>.uid, an imported file's <path>.import.
static func uid_of(path: String) -> int:
	var id: int = ResourceLoader.get_resource_uid(path)
	if id != ResourceUID.INVALID_ID:
		return id
	var text: String = _uid_text_of(path)
	return ResourceUID.INVALID_ID if text.is_empty() else ResourceUID.text_to_id(text)


## The value of key="..." in line, where key starts the line or follows a space; "" when absent.
static func quoted_value(line: String, key: String) -> String:
	var marker: String = key + '="'
	var start: int = 0 if line.begins_with(marker) else line.find(" " + marker)
	if start < 0:
		return ""
	start = line.find(marker, start) + marker.length()
	var end: int = line.find('"', start)
	return line.substr(start, end - start) if end > start else ""


## The uid of each ext_resource tag in a scene's text that carries one, by its path.
static func ext_uids_in(text: String) -> Dictionary:
	var uids: Dictionary = {}
	for line in text.split("\n"):
		var path: String = ext_resource_path(line)
		var uid: String = quoted_value(line, "uid")
		if not path.is_empty() and not uid.is_empty():
			uids[path] = uid
	return uids


## text with uid="<uids[path]>" added to each ext_resource tag that has no uid and whose path is
## in uids (res:// path to uid:// text); every other line as it was.
static func with_ext_uids(text: String, uids: Dictionary) -> String:
	var lines: PackedStringArray = text.split("\n")
	for index in lines.size():
		var line: String = lines[index]
		var path: String = ext_resource_path(line)
		if path.is_empty() or line.contains(UID_ATTRIBUTE) or not uids.has(path):
			continue
		lines[index] = line.replace(
			PATH_ATTRIBUTE, '%s%s"%s' % [UID_ATTRIBUTE, uids[path], PATH_ATTRIBUTE]
		)
	return "\n".join(lines)


## The files reached from the file at path through ResourceLoader.get_dependencies, path first,
## each once, in the order they are found, however deep; every dependency is followed, whatever
## its extension. A file that does not exist is left out and not followed. A path in visited is
## left out too, and each path listed is added to it, so a cycle ends and a later call sharing
## visited lists only files no earlier call did.
static func dependency_closure(path: String, visited: Dictionary) -> PackedStringArray:
	var reached: PackedStringArray = []
	var queue: PackedStringArray = [path]
	while not queue.is_empty():
		var current: String = queue[0]
		queue.remove_at(0)
		if visited.has(current) or not FileAccess.file_exists(current):
			continue
		visited[current] = true
		reached.append(current)
		queue.append_array(dependency_paths(current))
	return reached


## The paths of the file at path's ResourceLoader.get_dependencies entries. An entry is a path,
## or, for a dependency saved with its uid, "<uid>::::<fallback path>", the second section always
## empty (4.7.2 doc/classes/ResourceLoader.xml): its fallback path is taken, and the uid is not
## resolved, since outside the editor that reads only the uid cache (see uid_of).
static func dependency_paths(path: String) -> PackedStringArray:
	var paths: PackedStringArray = []
	for dependency in ResourceLoader.get_dependencies(path):
		var last: String = dependency.get_slice("::", dependency.get_slice_count("::") - 1)
		if not last.is_empty() and not last.begins_with(UID_PREFIX):
			paths.append(last)
	return paths


## The path="..." of an ext_resource tag line, or "" for any other line.
static func ext_resource_path(line: String) -> String:
	if not line.begins_with(EXT_TAG):
		return ""
	var start: int = line.find(PATH_ATTRIBUTE)
	if start < 0:
		return ""
	start += PATH_ATTRIBUTE.length()
	var end: int = line.find('"', start)
	return line.substr(start, end - start) if end > start else ""


## The uid:// text the file at path records for itself, or "".
static func _uid_text_of(path: String) -> String:
	if path.ends_with(".tscn") or path.ends_with(".tres"):
		var file := FileAccess.open(path, FileAccess.READ)
		return "" if file == null else quoted_value(file.get_line(), "uid")
	for sidecar: String in [path + ".uid", path + ".import"]:
		var found: String = _sidecar_uid(sidecar)
		if not found.is_empty():
			return found
	return ""


## The uid:// text of a script's .uid file (the whole file) or an .import file (its uid="...").
static func _sidecar_uid(sidecar: String) -> String:
	if not FileAccess.file_exists(sidecar):
		return ""
	for line in FileAccess.get_file_as_string(sidecar).split("\n"):
		var trimmed: String = line.strip_edges()
		if trimmed.begins_with("uid://"):
			return trimmed
		if trimmed.begins_with('uid="'):
			return quoted_value(trimmed, "uid")
	return ""


## Writes each ext_resource's uid back into the text file at path, from known first, else from
## the file the tag names; an error code.
static func _restore_ext_uids(path: String, known: Dictionary) -> int:
	var text: String = FileAccess.get_file_as_string(path)
	var uids: Dictionary = known.duplicate()
	for line in text.split("\n"):
		var ext_path: String = ext_resource_path(line)
		if ext_path.is_empty() or uids.has(ext_path):
			continue
		var id: int = uid_of(ext_path)
		if id != ResourceUID.INVALID_ID:
			uids[ext_path] = ResourceUID.id_to_text(id)
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return FileAccess.get_open_error()
	file.store_string(with_ext_uids(text, uids))
	file.close()
	return OK
