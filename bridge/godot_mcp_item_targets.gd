extends RefCounted
## Item targets for the target resolver (godot_mcp_targets.gd): an item drawn inside an ItemList,
## a TabBar, a TabContainer's tab bar or a Tree, named by {text | index | path, column?} and
## matched by the text as drawn (translated as the Control translates it), placed at a point of
## its rect in the local space of the Control that draws it and checked back through that
## Control's own hit test; or a String saying why it cannot be aimed at. Static functions called
## on the script itself.

## The classes an item target takes, each with its descendants.
const LIST_CLASSES: Array[String] = ["ItemList", "TabBar", "TabContainer", "Tree"]
## A node of another class: its path and class.
const NOT_A_LIST := (
	"%s is a %s; item targets take an ItemList, TabBar, TabContainer, Tree, "
	+ "OptionButton, MenuButton or PopupMenu"
)
## A Tree's key on a flat list: the list's path, its class and the key.
const ITEM_KEY_REFUSED := "%s is a %s; item.%s is for a Tree only"
## An index on a Tree: the Tree's path.
const ITEM_INDEX_TREE := "%s is a Tree; a Tree item takes text or path, not index"
## An item the bridge cannot read (the server's shape check keeps these out): the item as JSON.
const BAD_ITEM := "item must be an object with one of text, index or a non-empty path; got %s"
## No item reads the text: the list's path, the text, the drawn texts as listed_texts gives them.
const NO_ITEM := "%s has no item '%s'; items: %s"
## An index out of range: the list's path, the index, the item count.
const NO_INDEX := "%s has no item %d; it has %d"
## A path step no shown child reads: the Tree's path, the path as path_text gives it, the text of
## the item whose children were searched (the Tree's name at the first level), those children's.
const NO_PATH := "%s has no item at path %s; '%s' has children: %s"
## A column out of range: the Tree's path, its column count, the column.
const NO_COLUMN := "%s has %d columns; item.column %d is out of range"
## Several shown items read the text: the list's path, the count, the text, the matches listed
## (a flat list's as "index N at x,y,w,h", a Tree's as "path [..] at x,y,w,h").
const AMBIGUOUS_ITEM := "%s has %d items reading '%s': %s; narrow with item.index"
## A Tree item that folds whose column 0 is no wider than its fold arrow's indent: its text, the
## Tree's path and column 0's rect in the root's viewport coordinates as rect_text gives it.
const PAST_FOLD_INDENT := (
	"item '%s' of %s sits past its fold arrow's indent in column 0 (%s); "
	+ "widen the column or aim with {x, y}"
)
const AMBIGUOUS_TREE_ITEM := "%s has %d items reading '%s': %s; narrow with item.path"
## A Tree's matches that all have one path, which no item.path tells apart: then that path.
const SHARED_PATH_ITEM := (
	"%s has %d items reading '%s': %s; they share the path %s; "
	+ "aim with {x, y} inside one of the rects listed"
)
## The refusals of an item found but not reachable: the item's text and the list's path first.
const HIDDEN_ITEM := "item '%s' of %s is hidden"
const COLLAPSED_ITEM := "item '%s' of %s is under the collapsed item %s; expand it first"
## Then the item's rect and the list's visible rect (less its column titles and scroll bars), in
## the root's viewport coordinates as rect_text gives them.
const SCROLLED_OUT_ITEM := (
	"item '%s' of %s is at %s, outside the list's visible rect %s; " + "scroll the list first"
)
## A tab or an ItemList or Tree item whose point the Control's hit test does not map back to it.
const TAB_NOT_DRAWN := (
	"item '%s' of %s has no drawn rect; it may be outside the tab bar's drawn range "
	+ "(scroll the tabs) or not laid out yet"
)
const LIST_ITEM_NOT_DRAWN := (
	"item '%s' of %s has no drawn rect at its point; "
	+ "it may be scrolled under a header or not laid out yet"
)
## How many texts or matches a refusal lists.
const MAX_LISTED := 10


