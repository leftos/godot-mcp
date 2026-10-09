extends Node
## Item targets in a PopupMenu, the input player's (godot_mcp_input.gd) child: an item of an
## OptionButton's or a MenuButton's popup, or of a PopupMenu named itself, by {text | index}. The
## target resolver (godot_mcp_targets.gd) resolves one with the static functions, sending
## nothing. PopupMenu binds no item rect or hit test, only get_focused_item
## (scene/gui/popup_menu.cpp L3480 in 4.7.2), which follows the mouse (_mouse_over_update,
## L845-880) and reads -1 over a separator, a disabled item or no item; so a gesture finds the
## item's point here with real motions (place), opening a closed button's popup with a real click
## first, and holds the pointer on an item with a submenu until the submenu shows (hold_submenu).

const ItemTargets := preload("godot_mcp_item_targets.gd")

## The internal Control a PopupMenu draws its items in (scene/gui/popup_menu.cpp L3812 in 4.7.2).
const ITEMS_CLASS := "PopupMenuItems"
## A Tree's key on a popup item: the node's path, its class and the key.
const POPUP_ITEM_KEY := "%s is a %s; a popup item takes text or index, not item.%s"
## The refusals of a popup whatever the item, each naming it as popup_name does.
const NATIVE_POPUP := (
	"%s is a native menu, an OS window of its own, and input events cannot reach a native menu; "
	+ "a popup is embedded only while gui_embed_subwindows is on "
	+ "(display/window/subwindows/embed_subwindows) and its force_native is off"
)
const NOT_OPEN := "%s is not open; open it first"
## The name of the popup whose search filters the items (the popup itself or a PopupMenu above
## it), then its search bar's text.
const SEARCH_FILTERED := (
	"%s filters its items by the search '%s'; hidden items cannot be told apart, "
	+ "clear the search first"
)
## Defensive, unreachable in 4.7.2: every PopupMenu builds its PopupMenuItems Control in its
## constructor (scene/gui/popup_menu.cpp L3812-3818 in 4.7.2).
const NO_ITEMS_CONTROL := "%s has no PopupMenuItems Control to aim into"
## An item that takes no press: the item as label_of gives it, then the popup's name.
const SEPARATOR_ITEM := "%s in %s is a separator; Godot ignores a press on it"
const DISABLED_ITEM := "%s in %s is disabled; Godot ignores a press on it"
## Several items reading the text: the popup's name, the count, the text, the indices listed.
const AMBIGUOUS_POPUP_ITEM := "%s has %d items reading '%s': %s; narrow with item.index"
## A closed OptionButton's or MenuButton's popup: the button's path, then for OTHER_POPUP_OPEN
## the open popup's path, and for DID_NOT_OPEN the wait.
const CLOSED_POPUP := "the popup of %s is closed; click the button first"
const OTHER_POPUP_OPEN := (
	"the popup of %s is closed and %s is open, so the press that would open it only closes "
	+ "that one; close it first"
)
const DID_NOT_OPEN := "clicking %s did not open its popup within %d ms"
## A disabled OptionButton or MenuButton, whose press opens nothing: the button's path.
const BUTTON_DISABLED := "%s is disabled; it cannot open its popup"
## Appended to a refusal that comes after the gesture opened the popup, which it leaves open.
const OPENED_NOTE := "; the popup is open now"
const DRAG_REFUSED := "an item in a popup cannot start or end a drag; click it instead"
## The probe's failures: the item as label_of gives it and the popup's name first; then the
## motions sent, the readings and a hint (PROBE_BELOW, PROBE_ABOVE or none).
const PROBE_FAILED := "could not find %s in %s within %d motions; get_focused_item read %s%s"
const PROBE_BELOW := "; it may lie below the popup's visible items: scroll the popup first"
const PROBE_ABOVE := "; it may lie above the popup's visible items: scroll the popup first"
## The popup's name, then the item.
const POPUP_CLOSED := "%s closed while the pointer looked for %s"
## Defensive, unreachable in 4.7.2: popup() fits an embedded popup into its embedder, so some of
## its items always show.
const ITEMS_OFF_SCREEN := "%s draws no items inside the viewport"
## The item, the popup's name, the submenu's path and the wait.
const SUBMENU_DID_NOT_OPEN := "%s in %s did not open its submenu %s within %d ms"
## How long the popup a click opens may take to show, and a submenu past its popup delay.
const OPEN_TIMEOUT_MS := 1000
const SUBMENU_GRACE_MS := 1000
## How far inside its bounds a probe's y stays, in pixels.
const PROBE_MARGIN := 0.5

