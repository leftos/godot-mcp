# gdlint: disable=max-public-methods
extends "res://gd_test.gd"
## SceneFiles.dependency_closure (headless/scene_files.gd) on text resources written to user://:
## every dependency followed once, a cycle ending, a missing file left out, visited shared.

const SCENE_FILES_SCRIPT := "../../headless/scene_files.gd"
const SCENE_OPS_SCRIPT := "../../headless/scene_ops.gd"
const RunDir := preload("res://run_dir.gd")
const THIRD_UID := "uid://bqclosure3rda"
const TEXT_LOADER := "scene/resources/resource_format_text.cpp"
const LOST := "res://a.tscn:9 - Parse Error: [ext_resource] referenced non-existent resource"
const STAMP_SCENE := '[gd_scene format=3]\n\n[node name="Hand" type="Node2D"]\n'

var first: String = RunDir.path("closure_first.tres")
var second: String = RunDir.path("closure_second.tres")
var third: String = RunDir.path("closure_third.tres")
var missing: String = RunDir.path("closure_missing.tres")
var _files: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_FILES_SCRIPT).simplify_path()
)
var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_OPS_SCRIPT).simplify_path()
)


## An engine log holding entries, as the one SceneOps reads.
class EngineLog:
	extends RefCounted
	var entries: Array = []

	func _init(held: Array) -> void:
		entries = held

	func since(start: int) -> Array:
		return entries.slice(start)


func test_is_text_takes_the_text_formats_in_any_case() -> void:
	assert_true(_files.is_text("res://a.tscn"), "tscn")
	assert_true(_files.is_text("res://a.TRES"), "TRES")
	assert_true(not _files.is_text("res://a.xscn"), "xscn")
	assert_true(not _files.is_text("res://a.scn"), "scn")


func test_load_failure_of_a_recognized_format_is_the_message_alone() -> void:
	assert_eq(_files.load_failure("res://a.tscn", "failed"), "failed", "a loader reads it")


func test_load_failure_of_an_unrecognized_format_says_to_build() -> void:
	var failure: String = _files.load_failure("res://a.XSCN", "failed")
	assert_true(failure.begins_with("failed: no loader reads .xscn files"), failure)
	assert_true(failure.contains("build the C# project first"), failure)


func test_a_save_as_within_xscn_carries_the_source() -> void:
	assert_true(_files.carries_source("res://a.xscn", "res://b.xscn"), "xscn")


func test_a_save_as_within_xres_carries_the_source() -> void:
	assert_true(_files.carries_source("res://a.xres", "res://b.XRES"), "xres")


func test_a_save_as_within_tscn_carries_nothing() -> void:
	assert_true(not _files.carries_source("res://a.tscn", "res://b.tscn"), "tscn")


func test_a_save_as_to_another_format_carries_nothing() -> void:
	assert_true(not _files.carries_source("res://a.xscn", "res://b.tscn"), "xscn to tscn")


func test_a_save_in_place_carries_nothing() -> void:
	assert_true(not _files.carries_source("res://a.xscn", "res://a.xscn"), "in place")
	assert_true(not _files.carries_source("", "res://a.xscn"), "no source")


func test_a_save_from_an_xscn_to_a_tscn_reads_no_layout_source() -> void:
	var source: String = _written("layout_source.xscn", "; not scene text\n")
	assert_eq(_files._layout_source(source, "res://target.tscn"), "", "xscn text is never read")


func test_a_save_from_a_tscn_to_an_xscn_reads_no_layout_source() -> void:
	var source: String = _written("layout_source.tscn", "[gd_scene format=3]\n")
	assert_eq(_files._layout_source(source, "res://target.xscn"), "", "no splice into an xscn")


func test_a_save_from_a_tscn_to_a_tscn_reads_the_source() -> void:
	var text := "[gd_scene format=3]\n"
	var source: String = _written("layout_source_text.tscn", text)
	assert_eq(_files._layout_source(source, "res://target.tscn"), text, "the text it splices into")


func test_a_text_loader_parse_error_is_a_load_loss() -> void:
	assert_true(_loses("error", TEXT_LOADER, LOST), "a resource it could not fetch")


func test_a_text_loader_line_without_the_mark_is_no_load_loss() -> void:
	var message := "Cannot open file 'res://nope.tres'."
	assert_true(not _loses("error", TEXT_LOADER, message), "a script's own load() logs this too")


func test_a_backslash_path_of_the_text_loader_is_a_load_loss() -> void:
	assert_true(_loses("error", TEXT_LOADER.replace("/", "\\"), LOST), "a Windows build's path")


func test_a_longer_path_of_the_text_loader_is_a_load_loss() -> void:
	assert_true(_loses("error", "./" + TEXT_LOADER, LOST), "matched by its end")


func test_the_resource_loaders_error_is_no_load_loss() -> void:
	var message := "Failed loading resource: res://nope.tres."
	assert_true(not _loses("error", "core/io/resource_loader.cpp", message), "a failed load()")


func test_a_push_error_is_no_load_loss() -> void:
	assert_true(not _loses("error", "core/variant/variant_utility.cpp", LOST), "a script's own")


func test_a_script_parse_failure_is_no_load_loss() -> void:
	var file := "modules/gdscript/gdscript_resource_format.cpp"
	assert_true(not _loses("error", file, LOST), "broken_script_of judges it")


func test_an_error_in_a_script_file_is_no_load_loss() -> void:
	assert_true(not _loses("error", "res://noisy.gd", LOST), "a script's own")


