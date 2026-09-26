extends ColorRect
## A drop target: accepts anything, counts the drops and shows the last one as "dropped:<data>".

var drop_count: int = 0


func _can_drop_data(_at_position: Vector2, _data: Variant) -> bool:
	return true


func _drop_data(_at_position: Vector2, data: Variant) -> void:
	drop_count += 1
	($DropLabel as Label).text = "dropped:%s" % str(data)
