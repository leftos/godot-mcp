extends RefCounted
## A TabContainer of a scene opened headless: its stored current tab put back on its tab bar.


## Sets the current tab of node, when a TabContainer, to the current_tab the record at index of
## state stores, through its tab bar: out of the tree TabContainer.set_current_tab only keeps the
## value for entering the tree, its getter reads the bar, and the bar's first add_tab makes tab 0
## current (4.7.2 tab_container.cpp L692-703, tab_bar.cpp L1428-1429), so pack would save 0.
static func restore_current(node: Node, state: SceneState, index: int) -> void:
	var tabs := node as TabContainer
	if tabs == null:
		return
	for pair in state.get_node_property_count(index):
		if state.get_node_property_name(index, pair) != "current_tab":
			continue
		var value: Variant = state.get_node_property_value(index, pair)
		# A value out of range is left alone, as the tab bar would refuse it; one the bar already
		# holds too, since setting it again moves the bar's previous tab (tab_bar.cpp L832-833).
		if typeof(value) != TYPE_INT or value == tabs.current_tab:
			return
		if value >= 0 and value < tabs.get_tab_count():
			_set_current_quietly(tabs.get_tab_bar(), value)


## Sets bar's current tab with its signals blocked: tab_changed would run TabContainer's _repaint at
## once out of the tree, which the engine's own out-of-tree paths defer (tab_container.cpp L499-500,
## L616-617), and so change the pages' visibility the scene stores.
static func _set_current_quietly(bar: TabBar, value: int) -> void:
	bar.set_block_signals(true)
	bar.current_tab = value
	bar.set_block_signals(false)
