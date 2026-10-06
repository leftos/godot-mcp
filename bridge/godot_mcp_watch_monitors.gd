extends RefCounted
## The godot-mcp bridge's watch monitors, a static helper of the watch (godot_mcp_watch.gd):
## resolves a watch's monitor names, reads each monitor once a sampled frame, measures frame_ms and
## the sampler's own time, and at the watch's end summarises each series (nearest-rank percentiles,
## its maximum and the frame of it, its mean and its spikes) and frees it, so no series leaves the
## game. A built-in monitor is named by its path as Performance.get_monitor_name gives it, which is
## not bound to scripts, so NAMES copies 4.7.2's table (main/performance.cpp L165-237) in Monitor
## enum order. frame_ms is the wall time between the stamps of two frames the watch sees; a custom
## monitor is read by its id through Performance.get_custom_monitor, which calls the game's
## callable.

## Every Performance.Monitor constant and its get_monitor_name path in Godot 4.7.2, in enum order.
const NAMES := {
	"TIME_FPS": "time/fps",
	"TIME_PROCESS": "time/process",
	"TIME_PHYSICS_PROCESS": "time/physics_process",
	"TIME_NAVIGATION_PROCESS": "time/navigation_process",
	"MEMORY_STATIC": "memory/static",
	"MEMORY_STATIC_MAX": "memory/static_max",
	"MEMORY_MESSAGE_BUFFER_MAX": "memory/msg_buf_max",
	"OBJECT_COUNT": "object/objects",
	"OBJECT_RESOURCE_COUNT": "object/resources",
	"OBJECT_NODE_COUNT": "object/nodes",
	"OBJECT_ORPHAN_NODE_COUNT": "object/orphan_nodes",
	"RENDER_TOTAL_OBJECTS_IN_FRAME": "raster/total_objects_drawn",
	"RENDER_TOTAL_PRIMITIVES_IN_FRAME": "raster/total_primitives_drawn",
	"RENDER_TOTAL_DRAW_CALLS_IN_FRAME": "raster/total_draw_calls",
	"RENDER_VIDEO_MEM_USED": "video/video_mem",
	"RENDER_TEXTURE_MEM_USED": "video/texture_mem",
	"RENDER_BUFFER_MEM_USED": "video/buffer_mem",
	"PHYSICS_2D_ACTIVE_OBJECTS": "physics_2d/active_objects",
	"PHYSICS_2D_COLLISION_PAIRS": "physics_2d/collision_pairs",
	"PHYSICS_2D_ISLAND_COUNT": "physics_2d/islands",
	"PHYSICS_3D_ACTIVE_OBJECTS": "physics_3d/active_objects",
	"PHYSICS_3D_COLLISION_PAIRS": "physics_3d/collision_pairs",
	"PHYSICS_3D_ISLAND_COUNT": "physics_3d/islands",
	"AUDIO_OUTPUT_LATENCY": "audio/driver/output_latency",
	"NAVIGATION_ACTIVE_MAPS": "navigation/active_maps",
	"NAVIGATION_REGION_COUNT": "navigation/regions",
	"NAVIGATION_AGENT_COUNT": "navigation/agents",
	"NAVIGATION_LINK_COUNT": "navigation/links",
	"NAVIGATION_POLYGON_COUNT": "navigation/polygons",
	"NAVIGATION_EDGE_COUNT": "navigation/edges",
	"NAVIGATION_EDGE_MERGE_COUNT": "navigation/edges_merged",
	"NAVIGATION_EDGE_CONNECTION_COUNT": "navigation/edges_connected",
	"NAVIGATION_EDGE_FREE_COUNT": "navigation/edges_free",
	"NAVIGATION_OBSTACLE_COUNT": "navigation/obstacles",
	"PIPELINE_COMPILATIONS_CANVAS": "pipeline/compilations_canvas",
	"PIPELINE_COMPILATIONS_MESH": "pipeline/compilations_mesh",
	"PIPELINE_COMPILATIONS_SURFACE": "pipeline/compilations_surface",
	"PIPELINE_COMPILATIONS_DRAW": "pipeline/compilations_draw",
	"PIPELINE_COMPILATIONS_SPECIALIZATION": "pipeline/compilations_specialization",
	"NAVIGATION_2D_ACTIVE_MAPS": "navigation_2d/active_maps",
	"NAVIGATION_2D_REGION_COUNT": "navigation_2d/regions",
	"NAVIGATION_2D_AGENT_COUNT": "navigation_2d/agents",
	"NAVIGATION_2D_LINK_COUNT": "navigation_2d/links",
	"NAVIGATION_2D_POLYGON_COUNT": "navigation_2d/polygons",
	"NAVIGATION_2D_EDGE_COUNT": "navigation_2d/edges",
	"NAVIGATION_2D_EDGE_MERGE_COUNT": "navigation_2d/edges_merged",
	"NAVIGATION_2D_EDGE_CONNECTION_COUNT": "navigation_2d/edges_connected",
	"NAVIGATION_2D_EDGE_FREE_COUNT": "navigation_2d/edges_free",
	"NAVIGATION_2D_OBSTACLE_COUNT": "navigation_2d/obstacles",
	"NAVIGATION_3D_ACTIVE_MAPS": "navigation_3d/active_maps",
	"NAVIGATION_3D_REGION_COUNT": "navigation_3d/regions",
	"NAVIGATION_3D_AGENT_COUNT": "navigation_3d/agents",
	"NAVIGATION_3D_LINK_COUNT": "navigation_3d/links",
	"NAVIGATION_3D_POLYGON_COUNT": "navigation_3d/polygons",
	"NAVIGATION_3D_EDGE_COUNT": "navigation_3d/edges",
	"NAVIGATION_3D_EDGE_MERGE_COUNT": "navigation_3d/edges_merged",
	"NAVIGATION_3D_EDGE_CONNECTION_COUNT": "navigation_3d/edges_connected",
	"NAVIGATION_3D_EDGE_FREE_COUNT": "navigation_3d/edges_free",
	"NAVIGATION_3D_OBSTACLE_COUNT": "navigation_3d/obstacles",
}
const FRAME_MS := "frame_ms"
## The most spikes a monitor lists.
const MAX_SPIKES := 20
## The percentiles a summary gives, by key.
const PERCENTILES := {"p50": 50, "p95": 95, "p99": 99}
## The frame rate the frame_ms budget assumes when Engine.max_fps is 0 (no cap).
const DEFAULT_FPS := 60
## The frame_ms budget at a frame rate, in frames: headroom over one frame's length.
const BUDGET_FRAMES := 1.5
## The sampler's mean time a sampled frame past which the timeline warns, in ms.
const COST_WARNING_MS := 2.0
const NOT_A_NUMBER := (
	"Custom monitor '%s' returned a %s, not a number; watch a monitor whose callable returns an "
	+ "int or float."
)
const NO_MONITOR := (
	"No monitor '%s': not one of Godot's %d built-in monitors (such as object/nodes or "
	+ "raster/total_draw_calls), not frame_ms, and no custom monitor has that id; a game "
	+ "registers one with Performance.add_custom_monitor."
)
const MOVIE_WARNING := (
	"the session records with Movie Maker, so frame_ms measures how fast frames render, not what "
	+ "a player sees"
)
const RASTER_WARNING := (
	"no frame was drawn during the watch (minimized, headless or low-processor mode), so "
	+ "raster/* monitors repeat the last drawn frame's values"
)
const COST_WARNING := (
	"sampling took %.2f ms a frame on average (%d %s), which slows the frames it watches; watch "
	+ "fewer tracks"
)