## The input player (godot_mcp_input.gd) and the target resolver (godot_mcp_targets.gd), set by
## the input player before this node enters the tree.
var gestures: Node
var targets: Node


## The PopupMenu whose items an item target on node names: node itself, or an OptionButton's or a
## MenuButton's popup; null for any other node.
static func popup_of(node: Node) -> PopupMenu:
	if node is PopupMenu:
		return node as PopupMenu
	if node is OptionButton:
		return (node as OptionButton).get_popup()
	if node is MenuButton:
		return (node as MenuButton).get_popup()
	return null


## The item an item spec names in node's popup (popup_of): {popup, index, button (the
## OptionButton or MenuButton, null for a PopupMenu named itself), items (the Control that draws
## them), label (label_of), named (popup_name), report (the aimed_at item {index, text,
## submenu?})}; or a String saying why there is none. Nothing is sent.
static func resolve(node: Node, item: Variant) -> Variant:
	var popup: PopupMenu = popup_of(node)
	var refusal: String = shape_refusal(str(node.get_path()), node.get_class(), item)
	if refusal.is_empty():
		refusal = reach_refusal(node, popup)
	if not refusal.is_empty():
		return refusal
	var named: String = popup_name(node)
	var texts: PackedStringArray = drawn_texts(popup)
	var picked: Variant = pick(named, texts, _separators(popup), item as Dictionary)
	if picked is String:
		return picked
	var index: int = picked
	var label: String = label_of(texts[index], index)
	refusal = item_refusal(
		named, label, popup.is_item_separator(index), popup.is_item_disabled(index)
	)
	if not refusal.is_empty():
		return refusal
	return {
		"popup": popup,
		"index": index,
		"button": null if node is PopupMenu else node,
		"items": items_of(popup),
		"label": label,
		"named": named,
		"report": _report(popup, index, texts[index]),
	}


## Why an item spec cannot be read on a popup (path and node_class name the node), or "": an
## object with exactly one of text and index; path and column are a Tree's.
static func shape_refusal(path: String, node_class: String, item: Variant) -> String:
	if ItemTargets.malformed(item):
		return ItemTargets.BAD_ITEM % JSON.stringify(item)
	for key: String in ["path", "column"]:
		if (item as Dictionary).has(key):
			return POPUP_ITEM_KEY % [path, node_class, key]
	return ""


## Why node's popup cannot be aimed into, whatever the item, or "": not embedded (a native menu,
## which input events cannot reach), a search filtering its items (filtering_search; PopupMenu
## hides the items a search leaves out and binds no read of which, scene/gui/popup_menu.cpp
## L1143-1199 in 4.7.2), a PopupMenu named itself that is not open, or no items Control.
static func reach_refusal(node: Node, popup: PopupMenu) -> String:
	var named: String = popup_name(node)
	if not popup.is_embedded() or popup.is_native_menu():
		return NATIVE_POPUP % named
	var search: Array = filtering_search(popup)
	if not search.is_empty():
		var filtering: String = named if search[0] == popup else str(search[0].get_path())
		return SEARCH_FILTERED % [filtering, search[1]]
	if node is PopupMenu and not popup.visible:
		return NOT_OPEN % named
	if items_of(popup) == null:
		return NO_ITEMS_CONTROL % named
	return ""


## A popup as a refusal names it: a PopupMenu's path, else "the popup of <the button's path>".
static func popup_name(node: Node) -> String:
	if node is PopupMenu:
		return str(node.get_path())
	return "the popup of %s" % str(node.get_path())


## The PopupMenu whose search filters popup's items and its query, [menu, query]: popup itself or
## the nearest PopupMenu above it with a query, since a search filters every submenu below it with
## its own query (scene/gui/popup_menu.cpp L1143-1147, L1166-1175, L1194-1198 in 4.7.2); [] when
## none does.
static func filtering_search(popup: PopupMenu) -> Array:
	var menu: Node = popup
	while menu is PopupMenu:
		var query: String = search_query(menu as PopupMenu)
		if not query.is_empty():
			return [menu, query]
		menu = menu.get_parent()
	return []


