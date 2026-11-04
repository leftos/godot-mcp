extends RefCounted
## A game's end as a user's window close gives it: the close request first, then the bridge's own
## quit for a game that does not quit by itself. The bridge makes one in _init; the unit tests
## replace its do_quit and now_ms.
##
## A user's close reaches the root Window's WINDOW_EVENT_CLOSE_REQUEST (4.7.2
## scene/main/window.cpp L906-912), which sends NOTIFICATION_WM_CLOSE_REQUEST to the root and every
## descendant that is not a Window (_propagate_window_notification, L827-837) and then emits the
## root's close_requested, on which SceneTree quits when auto_accept_quit is on
## (scene/main/scene_tree.cpp L893-897, L2218). A headless game never gets one from its display
## server, but the notification and the signal are plain object calls there too.

## The process frames a game that ignores the close request gets before the bridge quits it.
const FRAMES := 10
## The real ms since the request after which the bridge quits it with fewer frames passed.
const LIMIT_MS := 1000

## Called with the tree to quit it; SceneTree.quit unless a test counts the calls.
var do_quit: Callable = func(tree: SceneTree) -> void: tree.quit()
## Real ms, never the game's scaled clock; Time.get_ticks_msec unless a test moves it.
var now_ms: Callable = Time.get_ticks_msec

## Set by the first request: a second shutdown or a lost connection while it waits adds nothing.
var _closing: bool = false


## Sends the close request, then quits the tree after FRAMES process frames or LIMIT_MS of real
## time, whichever comes first. A game that accepted the close (auto_accept_quit, or its handler's
## own quit) exits at the end of this iteration (scene_tree.cpp L790, main/main.cpp L5062-5064), so
## this never resumes. process_frame is emitted on a paused tree too (scene_tree.cpp L713), so a
## paused game is still quit; WM_CLOSE_REQUEST is not one of the notifications pause withholds
## (scene/main/node.cpp L892-909).
func request_close_then_quit(tree: SceneTree) -> void:
	if _closing:
		return
	_closing = true
	var started: int = now_ms.call()
	send_close_request(tree.root)
	var frames: int = 0
	while frames < FRAMES and now_ms.call() - started < LIMIT_MS:
		await tree.process_frame
		frames += 1
	do_quit.call(tree)


## The close request as the root Window sends it: the notification first, root and descendants in
## tree order, internal children included and child Windows skipped, then close_requested.
static func send_close_request(root: Window) -> void:
	_notify_close(root)
	root.close_requested.emit()


static func _notify_close(node: Node) -> void:
	node.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)
	for child: Node in node.get_children(true):
		if not child is Window:
			_notify_close(child)