## A watch's monitor state from params.monitors, or the refusal of the first name that names no
## monitor: the monitors in request order (_new_monitor), the frame_ms budget, the last frame's
## stamp, the sampler's time and frames, the frames drawn at the first and last sample, whether
## the session records with Movie Maker (movie_path gives a path; asked only when frame_ms is
## watched), and the summary and warning finish leaves.
static func begin(params: Dictionary, movie_path: Callable) -> Variant:
	var monitors: Array = []
	var names: Variant = params.get("monitors")
	for name: Variant in names if names is Array else []:
		var monitor: Variant = _resolve(str(name))
		if monitor is String:
			return monitor
		monitors.append(monitor)
	var frame_ms: bool = monitors.any(func(monitor: Dictionary) -> bool: return _is_frame(monitor))
	return {
		"monitors": monitors,
		"budget": _budget(params),
		"last_usec": -1,
		"cost_usec": 0,
		"cost_frames": 0,
		"drawn_first": -1,
		"drawn_last": -1,
		"movie": frame_ms and not str(movie_path.call()).is_empty(),
		"summary": [],
		"warning": "",
	}


## frame_ms; a built-in monitor by its table name, which wins over a custom id of that name; else
## a custom monitor, whose first read must be a number; else the refusal of the name.
static func _resolve(name: String) -> Variant:
	if name == FRAME_MS:
		return _new_monitor(name, "frame", 0)
	var constant: Variant = NAMES.find_key(name)
	if constant != null:
		var id: int = ClassDB.class_get_integer_constant("Performance", constant)
		return _new_monitor(name, "builtin", id)
	if not Performance.has_custom_monitor(name):
		return NO_MONITOR % [name, NAMES.size()]
	var first: Variant = Performance.get_custom_monitor(name)
	if not _is_number(first):
		return NOT_A_NUMBER % [name, type_string(typeof(first))]
	return _new_monitor(name, "custom", 0)


