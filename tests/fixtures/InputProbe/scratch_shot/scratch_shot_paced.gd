extends "res://scratch/scratch_probe.gd"
## One paced step that changes the scene's state, holds it for 0.5 s of game time and restores it,
## as a scratch that checks a state and puts it back does: a full-screen rect turns red with
## shot_ready true, then blue again with shot_ready false. Its pace (2 s, the profile's) outlasts
## the restore, so a shot at the end of the pace shows blue. Step 1 reports the game ms since
## step 0's PlayStep.

var shot_ready: bool = false
var _rect: ColorRect
var _game_ms: float = 0.0
var _flashed_at: float = 0.0


func _init() -> void:
	add_step("flash", _flash)
	add_step("elapsed", _elapsed)


func _ready() -> void:
	_rect = ColorRect.new()
	_rect.color = Color.BLUE
	_rect.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(_rect)


func _process(delta: float) -> void:
	_game_ms += delta * 1000.0


func _flash() -> void:
	_flashed_at = _game_ms
	_rect.color = Color.RED
	shot_ready = true
	set_note("red")
	await get_tree().create_timer(0.5).timeout
	_rect.color = Color.BLUE
	shot_ready = false


func _elapsed() -> void:
	set_note("%d" % int(_game_ms - _flashed_at))