## The item an item spec names in list: {drawer, local, report}, drawer the Control that draws it
## (a TabContainer's tab bar, else list), local the point aimed at in drawer's local space, report
## the aimed_at item {index, text, path?, disabled?}; or a String saying why there is none.
## targets is the resolver, whose viewport_transform, viewport_rect and rect_text place a
## refusal's rects.
static func resolve(targets: Node, list: Node, item: Variant) -> Variant:
	var refusal: String = shape_refusal(str(list.get_path()), list.get_class(), item)
	if not refusal.is_empty():
		return refusal
	if list is Tree:
		return _tree_item(targets, list as Tree, item as Dictionary)
	return _flat_item(targets, list as Control, item as Dictionary)


## Why an item spec cannot be read on a node of list_class (path names it), or "": the class must
## be one of LIST_CLASSES or descend from one, the spec an object with exactly one of text, index
## and a non-empty path; path and column are a Tree's only, index a flat list's only.
static func shape_refusal(path: String, list_class: String, item: Variant) -> String:
	var kind: String = item_kind(list_class)
	if kind.is_empty():
		return NOT_A_LIST % [path, list_class]
	if malformed(item):
		return BAD_ITEM % JSON.stringify(item)
	var spec: Dictionary = item
	if kind == "tree":
		return ITEM_INDEX_TREE % path if spec.has("index") else ""
	for key: String in ["path", "column"]:
		if spec.has(key):
			return ITEM_KEY_REFUSED % [path, list_class, key]
	return ""


## "tree" for a Tree, "flat" for an ItemList, a TabBar or a TabContainer, "" for any other class.
static func item_kind(list_class: String) -> String:
	for known: String in LIST_CLASSES:
		if ClassDB.is_parent_class(list_class, known):
			return "tree" if known == "Tree" else "flat"
	return ""


## Whether an item spec is not an object with exactly one of text, index and path, or has a path
## that is not a non-empty array.
static func malformed(item: Variant) -> bool:
	if not item is Dictionary:
		return true
	var spec: Dictionary = item
	var count: int = 0
	for key: String in ["text", "index", "path"]:
		count += 1 if spec.has(key) else 0
	var path: Variant = spec.get("path", [""])
	return count != 1 or not path is Array or (path as Array).is_empty()


## A text as node draws it under mode (Node.AutoTranslateMode): atr when it inherits, tr when it
## always translates, as is when it never does (scene/gui/item_list.cpp L1986-2000,
## scene/gui/tree.cpp L297-312 in 4.7.2).
static func drawn(node: Node, mode: int, text: String) -> String:
	if mode == Node.AUTO_TRANSLATE_MODE_INHERIT:
		return node.atr(text)
	if mode == Node.AUTO_TRANSLATE_MODE_ALWAYS:
		return node.tr(text)
	return text


## An ItemList's item, or a tab of a TabBar or of a TabContainer's tab bar.
static func _flat_item(targets: Node, list: Control, spec: Dictionary) -> Variant:
	var drawer: Control = list
	if list is TabContainer:
		drawer = (list as TabContainer).get_tab_bar()
	var path: String = str(list.get_path())
	var texts: PackedStringArray = _flat_texts(drawer)
	var picked: Variant = flat_pick(path, texts, _flat_hidden(drawer), spec)
	if picked is String:
		return picked
	if picked is Array:
		var wanted: String = str(spec["text"]).strip_edges()
		return _flat_ambiguity(targets, [path, wanted], drawer, picked)
	var index: int = picked
	if not drawer.is_visible_in_tree():
		return HIDDEN_ITEM % [texts[index], path]
	return _flat_placed(targets, drawer, index, [texts[index], path])


## A flat list's item at its drawn rect's centre, checked back; named is [its text, the list's
## path].
static func _flat_placed(targets: Node, drawer: Control, index: int, named: Array) -> Variant:
	var rect: Rect2 = _flat_rect(drawer, index)
	var local: Vector2 = rect.get_center()
	var refusal: String = _reach_refusal(targets, drawer, rect, local, named)
	if refusal.is_empty() and _flat_hit(drawer, local) != index:
		refusal = _not_drawn(drawer) % named
	if not refusal.is_empty():
		return refusal
	var report: Dictionary = {"index": index, "text": named[0]}
	if _flat_disabled(drawer, index):
		report["disabled"] = true
	return {"drawer": drawer, "local": local, "report": report}