## A monitor: its name, its kind (frame, builtin or custom), its Performance.Monitor value for a
## built-in one, its series (each reading and the frame of it) and its non-numeric reads.
static func _new_monitor(name: String, kind: String, id: int) -> Dictionary:
	return {"name": name, "kind": kind, "id": id, "values": [], "frames": [], "non_numeric": 0}


static func _is_frame(monitor: Dictionary) -> bool:
	return monitor["kind"] == "frame"


static func _is_number(value: Variant) -> bool:
	return value is int or value is float


## params.budgetMs, else BUDGET_FRAMES frames at Engine.max_fps (DEFAULT_FPS when 0), in ms to 3
## decimals.
static func _budget(params: Dictionary) -> float:
	if params.has("budgetMs"):
		return snappedf(float(params["budgetMs"]), 0.001)
	var target: int = Engine.max_fps if Engine.max_fps > 0 else DEFAULT_FPS
	return snappedf(BUDGET_FRAMES * 1000.0 / float(target), 0.001)


## Stamps a frame the watch saw and did not sample (a paused one), so the next frame_ms reading
## measures from it rather than across the pause.
static func stamp(state: Dictionary, usec: int) -> void:
	state["last_usec"] = usec


## Reads every monitor at frame, a sampled frame stamped now (usec, read as the frame reached the
## watch): frame_ms as the interval since the last stamp, none on the first; then adds the time the
## frame's sampling took, from began to clock's reading now, to the sampler's cost.
static func sample(state: Dictionary, frame: int, now: int, began: int, clock: Callable) -> void:
	var last: int = state["last_usec"]
	state["last_usec"] = now
	for monitor: Dictionary in state["monitors"]:
		var value: Variant = _read(monitor, now, last)
		if value != null:
			(monitor["values"] as Array).append(value)
			(monitor["frames"] as Array).append(frame)
	var drawn: int = Engine.get_frames_drawn()
	if int(state["drawn_first"]) < 0:
		state["drawn_first"] = drawn
	state["drawn_last"] = drawn
	state["cost_usec"] = int(state["cost_usec"]) + int(clock.call()) - began
	state["cost_frames"] = int(state["cost_frames"]) + 1


## A monitor's reading now, or null for none: frame_ms in ms to 3 decimals from the stamps (none
## without a last one); a built-in one through Performance.get_monitor; a custom one through
## get_custom_monitor, none (counted in non_numeric) when it is not a number or the id is gone.
static func _read(monitor: Dictionary, now: int, last: int) -> Variant:
	if _is_frame(monitor):
		return null if last < 0 else snappedf(float(now - last) / 1000.0, 0.001)
	if monitor["kind"] == "builtin":
		var id: Performance.Monitor = monitor["id"]
		return Performance.get_monitor(id)
	var value: Variant = null
	if Performance.has_custom_monitor(monitor["name"]):
		value = Performance.get_custom_monitor(monitor["name"])
	if _is_number(value):
		return value
	monitor["non_numeric"] = int(monitor["non_numeric"]) + 1
	return null


## Summarises every monitor of watch's state into its summary, frees the series, and joins the
## warnings that apply into its warning.
static func finish(watch: Dictionary) -> void:
	var state: Dictionary = watch["perf"]
	var summary: Array = []
	for monitor: Dictionary in state["monitors"]:
		summary.append(_summarise(monitor, state["budget"]))
		(monitor["values"] as Array).clear()
		(monitor["frames"] as Array).clear()
	state["summary"] = summary
	state["warning"] = "; ".join(_warnings(watch))


## A monitor's summary: {name, samples, custom?, nonNumeric?, over? (frame_ms), and with samples
## p50, p95, p99, max, maxAt, mean, spikes?}.
static func _summarise(monitor: Dictionary, budget: float) -> Dictionary:
	var values: Array = monitor["values"]
	var summary: Dictionary = {"name": monitor["name"], "samples": values.size()}
	if monitor["kind"] == "custom":
		summary["custom"] = true
	if int(monitor["non_numeric"]) > 0:
		summary["nonNumeric"] = monitor["non_numeric"]
	var above: Variant = null
	if _is_frame(monitor):
		above = budget
		summary["over"] = _over(values, budget)
	if not values.is_empty():
		_add_statistics(summary, monitor, above)
	return summary


