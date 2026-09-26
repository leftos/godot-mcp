extends RefCounted
## export_mesh_library, which scene_ops.gd runs on a scene it opens and never saves: a MeshLibrary
## built from the scene by the editor's Import from Scene rule (4.7.2
## editor/scene/3d/mesh_library_editor_plugin.cpp L515-618), saved to params.output.
##
## The walk starts at the root's children and goes down through every node but a MeshInstance3D,
## whose own children are not searched; each MeshInstance3D with a mesh is an item named after it.
## A later node of the same name takes the item over, keeping its id and, when it has no navigation
## mesh of its own, the earlier node's, as the editor's does. The item's transform is the node's
## local transform, its parents' left out, and its StaticBody3D children's enabled shapes are placed
## by it, then the body's, then the shape owner's, as the editor places them. Its first
## NavigationRegion3D child with a navigation mesh gives the navigation mesh, placed by the item's
## transform and then the region's: here the editor uses the region's alone, which misplaces the
## navigation mesh under a mesh away from the origin. The item's mesh is the node's own, so a mesh
## saved in its own file stays a reference to that file, unless the node overrides a surface
## material: the library then holds a copy with the overrides set (the editor always copies). No
## previews are made: they need EditorInterface.make_mesh_previews.

const SceneFiles := preload("scene_files.gd")


## Builds the library from the scene rooted at root (context.scene) and saves it to params.output:
## {result: {outputPath, items: [{id, name, shapes, navigation}]}}, or {error} with nothing
## written. params.meshItemNames, when not empty, keeps only the items of those names.
static func apply_export_mesh_library(
	root: Node, params: Dictionary, context: Dictionary
) -> Dictionary:
	var scene_path: String = context.get("scene", "")
	var output: String = params.get("output", "")
	var wanted: Array = params.get("meshItemNames", [])
	var found: Dictionary = {}
	for child in root.get_children(true):
		_collect(child, found)
	var refusal: String = _refusal(scene_path, output, found, wanted)
	if not refusal.is_empty():
		return {"error": refusal}
	var library := MeshLibrary.new()
	var items: Array = []
	for item_name: String in found:
		if wanted.is_empty() or wanted.has(item_name):
			items.append(_add_item(library, found[item_name]))
	var source: String = FileAccess.get_file_as_string(ProjectSettings.globalize_path(scene_path))
	var saved: Dictionary = SceneFiles.save_resource(
		library, output, SceneFiles.uid_for(output), SceneFiles.ext_uids_in(source)
	)
	if saved.has("error"):
		return saved
	return {"result": {"outputPath": output, "items": items}}


## Why output cannot be written because the scene uses it, or "": dependencies are the scene's
## ResourceLoader.get_dependencies entries, "<path>[::<type>]" or "<uid>::<type>::<path>".
static func dependency_refusal(
	scene_path: String, output: String, dependencies: PackedStringArray
) -> String:
	for dependency in dependencies:
		if output in dependency.split("::"):
			return "%s is used by %s; export the library to another file." % [output, scene_path]
	return ""


## Why wanted names an item the scene does not have, or "": each missing name, and the names the
## scene's items have, in tree order.
static func unknown_names_refusal(scene_path: String, names: Array, wanted: Array) -> String:
	var missing: PackedStringArray = []
	for item_name: String in wanted:
		if not names.has(item_name) and not missing.has(item_name):
			missing.append(item_name)
	if missing.is_empty():
		return ""
	var listed: PackedStringArray = PackedStringArray(names)
	return (
		"%s has no MeshInstance3D named %s; its mesh items are: %s."
		% [scene_path, ", ".join(missing), ", ".join(listed)]
	)


## Why the library is not exported, or "": the scene uses output, has no item, or has none of a
## wanted name.
static func _refusal(
	scene_path: String, output: String, found: Dictionary, wanted: Array
) -> String:
	var dependencies: PackedStringArray = ResourceLoader.get_dependencies(scene_path)
	var used: String = dependency_refusal(scene_path, output, dependencies)
	if not used.is_empty():
		return used
	if found.is_empty():
		return "%s has no MeshInstance3D with a mesh." % scene_path
	return unknown_names_refusal(scene_path, found.keys(), wanted)


