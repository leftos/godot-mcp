@tool
extends RefCounted
## The godot-mcp bridge in an editor session (run_editor), which the editor host adds inside the
## Godot editor. Static functions called on the script itself: which logged entries are the
## editor's own noise, which commands an editor refuses, its unsaved scenes, and its close.

const PREVIEW_REFUSAL := "preview_scene does not run in an editor session."
const FRAME_REFUSAL := "frame_control does not pause or step an editor session."
const UNSAVED_REFUSAL := "editor_unsaved answers only in an editor session."
## The progress dialog's line that logs the noise (is_noise).
const PROGRESS_DIALOG_LINE := 183


## Whether a logged entry is the error a --script editor logs each time it shows a progress
## dialog (4.7.2 editor/gui/progress_dialog.cpp L183, ERR_FAIL_NULL(current_window)): the editor
## makes a SceneTree before it reads --script and never frees it (main/main.cpp L4361-4363).
static func is_noise(file: String, line: int) -> bool:
	return line == PROGRESS_DIALOG_LINE and file.ends_with("progress_dialog.cpp")


## Why command cannot run, or empty when it can: an editor refuses preview_scene and every
## frame_control action, each of which pauses, steps or scales the editor's own time; a game
## refuses editor_unsaved.
static func refusal(command: String, editor: bool) -> String:
	if not editor:
		return UNSAVED_REFUSAL if command == "editor_unsaved" else ""
	if command == "preview":
		return PREVIEW_REFUSAL
	if command == "frame":
		return FRAME_REFUSAL
	return ""


## The paths of the scenes the editor holds unsaved changes in.
static func unsaved_scenes() -> Array:
	var scenes: PackedStringArray = _editor_interface().get_unsaved_scenes()
	return Array(scenes)


## Closes the editor as its close button does: EditorNode's close request runs the editor's exit
## path, saving its layout and unloading its plugins, before it quits the tree (4.7.2
## editor/editor_node.cpp L1080-1082, L4192-4205), which SceneTree.quit() skips. With unsaved
## scenes the editor shows its save dialog instead, which stop_project refuses to reach.
static func request_close() -> void:
	var editor_node: Node = _editor_interface().get_base_control().get_parent()
	editor_node.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)


## The EditorInterface singleton, read by name so this script also compiles in a game.
static func _editor_interface() -> Object:
	return Engine.get_singleton("EditorInterface")