## The index spec names among a flat list's drawn texts (hidden[i] says item i is hidden): by
## index, or the one shown item whose text, trimmed, is spec's text trimmed, case kept. Several
## shown matches come back as an Array of their indices, which the caller lists with their rects.
## A String says why there is none: an index out of range, a hidden item, or no item reading the
## text, listing the shown ones.
static func flat_pick(
	path: String, texts: PackedStringArray, hidden: Array[bool], spec: Dictionary
) -> Variant:
	if spec.has("index"):
		return _pick_index(path, texts, hidden, int(spec["index"]))
	var wanted: String = str(spec["text"]).strip_edges()
	var matches: Array[int] = matching(texts, wanted)
	var shown: Array[int] = []
	for index: int in matches:
		if not hidden[index]:
			shown.append(index)
	if shown.size() == 1:
		return shown[0]
	if shown.size() > 1:
		return shown
	if not matches.is_empty():
		return HIDDEN_ITEM % [wanted, path]
	return NO_ITEM % [path, wanted, listed_texts(_unhidden(texts, hidden))]


static func _pick_index(
	path: String, texts: PackedStringArray, hidden: Array[bool], index: int
) -> Variant:
	if index < 0 or index >= texts.size():
		return NO_INDEX % [path, index, texts.size()]
	if hidden[index]:
		return HIDDEN_ITEM % [texts[index], path]
	return index


## The indices of the texts that, trimmed, are wanted.
static func matching(texts: PackedStringArray, wanted: String) -> Array[int]:
	var matches: Array[int] = []
	for index in texts.size():
		if texts[index].strip_edges() == wanted:
			matches.append(index)
	return matches


static func _unhidden(texts: PackedStringArray, hidden: Array[bool]) -> PackedStringArray:
	var shown := PackedStringArray()
	for index in texts.size():
		if not hidden[index]:
			shown.append(texts[index])
	return shown


## Texts as a refusal lists them: at most MAX_LISTED, each quoted, joined with ", ", and … when
## there are more; "none" when there are none.
static func listed_texts(texts: PackedStringArray) -> String:
	if texts.is_empty():
		return "none"
	var listed := PackedStringArray()
	for text: String in texts.slice(0, MAX_LISTED):
		listed.append("'%s'" % text)
	if texts.size() > MAX_LISTED:
		listed.append("…")
	return ", ".join(listed)


## count matches as a refusal lists them: listed (at most MAX_LISTED described) joined with ", ",
## and … when there are more.
static func listed_matches(count: int, listed: PackedStringArray) -> String:
	var shown: PackedStringArray = listed.duplicate()
	if count > listed.size():
		shown.append("…")
	return ", ".join(shown)


## A Tree's ambiguity refusal for the Tree at path: count items read wanted, listed describes the
## first ones and paths holds each listed one's path as path_text gives it. When every listed
## match has the same path, no item.path tells them apart, so SHARED_PATH_ITEM says to aim by
## point; else AMBIGUOUS_TREE_ITEM says to narrow with item.path.
static func tree_ambiguity_refusal(
	path: String, wanted: String, count: int, listed: PackedStringArray, paths: PackedStringArray
) -> String:
	var listing: String = listed_matches(count, listed)
	if count <= paths.size() and paths.count(paths[0]) == paths.size():
		return SHARED_PATH_ITEM % [path, count, wanted, listing, paths[0]]
	return AMBIGUOUS_TREE_ITEM % [path, count, wanted, listing]


## A Tree path as a refusal and aimed_at show it: ["Weapons", "Sword"].
static func path_text(steps: PackedStringArray) -> String:
	var quoted := PackedStringArray()
	for step: String in steps:
		quoted.append(JSON.stringify(step))
	return "[%s]" % ", ".join(quoted)


## Whether a point in a drawer's local space lies outside its visible rect: the item is scrolled
## out of view, or under a column title or a scroll bar.
static func scrolled_out(local: Vector2, visible: Rect2) -> bool:
	return not visible.has_point(local)


## A list's visible rect in its local space: its own rect below top (a Tree's column titles) less
## the rects of its visible scroll bars, each cutting the side it lies on.
static func visible_rect(size: Vector2, top: float, bars: Array[Rect2]) -> Rect2:
	var rect := Rect2(0.0, top, size.x, size.y - top)
	for bar: Rect2 in bars:
		rect = _trimmed(rect, bar)
	return rect


