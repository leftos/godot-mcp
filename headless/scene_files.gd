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
## The extensions of the files the editor's scan gives a <path>.uid: those whose loader keeps no uid
## of its own (4.7.2 core/io/resource_loader.cpp L1428-1440, should_create_uid_file).
const UID_FILE_EXTENSIONS: Array[String] = ["gd", "cs", "gdshader", "gdshaderinc"]


## Saves resource to path with the uid uid, creating missing folders, and, for a text scene or
## resource, gives each ext_resource its uid back: the one in known (res:// path to uid:// text)
## first, else the one its file records. With keep_layout, a file that existed before the save
## keeps the text of every section the save left alone (SceneSplice.splice); when a section
## cannot be mapped the file stays as saved and the result carries a warning. {uid, warning?} with
## uid as uid:// text, or {error}. A script the scene names that had no uid is given one
## (uid_or_new), and the saved dictionary's uidFilesWritten lists the .uid files it wrote.
static func save_resource(
	resource: Resource, path: String, uid: int, known: Dictionary, keep_layout: bool
) -> Dictionary:
	return save_resource_from(resource, path, uid, known, path if keep_layout else "")


## save_resource, the text kept for the sections the save left alone read from the file at
## layout_from before the save ("" keeps none): path itself for an edit in place, the source scene
## for a save-as, whose file takes the saved header (its own uid, in Godot's form) and the source's
## text for everything else. Only a text scene or resource is spliced.
static func save_resource_from(
	resource: Resource, path: String, uid: int, known: Dictionary, layout_from: String
) -> Dictionary:
	var error: int = DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	if error != OK and error != ERR_ALREADY_EXISTS:
		return {"error": "cannot create the folder of %s: %s" % [path, error_string(error)]}
	var original: String = _layout_source(layout_from, path)
	error = ResourceSaver.save(resource, path)
	if error != OK:
		return {"error": "%s could not be saved: %s" % [path, error_string(error)]}
	var written: PackedStringArray = []
	error = _write_uids(path, uid, known, written)
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
	note_written(saved, written)
	if not original.is_empty():
		var warning: String = _splice_into(path, original, layout_from != path)
		if not warning.is_empty():
			saved["warning"] = warning
	return saved


## Adds the .uid files in written to result's uidFilesWritten; result is left alone when there are
## none.
static func note_written(result: Dictionary, written: PackedStringArray) -> void:
	if not written.is_empty():
		var listed: Array = result.get("uidFilesWritten", [])
		listed.append_array(Array(written))
		result["uidFilesWritten"] = listed


## Sets the uid of the file at path and, in a text file, writes each ext_resource's uid back,
## appending each .uid file written to written; an error code.
static func _write_uids(
	path: String, uid: int, known: Dictionary, written: PackedStringArray
) -> int:
	var error: int = ResourceSaver.set_uid(path, uid)
	if error == OK and path.get_extension().to_lower() in TEXT_EXTENSIONS:
		error = _restore_ext_uids(path, known, written)
	return error


## The text of the file at layout_from before a save to path that keeps its layout; "" when
## layout_from is "" or no file, or path is not a text file.
static func _layout_source(layout_from: String, path: String) -> String:
	if layout_from.is_empty() or not FileAccess.file_exists(layout_from):
		return ""
	if not path.get_extension().to_lower() in TEXT_EXTENSIONS:
		return ""
	return FileAccess.get_file_as_string(layout_from)


## Splices the file at path, as just saved, into original and writes the result back: "", or the
## warning that the file stays in Godot's own form and why. With own_header (a save-as), original's
## header line is first replaced by the saved one, so the file keeps its own uid.
static func _splice_into(path: String, original: String, own_header: bool) -> String:
	var saved: String = FileAccess.get_file_as_string(path)
	if own_header:
		original = _with_header_of(original, saved)
	var spliced: Dictionary = SceneSplice.splice(original, saved)
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


## original with its first line replaced by saved's first line, original's line ending kept;
## original as it is when it has one line.
static func _with_header_of(original: String, saved: String) -> String:
	var end: int = original.find("\n")
	if end < 0:
		return original
	if end > 0 and original[end - 1] == "\r":
		end -= 1
	return saved.get_slice("\n", 0).trim_suffix("\r") + original.substr(end)


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


## The uid of the file at path (uid_of). A res:// file with none, no .import and no .uid, of a kind
## the editor's scan gives a .uid (UID_FILE_EXTENSIONS), gets the one that scan would write:
## ResourceUID.create_id_for_path, stored as one line in <path>.uid (4.7.2
## editor/file_system/editor_file_system.cpp L1384-1394) and registered in ResourceUID, and the
## .uid file's path is appended to written. INVALID_ID when there is none and none was written.
static func uid_or_new(path: String, written: PackedStringArray) -> int:
	var id: int = uid_of(path)
	if id != ResourceUID.INVALID_ID or not _takes_uid_file(path):
		return id
	id = ResourceUID.create_id_for_path(path)
	var file := FileAccess.open(path + ".uid", FileAccess.WRITE)
	if file == null:
		var reason: String = error_string(FileAccess.get_open_error())
		push_warning("%s.uid could not be written (%s): %s keeps no uid." % [path, reason, path])
		return ResourceUID.INVALID_ID
	file.store_line(ResourceUID.id_to_text(id))
	file.close()
	if ResourceUID.has_id(id):
		ResourceUID.set_id(id, path)
	else:
		ResourceUID.add_id(id, path)
	written.append(path + ".uid")
	return id


## Whether path is a res:// file of a kind the editor's scan gives a .uid, with neither a .uid nor
## an .import beside it.
static func _takes_uid_file(path: String) -> bool:
	if not path.begins_with("res://") or not FileAccess.file_exists(path):
		return false
	if FileAccess.file_exists(path + ".uid") or FileAccess.file_exists(path + ".import"):
		return false
	return path.get_extension().to_lower() in UID_FILE_EXTENSIONS


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
## the file the tag names (uid_or_new, which appends each .uid file it writes to written); an error
## code.
static func _restore_ext_uids(path: String, known: Dictionary, written: PackedStringArray) -> int:
	var text: String = FileAccess.get_file_as_string(path)
	var uids: Dictionary = known.duplicate()
	for line in text.split("\n"):
		var ext_path: String = ext_resource_path(line)
		if ext_path.is_empty() or uids.has(ext_path):
			continue
		var id: int = uid_or_new(ext_path, written)
		if id != ResourceUID.INVALID_ID:
			uids[ext_path] = ResourceUID.id_to_text(id)
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return FileAccess.get_open_error()
	file.store_string(with_ext_uids(text, uids))
	file.close()
	return OK