## The text in a popup's search bar, else "". It counts while the bar is hidden too: hiding the bar
## (search_bar_enabled off, or fewer items than search_bar_min_item_count) keeps its text and the
## items it filtered out, which only the popup's hiding clears (scene/gui/popup_menu.cpp
## L1092-1105, L1667-1674 in 4.7.2).
static func search_query(popup: PopupMenu) -> String:
	var bar := _internal(popup, "LineEdit") as LineEdit
	return "" if bar == null else bar.text


## The Control a popup draws its items in, or null.
static func items_of(popup: PopupMenu) -> Control:
	return _internal(popup, ITEMS_CLASS) as Control


## The first node under root, internal children included and Windows (submenus) skipped, whose
## class is node_class; null when there is none.
static func _internal(root: Node, node_class: String) -> Node:
	for child: Node in root.get_children(true):
		if child is Window:
			continue
		if child.get_class() == node_class:
			return child
		var found: Node = _internal(child, node_class)
		if found != null:
			return found
	return null


## Each item's text as the popup draws it, translated as its auto-translate mode says.
static func drawn_texts(popup: PopupMenu) -> PackedStringArray:
	var texts := PackedStringArray()
	for index in popup.item_count:
		var mode: int = popup.get_item_auto_translate_mode(index)
		texts.append(ItemTargets.drawn(popup, mode, popup.get_item_text(index)))
	return texts


static func _separators(popup: PopupMenu) -> Array[bool]:
	var separators: Array[bool] = []
	for index in popup.item_count:
		separators.append(popup.is_item_separator(index))
	return separators


## The index spec names among a popup's drawn texts (separators[i] says item i is one): by index,
## or the one item whose text, trimmed, is spec's text trimmed, case kept. A String when the index
## is out of range, several items read the text (listed by index), or none does (the texts of the
## items that are not separators listed). named is the popup as popup_name gives it.
static func pick(
	named: String, texts: PackedStringArray, separators: Array[bool], spec: Dictionary
) -> Variant:
	if spec.has("index"):
		var index: int = int(spec["index"])
		if index < 0 or index >= texts.size():
			return ItemTargets.NO_INDEX % [named, index, texts.size()]
		return index
	var wanted: String = str(spec["text"]).strip_edges()
	var matches: Array[int] = ItemTargets.matching(texts, wanted)
	if matches.size() == 1:
		return matches[0]
	if matches.size() > 1:
		return _ambiguity(named, wanted, matches)
	var listed := PackedStringArray()
	for index in texts.size():
		if not separators[index]:
			listed.append(texts[index])
	return ItemTargets.NO_ITEM % [named, wanted, ItemTargets.listed_texts(listed)]


static func _ambiguity(named: String, wanted: String, matches: Array[int]) -> String:
	var listed := PackedStringArray()
	for index: int in matches.slice(0, ItemTargets.MAX_LISTED):
		listed.append("index %d" % index)
	var listing: String = ItemTargets.listed_matches(matches.size(), listed)
	return AMBIGUOUS_POPUP_ITEM % [named, matches.size(), wanted, listing]


## An item as a refusal names it: its text quoted, or "item <index>" when it has none.
static func label_of(text: String, index: int) -> String:
	if text.strip_edges().is_empty():
		return "item %d" % index
	return "'%s'" % text


## Why a picked item takes no press, or "": a separator, or a disabled item, which PopupMenu
## ignores a press on and get_focused_item never reads (scene/gui/popup_menu.cpp L725-727, L847
## in 4.7.2).
static func item_refusal(named: String, label: String, separator: bool, disabled: bool) -> String:
	if separator:
		return SEPARATOR_ITEM % [label, named]
	if disabled:
		return DISABLED_ITEM % [label, named]
	return ""


static func _report(popup: PopupMenu, index: int, text: String) -> Dictionary:
	var report: Dictionary = {"index": index, "text": text}
	var submenu: PopupMenu = popup.get_item_submenu_node(index)
	if submenu != null:
		report["submenu"] = str(submenu.get_path())
	return report


## How many motions the probe may send for a popup of count items: 2 * ceil(log2(count)) + 4.
static func probe_budget(count: int) -> int:
	var bits: int = 0
	while (1 << bits) < count:
		bits += 1
	return 2 * bits + 4


