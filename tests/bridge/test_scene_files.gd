extends "res://gd_test.gd"
## SceneFiles.dependency_closure (headless/scene_files.gd) on text resources written to user://:
## every dependency followed once, a cycle ending, a missing file left out, visited shared.

const SCENE_FILES_SCRIPT := "../../headless/scene_files.gd"
const RunDir := preload("res://run_dir.gd")
const THIRD_UID := "uid://bqclosure3rda"

var first: String = RunDir.path("closure_first.tres")
var second: String = RunDir.path("closure_second.tres")
var third: String = RunDir.path("closure_third.tres")
var missing: String = RunDir.path("closure_missing.tres")
var _files: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_FILES_SCRIPT).simplify_path()
)


func test_closure_follows_every_dependency_once_through_a_cycle() -> void:
	_write(first, _resource([_ext(second, ""), _ext(missing, "")]))
	_write(second, _resource([_ext(third, THIRD_UID)]))
	_write(third, _resource([_ext(first, "")]))
	_remove(missing)
	var visited: Dictionary = {}

	var closure: PackedStringArray = _files.dependency_closure(first, visited)

	assert_eq(
		closure,
		PackedStringArray([first, second, third]),
		"the start, each file once, a uid entry by its fallback path, no missing file"
	)
	assert_eq(visited.size(), 3, "each listed file is marked visited, the missing one is not")
	_remove_all()


func test_a_visited_file_is_neither_listed_nor_followed() -> void:
	_write(first, _resource([_ext(second, "")]))
	_write(second, _resource([_ext(third, "")]))
	_write(third, _resource([]))
	var visited: Dictionary = {second: true}

	var closure: PackedStringArray = _files.dependency_closure(first, visited)

	assert_eq(closure, PackedStringArray([first]), "a visited file stops the walk there")
	_remove_all()


func test_a_missing_start_lists_nothing() -> void:
	_remove(missing)

	var closure: PackedStringArray = _files.dependency_closure(missing, {})

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
	for path: String in [first, second, third, missing]:
		_remove(path)
