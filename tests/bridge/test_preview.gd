extends "res://gd_test.gd"
## preview_scene's camera framing (bridge/godot_mcp_preview.gd): the transform frame_aabb gives
## a box looks at its centre from the preview's direction and sees every corner within the
## vertical field of view. The node is never created; frame_aabb is static.

const FOV := 75.0

var _preview_script: GDScript = load_bridge_script("godot_mcp_preview.gd")


func test_the_camera_looks_at_the_boxs_centre() -> void:
	var box := AABB(Vector3(2.5, 0.5, -2.5), Vector3(1, 1, 1))
	var frame: Transform3D = _preview_script.frame_aabb(box, FOV)
	var forward: Vector3 = -frame.basis.z
	var to_centre: Vector3 = (box.get_center() - frame.origin).normalized()
	assert_approx(forward.dot(to_centre), 1.0, "forward points at the centre")


func test_the_camera_sits_on_the_preview_direction_from_the_centre() -> void:
	var box := AABB(Vector3(-1, -1, -1), Vector3(2, 2, 2))
	var frame: Transform3D = _preview_script.frame_aabb(box, FOV)
	var offset: Vector3 = (frame.origin - box.get_center()).normalized()
	assert_approx(offset.dot(Vector3(1.0, 0.8, 1.0).normalized()), 1.0, "above, right and in front")


func test_every_corner_is_inside_the_vertical_field_of_view() -> void:
	for box: AABB in [
		AABB(Vector3(2.5, 0.5, -2.5), Vector3(1, 1, 1)),
		AABB(Vector3(-50, 0, -3), Vector3(100, 2, 6)),
		AABB(Vector3(0, 0, 0), Vector3(0.1, 20, 0.1)),
	]:
		var frame: Transform3D = _preview_script.frame_aabb(box, FOV)
		var forward: Vector3 = -frame.basis.z
		for corner: int in 8:
			var angle: float = rad_to_deg(forward.angle_to(box.get_endpoint(corner) - frame.origin))
			assert_true(
				angle <= FOV * 0.5 + 0.001, "%s corner %d at %.2f degrees" % [box, corner, angle]
			)


func test_the_bounding_sphere_just_fits_the_field_of_view() -> void:
	var box := AABB(Vector3(-1, -1, -1), Vector3(2, 2, 2))
	var frame: Transform3D = _preview_script.frame_aabb(box, FOV)
	var radius: float = box.size.length() * 0.5
	var distance: float = frame.origin.distance_to(box.get_center())
	assert_approx(
		rad_to_deg(asin(radius / distance)), FOV * 0.5, "the sphere touches the view's edge"
	)


func test_a_point_sized_box_still_gets_a_distance() -> void:
	var box := AABB(Vector3(4, 5, 6), Vector3.ZERO)
	var frame: Transform3D = _preview_script.frame_aabb(box, FOV)
	var distance: float = frame.origin.distance_to(box.get_center())
	assert_approx(distance, 0.5 / sin(deg_to_rad(FOV * 0.5)), "framed as a sphere of radius 0.5")