## Adds node to found (item name to the MeshInstance3D nodes of that name, in tree order) when it
## is a MeshInstance3D with a mesh, else searches its children; a MeshInstance3D's children are
## never searched.
static func _collect(node: Node, found: Dictionary) -> void:
	var mesh_instance := node as MeshInstance3D
	if mesh_instance == null:
		for child in node.get_children(true):
			_collect(child, found)
		return
	if mesh_instance.mesh == null:
		return
	var item_name: String = String(node.name)
	if found.has(item_name):
		push_warning(
			(
				"export_mesh_library: a second MeshInstance3D named '%s' takes the first's item over."
				% item_name
			)
		)
	else:
		found[item_name] = []
	(found[item_name] as Array).append(mesh_instance)


## Adds the item the last of nodes (MeshInstance3D nodes of one name) makes to library, with the
## next unused id: {id, name, shapes, navigation}.
static func _add_item(library: MeshLibrary, nodes: Array) -> Dictionary:
	var node: MeshInstance3D = nodes.back()
	var id: int = library.get_last_unused_item_id()
	library.create_item(id)
	library.set_item_name(id, String(node.name))
	library.set_item_mesh(id, _item_mesh(node))
	library.set_item_mesh_cast_shadow(
		id, int(node.cast_shadow) as RenderingServer.ShadowCastingSetting
	)
	library.set_item_mesh_transform(id, node.transform)
	var shapes: Array = _shapes_of(node)
	library.set_item_shapes(id, shapes)
	var navigation: Dictionary = _navigation_of(nodes)
	if not navigation.is_empty():
		library.set_item_navigation_mesh(id, navigation["mesh"])
		library.set_item_navigation_mesh_transform(id, navigation["transform"])
	var shape_count: int = floori(shapes.size() / 2.0)
	return {
		"id": id,
		"name": String(node.name),
		"shapes": shape_count,
		"navigation": not navigation.is_empty(),
	}


## node's mesh; a copy with node's surface override materials set on it when it has any, as the
## editor's item has them, and else the mesh itself, so a mesh saved in its own file stays a
## reference to that file.
static func _item_mesh(node: MeshInstance3D) -> Mesh:
	var mesh: Mesh = node.mesh
	var copy: Mesh = null
	for surface in mesh.get_surface_count():
		var material: Material = node.get_surface_override_material(surface)
		if material == null:
			continue
		if copy == null:
			copy = mesh.duplicate() as Mesh
		copy.surface_set_material(surface, material)
	return mesh if copy == null else copy


## The [shape, transform, ...] pairs of the enabled shapes of node's StaticBody3D children, each
## transform node's own, then the body's, then the shape owner's.
static func _shapes_of(node: MeshInstance3D) -> Array:
	var shapes: Array = []
	for child in node.get_children(true):
		var body := child as StaticBody3D
		if body == null:
			continue
		for owner_id: int in body.get_shape_owners():
			if not body.is_shape_owner_disabled(owner_id):
				var placed: Transform3D = (
					node.transform * body.transform * body.shape_owner_get_transform(owner_id)
				)
				shapes.append_array(_owner_shapes(body, owner_id, placed))
	return shapes


## The [shape, transform, ...] pairs of the shapes body's shape owner owner_id holds.
static func _owner_shapes(body: StaticBody3D, owner_id: int, placed: Transform3D) -> Array:
	var shapes: Array = []
	for index in body.shape_owner_get_shape_count(owner_id):
		var shape: Shape3D = body.shape_owner_get_shape(owner_id, index)
		if shape != null:
			shapes.append_array([shape, placed])
	return shapes


## The navigation mesh of the last of nodes that has one, {mesh, transform}, placed by that node's
## transform and then its region's, or {} when none has one.
static func _navigation_of(nodes: Array) -> Dictionary:
	for index in range(nodes.size() - 1, -1, -1):
		var node: MeshInstance3D = nodes[index]
		var region: NavigationRegion3D = _navigation_region_of(node)
		if region != null:
			return {"mesh": region.navigation_mesh, "transform": node.transform * region.transform}
	return {}


## node's first NavigationRegion3D child that has a navigation mesh, or null.
static func _navigation_region_of(node: MeshInstance3D) -> NavigationRegion3D:
	for child in node.get_children(true):
		var region := child as NavigationRegion3D
		if region != null and region.navigation_mesh != null:
			return region
	return null
