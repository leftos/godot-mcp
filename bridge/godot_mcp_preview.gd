extends Node
## preview_scene's side of the bridge. The server starts the game on one scene with
## GODOT_MCP_PREVIEW set, and the bridge pauses the tree in its _ready, before the scene enters
## (autoloads join the root before the main scene: 4.7.2 main/main.cpp L4760-4767). The
## preview command then frames a 3D scene that has no current camera, waits two drawn frames
## and saves the screenshot as the screenshot command does.

## The camera's direction from the centre of what it frames: above, to the right and in front.
const VIEW_DIRECTION := Vector3(1.0, 0.8, 1.0)
## The smallest radius framed, so a scene of flat or point-sized geometry still gets a distance.
const MIN_RADIUS := 0.5
const DRAWN_FRAMES := 2

## The bridge (godot_mcp_bridge.gd), set by it before this node enters the tree.
var bridge: Node
## The camera this module added, once it has; null when the scene needed none.
var _camera: Camera3D


## A camera transform that frames bounds: it looks at the centre from VIEW_DIRECTION, far enough
## that the box's bounding sphere fits the vertical field of view fov_degrees.
static func frame_aabb(bounds: AABB, fov_degrees: float) -> Transform3D:
	var centre: Vector3 = bounds.get_center()
	var radius: float = maxf(bounds.size.length() * 0.5, MIN_RADIUS)
	var distance: float = radius / sin(deg_to_rad(fov_degrees) * 0.5)
	var eye: Vector3 = centre + VIEW_DIRECTION.normalized() * distance
	return Transform3D(Basis.IDENTITY, eye).looking_at(centre, Vector3.UP)


## The merged world-space box of every visible GeometryInstance3D under scene, itself included;
## null when there is none. Only geometry counts: a light's, probe's or decal's box (every
## VisualInstance3D has one) spans its reach, not anything drawn.
static func visible_bounds(scene: Node) -> Variant:
	var found: Array[Node] = scene.find_children("*", "GeometryInstance3D", true, false)
	found.push_front(scene)
	var bounds := AABB()
	var any := false
	for node: Node in found:
		var instance := node as GeometryInstance3D
		if instance == null or not instance.is_visible_in_tree():
			continue
		var box: AABB = instance.global_transform * instance.get_aabb()
		bounds = bounds.merge(box) if any else box
		any = true
	return bounds if any else null


## Frames the current scene when it needs a camera, waits DRAWN_FRAMES drawn frames and saves
## the root viewport as the screenshot command does. Returns the screenshot's {path, width,
## height, previewPath?, …} plus {scene, cameraAdded}, or a String saying why it could not.
func capture(params: Dictionary) -> Variant:
	var scene: Node = get_tree().current_scene
	if scene == null:
		return "the game has no current scene to show"
	_add_camera_if_needed(scene)
	for _frame: int in DRAWN_FRAMES:
		await bridge._frame.wait_for_drawn_frame()
	var saved: Variant = bridge._frame.save_screenshot(bridge._frame.grab_frame(), params)
	if saved is String:
		return saved
	saved["scene"] = scene.scene_file_path
	saved["cameraAdded"] = _camera != null
	return saved


## Adds a current camera framing the scene's visible geometry when the viewport has a 3D world,
## no current camera and something to frame. Once added, it stays for the run.
func _add_camera_if_needed(scene: Node) -> void:
	var viewport: Viewport = get_viewport()
	if _camera != null or viewport.find_world_3d() == null or viewport.get_camera_3d() != null:
		return
	var found: Variant = visible_bounds(scene)
	if found == null:
		return
	var bounds: AABB = found
	_camera = Camera3D.new()
	_camera.name = "PreviewCamera"
	_camera.transform = frame_aabb(bounds, _camera.fov)
	var reach: float = _camera.transform.origin.distance_to(bounds.get_center())
	_camera.far = maxf(_camera.far, reach + bounds.size.length())
	add_child(_camera)
	_camera.make_current()