func test_a_warning_from_the_text_loader_is_no_load_loss() -> void:
	assert_true(not _loses("warning", TEXT_LOADER, LOST), "nothing was dropped")


func test_entries_before_start_are_not_counted() -> void:
	var entry := {"type": "error", "file": TEXT_LOADER, "message": LOST}
	assert_true(not _ops._load_losses_since(EngineLog.new([entry]), 1), "logged before the load")


func test_no_engine_log_is_no_load_loss() -> void:
	assert_true(not _ops._load_losses_since(null, 0), "nothing to read")


func test_an_unreadable_source_leaves_the_target_as_it_was() -> void:
	var target: String = _written("unread_target.xscn", "held\n")
	var from: String = RunDir.path("unread_source_missing.xscn")
	var error: int = _files._save_over_source(Resource.new(), target, from)
	assert_eq(error, ERR_FILE_NOT_FOUND, "the read's own error")
	assert_eq(FileAccess.get_file_as_string(target), "held\n", "the target is untouched")


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


func test_a_header_with_a_uid_is_known() -> void:
	var scene: String = _written("stamp_uid.tscn", '[gd_scene format=3 uid="uid://bqstamp"]\n')
	var resource: String = _written(
		"stamp_uid.tres", '[gd_resource type="Resource" format=3 uid="uid://bqres"]\n'
	)
	assert_eq(_files.header_uid_of(scene), "uid://bqstamp", "a scene")
	assert_eq(_files.header_uid_of(resource), "uid://bqres", "a resource")


func test_a_header_without_a_uid_is_not() -> void:
	assert_eq(_files.header_uid_of(_written("stamp_none.tscn", STAMP_SCENE)), "", "no uid")
	assert_eq(_files.header_uid_of(_written("stamp_bad.tscn", "not a header\n")), "", "unreadable")


func test_a_header_split_over_lines_is_read_whole() -> void:
	var text := '\n[gd_scene\nuid="uid://bqsplit"]\n\n[node name="S" type="Node2D"]\n'
	var path: String = _res_written("split.tscn", text)
	var reply: Dictionary = _files.stamp_uids({"files": [path]})
	assert_eq(reply["result"]["alreadyHad"], 1, "the split header's uid counts")
	assert_eq(reply["result"]["stamped"].size(), 0, "not rewritten")
	assert_eq(FileAccess.get_file_as_string(path), text, "untouched")
	_res_remove(path)


func test_stamp_uids_changes_the_header_alone() -> void:
	var path: String = _res_written("hand.tscn", STAMP_SCENE)
	var reply: Dictionary = _files.stamp_uids({"files": [path]})
	var result: Dictionary = reply["result"]
	assert_true(reply["ok"], "it answers")
	assert_eq(result["checked"], 1, "one file checked")
	assert_eq(result["alreadyHad"], 0, "none had a uid")
	var stamped: Array = result["stamped"]
	assert_eq(stamped.size(), 1, "the file it changed")
	assert_eq(stamped[0]["path"], path, "its path")
	var uid: String = stamped[0]["uid"]
	assert_true(uid.begins_with("uid://"), uid)
	assert_eq(
		FileAccess.get_file_as_string(path),
		'[gd_scene format=3 uid="%s"]\n\n[node name="Hand" type="Node2D"]\n' % uid,
		"only the header line changed"
	)
	_res_remove(path)


func test_stamp_uids_leaves_a_file_that_has_a_uid() -> void:
	var text := '[gd_scene format=3 uid="uid://bqkept01"]\n\n[node name="Kept" type="Node2D"]\n'
	var path: String = _res_written("kept.tscn", text)
	var reply: Dictionary = _files.stamp_uids({"files": [path]})
	assert_eq(reply["result"]["alreadyHad"], 1, "counted as having one")
	assert_eq(reply["result"]["stamped"].size(), 0, "not stamped")
	assert_eq(FileAccess.get_file_as_string(path), text, "untouched")
	_res_remove(path)


func test_stamp_uids_drops_the_headers_other_attributes() -> void:
	var path: String = _res_written(
		"crlf.tscn", '[gd_scene load_steps=2 format=3]\r\n\r\n[node name="Hand" type="Node2D"]\r\n'
	)
	var reply: Dictionary = _files.stamp_uids({"files": [path]})
	var uid: String = reply["result"]["stamped"][0]["uid"]
	assert_eq(
		FileAccess.get_file_as_string(path),
		'[gd_scene format=3 uid="%s"]\r\n\r\n[node name="Hand" type="Node2D"]\r\n' % uid,
		"load_steps gone, the CRLF after the header kept"
	)
	_res_remove(path)


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


## Whether a log holding one entry of type from file reads as a load loss.
func _loses(type: String, file: String, message: String) -> bool:
	var entry := {"type": type, "file": file, "message": message}
	return _ops._load_losses_since(EngineLog.new([entry]), 0)


## Writes text to name under the run's folder and returns its path.
func _written(name: String, text: String) -> String:
	var path: String = RunDir.path(name)
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()
	return path


## Writes text to name under res:// and returns its path: ResourceSaver.set_uid changes a res://
## file alone. A path this run's uid cache already lists is stamped with that id, so the name need
## not be new.
func _res_written(name: String, text: String) -> String:
	var path: String = "res://gdtest-%s" % name
	_write(path, text)
	return path


## Removes a file _res_written wrote.
func _res_remove(path: String) -> void:
	DirAccess.remove_absolute(path)
