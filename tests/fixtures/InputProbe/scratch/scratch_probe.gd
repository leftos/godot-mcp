extends Node
## The scratch protocol run_scratches plays, for the scratch scenes beside it (ScratchRunnerTests):
## each scene's script adds its steps in _init, PlayStep plays one, and GetStatus answers the
## step's name and the note the step left with set_note.

var _names: PackedStringArray = []
var _steps: Array[Callable] = []
var _current: String = ""
var _note: String = ""


## Adds a step, played as the next index.
func add_step(step_name: String, step: Callable) -> void:
	_names.append(step_name)
	_steps.append(step)


## Notes what the step did: GetStatus's second line, and a [scratch] line on stdout.
func set_note(note: String) -> void:
	_note = note
	print("[scratch] %s" % note)


func GetStepCount() -> int:
	return _steps.size()


func GetStepName(index: int) -> String:
	return _names[index]


func PlayStep(index: int) -> void:
	_current = _names[index]
	_note = ""
	_steps[index].call()


func GetStatus() -> String:
	return "%s\n%s" % [_current, _note]
