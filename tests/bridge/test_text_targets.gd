extends "res://gd_test.gd"
## The shown text (bridge/godot_mcp_shown_text.gd) and the text targets' pure helpers
## (bridge/godot_mcp_text_targets.gd), on nodes never added to the tree and on entries built by
## hand; the scan of a live tree is covered by TextTargetTests.

var _shown_text: GDScript = load_bridge_script("godot_mcp_shown_text.gd")
var _text_targets: GDScript = load_bridge_script("godot_mcp_text_targets.gd")


func test_shown_text_reads_each_class_as_drawn() -> void:
	var button := Button.new()
	button.text = "New Game"
	var check := CheckBox.new()
	check.text = "Sound"
	var link := LinkButton.new()
	link.text = "Credits"
	var label := Label.new()
	label.text = "Strike"
	var rich := RichTextLabel.new()
	rich.bbcode_enabled = true
	rich.text = "[color=#ff0000]Red[/color] move"
	var rect := ColorRect.new()
	var nodes: Array[Control] = [button, check, link, label, rich, rect]
	var shown: Array = []
	for node: Control in nodes:
		shown.append(_shown_text.shown_text(node))
		node.free()
	assert_eq(shown, ["New Game", "Sound", "Credits", "Strike", "Red move", null], "as drawn")


func test_shown_text_reads_a_line_edit_placeholder_only_while_empty() -> void:
	var line := LineEdit.new()
	line.placeholder_text = "Your name"
	assert_eq(_shown_text.shown_text(line), "Your name", "empty: the placeholder")
	line.text = "Alex"
	assert_eq(_shown_text.shown_text(line), "Alex", "typed: the text")
	line.free()


func test_shown_text_reads_a_secret_line_edit_as_its_secret_characters() -> void:
	var line := LineEdit.new()
	line.placeholder_text = "Password"
	line.secret = true
	assert_eq(_shown_text.shown_text(line), "Password", "empty: still the placeholder")
	line.text = "hunter"
	assert_eq(_shown_text.shown_text(line), "••••••", "the default secret character")
	line.secret_character = "*#"
	assert_eq(_shown_text.shown_text(line), "******", "the secret character's first")
	line.free()


func test_shown_text_upper_cases_an_uppercase_label_as_the_text_server_does() -> void:
	var label := Label.new()
	label.text = "Straße"
	label.uppercase = true
	assert_eq(_shown_text.shown_text(label), "STRASSE", "the text server's upper case: ß is SS")
	label.uppercase = false
	assert_eq(_shown_text.shown_text(label), "Straße", "uppercase off")
	label.free()


func test_shown_text_reads_an_option_button_selected_item() -> void:
	var option := OptionButton.new()
	option.add_item("Alex")
	option.add_item("Sam")
	option.select(1)
	assert_eq(_shown_text.shown_text(option), "Sam", "the selected item's text")
	option.select(-1)
	assert_eq(_shown_text.shown_text(option), "", "nothing selected")
	option.free()


func test_shown_text_translates_as_the_node_and_the_option_item_say() -> void:
	var translation := Translation.new()
	translation.locale = TranslationServer.get_locale()
	translation.add_message("Play", "Jouer")
	TranslationServer.add_translation(translation)
	var button := Button.new()
	button.text = "Play"
	var raw := Button.new()
	raw.text = "Play"
	raw.auto_translate_mode = Node.AUTO_TRANSLATE_MODE_DISABLED
	var option := OptionButton.new()
	option.add_item("Play")
	option.add_item("Play")
	option.get_popup().set_item_auto_translate_mode(1, Node.AUTO_TRANSLATE_MODE_DISABLED)
	option.select(0)
	var inherited: Variant = _shown_text.shown_text(option)
	option.select(1)
	var disabled: Variant = _shown_text.shown_text(option)
	var shown: Array = [_shown_text.shown_text(button), _shown_text.shown_text(raw)]
	TranslationServer.remove_translation(translation)
	assert_eq(shown, ["Jouer", "Play"], "translated, unless the node's mode is disabled")
	assert_eq([inherited, disabled], ["Jouer", "Play"], "an item's own mode wins")
	for node: Node in [button, raw, option]:
		node.free()


func test_shown_text_translates_a_label_a_link_and_a_placeholder() -> void:
	var translation := Translation.new()
	translation.locale = TranslationServer.get_locale()
	translation.add_message("Play", "Jouer")
	translation.add_message("Your name", "Votre nom")
	TranslationServer.add_translation(translation)
	var label := Label.new()
	label.text = "Play"
	var link := LinkButton.new()
	link.text = "Play"
	var line := LineEdit.new()
	line.placeholder_text = "Your name"
	var shown: Array = []
	for node: Control in [label, link, line]:
		shown.append(_shown_text.shown_text(node))
	TranslationServer.remove_translation(translation)
	assert_eq(shown, ["Jouer", "Jouer", "Votre nom"], "each translated as drawn")
	for node: Node in [label, link, line]:
		node.free()


