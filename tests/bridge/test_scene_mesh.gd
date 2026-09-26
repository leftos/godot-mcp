extends "res://gd_test.gd"
## export_mesh_library's deferred write (headless/scene_mesh.gd): a library built with
## context.pending_writes is only queued, and write_pending saves it.

const SCENE_MESH_SCRIPT := "../../headless/scene_mesh.gd"
const SCENE := "user://mesh_probe.tscn"
const OUTPUT := "user://mesh_probe_library.tres"

var _mesh: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(SCENE_MESH_SCRIPT).simplify_path()
)


func test_pending_write_waits_for_write_pending() -> void:
	_write(SCENE, '[gd_scene format=3]\n\n[node name="Probe" type="Node3D"]\n')
	_remove(OUTPUT)
	var root := Node3D.new()
	var box := MeshInstance3D.new()
	box.name = "Box"
	box.mesh = BoxMesh.new()
	root.add_child(box)
	var pending: Array = []
	var context: Dictionary = {"scene": SCENE, "build": "", "pending_writes": pending}
	var params: Dictionary = {"output": OUTPUT, "meshItemNames": []}

	var applied: Dictionary = _mesh.apply_export_mesh_library(root, params, context)
	root.free()

	var items: Array = applied.get("result", {}).get("items", [])
	assert_eq(items.size(), 1, "the result lists the item as a written library would")
	assert_eq(pending.size(), 1, "the write is queued")
	assert_true(not FileAccess.file_exists(OUTPUT), "nothing is written before write_pending")
	assert_eq(_mesh.write_pending(pending), {}, "write_pending saves every queued write")
	assert_true(FileAccess.file_exists(OUTPUT), "write_pending writes the library")
	var library := ResourceLoader.load(OUTPUT, "", ResourceLoader.CACHE_MODE_IGNORE) as MeshLibrary
	assert_true(library != null, "the file is a MeshLibrary")
	if library != null:
		assert_eq(library.get_item_name(0), "Box", "the queued library's item")
	_remove(OUTPUT)
	_remove(SCENE)


func _write(path: String, text: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	file.store_string(text)
	file.close()


func _remove(path: String) -> void:
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)
