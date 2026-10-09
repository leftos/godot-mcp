extends RefCounted
## The godot-mcp bridge's main window: run_project's size, a quiet session's parking and the
## restores that give a parked window back. Static functions called on the script itself.

## A quiet session's frame-rate cap when the project sets none: its frames are never seen, so
## drawing at the monitor's refresh rate only burns the GPU.
const QUIET_MAX_FPS := 60
## Where a quiet session's override.cfg asks for the main window (the server's
## OverrideFile.OffScreenPosition), as an absolute initial position (type 0).
const PARK_POSITION := Vector2i(-9999, -9999)
## The setting a quiet session's override.cfg sets to create the window unfocusable.
const NO_FOCUS_SETTING := "display/window/size/no_focus"

## Whether park_window moved the window and restore_window has not given it back since.
static var parked: bool = false


## Gives the window run_project's --resolution (GODOT_MCP_WINDOW_SIZE, "WIDTHxHEIGHT") exactly.
## Windows holds a window created larger than the desktop to the desktop's size
## (platform/windows/display_server_windows.cpp _create_window L7175-7257 in 4.7.2), and a resize
## after start is not held (window_set_size's MoveWindow, L2492-2520). The root Window's size is
## set rather than the display server's, so the root's viewport follows in the same call.
static func apply_window_size(root: Window) -> void:
	var wanted: String = OS.get_environment("GODOT_MCP_WINDOW_SIZE")
	if wanted.get_slice_count("x") != 2:
		return
	var size := Vector2i(wanted.get_slice("x", 0).to_int(), wanted.get_slice("x", 1).to_int())
	if size.x < 1 or size.y < 1 or DisplayServer.window_get_size() == size:
		return
	root.size = size


## A quiet session's window: its override.cfg created it unfocused, and asked for an off-screen
## position that Windows clamps onto the primary screen at creation
## (platform/windows/display_server_windows.cpp L7180-7183, L7206-7211 in 4.7.2), so it is moved
## off-screen here, where window_set_position does not clamp, and made click-through. A run the
## server started on its hidden desktop (GODOT_MCP_HIDDEN_DESKTOP) is out of sight already, so its
## window goes to (0, 0) instead: there, a window that is not embedded (a popup of a project that
## turns embed_subwindows off) opens where the game asked, at its offset from the root window.
static func park_window() -> void:
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_MOUSE_PASSTHROUGH, true)
	var on_hidden_desktop: bool = OS.get_environment("GODOT_MCP_HIDDEN_DESKTOP") == "1"
	DisplayServer.window_set_position(Vector2i.ZERO if on_hidden_desktop else PARK_POSITION)
	parked = true


## A game started without a server from a quiet session's override.cfg (one a killed server
## left): the file created its window unfocusable and asked for it at PARK_POSITION, which Windows
## clamps onto the primary screen (see park_window). The window is made focusable again, centred
## on its screen's usable area and brought to the front. Returns whether it restored the window;
## a headless run has none.
static func restore_parked_window() -> bool:
	if DisplayServer.get_name() == "headless" or not parked_by_override():
		return false
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, false)
	centre_window()
	DisplayServer.window_move_to_foreground()
	return true


## Whether the project settings place the main window as a quiet session's override.cfg does:
## at PARK_POSITION, absolute. The window's own position cannot tell, since Windows clamped it.
static func parked_by_override() -> bool:
	return (
		ProjectSettings.get_setting("display/window/size/initial_position_type", -1) == 0
		and (
			ProjectSettings.get_setting("display/window/size/initial_position", Vector2i.ZERO)
			== PARK_POSITION
		)
	)


## Gives back a window park_window moved (nothing when it is not parked): click-through off,
## focusable again when the override created it unfocusable, and centred on its screen. A
## headless run has no window.
static func restore_window() -> void:
	if not parked:
		return
	parked = false
	if DisplayServer.get_name() == "headless":
		return
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_MOUSE_PASSTHROUGH, false)
	if ProjectSettings.get_setting(NO_FOCUS_SETTING, false):
		DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, false)
	centre_window()


## The main window's native handle, an HWND on Windows, which the hello reports as hwnd for the
## server's real-time recording; 0 under the headless display server, which has no window
## (servers/display/display_server_headless.h L146 in 4.7.2).
static func native_handle() -> int:
	return DisplayServer.window_get_native_handle(DisplayServer.WINDOW_HANDLE)


## Centres the main window on its screen's usable area, the primary screen's when it is on none.
static func centre_window() -> void:
	var screen: int = DisplayServer.window_get_current_screen()
	if screen == DisplayServer.INVALID_SCREEN:
		screen = DisplayServer.get_primary_screen()
	var usable: Rect2i = DisplayServer.screen_get_usable_rect(screen)
	var window_size: Vector2i = DisplayServer.window_get_size()
	DisplayServer.window_set_position(usable.position + (usable.size - window_size) / 2)
