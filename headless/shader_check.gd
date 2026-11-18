extends RefCounted
## validate's shader checks. A .gdshader is compiled as a headless Godot's Dummy renderer compiles
## it: loading one only stores its code (4.7.2 shader.cpp L56-62 makes its RID lazily), so compile
## asks for the RID, which runs the shader compiler and logs each error while it runs. A
## .gdshaderinc is never compiled alone; includers finds the shaders that include it, directly or
## through other includes, and validate compiles those.

const RES_PREFIX := "res://"
const SHADER := "gdshader"
const INCLUDE := "gdshaderinc"
## How many of the shaders that include a .gdshaderinc target are compiled for it.
const MAX_INCLUDERS := 20
## How deep the preprocessor follows includes (4.7.2 shader_preprocessor.cpp L752).
const MAX_INCLUDE_DEPTH := 25
## The Dummy driver logs from this file: its shader type refusal (4.7.2 material_storage.cpp L185)
## and its "Shader compilation failed." (L192), which follows the compiler's own errors.
const DUMMY_FILE := "material_storage.cpp"
const DUMMY_TYPE_REFUSAL := "not supported in Dummy renderer."
const COMPILE_FAILED := "Shader compilation failed."
## What the compiler says of a shader whose preprocess failed: Shader.set_code then hands it the
## code unprocessed, whose first # it cannot read (4.7.2 shader.cpp L94-106).
const FAILED_PREPROCESS := "Tokenizer: Unknown character #35: '#'"
const INCLUDE_PATTERN := '^\\s*#include\\s*"([^"]*)"'
const SHADER_TYPE_PATTERN := "^\\s*shader_type\\b"
## The shader preprocessor's text for an include that does not exist (4.7.2
## shader_preprocessor.cpp L715-717), which Shader.set_code never logs (shader.cpp L105).
const MISSING_INCLUDE := "Shader include file does not exist: %s"


## Whether path names a file of kind, an extension without its dot, in any letter case.
static func is_kind(path: String, kind: String) -> bool:
	return path.get_extension().to_lower() == kind


## What compiling the .gdshader at path, loaded as resource, logs into log (operations.gd's
## ErrorLog), as compile_errors keeps it. When the preprocess failed, the compiler's misplaced
## error is replaced by the includes the shader reaches that do not exist, if it reaches any.
## Nothing for any other file.
static func compiled(resource: Resource, path: String, log: Object) -> Array:
	if not is_kind(path, SHADER) or not resource is Shader:
		return []
	var start: int = log.count()
	resource.get_rid()
	# get_rid logs the errors; force_sync flushes a compile the rendering server queued instead.
	RenderingServer.force_sync()
	var text: String = FileAccess.get_file_as_string(path)
	var errors: Array = compile_errors(log.since(start), path, text)
	var preprocess_failed: bool = errors.all(
		func(error: Dictionary) -> bool: return error["message"] == FAILED_PREPROCESS
	)
	if not errors.is_empty() and preprocess_failed:
		var missing: Array = missing_includes(text, path)
		if not missing.is_empty():
			return missing
	return errors


## Of entries, what compiling the shader at path, whose code is text, logged: the compiler's errors
## (an entry with no file, in the shader's own text, given path) and the Dummy driver's shader type
## refusal, put on the shader_type line. Every other entry the engine logs is left out; but a
## failed compile with neither gives the driver's "Shader compilation failed." under path.
static func compile_errors(entries: Array, path: String, text: String) -> Array:
	var kept: Array = []
	var failed: bool = false
	for entry: Dictionary in entries:
		if entry.get("errorType") == Logger.ERROR_TYPE_SHADER:
			var file: String = entry["file"]
			kept.append(_error(entry["message"], path if file.is_empty() else file, entry["line"]))
		elif _is_dummy(entry, DUMMY_TYPE_REFUSAL):
			kept.append(_error(entry["message"], path, shader_type_line(text)))
		elif _is_dummy(entry, COMPILE_FAILED):
			failed = true
	if kept.is_empty() and failed:
		kept.append(_error(COMPILE_FAILED, path, 0))
	return kept


static func _is_dummy(entry: Dictionary, message_end: String) -> bool:
	var message: String = entry["message"]
	return (entry["file"] as String).ends_with(DUMMY_FILE) and message.ends_with(message_end)


static func _error(message: String, file: String, line: int) -> Dictionary:
	return {"type": "error", "message": message, "file": file, "line": line}


## The line of text's shader_type statement, comments left out, or 0.
static func shader_type_line(text: String) -> int:
	var pattern := RegEx.create_from_string(SHADER_TYPE_PATTERN)
	var code: PackedStringArray = without_comments(text).split("\n")
	for index: int in code.size():
		if pattern.search(code[index]) != null:
			return index + 1
	return 0


## An error entry for each #include that names a file ResourceLoader does not know, in text, the
## code of the file at path, and in the includes it reaches that exist (each once, at most
## MAX_INCLUDE_DEPTH deep), against the file holding the #include line.
static func missing_includes(text: String, path: String) -> Array:
	var missing: Array = []
	_add_missing(text, path, {path: true}, 0, missing)
	return missing


