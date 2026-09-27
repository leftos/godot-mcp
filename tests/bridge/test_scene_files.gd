extends "res://gd_test.gd"
## SceneFiles.dependency_closure (headless/scene_files.gd) on text resources written to user://:
## every dependency followed once, a cycle ending, a missing file left out, visited shared.

const SCENE_FILES_SCRIPT := "../../headless/scene_files.gd"
const FIRST := "user://closure_first.tres"
const SECOND := "user://closure_second.tres"
const THIRD := "user://closure_third.tres"
const MISSING := "user://closure_missing.tres"
const THIRD_UID := "uid://bqclosure3rda"

var _files: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_FILES_SCRIPT).simplify_path()
)


func test_closure_follows_every_dependency_once_through_a_cycle() -> void:
	_write(FIRST, _resource([_ext(SECOND, ""), _ext(MISSING, "")]))
	_write(SECOND, _resource([_ext(THIRD, THIRD_UID)]))
	_write(THIRD, _resource([_ext(FIRST, "")]))
	_remove(MISSING)
	var visited: Dictionary = {}

	var closure: PackedStringArray = _files.dependency_closure(FIRST, visited)

	assert_eq(
		closure,
		PackedStringArray([FIRST, SECOND, THIRD]),
		"the start, each file once, a uid entry by its fallback path, no missing file"
	)
	assert_eq(visited.size(), 3, "each listed file is marked visited, the missing one is not")
	_remove_all()


func test_a_visited_file_is_neither_listed_nor_followed() -> void:
	_write(FIRST, _resource([_ext(SECOND, "")]))
	_write(SECOND, _resource([_ext(THIRD, "")]))
	_write(THIRD, _resource([]))
	var visited: Dictionary = {SECOND: true}

	var closure: PackedStringArray = _files.dependency_closure(FIRST, visited)

	assert_eq(closure, PackedStringArray([FIRST]), "a visited file stops the walk there")
	_remove_all()


func test_a_missing_start_lists_nothing() -> void:
	_remove(MISSING)

	var closure: PackedStringArray = _files.dependency_closure(MISSING, {})

	assert_eq(closure, PackedStringArray(), "a start that does not exist reaches nothing")


func _resource(ext_tags: Array) -> String:
	var text: String = '[gd_resource type="Resource" format=3]\n\n'
	for index in ext_tags.size():
		text += (ext_tags[index] as String) % str(index + 1)
	return text + "\n[resource]\n"


## An ext_resource tag for path, with uid when it is not "", its id left as %s.
func _ext(path: String, uid: String) -> String:
	var uid_attribute: String = "" if uid.is_empty() else ' uid="%s"' % uid
	return '[ext_resource type="Resource"%s path="%s" id="%%s"]\n' % [uid_attribute, path]


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()


func _remove(path: String) -> void:
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


func _remove_all() -> void:
	for path: String in [FIRST, SECOND, THIRD, MISSING]:
		_remove(path)
