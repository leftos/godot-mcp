extends Button
## A 12 x 12 button that counts its presses; with fail_on_press set, each press also raises a
## script error, for the test that input tools report their handlers' errors.

var press_count: int = 0
var fail_on_press: bool = false


func _ready() -> void:
	pressed.connect(_on_pressed)


func _on_pressed() -> void:
	press_count += 1
	if fail_on_press:
		var missing: Object = null
		missing.call("free")
