@tool
extends Node
## The editor actions of an editor session (run_editor), a child of the bridge there only: the
## editor command's open, save, save_all, close, play, stop and select_file through
## EditorInterface, the editor_state read and editor_unsaved.
##
## Godot's calls report no failure (a failed one shows a dialog instead), so each action checks its
## input before the call and its effect after. A call that can show a progress dialog runs a whole
## engine frame inside itself (4.7.2 editor/gui/progress_dialog.cpp L148-152), so every such action
## goes through guarded, whose busy flag holds every other command until the effect shows.

## The guard's hang guard: an effect not shown this long after its call is reported as not shown.
const DEADLINE_MS := 30000
const ACTIONS := "open, save, save_all, close, play, stop, select_file"
const NOT_OPEN := "%s is not open in the editor; open it first with editor {action: open}."
const UNSAVED := "The scene %s has unsaved changes; save it first or pass discard: true."
const NO_SCENE := "the editor has no edited scene."
const UNTITLED := "the edited scene has no file path; save it with Save As in the editor first."
const LATE := (
	"editor %s did not take effect within 30 s: expected %s; a dialog may be open, check "
	+ "take_screenshot"
)
## Added to an open's LATE: open_scene_from_path does nothing while the editor changes scenes
## (4.7.2 editor/editor_interface.cpp L699-703), which no script can read.
const CHANGING := "; the editor may have been changing scenes"

## Set while a guarded call runs and until its effect shows: the bridge then dispatches no command
## and its _process returns at once.
var busy: bool = false
## The bridge (godot_mcp_bridge.gd), whose replies answer the requests.
var bridge: Node
## EditorInterface, or a test's stand-in; null outside the editor.
var editor_interface: Object = (
	Engine.get_singleton("EditorInterface") if Engine.is_editor_hint() else null
)
## The wall clock in ms: Time.get_ticks_msec, or a test's stand-in.
var now_ms: Callable = Callable(Time, "get_ticks_msec")


## The commands this module answers, func(id, params), by name.
func handlers() -> Dictionary:
	return {"editor": handle_editor, "editor_state": handle_state, "editor_unsaved": handle_unsaved}


## Makes call with busy set, then waits frames until effect_check holds or deadline_ms of wall
## time has passed since the call began; busy is cleared on every path. Returns whether the effect
## showed.
func guarded(call: Callable, effect_check: Callable, deadline_ms: int) -> bool:
	busy = true
	var started: int = now_ms.call()
	call.call()
	var shown: bool = effect_check.call() == true
	while not shown and now_ms.call() - started < deadline_ms:
		await (Engine.get_main_loop() as SceneTree).process_frame
		shown = effect_check.call() == true
	busy = false
	return shown


## Runs params.action and replies the editor's state with action and the action's own fields, or
## why the action was refused or did not take effect.
func handle_editor(id: int, params: Dictionary) -> void:
	var action: String = str(params.get("action", ""))
	var actions: Dictionary = {
		"open": _open,
		"save": _save,
		"save_all": _save_all,
		"close": _close,
		"play": _play,
		"stop": _stop,
		"select_file": _select_file,
	}
	if not actions.has(action):
		bridge._reply_error(id, "unknown editor action '%s'; it is one of %s." % [action, ACTIONS])
		return
	# A coroutine's call returns its state, which this await resolves; a plain one's, its value.
	var outcome: Dictionary = await (actions[action] as Callable).call(params)
	if outcome.has("error"):
		bridge._reply_error(id, str(outcome["error"]))
		return
	var result: Dictionary = state()
	result["action"] = action
	result.merge(outcome["result"])
	bridge._reply_ok(id, result)


## Replies the editor's state (state).
func handle_state(id: int, _params: Dictionary) -> void:
	bridge._reply_ok(id, state())


## Replies the paths of the editor's unsaved scenes, {scenes}: stop_project refuses to lose them.
func handle_unsaved(id: int, _params: Dictionary) -> void:
	bridge._reply_ok(id, {"scenes": Array(editor_interface.get_unsaved_scenes())})


## {openScenes, currentScene, unsavedScenes, playingScene, scanning, importing}: currentScene is
## null with no edited scene, playingScene empty when no scene plays.
func state() -> Dictionary:
	var file_system: Object = editor_interface.get_resource_filesystem()
	var playing: bool = editor_interface.is_playing_scene()
	return {
		"openScenes": Array(editor_interface.get_open_scenes()),
		"currentScene": _current(),
		"unsavedScenes": Array(editor_interface.get_unsaved_scenes()),
		"playingScene": editor_interface.get_playing_scene() if playing else "",
		"scanning": file_system.is_scanning(),
		"importing": file_system.is_importing(),
	}


## Whether scene has been saved since since (Unix seconds): out of the unsaved list, its file
## written at or after then.
func _saved(scene: String, since: int) -> bool:
	if scene in editor_interface.get_unsaved_scenes():
		return false
	return FileAccess.get_modified_time(scene) >= since


func _open(params: Dictionary) -> Dictionary:
	var scene: String = str(params.get("scene", ""))
	var extension: String = scene.get_extension()
	if not scene.begins_with("res://") or not extension in ["tscn", "scn"]:
		return {"error": "scene must be a res:// .tscn or .scn path; got '%s'." % scene}
	if not ResourceLoader.exists(scene):
		return {"error": "%s does not exist." % scene}
	var call: Callable = Callable(editor_interface, "open_scene_from_path").bind(scene)
	if not await guarded(call, func() -> bool: return _current() == scene, DEADLINE_MS):
		return {"error": LATE % ["open", "%s as the edited scene" % scene] + CHANGING}
	return {"result": {"scene": scene}}