static func _trimmed(rect: Rect2, bar: Rect2) -> Rect2:
	if bar.size.y < bar.size.x:
		return Rect2(rect.position, Vector2(rect.size.x, bar.position.y - rect.position.y))
	if bar.get_center().x >= rect.get_center().x:
		return Rect2(rect.position, Vector2(bar.position.x - rect.position.x, rect.size.y))
	var left: float = bar.end.x
	return Rect2(Vector2(left, rect.position.y), Vector2(rect.end.x - left, rect.size.y))


## The x an ItemList's right-to-left layout mirrors a point's x about: get_item_at_position turns
## a local x into width + 2 * offset_x - 2 * h_scroll - margins - x before its search (the panel's
## offset taken off and the scroll added first; scene/gui/item_list.cpp L2003-2011 in 4.7.2), so
## a drawn rect is the mirror of its get_item_rect about this.
static func mirror_axis(width: float, offset_x: float, margins: float, h_scroll: float) -> float:
	return width + 2.0 * offset_x - 2.0 * h_scroll - margins


## The point aimed at in a Tree cell's rect: the centre of the part past indent, the width a click
## folds an item with children in (x_ofs + item_margin, scene/gui/tree.cpp L3170, L3409 in 4.7.2),
## on the left, or on the right in a right-to-left layout.
static func span_centre(rect: Rect2, indent: float, rtl: bool) -> Vector2:
	var start: float = rect.position.x
	var end: float = rect.end.x
	if rtl:
		end -= indent
	else:
		start += indent
	return Vector2((start + end) / 2.0, rect.get_center().y)


static func _flat_texts(drawer: Control) -> PackedStringArray:
	var texts := PackedStringArray()
	if drawer is TabBar:
		var bar := drawer as TabBar
		for index in bar.tab_count:
			texts.append(bar.atr(bar.get_tab_title(index)))
		return texts
	var items := drawer as ItemList
	for index in items.item_count:
		var mode: int = items.get_item_auto_translate_mode(index)
		texts.append(drawn(items, mode, items.get_item_text(index)))
	return texts


## Which of a flat list's items are hidden: a TabBar's hidden tabs; an ItemList hides none.
static func _flat_hidden(drawer: Control) -> Array[bool]:
	var hidden: Array[bool] = []
	var bar := drawer as TabBar
	if bar == null:
		hidden.resize((drawer as ItemList).item_count)
		hidden.fill(false)
		return hidden
	for index in bar.tab_count:
		hidden.append(bar.is_tab_hidden(index))
	return hidden


## An item's drawn rect in drawer's local space: a tab's get_tab_rect, which a right-to-left
## layout already mirrors; an ItemList item's get_item_rect less the scroll bars' values, which
## get_item_rect leaves in and get_item_at_position adds back (scene/gui/item_list.cpp L342-355,
## L2003-2007 in 4.7.2), mirrored in a right-to-left layout (mirror_axis).
static func _flat_rect(drawer: Control, index: int) -> Rect2:
	if drawer is TabBar:
		return (drawer as TabBar).get_tab_rect(index)
	var items := drawer as ItemList
	var h_scroll: float = items.get_h_scroll_bar().value
	var rect: Rect2 = items.get_item_rect(index)
	rect.position.y -= items.get_v_scroll_bar().value
	if not items.is_layout_rtl():
		rect.position.x -= h_scroll
		return rect
	var panel: StyleBox = items.get_theme_stylebox(&"panel")
	var margins: float = panel.get_margin(SIDE_LEFT) + panel.get_margin(SIDE_RIGHT)
	var axis: float = mirror_axis(items.size.x, panel.get_offset().x, margins, h_scroll)
	rect.position.x = axis - rect.end.x
	return rect


## The item drawer's own hit test finds at a local point, -1 for none.
static func _flat_hit(drawer: Control, local: Vector2) -> int:
	if drawer is TabBar:
		return (drawer as TabBar).get_tab_idx_at_point(local)
	return (drawer as ItemList).get_item_at_position(local, true)


