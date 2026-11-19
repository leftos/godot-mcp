extends Node
## A node reporting a tooltip the game draws itself, in the mcp_tooltip group from _ready: its
## _mcp_state answers {pending: true} for its first pending_reads reads, then tooltip_state, the
## value the test sets; while show_after_ms is set, it answers {pending: true, dueMs: the time
## left} until that many milliseconds have passed since its first read, then falls through to the
## rest. reads counts every read and frames holds the process frame of each; the read numbered
## free_on_read frees the node at free_path first.

var tooltip_state: Variant = null
var pending_reads: int = 0
var show_after_ms: int = 0
var reads: int = 0
var frames: Array = []
var free_on_read: int = 0
var free_path: String = ""

## The tick of the first read while show_after_ms is set, -1 until then.
var _first_read_ms: int = -1


func _ready() -> void:
	add_to_group("mcp_tooltip")


func _mcp_state() -> Variant:
	reads += 1
	frames.append(Engine.get_process_frames())
	if reads == free_on_read and has_node(free_path):
		get_node(free_path).free()
	if show_after_ms > 0:
		if _first_read_ms < 0:
			_first_read_ms = Time.get_ticks_msec()
		var waited: int = Time.get_ticks_msec() - _first_read_ms
		if waited < show_after_ms:
			return {"pending": true, "dueMs": show_after_ms - waited}
	if reads <= pending_reads:
		return {"pending": true}
	return tooltip_state
