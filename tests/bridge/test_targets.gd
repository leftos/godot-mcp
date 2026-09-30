extends "res://gd_test.gd"
## The target resolver's pure helpers (bridge/godot_mcp_targets.gd), on nodes never added to the
## tree. The runner's root enters the tree only after _initialize has run every test
## (scene/main/scene_tree.cpp L589-590 in 4.7.2), and a Camera2D's canvas transform, a
## CanvasLayer's, Camera3D.unproject_position (scene/3d/camera_3d.cpp L482) and
## Node3D.global_transform (scene/3d/node_3d.cpp L649) all need it, so the projections themselves
## are covered by WorldTargetTests.

var _targets_script: GDScript = load_bridge_script("godot_mcp_targets.gd")


func test_container_transform_scales_by_the_shrink_only_with_stretch() -> void:
	var targets: Node = _targets_script.new()
	var at := Transform2D(0.0, Vector2(100, 40))
	var point := Vector2(40, 30)
	var stretched: Transform2D = targets.container_transform(Transform2D.IDENTITY, true, 2, at)
	var unstretched: Transform2D = targets.container_transform(Transform2D.IDENTITY, false, 2, at)
	var unshrunk: Transform2D = targets.container_transform(Transform2D.IDENTITY, true, 1, at)
	assert_eq(stretched * point, Vector2(180, 100), "stretch on, shrink 2: doubled, then placed")
	assert_eq(unstretched * point, Vector2(140, 70), "stretch off: the shrink does nothing")
	assert_eq(unshrunk * point, Vector2(140, 70), "shrink 1: nothing to undo")
	targets.free()


func test_container_transform_applies_the_subviewport_final_transform_first() -> void:
	var targets: Node = _targets_script.new()
	var final := Transform2D(0.0, Vector2(5, 0))
	var at := Transform2D(0.0, Vector2(100, 40))
	var carried: Transform2D = targets.container_transform(final, true, 2, at)
	assert_eq(carried * Vector2(40, 30), Vector2(190, 100), "(40+5, 30) doubled, then placed")
	targets.free()


func test_stopping_control_takes_the_first_stop_on_the_way_up() -> void:
	var targets: Node = _targets_script.new()
	var outer := _control("Outer", Control.MOUSE_FILTER_STOP)
	var middle := _control("Middle", Control.MOUSE_FILTER_IGNORE)
	var inner := _control("Inner", Control.MOUSE_FILTER_PASS)
	outer.add_child(middle)
	middle.add_child(inner)
	assert_eq(targets.stopping_control(inner, false), outer, "Pass goes on, Ignore is skipped")
	assert_eq(targets.stopping_control(outer, false), outer, "a Stop hit takes the event itself")
	middle.mouse_filter = Control.MOUSE_FILTER_STOP
	assert_eq(targets.stopping_control(inner, false), middle, "the first Stop wins")
	assert_true(targets.stopping_control(null, false) == null, "nothing hovered")
	outer.free()
	targets.free()


func test_stopping_control_walks_through_a_node2d_and_stops_at_top_level() -> void:
	var targets: Node = _targets_script.new()
	var outer := _control("Outer", Control.MOUSE_FILTER_STOP)
	var between := Node2D.new()
	var inner := _control("Inner", Control.MOUSE_FILTER_PASS)
	outer.add_child(between)
	between.add_child(inner)
	assert_eq(targets.stopping_control(inner, false), outer, "a Node2D between passes the walk on")
	inner.top_level = true
	assert_true(
		targets.stopping_control(inner, false) == null, "a top-level Pass Control ends the walk"
	)
	inner.mouse_filter = Control.MOUSE_FILTER_STOP
	assert_true(
		targets.stopping_control(inner, false) == null,
		"a top-level Stop one ends it before its filter"
	)
	outer.free()
	targets.free()


func test_stopping_control_is_null_when_only_pass_controls_are_hit() -> void:
	var targets: Node = _targets_script.new()
	var outer := _control("Outer", Control.MOUSE_FILTER_PASS)
	var inner := _control("Inner", Control.MOUSE_FILTER_PASS)
	outer.add_child(inner)
	assert_true(targets.stopping_control(inner, false) == null, "Pass all the way up")
	outer.free()
	targets.free()