static func _add_missing(
	text: String, path: String, visited: Dictionary, depth: int, missing: Array
) -> void:
	for include: Dictionary in include_lines(text, path):
		var target: String = include["path"]
		if not ResourceLoader.exists(target):
			missing.append(_error(MISSING_INCLUDE % target, path, include["line"]))
		elif depth < MAX_INCLUDE_DEPTH and not visited.has(target):
			visited[target] = true
			var code: String = FileAccess.get_file_as_string(target)
			_add_missing(code, target, visited, depth + 1, missing)


## The project's .gdshader files that include target, a res:// path, directly or through other
## .gdshaderinc files, by path.
static func includers(target: String) -> PackedStringArray:
	var included_by: Dictionary = _included_by(files_under(RES_PREFIX))
	var found: PackedStringArray = []
	var visited: Dictionary = {target: true}
	var pending: Array = [target]
	while not pending.is_empty():
		for file: String in included_by.get(pending.pop_back(), []):
			if visited.has(file):
				continue
			visited[file] = true
			if is_kind(file, SHADER):
				found.append(file)
			else:
				pending.append(file)
	found.sort()
	return found


## Of each include files name, the files that include it: include path to its includers.
static func _included_by(files: PackedStringArray) -> Dictionary:
	var included_by: Dictionary = {}
	for file: String in files:
		for include: Dictionary in include_lines(FileAccess.get_file_as_string(file), file):
			var listed: Array = included_by.get_or_add(include["path"], [])
			if not listed.has(file):
				listed.append(file)
	return included_by


## Every .gdshader and .gdshaderinc under folder, leaving out hidden folders and those holding a
## .gdignore, which Godot does not scan.
static func files_under(folder: String) -> PackedStringArray:
	var found: PackedStringArray = []
	for file_name: String in DirAccess.get_files_at(folder):
		if is_kind(file_name, SHADER) or is_kind(file_name, INCLUDE):
			found.append(folder.path_join(file_name))
	for dir_name: String in DirAccess.get_directories_at(folder):
		var sub: String = folder.path_join(dir_name)
		if not dir_name.begins_with(".") and not FileAccess.file_exists(sub.path_join(".gdignore")):
			found.append_array(files_under(sub))
	return found


## The #include lines of text, the code of the file at file_path, as {line, path}: each line whose
## first non-blank text, comments left out, is #include "<path>", with path, a res:// path or one
## relative to the file's folder, resolved.
static func include_lines(text: String, file_path: String) -> Array:
	var pattern := RegEx.create_from_string(INCLUDE_PATTERN)
	var lines: Array = []
	var code: PackedStringArray = without_comments(text).split("\n")
	for index: int in code.size():
		var found: RegExMatch = pattern.search(code[index])
		if found != null:
			lines.append({"line": index + 1, "path": _resolved(found.get_string(1), file_path)})
	return lines


## text with its // and /* */ comments left out, each block comment's line breaks kept, so every
## line stays on its own number; a // or /* inside a "string" (an include's res:// path) is kept.
static func without_comments(text: String) -> String:
	var kept: PackedStringArray = []
	var at: int = 0
	while at < text.length():
		var next: int = _next_opener(text, at)
		if next < 0:
			kept.append(text.substr(at))
			break
		var end: int = _end_of(text, next)
		var piece: String = text.substr(next, end - next)
		kept.append(text.substr(at, next - at))
		kept.append(piece if piece.begins_with('"') else "\n".repeat(piece.count("\n")))
		at = end
	return "".join(kept)


## Where the first ", // or /* at or after at starts in text, or -1.
static func _next_opener(text: String, at: int) -> int:
	var first: int = -1
	for opener: String in ['"', "//", "/*"]:
		var found: int = text.find(opener, at)
		if found >= 0 and (first < 0 or found < first):
			first = found
	return first


## Where the string or comment that starts at start in text ends: past its closing " or */, at
## the line break that ends a // comment, or at the text's end.
static func _end_of(text: String, start: int) -> int:
	var opener: String = text.substr(start, 2)
	var closer: String = "\n"
	if opener == "/*":
		closer = "*/"
	elif opener.begins_with('"'):
		closer = '"'
	var end: int = text.find(closer, start + (1 if closer == '"' else 2))
	if end < 0:
		return text.length()
	return end if closer == "\n" else end + closer.length()


## include as the shader preprocessor resolves it from the file at file_path (4.7.2
## shader_preprocessor.cpp L710-713), simplified.
static func _resolved(include: String, file_path: String) -> String:
	var path: String = include.simplify_path()
	if path.is_relative_path():
		path = file_path.get_base_dir().path_join(path)
	return path.simplify_path()


## The note a .gdshaderinc target gets when found shaders include it: none, or more than
## MAX_INCLUDERS, of which the rest were not compiled; else "".
static func include_note(target: String, found: int) -> String:
	if found == 0:
		return "no .gdshader includes %s, so it was not compiled" % target
	if found > MAX_INCLUDERS:
		var left_out: int = found - MAX_INCLUDERS
		return (
			"%d .gdshader files include %s; the first %d by path were compiled, %d left out"
			% [found, target, MAX_INCLUDERS, left_out]
		)
	return ""