static func _flat_disabled(drawer: Control, index: int) -> bool:
	if drawer is TabBar:
		return (drawer as TabBar).is_tab_disabled(index)
	return (drawer as ItemList).is_item_disabled(index)


## AMBIGUOUS_ITEM for a flat list: named is [the list's path, the text]; each match listed as
## "index N at x,y,w,h", its rect in the root's viewport coordinates.
static func _flat_ambiguity(
	targets: Node, named: Array, drawer: Control, matches: Array[int]
) -> String:
	var xform: Transform2D = targets.viewport_transform(drawer)
	var listed := PackedStringArray()
	for index: int in matches.slice(0, MAX_LISTED):
		var rect: Rect2 = xform * _flat_rect(drawer, index)
		listed.append("index %d at %s" % [index, targets.rect_text(rect)])
	return (
		AMBIGUOUS_ITEM
		% [named[0], matches.size(), named[1], listed_matches(matches.size(), listed)]
	)


## Why an item at rect, aimed at local, both in drawer's local space, cannot be aimed at before
## the hit test is asked, or "": a rect with no area, or local outside drawer's visible rect
## (scrolled out; both rects given in the root's viewport coordinates). named is [the item's
## text, the list's path].
static func _reach_refusal(
	targets: Node, drawer: Control, rect: Rect2, local: Vector2, named: Array
) -> String:
	if not rect.has_area():
		return _not_drawn(drawer) % named
	var visible: Rect2 = _visible_rect(drawer)
	if not scrolled_out(local, visible):
		return ""
	var xform: Transform2D = targets.viewport_transform(drawer)
	var boxes: Array = [targets.rect_text(xform * rect), targets.rect_text(xform * visible)]
	return SCROLLED_OUT_ITEM % (named + boxes)


static func _not_drawn(drawer: Control) -> String:
	return TAB_NOT_DRAWN if drawer is TabBar else LIST_ITEM_NOT_DRAWN


## drawer's visible rect (visible_rect): a TabBar's whole rect; an ItemList's or a Tree's less its
## visible scroll bars (its internal ScrollBar children) and, for a Tree showing column titles,
## less the panel's top offset and the title row.
static func _visible_rect(drawer: Control) -> Rect2:
	if drawer is TabBar:
		return Rect2(Vector2.ZERO, drawer.size)
	var bars: Array[Rect2] = []
	for child: Node in drawer.get_children(true):
		if child is ScrollBar and (child as ScrollBar).visible:
			bars.append((child as ScrollBar).get_rect())
	var tree := drawer as Tree
	var top: float = 0.0
	if tree != null and tree.column_titles_visible:
		top = tree.get_theme_stylebox(&"panel").get_offset().y + _title_height(tree)
	return visible_rect(drawer.size, top, bars)


## A Tree's column-title row height as _get_title_button_height stores it, an int: for a one-line
## title, the title font's height plus the title button's minimum height (scene/gui/tree.cpp
## L4718-4727, L2217 in 4.7.2).
static func _title_height(tree: Tree) -> float:
	var font: Font = tree.get_theme_font(&"title_button_font")
	var font_size: int = tree.get_theme_font_size(&"title_button_font_size")
	var button: StyleBox = tree.get_theme_stylebox(&"title_button_normal")
	return floorf(font.get_height(font_size) + button.get_minimum_size().y)


## A Tree's item in column, by path or by text, refused when hidden or under a collapsed item.
static func _tree_item(targets: Node, tree: Tree, spec: Dictionary) -> Variant:
	var path: String = str(tree.get_path())
	var column: int = int(spec.get("column", 0))
	if column < 0 or column >= tree.columns:
		return NO_COLUMN % [path, tree.columns, column]
	var picked: Variant = null
	if spec.has("path"):
		picked = _tree_by_path(targets, tree, spec["path"], column)
	else:
		picked = _tree_by_text(targets, tree, str(spec["text"]).strip_edges(), column)
	if picked is String:
		return picked
	var item: TreeItem = picked
	var refusal: String = _tree_reach_refusal(tree, item, cell_text(tree, item, column))
	if not refusal.is_empty():
		return refusal
	return _tree_placed(targets, tree, item, column)


