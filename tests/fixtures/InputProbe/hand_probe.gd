extends Control
## A hand of cards for the input tools' element checks: a hidden CardSlot/CardFace with a visible
## unnamed card (@PanelContainer@N) laid over its place and another beside it, a Button whose
## centre another Control covers, two Buttons named Twin in different branches, a Button whose
## Label child ignores the mouse, a Button on a CanvasLayer offset 560 px to the right, a Button
## its parent Tray clips away entirely, and a drag source. Records in presses the name of every
## Control a mouse press reaches.

## The names of the Controls mouse presses reached, in order.
var presses: Array[String] = []
## The paths of the visible cards _ready adds, in order.
var cards: Array[String] = []


func _ready() -> void:
	var monsters: Control = $ViewerMonsters
	for index in 2:
		var card := PanelContainer.new()
		card.position = Vector2(index * 70, 0)
		card.size = Vector2(60, 50)
		monsters.add_child(card)
		cards.append(str(card.get_path()))
	_record_presses(self)


func _record_presses(node: Node) -> void:
	for child: Node in node.get_children():
		if child is Control:
			(child as Control).gui_input.connect(_on_gui_input.bind(str(child.name)))
		_record_presses(child)


func _on_gui_input(event: InputEvent, control_name: String) -> void:
	if event is InputEventMouseButton and (event as InputEventMouseButton).pressed:
		presses.append(control_name)