func test_pruned_leaves_out_the_bridge_a_node_being_freed_and_a_tooltip() -> void:
	var snapshot: GDScript = load_bridge_script("godot_mcp_ui_snapshot.gd")
	var bridge := Node.new()
	var menu := Control.new()
	var old_menu := Control.new()
	var tooltip := PopupPanel.new()
	tooltip.theme_type_variation = &"TooltipPanel"
	assert_true(_text_targets.pruned(bridge, bridge, snapshot), "the bridge")
	assert_true(not _text_targets.pruned(menu, bridge, snapshot), "a live menu")
	assert_true(_text_targets.pruned(tooltip, bridge, snapshot), "a tooltip")
	# queue_free outside the tree queues it on the SceneTree, which frees it after the tests.
	old_menu.queue_free()
	assert_true(_text_targets.pruned(old_menu, bridge, snapshot), "an old menu being freed")
	for node: Node in [bridge, menu, tooltip]:
		node.free()


func test_a_miss_quotes_a_long_text_trimmed_and_cut() -> void:
	var long_text: String = "\n  " + "line of the log\n".repeat(200)
	var entries: Array[Dictionary] = [_entry("/root/Log", "Log", long_text)]
	var quoted: String = _text_targets.quoted(long_text)
	assert_eq(quoted.length(), 61, "60 characters and …")
	assert_true(quoted.begins_with("line of the log\nline") and quoted.ends_with("…"), quoted)
	assert_eq(
		_text_targets.miss_refusal("line of the log", entries),
		"no visible Control shows 'line of the log'; near misses: /root/Log shows '%s'." % quoted,
		"the near miss quoted"
	)
	assert_eq(_text_targets.quoted("  Short  "), "Short", "a short text trimmed, not cut")


func test_text_matching_trims_and_keeps_case() -> void:
	var entries: Array[Dictionary] = [
		_entry("/root/A", "A", "  Play  "),
		_entry("/root/B", "B", "play"),
		_entry("/root/C", "C", "Play again"),
	]
	var matched: Array[Dictionary] = _text_targets.matching(entries, "Play")
	assert_eq(matched.size(), 1, "one exact match")
	assert_eq(matched[0]["path"], "/root/A", "the trimmed one, not another case")


func test_near_misses_rank_case_then_containing_then_contained() -> void:
	var entries: Array[Dictionary] = [
		_entry("/root/Contained", "Contained", "Host"),
		_entry("/root/Containing", "Containing", "Host game now"),
		_entry("/root/Blank", "Blank", "   "),
		_entry("/root/Other", "Other", "Quit"),
		_entry("/root/Case", "Case", "HOST GAME"),
	]
	var near: Array = []
	for entry: Dictionary in _text_targets.near_misses(entries, "host game"):
		near.append(entry["path"])
	assert_eq(near, ["/root/Case", "/root/Containing", "/root/Contained"], "ranked, blank left out")


func test_near_misses_stop_at_five() -> void:
	var entries: Array[Dictionary] = []
	for index in 7:
		entries.append(_entry("/root/Card%d" % index, "Card%d" % index, "Strike %d" % index))
	assert_eq(_text_targets.near_misses(entries, "Strike").size(), 5, "at most five")


func test_a_miss_lists_near_misses_and_name_hints() -> void:
	var entries: Array[Dictionary] = [
		_entry("/root/Menu/Host", "Host", "New Game"),
		_entry("/root/Menu/Hosting", "Hosting", "Host game"),
	]
	assert_eq(
		_text_targets.miss_refusal("Host", entries),
		(
			"no visible Control shows 'Host'; near misses: /root/Menu/Hosting shows 'Host game'"
			+ "; /root/Menu/Host is named 'Host' and shows 'New Game'."
		),
		"near misses, then the node of that name"
	)
	assert_eq(
		_text_targets.miss_refusal("Quit", entries),
		"no visible Control shows 'Quit'.",
		"nothing near"
	)


func test_ambiguity_lists_ten_matches_with_their_rects() -> void:
	var matches: Array[Dictionary] = []
	for index in 12:
		var entry: Dictionary = _entry("/root/Pick%d" % index, "Pick%d" % index, "Menu")
		entry["rect"] = "%d,0,10,10" % (index * 10)
		matches.append(entry)
	var refusal: String = _text_targets.ambiguity_refusal("Menu", matches.slice(0, 2))
	assert_eq(
		refusal,
		(
			"'Menu' is shown by 2 Controls: /root/Pick0 at 0,0,10,10, /root/Pick1 at 10,0,10,10; "
			+ "narrow it with under."
		),
		"each match at its rect"
	)
	refusal = _text_targets.ambiguity_refusal("Menu", matches)
	assert_true(refusal.begins_with("'Menu' is shown by 12 Controls: /root/Pick0 at"), refusal)
	assert_true(refusal.ends_with("/root/Pick9 at 90,0,10,10, …; narrow it with under."), refusal)


func _entry(path: String, node_name: String, shown: String) -> Dictionary:
	return {"control": null, "path": path, "name": node_name, "shown": shown}