## A Tree cell's text as drawn: translated as its column's auto-translate mode says.
static func cell_text(tree: Tree, item: TreeItem, column: int) -> String:
	return drawn(tree, item.get_auto_translate_mode(column), item.get_text(column))


## The one shown item whose drawn text in column, trimmed, is wanted, at any depth; a String when
## none or several are.
static func _tree_by_text(targets: Node, tree: Tree, wanted: String, column: int) -> Variant:
	var items: Array[TreeItem] = _tree_items(tree)
	var matches: Array[TreeItem] = _items_reading(tree, items, wanted, column)
	var shown: Array[TreeItem] = _visible_only(matches)
	if shown.size() == 1:
		return shown[0]
	if shown.size() > 1:
		return _tree_ambiguity(targets, tree, wanted, shown, column)
	if not matches.is_empty():
		return HIDDEN_ITEM % [wanted, str(tree.get_path())]
	var texts: PackedStringArray = _texts_of(tree, _visible_only(items), column)
	return NO_ITEM % [str(tree.get_path()), wanted, listed_texts(texts)]


## The item a path names: each step matched among the shown children of every item the step
## before matched, by drawn text in column 0, trimmed, against the step trimmed, from the first
## shown level; a String when a step matches none, or when the last matches several.
static func _tree_by_path(targets: Node, tree: Tree, steps: Array, column: int) -> Variant:
	var level: Array[TreeItem] = _visible_only(_first_level(tree))
	var parent_text: String = str(tree.name)
	var wanted: String = ""
	var matches: Array[TreeItem] = []
	for step: Variant in steps:
		wanted = str(step).strip_edges()
		matches = _items_reading(tree, level, wanted, 0)
		if matches.is_empty():
			var children: String = listed_texts(_texts_of(tree, level, 0))
			var given: String = path_text(PackedStringArray(steps))
			return NO_PATH % [str(tree.get_path()), given, parent_text, children]
		parent_text = cell_text(tree, matches[0], 0)
		level = _shown_children(matches)
	if matches.size() > 1:
		return _tree_ambiguity(targets, tree, wanted, matches, column)
	return matches[0]


static func _shown_children(items: Array[TreeItem]) -> Array[TreeItem]:
	var children: Array[TreeItem] = []
	for item: TreeItem in items:
		children.append_array(_visible_only(item.get_children()))
	return children


## The items shown at a Tree's first level: the root's children when the root is hidden, else the
## root; none when the Tree has no root.
static func _first_level(tree: Tree) -> Array[TreeItem]:
	var root: TreeItem = tree.get_root()
	if root == null:
		return []
	if tree.hide_root:
		return root.get_children()
	return [root]


## Every item from a Tree's first shown level down, depth first in drawing order.
static func _tree_items(tree: Tree) -> Array[TreeItem]:
	var items: Array[TreeItem] = []
	for item: TreeItem in _first_level(tree):
		_append_subtree(item, items)
	return items


static func _append_subtree(item: TreeItem, items: Array[TreeItem]) -> void:
	items.append(item)
	for child: TreeItem in item.get_children():
		_append_subtree(child, items)


static func _items_reading(
	tree: Tree, items: Array[TreeItem], wanted: String, column: int
) -> Array[TreeItem]:
	var reading: Array[TreeItem] = []
	for item: TreeItem in items:
		if cell_text(tree, item, column).strip_edges() == wanted:
			reading.append(item)
	return reading


## The items neither hidden nor under a hidden item; collapse is not visibility.
static func _visible_only(items: Array[TreeItem]) -> Array[TreeItem]:
	var shown: Array[TreeItem] = []
	for item: TreeItem in items:
		if item.is_visible_in_tree():
			shown.append(item)
	return shown


static func _texts_of(tree: Tree, items: Array[TreeItem], column: int) -> PackedStringArray:
	var texts := PackedStringArray()
	for item: TreeItem in items:
		texts.append(cell_text(tree, item, column))
	return texts