func test_stopping_control_lets_a_scroll_through_a_control_that_passes_scrolls() -> void:
	var targets: Node = _targets_script.new()
	var outer := _control("Outer", Control.MOUSE_FILTER_STOP)
	var inner := _control("Inner", Control.MOUSE_FILTER_STOP)
	outer.add_child(inner)
	assert_true(targets.stopping_control(inner, true) == null, "both pass scrolls by default")
	assert_eq(targets.stopping_control(inner, false), inner, "a click stops at the first")
	outer.set_force_pass_scroll_events(false)
	assert_eq(targets.stopping_control(inner, true), outer, "a scroll stops where it may not pass")
	outer.free()
	targets.free()


func test_grab_rect_adds_the_title_bar_and_margin_unless_borderless() -> void:
	var targets: Node = _targets_script.new()
	var rect := Rect2(100, 50, 200, 100)
	assert_eq(targets.grab_rect(rect, true, 30, 4), rect, "a borderless window: its own rect")
	assert_eq(
		targets.grab_rect(rect, false, 30, 4),
		Rect2(96, 16, 208, 138),
		"the title bar above, the margin around"
	)
	targets.free()


func test_kind_of_names_controls_2d_and_3d_nodes() -> void:
	var targets: Node = _targets_script.new()
	var nodes: Array[Node] = [Button.new(), Sprite2D.new(), Area3D.new(), Node.new()]
	var kinds: Array = []
	for node: Node in nodes:
		kinds.append(targets.kind_of(node))
		node.free()
	assert_eq(kinds, ["control", "node2d", "node3d", ""], "Button, Sprite2D, Area3D, Node")
	targets.free()


func test_offset_refusal_checks_the_offset_against_the_kind() -> void:
	var targets: Node = _targets_script.new()
	var flat := {"x": 1.0, "y": 2.0}
	var deep := {"x": 1.0, "y": 2.0, "z": 3.0}
	assert_eq(targets.offset_refusal(null, "control", "/root/B", "Button"), "", "no offset")
	assert_eq(
		targets.offset_refusal(flat, "control", "/root/B", "Button"),
		"offset aims inside a world node; /root/B is a Control.",
		"an offset on a Control"
	)
	assert_eq(
		targets.offset_refusal(deep, "node2d", "/root/A", "Area2D"),
		"/root/A is a Area2D; offset takes x and y.",
		"a z on a 2D node"
	)
	assert_eq(targets.offset_refusal(flat, "node2d", "/root/A", "Area2D"), "", "x and y on 2D")
	assert_eq(targets.offset_refusal(deep, "node3d", "/root/D", "Area3D"), "", "x, y, z on 3D")
	targets.free()


func test_offset_refusal_refuses_a_malformed_offset() -> void:
	var targets: Node = _targets_script.new()
	var malformed: Array = [{"x": 1.0}, {"x": 1.0, "y": "2"}, {"x": 1, "y": 2, "z": null}, [1, 2]]
	for offset: Variant in malformed:
		var refusal: String = targets.offset_refusal(offset, "node3d", "/root/D", "Area3D")
		assert_true(
			refusal.begins_with("offset must be an object {x, y, z?} of numbers; got "),
			"%s refused: %s" % [JSON.stringify(offset), refusal]
		)
	targets.free()


func test_offset_vector_defaults_z_and_the_whole_offset_to_zero() -> void:
	var targets: Node = _targets_script.new()
	assert_eq(targets.offset_vector(null), Vector3.ZERO, "no offset aims at the origin")
	assert_eq(targets.offset_vector({"x": 1, "y": -2.5}), Vector3(1, -2.5, 0), "z left out")
	assert_eq(targets.offset_vector({"x": 1, "y": 2, "z": 3}), Vector3(1, 2, 3), "z given")
	targets.free()


func test_text_helpers_round_to_one_decimal() -> void:
	var targets: Node = _targets_script.new()
	assert_eq(targets.point_text(Vector2(812, -40)), "812, -40", "whole coordinates")
	assert_eq(targets.point_text(Vector2(366.916, 180.04)), "366.9, 180", "one decimal at most")
	assert_eq(targets.size_text(Vector2(640, 360)), "640x360", "a viewport size")
	assert_eq(targets.rect_text(Rect2(0, 0, 640, 360)), "0,0,640,360", "a rect")
	targets.free()


func _control(node_name: String, filter: Control.MouseFilter) -> Control:
	var control := Control.new()
	control.name = node_name
	control.mouse_filter = filter
	return control
