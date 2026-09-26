extends Button
## A 12 x 12 button that counts its presses.

var press_count: int = 0


func _ready() -> void:
	pressed.connect(func() -> void: press_count += 1)
