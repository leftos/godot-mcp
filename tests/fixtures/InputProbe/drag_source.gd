extends ColorRect
## A drag source: dragging it carries its own name, with a label as the preview.


func _get_drag_data(_at_position: Vector2) -> Variant:
	var preview := Label.new()
	preview.text = str(name)
	set_drag_preview(preview)
	return str(name)