## The part of the items Control's local rect (size, at position in its ScrollContainer, whose
## size is scroll_size) in which a pointer hits an item: the part the ScrollContainer shows
## (_get_mouse_over clips to it, scene/gui/popup_menu.cpp L342-353 in 4.7.2), less what lies
## outside root_rect, the root's visible rect; xform maps the Control's local space to the root's
## viewport coordinates. Without area when none is left.
static func items_area(
	size: Vector2, position: Vector2, scroll_size: Vector2, xform: Transform2D, root_rect: Rect2
) -> Rect2:
	var shown: Rect2 = Rect2(Vector2.ZERO, size).intersection(Rect2(-position, scroll_size))
	var on_screen: Rect2 = (xform * shown).intersection(root_rect)
	if not on_screen.has_area():
		return Rect2()
	return xform.affine_inverse() * on_screen


## The probe's first state for the item at target of count items in an items Control height tall,
## in its local space: bounds lo and hi (area's top and bottom), the item height estimate h
## (height over count), the first y (the target's estimated centre, inside the bounds) and the
## direction dir an unknown reading steps in (1, down).
static func first_probe(area: Rect2, height: float, count: int, target: int) -> Dictionary:
	var h: float = height / maxf(float(count), 1.0)
	var lo: float = area.position.y
	var hi: float = area.end.y
	var y: float = clampf((float(target) + 0.5) * h, lo + PROBE_MARGIN, hi - PROBE_MARGIN)
	return {"lo": lo, "hi": hi, "h": h, "y": y, "dir": 1.0}


## The probe's next state after reading, the item get_focused_item read at state's y: done when it
## is target. An item before target makes y the new top bound, one after it the new bottom, and
## the next y is target's estimated distance in items away, or the bounds' middle when that falls
## outside them. -1 (a separator, a disabled item or no item) steps half an item on in dir (toward
## target since the last item read), and once that leaves the bounds, half an item the other way
## from where the unknown readings began. stuck when no y is left to try.
static func next_probe(state: Dictionary, reading: int, target: int) -> Dictionary:
	var next: Dictionary = state.duplicate()
	if reading == target:
		next["done"] = true
		return next
	if reading < 0:
		return _stepped(next)
	var y: float = state["y"]
	var before: bool = reading < target
	next["lo" if before else "hi"] = y
	next["dir"] = 1.0 if before else -1.0
	next.erase("anchor")
	next.erase("turned")
	var guess: float = y + float(target - reading) * float(state["h"])
	next["y"] = guess if _inside(next, guess) else (float(next["lo"]) + float(next["hi"])) / 2.0
	if not _inside(next, next["y"]):
		next["stuck"] = true
	return next


static func _stepped(next: Dictionary) -> Dictionary:
	var y: float = next["y"]
	var half: float = float(next["h"]) / 2.0
	if not next.has("anchor"):
		next["anchor"] = y
	var ahead: float = y + half * float(next["dir"])
	if _inside(next, ahead):
		next["y"] = ahead
		return next
	if next.has("turned"):
		next["stuck"] = true
		return next
	next["turned"] = true
	next["dir"] = -float(next["dir"])
	var back: float = float(next["anchor"]) + half * float(next["dir"])
	next["y"] = back
	if not _inside(next, back):
		next["stuck"] = true
	return next


## Whether y lies inside a probe state's bounds, PROBE_MARGIN in from each.
static func _inside(state: Dictionary, y: float) -> bool:
	return y >= float(state["lo"]) + PROBE_MARGIN and y <= float(state["hi"]) - PROBE_MARGIN


## PROBE_FAILED for the item labelled label in the popup named, after readings (the item indices
## get_focused_item read, -1 for none) while looking for target; the hint says the item may lie
## below or above the visible items when every item read is before or after it.
static func probe_failure(
	label: String, named: String, readings: Array[int], target: int
) -> String:
	var reached: Array[int] = []
	for reading: int in readings:
		if reading >= 0:
			reached.append(reading)
	var hint: String = ""
	if not reached.is_empty() and reached.max() < target:
		hint = PROBE_BELOW
	elif not reached.is_empty() and reached.min() > target:
		hint = PROBE_ABOVE
	return PROBE_FAILED % [label, named, readings.size(), str(readings), hint]


