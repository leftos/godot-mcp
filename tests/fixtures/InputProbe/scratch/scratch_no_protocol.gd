extends Node
## A scene whose root has every scratch protocol method but GetStatus: red before any step.


func GetStepCount() -> int:
	return 1


func GetStepName(_index: int) -> String:
	return "only"


func PlayStep(_index: int) -> void:
	pass
