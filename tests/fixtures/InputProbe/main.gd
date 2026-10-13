extends Control
## The InputProbe's root: reports that the scene is up and which user arguments the game received,
## gives MenuA the focus the gamepad tests move, plays a tone when ToneButton is pressed, and
## writes an exit marker as it leaves the tree.

## The user argument naming the file written as the scene leaves the tree: a game that quits
## leaves it, one that is killed does not.
const EXIT_MARKER_ARG := "--exit-marker="

## The tone ToneButton plays, built here so the fixture imports no audio file: a 440 Hz sine,
## 16-bit mono at 44.1 kHz, about 1 s long, at half of full scale.
const TONE_RATE := 44100
const TONE_HZ := 440.0
const TONE_SECONDS := 1.0
const TONE_AMPLITUDE := 0.5


func _ready() -> void:
	($Menu/MenuA as Button).grab_focus()
	var player := $TonePlayer as AudioStreamPlayer
	player.stream = _tone()
	($ToneButton as Button).pressed.connect(player.play)
	print("[probe] ready args=%s" % JSON.stringify(OS.get_cmdline_user_args()))


func _exit_tree() -> void:
	for arg: String in OS.get_cmdline_user_args():
		if not arg.begins_with(EXIT_MARKER_ARG):
			continue
		var path: String = arg.trim_prefix(EXIT_MARKER_ARG)
		var file := FileAccess.open(path, FileAccess.WRITE)
		if file == null:
			push_error("probe: cannot write the exit marker %s" % path)
			return
		file.store_string("exited")


## The tone as 16-bit little-endian samples in an AudioStreamWAV.
func _tone() -> AudioStreamWAV:
	var count := int(TONE_RATE * TONE_SECONDS)
	var data := PackedByteArray()
	data.resize(count * 2)
	for i: int in count:
		var level := sin(TAU * TONE_HZ * float(i) / TONE_RATE) * TONE_AMPLITUDE
		data.encode_s16(i * 2, int(level * 32767.0))
	var wav := AudioStreamWAV.new()
	wav.format = AudioStreamWAV.FORMAT_16_BITS
	wav.mix_rate = TONE_RATE
	wav.stereo = false
	wav.data = data
	return wav


## Raises an error from the fixture's own script, for the test that run_script tells errors
## located in the script it runs from errors raised elsewhere.
func probe_push_error() -> void:
	push_error("probe fixture error")
