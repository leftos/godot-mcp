extends "res://gd_test.gd"
## SceneEdit.keep_source_set (headless/scene_edit.gd), through SceneOps.edit_scene on scenes
## written under user://: a save whose drop check fails for one node keeps Godot's pack form for
## that node alone, and the other nodes keep their lines, the ones Godot's pack drops included.

const SCENE_OPS_SCRIPT := "../../headless/scene_ops.gd"
const SCENE_EDIT_SCRIPT := "../../headless/scene_edit.gd"
const RunDir := preload("res://run_dir.gd")
const KEEP_UID := "uid://b287keep0000a"
## The fixture's Note label, the node its edit changes.
const NOTE_HEAD := '[node name="Note" type="Label" parent="." unique_id=2005]'

var keep_scene: String = RunDir.path("keep_one.tscn")
var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_OPS_SCRIPT).simplify_path()
)
var _edit: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_EDIT_SCRIPT).simplify_path()
)


func test_one_mismatching_control_reverts_only_itself() -> void:
	var custom: Array = [
		'[node name="PresetCustom" type="Control" parent="." unique_id=2002]',
		"layout_mode = 1",
		"anchors_preset = 15",
		"anchor_right = 0.5",
		"grow_horizontal = 0",
		"",
	]
	var head: Array = [
		'[gd_scene format=3 uid="%s"]' % KEEP_UID,
		"",
		'[node name="Root" type="Control" unique_id=2001]',
		"layout_mode = 3",
		"anchors_preset = 15",
		"anchor_right = 1.0",
		"anchor_bottom = 1.0",
		"grow_horizontal = 2",
		"grow_vertical = 2",
		"",
	]
	var clean: Array = [
		'[node name="PresetOnly" type="Control" parent="." unique_id=2003]',
		"layout_mode = 1",
		"anchors_preset = 15",
		"anchor_right = 1.0",
		"anchor_bottom = 1.0",
		"grow_horizontal = 2",
		"grow_vertical = 2",
		"",
		'[node name="PosPlain" type="Label" parent="." unique_id=2004]',
		"layout_mode = 0",
		"size_flags_vertical = 4",
		'text = "a"',
		"",
	]
	_write(keep_scene, _text(head + custom + clean + [NOTE_HEAD, "layout_mode = 0", 'text = "a"']))
	var update: Dictionary = {"nodePath": "Note", "property": "text", "value": "b"}

	var updated: Dictionary = _ops.edit_scene(
		"set_node_properties", {"scene": keep_scene, "updates": [update]}
	)

	var packed_custom: Array = [
		'[node name="PresetCustom" type="Control" parent="." unique_id=2002]',
		"layout_mode = 1",
		"anchor_right = 0.5",
		"anchor_bottom = 1.0",
		"grow_horizontal = 0",
		"grow_vertical = 2",
		"",
	]
	var note: Array = [NOTE_HEAD, "layout_mode = 0", 'text = "b"']
	var expected: String = _text(head + packed_custom + clean + note)
	assert_eq(_saved(keep_scene), expected, "only PresetCustom takes the pack's form")
	var warning: String = (
		"%s: kept Godot's pack form for PresetCustom, since leaving out lines the source did not "
		+ "store would change how they load: PresetCustom.anchor_bottom would load as 0.0, not 1.0"
	)
	assert_eq(_warning_of(updated), warning % keep_scene, "the warning names PresetCustom")
	_remove_scene(keep_scene)


## SceneEdit._settled, the per-path loop both the drop and the restore pass run: a path whose node
## does not read its changed value fails alone, and the next build leaves it out, so the clean
## path keeps its change. No scene is known whose restore pass fails its check, so the restore
## pass's use of the loop is pinned here, on a build that claims a value Note does not read.
func test_a_path_that_does_not_read_its_value_fails_alone() -> void:
	var root := Control.new()
	for node_name: String in ["Note", "PosPlain"]:
		var label := Label.new()
		label.name = node_name
		label.text = "a"
		root.add_child(label)
		label.owner = root
	var packed := PackedScene.new()
	packed.pack(root)
	root.free()
	var bundled: Dictionary = packed.get("_bundled")
	var builds: Array = []
	var build := func(failed: Dictionary) -> Array:
		builds.append(failed.duplicate())
		var changed: Dictionary = {"Note": {"text": "zzz"}, "PosPlain": {"text": "a"}}
		for path: String in failed:
			changed.erase(path)
		return [bundled, changed]

	var failed: Dictionary = _edit._settled(packed, build)

	assert_eq(failed, {"Note": 'Note.text would load as "a", not "zzz"'}, "only Note fails")
	assert_eq(builds, [{}, failed], "the second build leaves Note out and PosPlain in")


## The warning of an edit's result, "" when it has none; a failed edit's error, so it shows.
func _warning_of(edited: Dictionary) -> String:
	if not edited.get("ok", false):
		return "error: %s" % edited.get("error", "")
	return str((edited["result"] as Dictionary).get("warning", ""))


func _saved(path: String) -> String:
	return FileAccess.get_file_as_string(path)


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	assert_true(file != null, "%s opens for writing" % path)
	file.store_string(text)
	file.close()


func _remove_scene(path: String) -> void:
	DirAccess.remove_absolute(path)
	DirAccess.remove_absolute(path + ".uidren")


func _text(lines: Array) -> String:
	return "\n".join(PackedStringArray(lines)) + "\n"
