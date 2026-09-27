class_name JsonTestResource
extends Resource
## A script-class Resource for test_json.gd's script-class conversions. The project is imported
## before the tests run (run.ps1 gdtest), so its class_name reaches ProjectSettings' global class
## list and from_json finds the class by its name as well as loading it by its path.

@export var amount: int = 0