## A Tree's ambiguity refusal (tree_ambiguity_refusal), each match listed as "path [..] at
## x,y,w,h", its cell's rect in column in the root's viewport coordinates.
static func _tree_ambiguity(
	targets: Node, tree: Tree, wanted: String, matches: Array[TreeItem], column: int
) -> String:
	var xform: Transform2D = targets.viewport_transform(tree)
	var listed := PackedStringArray()
	var paths := PackedStringArray()
	for item: TreeItem in matches.slice(0, MAX_LISTED):
		var shown: String = path_text(shown_path(tree, item))
		var rect: Rect2 = xform * tree.get_item_area_rect(item, column)
		paths.append(shown)
		listed.append("path %s at %s" % [shown, targets.rect_text(rect)])
	return tree_ambiguity_refusal(str(tree.get_path()), wanted, matches.size(), listed, paths)


## Why a Tree item cannot be reached, or "": hidden (it or an ancestor not visible), or under a
## collapsed item, the outermost named, since TreeItem.is_visible_in_tree ignores collapse.
static func _tree_reach_refusal(tree: Tree, item: TreeItem, text: String) -> String:
	var path: String = str(tree.get_path())
	if not item.is_visible_in_tree():
		return HIDDEN_ITEM % [text, path]
	var folded: TreeItem = collapsed_ancestor(item)
	if folded == null:
		return ""
	return COLLAPSED_ITEM % [text, path, path_text(shown_path(tree, folded))]


## The outermost collapsed ancestor of item, or null when none is collapsed.
static func collapsed_ancestor(item: TreeItem) -> TreeItem:
	var outermost: TreeItem = null
	var parent: TreeItem = item.get_parent()
	while parent != null:
		if parent.collapsed:
			outermost = parent
		parent = parent.get_parent()
	return outermost


## The drawn texts in column 0 of item and its ancestors from the Tree's first shown level down.
static func shown_path(tree: Tree, item: TreeItem) -> PackedStringArray:
	var steps := PackedStringArray()
	var hidden_root: TreeItem = tree.get_root() if tree.hide_root else null
	var at: TreeItem = item
	while at != null and at != hidden_root:
		steps.insert(0, cell_text(tree, at, 0))
		at = at.get_parent()
	return steps


## A Tree item's placement in its column's rect (get_item_area_rect, Tree-local with the scroll
## in) at _tree_local's point, checked back by get_item_at_position and get_column_at_position;
## or a String.
static func _tree_placed(targets: Node, tree: Tree, item: TreeItem, column: int) -> Variant:
	var text: String = cell_text(tree, item, column)
	var rect: Rect2 = tree.get_item_area_rect(item, column)
	var steps: PackedStringArray = shown_path(tree, item)
	var point: Variant = _tree_local(targets, tree, item, column, rect)
	if point is String:
		return point
	var local: Vector2 = point
	var named: Array = [text, str(tree.get_path())]
	var refusal: String = _reach_refusal(targets, tree, rect, local, named)
	var hit: bool = (
		tree.get_item_at_position(local) == item and tree.get_column_at_position(local) == column
	)
	if refusal.is_empty() and not hit:
		refusal = LIST_ITEM_NOT_DRAWN % named
	if not refusal.is_empty():
		return refusal
	var report: Dictionary = {"index": item.get_index(), "text": text, "path": Array(steps)}
	if not item.is_selectable(column):
		report["disabled"] = true
	return {"drawer": tree, "local": local, "report": report}


## The point aimed at in a Tree cell's rect: its centre, or in column 0 of an item that folds
## (folds) the centre of the part past the indent a click there folds it in (span_centre); a
## String when that indent reaches the column's end.
static func _tree_local(
	targets: Node, tree: Tree, item: TreeItem, column: int, rect: Rect2
) -> Variant:
	if column != 0 or not folds(tree, item):
		return rect.get_center()
	var indent: float = shown_path(tree, item).size() * tree.get_theme_constant(&"item_margin")
	if indent >= rect.size.x:
		var box: String = targets.rect_text(targets.viewport_transform(tree) * rect)
		return PAST_FOLD_INDENT % [cell_text(tree, item, column), str(tree.get_path()), box]
	return span_centre(rect, indent, tree.is_layout_rtl())


## Whether a click in item's fold indent folds it: it has children, and neither the Tree hides
## folding nor the item disables it (scene/gui/tree.cpp L3170 in 4.7.2).
static func folds(tree: Tree, item: TreeItem) -> bool:
	return item.get_child_count() > 0 and not tree.hide_folding and not item.disable_folding
