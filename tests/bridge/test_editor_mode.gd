extends "res://gd_test.gd"
## The bridge's editor mode (bridge/godot_mcp_editor_mode.gd): which logged entries are a --script
## editor's own noise, and which commands an editor session or a game refuses.

var _editor_mode: GDScript = load_bridge_script("godot_mcp_editor_mode.gd")


func test_the_progress_dialog_line_183_entry_is_noise() -> void:
	assert_true(
		_editor_mode.is_noise("editor/gui/progress_dialog.cpp", 183),
		"the progress dialog's current_window error is the editor's own noise"
	)


func test_any_other_entry_is_not_noise() -> void:
	assert_true(
		not _editor_mode.is_noise("editor/gui/progress_dialog.cpp", 182),
		"another line of the progress dialog is kept"
	)
	assert_true(not _editor_mode.is_noise("res://main.gd", 183), "line 183 of a script is kept")
	assert_true(
		not _editor_mode.is_noise("scene/main/node.cpp", 183),
		"line 183 of another engine file is kept"
	)
	assert_true(not _editor_mode.is_noise("", 0), "an entry without a file is kept")


func test_an_editor_refuses_preview_and_frame_control_and_runs_the_rest() -> void:
	assert_eq(
		_editor_mode.refusal("preview", true),
		"preview_scene does not run in an editor session.",
		"preview_scene"
	)
	assert_eq(
		_editor_mode.refusal("frame", true),
		"frame_control does not pause or step an editor session.",
		"frame_control"
	)
	for command: String in ["ui_elements", "input", "screenshot", "run_script", "editor_unsaved"]:
		assert_eq(_editor_mode.refusal(command, true), "", command + " runs in an editor")


func test_a_game_refuses_only_editor_unsaved() -> void:
	assert_eq(
		_editor_mode.refusal("editor_unsaved", false),
		"editor_unsaved answers only in an editor session.",
		"editor_unsaved in a game"
	)
	for command: String in ["preview", "frame", "ui_elements"]:
		assert_eq(_editor_mode.refusal(command, false), "", command + " runs in a game")