## Why a gesture cannot aim into a probe's popup as it stands, or "": an open popup needs nothing;
## a closed one opens only for a gesture that presses (opens), never for a PopupMenu named itself
## (NOT_OPEN), not from a disabled button, whose press opens nothing (BUTTON_DISABLED), and not
## while another popup is open, which the opening press would only close. Nothing is sent.
func open_refusal(probe: Dictionary, opens: bool) -> String:
	var popup: PopupMenu = probe["popup"]
	var button: Control = probe["button"]
	if popup.visible:
		return ""
	if button == null:
		return NOT_OPEN % probe["named"]
	if not opens:
		return CLOSED_POPUP % str(button.get_path())
	if (button as BaseButton).disabled:
		return BUTTON_DISABLED % str(button.get_path())
	var open: Window = _other_open_popup(popup)
	if open != null:
		return OTHER_POPUP_OPEN % [str(button.get_path()), str(open.get_path())]
	return ""


## A visible embedded window of the root other than popup that closes when a click lands outside
## it (_closes_on_click), or null.
func _other_open_popup(popup: PopupMenu) -> Window:
	for window: Window in get_tree().root.get_embedded_subwindows():
		if window != popup and _closes_on_click(window):
			return window
	return null


## Whether window is a visible Popup with its popup_window flag on (Window.FLAG_POPUP), which a
## click outside it closes, a tooltip aside; a Popup with the flag off stays open like any window.
func _closes_on_click(window: Window) -> bool:
	return (
		window.visible
		and window is Popup
		and window.get_flag(Window.FLAG_POPUP)
		and not gestures.bridge._ui_snapshot.is_tooltip(window)
	)


## Whether an aimedAt report is an item with a submenu, which a gesture holds open and presses
## nothing on.
static func holds_submenu(aimed: Variant) -> bool:
	if not aimed is Dictionary:
		return false
	var item: Variant = (aimed as Dictionary).get("item")
	return item is Dictionary and (item as Dictionary).has("submenu")


## The input player's aim at an item in a popup: placed by probing (place, opening the popup first
## when opens allows), the pointer moved there as the probe moved it, recorded and checked as any
## aim is (the input player's record_aim), then held on an item with a submenu until the submenu
## shows (hold_submenu). Returns the viewport point, or a String saying why not, which says the
## popup is open now when the gesture opened it (with_opened).
func aim_item(resolved: Dictionary, checks_hit: bool, opens: bool) -> Variant:
	var refusal: String = await place(resolved, opens)
	if refusal.is_empty():
		var point: Vector2 = resolved["point"]
		move(point)
		gestures.listen_at(point)
		refusal = gestures.record_aim(resolved, point, checks_hit, false)
		if refusal.is_empty():
			refusal = await hold_submenu(resolved)
		if refusal.is_empty():
			return point
	return with_opened(refusal, resolved.get("opened", false))


## A refusal as the gesture reports it: with OPENED_NOTE when the gesture opened the popup first.
static func with_opened(refusal: String, opened: bool) -> String:
	return refusal + OPENED_NOTE if opened else refusal


## Places a probe aim's point (in the root's viewport coordinates) on its item, opening a closed
## button's popup first when open_refusal allows: a real click at the button's centre through the
## gesture path and its hit check, then a wait for the popup to show, after which the aim reports
## opened. The buttons open on the press (ACTION_MODE_BUTTON_PRESS, scene/gui/option_button.cpp
## L710, scene/gui/menu_button.cpp L249 in 4.7.2), so the popup shows with the button held and
## arms its grabbed-click guard (NOTIFICATION_POST_POPUP, scene/gui/popup_menu.cpp L1565-1568);
## that click's own release then reaches the popup's gui_input, which clears the guard before
## anything else (L708-712), so the item's own press needs no wait. Returns "", or why the aim
## cannot be placed.
func place(aim: Dictionary, opens: bool) -> String:
	var probe: Dictionary = aim["probe"]
	var refusal: String = open_refusal(probe, opens)
	if refusal.is_empty() and not (probe["popup"] as PopupMenu).visible:
		refusal = await _open(probe)
		aim["opened"] = refusal.is_empty()
	if not refusal.is_empty():
		return refusal
	var local: Variant = await _find(probe)
	if local is String:
		return local
	aim["point"] = targets.viewport_transform(probe["items"]) * (local as Vector2)
	return ""