## Saves params.scene (the edited one when empty), switching to it for the save and back after.
func _save(params: Dictionary) -> Dictionary:
	var scene: String = str(params.get("scene", ""))
	var current: Variant = _current()
	var refused: String = _target_refusal(scene, current)
	if not refused.is_empty():
		return {"error": refused}
	var target: String = scene if not scene.is_empty() else str(current)
	var since: int = int(Time.get_unix_time_from_system())
	var call: Callable = func() -> void:
		_switch_to(target)
		editor_interface.save_scene()
		_switch_to(current if current is String else "")
	if not await guarded(call, _saved.bind(target, since), DEADLINE_MS):
		return {"error": LATE % ["save", "%s saved" % target]}
	return {"result": {"scene": target, "saved": true}}


func _save_all(_params: Dictionary) -> Dictionary:
	var before: Array = Array(editor_interface.get_unsaved_scenes())
	var call: Callable = Callable(editor_interface, "save_all_scenes")
	var all_saved: Callable = func() -> bool:
		return editor_interface.get_unsaved_scenes().is_empty()
	if not await guarded(call, all_saved, DEADLINE_MS):
		return {"error": LATE % ["save_all", "no unsaved scene"]}
	return {"result": {"saved": before}}


## Closes params.scene (the edited one when empty), refusing an unsaved one unless params.discard:
## close_scene discards its changes without asking (4.7.2 editor/editor_node.cpp,
## EditorNode::close_scene).
func _close(params: Dictionary) -> Dictionary:
	var scene: String = str(params.get("scene", ""))
	var current: Variant = _current()
	var refused: String = _target_refusal(scene, current)
	if not refused.is_empty():
		return {"error": refused}
	var target: String = scene if not scene.is_empty() else str(current)
	if not bool(params.get("discard", false)) and target in editor_interface.get_unsaved_scenes():
		return {"error": UNSAVED % target}
	var call: Callable = func() -> void:
		_switch_to(target)
		editor_interface.close_scene()
	var closed: Callable = func() -> bool: return not target in editor_interface.get_open_scenes()
	if not await guarded(call, closed, DEADLINE_MS):
		return {"error": LATE % ["close", "%s closed" % target]}
	return {"result": {"scene": target}}


## Plays the edited scene (params.scene empty or "current"), the main scene ("main") or a res://
## scene; done once the editor says a scene plays, without waiting for the game's process.
func _play(params: Dictionary) -> Dictionary:
	var scene: String = str(params.get("scene", ""))
	if scene.is_empty():
		scene = "current"
	var refused: String = _play_refusal(scene)
	if not refused.is_empty():
		return {"error": refused}
	var call: Callable = Callable(editor_interface, "play_custom_scene").bind(scene)
	if scene == "current":
		call = Callable(editor_interface, "play_current_scene")
	elif scene == "main":
		call = Callable(editor_interface, "play_main_scene")
	var playing: Callable = Callable(editor_interface, "is_playing_scene")
	if not await guarded(call, playing, DEADLINE_MS):
		return {"error": LATE % ["play", "a scene playing"]}
	return {"result": {"scene": scene}}


## Why scene ("current", "main" or a res:// path) cannot be played, or empty when it can.
func _play_refusal(scene: String) -> String:
	if scene == "current":
		var current: Variant = _current()
		if current == null:
			return NO_SCENE
		return UNTITLED if str(current).is_empty() else ""
	if scene == "main":
		var main: String = str(ProjectSettings.get_setting("application/run/main_scene", ""))
		if main.is_empty():
			return "the project has no main scene (application/run/main_scene)."
		return ""
	if not scene.begins_with("res://"):
		return "scene must be current, main or a res:// scene path; got '%s'." % scene
	return "" if ResourceLoader.exists(scene) else "%s does not exist." % scene


func _stop(_params: Dictionary) -> Dictionary:
	var was_playing: bool = editor_interface.is_playing_scene()
	editor_interface.stop_playing_scene()
	return {"result": {"wasPlaying": was_playing}}


func _select_file(params: Dictionary) -> Dictionary:
	var path: String = str(params.get("path", ""))
	if not FileAccess.file_exists(path) and not DirAccess.dir_exists_absolute(path):
		return {"error": "%s does not exist." % path}
	editor_interface.select_file(path)
	return {"result": {"selected": editor_interface.get_current_path()}}


## Why scene (the edited one when empty, given current, the edited scene's path or null) cannot be
## saved or closed, or empty when it can.
func _target_refusal(scene: String, current: Variant) -> String:
	if scene.is_empty():
		if current == null:
			return NO_SCENE
		return UNTITLED if str(current).is_empty() else ""
	if not scene in editor_interface.get_open_scenes():
		return NOT_OPEN % scene
	return ""


## Makes scene the edited one when it is not: opening an open scene switches to its tab at once
## (4.7.2 editor/editor_node.cpp, EditorNode::open_scene and _set_current_scene).
func _switch_to(scene: String) -> void:
	if not scene.is_empty() and _current() != scene:
		editor_interface.open_scene_from_path(scene)


## The edited scene's path, empty for an untitled one, or null with no edited scene.
func _current() -> Variant:
	var root: Node = editor_interface.get_edited_scene_root()
	return root.scene_file_path if root != null else null
