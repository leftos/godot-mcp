extends RefCounted
## The text a Control shows, as get_ui_elements reports it and a {text} input target matches it.
## Static functions called on the script itself.

## The character a secret LineEdit draws when its secret_character is empty
## (scene/gui/line_edit.cpp L3109 in 4.7.2).
const DEFAULT_SECRET := "•"


## The text control draws, or null for a Control that shows none of its own: a Button's (and its
## subclasses': CheckBox, CheckButton, MenuButton, OptionButton), a Label's or a LinkButton's text
## as translated for drawing, a LineEdit's as _line_edit_text gives it, a RichTextLabel's text
## without its BBCode.
static func shown_text(control: Control) -> Variant:
	if control is OptionButton:
		return _option_text(control as OptionButton)
	if control is Button or control is LinkButton:
		return control.atr(str(control.get("text")))
	if control is Label:
		return _label_text(control as Label)
	if control is LineEdit:
		return _line_edit_text(control as LineEdit)
	if control is RichTextLabel:
		return (control as RichTextLabel).get_parsed_text()
	return null


## A Label draws its translated text in upper case, as its language's text server rules say, when
## uppercase is on (scene/gui/label.cpp L170 in 4.7.2).
static func _label_text(label: Label) -> String:
	var shown: String = label.atr(label.text)
	if not label.uppercase:
		return shown
	return TextServerManager.get_primary_interface().string_to_upper(shown, label.language)


## A LineEdit draws its translated placeholder while its text is empty, and a secret one its
## secret character's first (DEFAULT_SECRET when empty) once per character of its text
## (scene/gui/line_edit.cpp L3106-3110 in 4.7.2).
static func _line_edit_text(line: LineEdit) -> String:
	if line.text.is_empty():
		return line.atr(line.placeholder_text)
	if not line.secret:
		return line.text
	var secret: String = (
		DEFAULT_SECRET if line.secret_character.is_empty() else line.secret_character
	)
	return secret.left(1).repeat(line.text.length())


## An OptionButton translates its text as its selected item's auto_translate_mode says, the
## button's own mode when that item inherits (scene/gui/option_button.cpp L506-523 in 4.7.2).
static func _option_text(option: OptionButton) -> String:
	var selected: int = option.selected
	if selected < 0 or selected >= option.item_count:
		return option.atr(option.text)
	match option.get_popup().get_item_auto_translate_mode(selected):
		Node.AUTO_TRANSLATE_MODE_ALWAYS:
			return option.tr(option.text)
		Node.AUTO_TRANSLATE_MODE_DISABLED:
			return option.text
	return option.atr(option.text)