## Clicks a probe's button at its centre and waits for its popup to show; the click's pressedOn
## and releasedOn are dropped, the item's gesture records its own. "" or why it did not open.
func _open(probe: Dictionary) -> String:
	var button: Control = probe["button"]
	var point: Variant = await gestures.aim({"element": str(button.get_path())}, true, false, false)
	if point is String:
		return point
	await gestures.click_at(gestures.to_window(point), MOUSE_BUTTON_LEFT, false)
	gestures.drop_press_hits()
	var shown: bool = await _shows(probe["popup"], OPEN_TIMEOUT_MS)
	if shown:
		return ""
	return DID_NOT_OPEN % [str(button.get_path()), OPEN_TIMEOUT_MS]


## Whether window shows within timeout_ms (in a recording, its clip frames), checked after each
## process frame and at least one, so a popup just shown has been laid out.
func _shows(window: Window, timeout_ms: int) -> bool:
	var until: int = Time.get_ticks_msec() + timeout_ms
	var frames: int = gestures.clip_frames(timeout_ms, gestures.clip_fps())
	var waited: int = 0
	var shown: bool = false
	while not shown and (waited == 0 or gestures.within(waited, frames, until)):
		await get_tree().process_frame
		waited += 1
		shown = is_instance_valid(window) and window.visible
	return shown


## The point in the items Control's local space where get_focused_item reads the probe's item,
## found with real motions (move) at the items area's horizontal centre, a frame after each,
## bisecting on y (first_probe, next_probe; at most probe_budget motions). The last leaves the
## pointer there, the item highlighted as a player's pointer leaves it. A String when the popup
## closes or no motion finds the item.
func _find(probe: Dictionary) -> Variant:
	var popup: PopupMenu = probe["popup"]
	var items: Control = probe["items"]
	var xform: Transform2D = targets.viewport_transform(items)
	var scroll := items.get_parent() as Control
	var scroll_size: Vector2 = items.size if scroll == null else scroll.size
	var root_rect: Rect2 = get_tree().root.get_visible_rect()
	var area: Rect2 = items_area(items.size, items.position, scroll_size, xform, root_rect)
	if not area.has_area():
		return ITEMS_OFF_SCREEN % probe["named"]
	var target: int = probe["index"]
	var state: Dictionary = first_probe(area, items.size.y, popup.item_count, target)
	var readings: Array[int] = []
	while readings.size() < probe_budget(popup.item_count) and not state.has("stuck"):
		var y: float = state["y"]
		move(xform * Vector2(area.get_center().x, y))
		await get_tree().process_frame
		if not is_instance_valid(popup) or not popup.visible:
			return POPUP_CLOSED % [probe["named"], probe["label"]]
		readings.append(popup.get_focused_item())
		state = next_probe(state, readings[-1], target)
		if state.has("done"):
			return Vector2(area.get_center().x, y)
	return probe_failure(probe["label"], probe["named"], readings, target)


## Moves the pointer to a viewport point as a player's mouse moves over a popup: PopupMenu ignores
## a motion without velocity (scene/gui/popup_menu.cpp L750 in 4.7.2) and Input fills in none for
## an event it is given (core/input/input.cpp L865-890), so this motion carries its relative over
## the last process frame's time.
func move(point: Vector2) -> void:
	var window_point: Vector2 = gestures.to_window(point)
	var relative: Vector2 = window_point - gestures.bridge._pointer
	var motion: InputEventMouseMotion = gestures.motion_event(
		window_point, relative, gestures.bridge._held_mask
	)
	var seconds: float = maxf(get_process_delta_time(), 0.001)
	motion.velocity = relative / seconds
	motion.screen_velocity = relative / seconds
	gestures.dispatch(motion)


## Holds the pointer on an aim's item while the item has a submenu, until the submenu shows: the
## popup opens it once the pointer has rested on the item for its submenu_popup_delay
## (_mouse_over_update and _submenu_timeout, scene/gui/popup_menu.cpp L845-880, L500-504 in
## 4.7.2). "" when it shows, is already open, or the item has none; else why not.
func hold_submenu(aim: Dictionary) -> String:
	var probe: Dictionary = aim["probe"]
	var popup: PopupMenu = probe["popup"]
	var submenu: PopupMenu = popup.get_item_submenu_node(probe["index"])
	if submenu == null or submenu.visible:
		return ""
	var wait_ms: int = int(popup.submenu_popup_delay * 1000.0) + SUBMENU_GRACE_MS
	var shown: bool = await _shows(submenu, wait_ms)
	if shown:
		return ""
	return SUBMENU_DID_NOT_OPEN % [probe["label"], probe["named"], str(submenu.get_path()), wait_ms]