## {budget, count, frames}: the budget, the readings over it, and the readings.
static func _over(values: Array, budget: float) -> Dictionary:
	var count: int = 0
	for value: float in values:
		if value > budget:
			count += 1
	return {"budget": budget, "count": count, "frames": values.size()}


## Adds the nearest-rank percentiles (index ceil(p * n) - 1 of the ascending readings), max, maxAt
## (the frame of the first maximum), mean and spikes to summary; spikes are the readings above
## above when it is set (frame_ms's budget, an empty list when none is), else the highest, left
## out when the series is constant.
static func _add_statistics(summary: Dictionary, monitor: Dictionary, above: Variant) -> void:
	var values: Array = monitor["values"]
	var sorted: Array = values.duplicate()
	sorted.sort()
	for key: String in PERCENTILES:
		var rank: int = ceili(float(int(PERCENTILES[key]) * sorted.size()) / 100.0)
		summary[key] = sorted[rank - 1]
	summary["max"] = sorted[-1]
	summary["maxAt"] = monitor["frames"][values.find(sorted[-1])]
	var total: float = 0.0
	for value: Variant in values:
		total += float(value)
	var mean: float = total / float(values.size())
	summary["mean"] = snappedf(mean, 0.001) if above != null else mean
	if above != null or sorted[-1] != sorted[0]:
		summary["spikes"] = _spikes(monitor, sorted, above)


## At most MAX_SPIKES readings as [frame, value], highest first and, among equal ones, earliest
## first: those above above when it is set, else the highest. One pass over the series against
## the MAX_SPIKES-th highest reading, so only the readings kept are sorted.
static func _spikes(monitor: Dictionary, sorted: Array, above: Variant) -> Array:
	var values: Array = monitor["values"]
	var frames: Array = monitor["frames"]
	var cut: Variant = sorted[sorted.size() - mini(MAX_SPIKES, sorted.size())]
	var higher: Array = []
	var ties: Array = []
	for index in values.size():
		var value: Variant = values[index]
		if above != null and value <= above:
			continue
		if value > cut:
			higher.append([frames[index], value])
		elif value == cut and ties.size() < MAX_SPIKES:
			ties.append([frames[index], value])
	higher.sort_custom(func(a: Array, b: Array) -> bool: return _higher_first(a, b))
	higher.append_array(ties.slice(0, MAX_SPIKES - higher.size()))
	return higher


## Whether spike a, [frame, value], goes before spike b: a higher value, or an earlier frame.
static func _higher_first(a: Array, b: Array) -> bool:
	return a[1] > b[1] or (a[1] == b[1] and a[0] < b[0])


## The warnings that apply to watch: Movie Maker under frame_ms, raster/* monitors with no frame
## drawn across two samples or more, and a sampler that took over COST_WARNING_MS a sampled frame.
static func _warnings(watch: Dictionary) -> PackedStringArray:
	var state: Dictionary = watch["perf"]
	var monitors: Array = state["monitors"]
	var warnings: PackedStringArray = []
	if state["movie"]:
		warnings.append(MOVIE_WARNING)
	var frames: int = state["cost_frames"]
	var raster: bool = monitors.any(
		func(monitor: Dictionary) -> bool: return str(monitor["name"]).begins_with("raster/")
	)
	if raster and frames >= 2 and int(state["drawn_last"]) == int(state["drawn_first"]):
		warnings.append(RASTER_WARNING)
	var cost_ms: float = float(state["cost_usec"]) / 1000.0 / float(maxi(1, frames))
	if cost_ms > COST_WARNING_MS:
		var tracks: int = (
			(watch["tracks"] as Array).size() + (watch["signals"] as Array).size() + monitors.size()
		)
		warnings.append(COST_WARNING % [cost_ms, tracks, "track" if tracks == 1 else "tracks"])
	return warnings


## Adds start's monitors, [{name, custom?: true}], to reply when the watch has any.
static func describe(reply: Dictionary, state: Dictionary) -> void:
	var monitors: Array = state["monitors"]
	if monitors.is_empty():
		return
	var described: Array = []
	for monitor: Dictionary in monitors:
		var entry: Dictionary = {"name": monitor["name"]}
		if monitor["kind"] == "custom":
			entry["custom"] = true
		described.append(entry)
	reply["monitors"] = described


## Adds the monitors' summaries and the warning finish left to a timeline, each when there is one.
static func add_to(result: Dictionary, state: Dictionary) -> void:
	if not (state["summary"] as Array).is_empty():
		result["monitors"] = state["summary"]
	if not str(state["warning"]).is_empty():
		result["warning"] = state["warning"]
