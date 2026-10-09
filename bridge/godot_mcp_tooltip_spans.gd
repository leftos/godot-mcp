extends RefCounted
## Tooltip spans: the runs of a RichTextLabel's text over which get_tooltip answers a text of its
## own (a [hint]'s description, a meta's tooltip, an image's tooltip, then the label's own
## tooltip_text: scene/gui/rich_text_label.cpp L3412-3431 in 4.7.2), found by probing, since
## Godot 4.7.2 lists neither hints nor character rects (RichTextLabel's class reference has no
## such method; Control.get_tooltip takes a point in the Control's local coordinates). A span is
## {text, rects, aim}: its tooltip text, one rect per line it covers, in reading order, and the
## point of its first run a sample confirmed. Only the part of the label a player sees is probed:
## its text area, its drawn content, the viewport and every clipping ancestor. Item targets
## (godot_mcp_item_targets.gd) aim at one; the hover's near-miss warning names the nearest.
## Static functions called on the script itself.

## How many spans a refusal lists, as the item targets' MAX_LISTED.
const MAX_LISTED := 10
## No span reads the text: the label's path, the text, the spans as listed gives them.
const NO_SPAN := "%s has no tooltip span '%s'; spans: %s"
## An index out of range: the label's path, the index, the span count, the spans listed.
const NO_SPAN_INDEX := "%s has no tooltip span %d; it has %d: %s"
## Several spans read the text: the label's path, the count, the text, each "index N at x,y,w,h".
const AMBIGUOUS_SPAN := "%s has %d tooltip spans reading '%s': %s; narrow with item.index"
## A hover that found no tooltip over a label with spans: the point, the label's path, the
## nearest span's index, text and first rect, the distance to it, the index again.
const NEAR_MISS := (
	"no tooltip at (%s) on %s; its nearest tooltip span is index %d '%s' at %s, %s px away; "
	+ "aim at it with item {index: %d}"
)
## The most get_tooltip calls one probe makes: a call walks the label's paragraphs from the first
## shown one to the one it hits, or to the label's bottom on a miss (_find_click,
## scene/gui/rich_text_label.cpp L1795-1848 in 4.7.2), and the probe runs on the main thread.
const SAMPLE_BUDGET := 20000
## The longest one probe runs, in milliseconds of Time.get_ticks_usec, since a call's walk grows
## with the label's paragraphs and SAMPLE_BUDGET alone leaves a long label's probe unbounded.
const TIME_BUDGET_MS := 250
## A probe that ran out of SAMPLE_BUDGET or TIME_BUDGET_MS: the label's path, the samples taken,
## the whole milliseconds spent, the point it stopped at.
const PROBE_STOPPED := (
	"the tooltip span probe of %s stopped after %d samples in %d ms, "
	+ "so spans past (%s) were not looked for."
)
## The heights, as fractions of a line's height from its top, at which each x is sampled: the
## middle on a line no taller than the normal font's; on a taller one, then low for a small glyph
## on a deep baseline beside a large font, then high for a glyph or image the middle misses.
const PLAIN_HEIGHTS: Array[float] = [0.5]
const TALL_HEIGHTS: Array[float] = [0.5, 0.75, 0.25]


## One probe's get_tooltip calls over a label: the samples it may take and has taken, when it
## started and must end (Time.get_ticks_usec), and, once either budget ran out (null before),
## where: {point, samples, ms}, the local point it did not sample, the samples taken and the
## whole milliseconds spent.
class Sampler:
	extends RefCounted
	var label: RichTextLabel
	var budget: int
	var taken: int = 0
	var started: int
	var deadline: int
	var stopped: Variant = null

	func _init(probed: RichTextLabel, samples: int, time_ms: int) -> void:
		label = probed
		budget = samples
		started = Time.get_ticks_usec()
		deadline = started + time_ms * 1000

	## The tooltip span text at point, stripped, "" when get_tooltip answers nothing of the
	## span's own (empty, or the label's tooltip_text); null once a budget ran out.
	func tip(point: Vector2) -> Variant:
		if stopped != null:
			return null
		var now: int = Time.get_ticks_usec()
		if taken >= budget or now >= deadline:
			stopped = {"point": point, "samples": taken, "ms": int((now - started) / 1000.0)}
			return null
		taken += 1
		var answer: String = label.get_tooltip(point)
		if answer == label.tooltip_text:
			return ""
		return answer.strip_edges()


## The label's spans in its local space, from the lines shown in its probed region (_region),
## as {spans, stopped}: stopped is null, or {point, samples, ms} where SAMPLE_BUDGET or
## TIME_BUDGET_MS ran out (Sampler).
static func probe(label: RichTextLabel) -> Dictionary:
	return _scan(label, null)


## The spans a near-miss warning weighs for a local point, as probe gives them: the lines in
## reading order down to the first below point whose vertical distance from it is at least the
## nearest span's found so far, so the nearest, and its index, are the ones probe would give.
static func probe_near(label: RichTextLabel, point: Vector2) -> Dictionary:
	return _scan(label, point)


static func _scan(label: RichTextLabel, point: Variant) -> Dictionary:
	var area: Rect2 = text_area(label)
	var region: Rect2 = _region(label, area)
	var sampler := Sampler.new(label, SAMPLE_BUDGET, TIME_BUDGET_MS)
	var plain: float = _plain_height(label)
	var runs: Array = []
	var best: float = INF
	for entry: Dictionary in _bands(label, area, region):
		if point != null and _past(entry["band"], point, best):
			break
		var line_runs: Array = _line_runs(sampler, entry, region, plain)
		runs.append_array(line_runs)
		best = minf(best, _nearest_distance(line_runs, point))
		if sampler.stopped != null:
			break
	return {"spans": joined(runs), "stopped": sampler.stopped}


## The rect of the label its text is laid out and hit-tested in, local: its own less its normal
## stylebox's margins (_get_text_rect, scene/gui/rich_text_label.cpp L205-207 in 4.7.2) and,
## while shown, its vertical scroll bar, on the left under a right-to-left layout, where the
## text shifts right by its width (_find_click_in_line, L1887-1908).
static func text_area(label: RichTextLabel) -> Rect2:
	var style: StyleBox = label.get_theme_stylebox(&"normal")
	var area := Rect2(style.get_offset(), label.size - style.get_minimum_size())
	var bar: VScrollBar = label.get_v_scroll_bar()
	if bar.visible:
		var width: float = bar.get_combined_minimum_size().x
		area.size.x -= width
		if label.is_layout_rtl():
			area.position.x += width
	return area


## The local rect a probe samples: area within the label's drawn glyphs and images
## (get_visible_content_rect, which leaves out text a typewriter has not revealed yet:
## scene/gui/rich_text_label.cpp L1253, L1652 in 4.7.2), the viewport's visible rect and the rect
## of every ancestor Control that clips its contents, up to a top-level one.
static func _region(label: RichTextLabel, area: Rect2) -> Rect2:
	var region: Rect2 = area.intersection(Rect2(label.get_visible_content_rect()))
	var to_local: Transform2D = label.get_global_transform_with_canvas().affine_inverse()
	region = region.intersection(to_local * label.get_viewport().get_visible_rect())
	var node: Node = label
	while node is CanvasItem and not (node as CanvasItem).is_set_as_top_level():
		node = node.get_parent()
		var clipper := node as Control
		if clipper != null and clipper.clip_contents:
			var clip: Rect2 = (
				clipper.get_global_transform_with_canvas() * Rect2(Vector2(), clipper.size)
			)
			region = region.intersection(to_local * clip)
	return region


## The lines whose band overlaps region, each {line, band}, band its {top, height} in local
## space: top as _find_click places it, from the first line inside region.
static func _bands(label: RichTextLabel, area: Rect2, region: Rect2) -> Array:
	var bands: Array = []
	if not region.has_area():
		return bands
	var spread: Vector2 = _spread(label, area)
	var base: float = area.position.y + spread.x - _scroll(label)
	var count: int = label.get_line_count()
	var line: int = _first_line(label, Vector3(base, spread.y, region.position.y), count)
	while line < count:
		var top: float = base + _content_top(label, line) + line * spread.y
		if top >= region.end.y:
			break
		bands.append({"line": line, "band": Vector2(top, label.get_line_height(line))})
		line += 1
	return bands


## The first of count lines whose bottom lies below at.z, by bisection, the lines placed from
## at.x with at.y between each two; count when none does.
static func _first_line(label: RichTextLabel, at: Vector3, count: int) -> int:
	var low: int = 0
	var high: int = count
	while low < high:
		var middle: int = (low + high) >> 1
		var top: float = at.x + _content_top(label, middle) + middle * at.y
		if top + label.get_line_height(middle) > at.z:
			high = middle
		else:
			low = middle + 1
	return low


## A line's top in the label's content: its paragraph's offset for a paragraph's first line, since
## get_line_offset leaves paragraph_separation out there (scene/gui/rich_text_label.cpp L6839 in
## 4.7.2); else get_line_offset, the paragraph's offset and the get_line_height of each line
## above it in the paragraph.
static func _content_top(label: RichTextLabel, line: int) -> float:
	var paragraph: int = label.get_character_paragraph(label.get_line_range(line).x)
	if paragraph < 0:
		return label.get_line_offset(line)
	if line == 0 or paragraph != label.get_character_paragraph(label.get_line_range(line - 1).x):
		return label.get_paragraph_offset(paragraph)
	return label.get_line_offset(line)


## The vertical scroll as the label's hit test takes it, whole pixels (_find_click,
## scene/gui/rich_text_label.cpp L1795 in 4.7.2).
static func _scroll(label: RichTextLabel) -> float:
	return float(int(label.get_v_scroll_bar().value))


## How the vertical alignment places lines when the text is shorter than area, as _find_click
## does (scene/gui/rich_text_label.cpp L1801-1836 in 4.7.2, whose total height
## get_content_height returns): {how far the first line moves down, the room added below each
## line}. Center and bottom move it; fill spreads the spare room between the lines.
static func _spread(label: RichTextLabel, area: Rect2) -> Vector2:
	var spare: float = area.size.y - label.get_content_height()
	if spare <= 0.0:
		return Vector2.ZERO
	match label.vertical_alignment:
		VERTICAL_ALIGNMENT_CENTER:
			return Vector2(spare / 2.0, 0.0)
		VERTICAL_ALIGNMENT_BOTTOM:
			return Vector2(spare, 0.0)
		VERTICAL_ALIGNMENT_FILL:
			var lines: int = label.get_line_count()
			return Vector2(0.0, spare / (lines - 1)) if lines > 1 else Vector2.ZERO
	return Vector2.ZERO


## The height of a line of the normal font alone: its height and the line separation, as
## get_line_height counts a line.
static func _plain_height(label: RichTextLabel) -> float:
	var font: Font = label.get_theme_font(&"normal_font")
	var font_size: int = label.get_theme_font_size(&"normal_font_size")
	return font.get_height(font_size) + label.get_theme_constant(&"line_separation")


## Whether a line at band ({top, height}) lies below point by at least best.
static func _past(band: Vector2, point: Vector2, best: float) -> bool:
	return band.x > point.y and band.x - point.y >= best


## The runs of the line at entry ({line, band}), sampled at each whole x of region and the
## heights of _sample_ys; cut short when the sampler's budget runs out.
static func _line_runs(sampler: Sampler, entry: Dictionary, region: Rect2, plain: float) -> Array:
	var band: Vector2 = entry["band"]
	var ys: PackedFloat32Array = _sample_ys(band, region, plain)
	var left: int = ceili(region.position.x)
	var tips := PackedStringArray()
	var answered := PackedFloat32Array()
	for x in range(left, ceili(region.end.x)):
		var found: Array = _tip_at(sampler, x, ys)
		if found.is_empty():
			break
		tips.append(found[0])
		answered.append(found[1])
	return runs_of(tips, answered, left, entry["line"], band)


## The ys of band ({top, height}) inside region a line is sampled at: PLAIN_HEIGHTS on a line no
## taller than plain, else TALL_HEIGHTS.
static func _sample_ys(band: Vector2, region: Rect2, plain: float) -> PackedFloat32Array:
	var heights: Array[float] = PLAIN_HEIGHTS if band.y <= plain else TALL_HEIGHTS
	var ys := PackedFloat32Array()
	for height: float in heights:
		var y: float = floorf(band.x + band.y * height)
		if y >= region.position.y and y < region.end.y:
			ys.append(y)
	return ys


## The first span text at x over ys and the y that answered it, ["", -1] for none; [] once the
## sampler's budget runs out.
static func _tip_at(sampler: Sampler, x: int, ys: PackedFloat32Array) -> Array:
	for y: float in ys:
		var tip: Variant = sampler.tip(Vector2(x, y))
		if not tip is String:
			return []
		if not (tip as String).is_empty():
			return [tip, y]
	return ["", -1.0]


## The distance from point to the nearest of runs' rects; INF for none or a null point.
static func _nearest_distance(runs: Array, point: Variant) -> float:
	var best: float = INF
	if point == null:
		return best
	for run: Dictionary in runs:
		best = minf(best, distance_to(run["rect"], point))
	return best


## The runs of one line: tips[i] is the tooltip at x = left + i ("" for none) and answered[i] the
## y whose sample answered it, band the line's {top, height}; each run of equal, non-empty tips
## is {text, line, rect, aim}, aim the centre of its first stretch of samples answered at one y.
static func runs_of(
	tips: PackedStringArray, answered: PackedFloat32Array, left: float, line: int, band: Vector2
) -> Array:
	var runs: Array = []
	var start: int = 0
	for index in tips.size() + 1:
		var tip: String = tips[index] if index < tips.size() else ""
		if index > 0 and tip == tips[index - 1]:
			continue
		if index > 0 and not tips[index - 1].is_empty():
			var rect := Rect2(left + start, band.x, index - start, band.y)
			var aim: Vector2 = _aim(answered, left, start, index)
			runs.append({"text": tips[index - 1], "line": line, "rect": rect, "aim": aim})
		start = index
	return runs


## The centre of the stretch of answered[start, end) from start whose samples answered at
## answered[start]'s y, at that y; x counts from left.
static func _aim(answered: PackedFloat32Array, left: float, start: int, end: int) -> Vector2:
	var y: float = answered[start]
	var stop: int = start + 1
	while stop < end and answered[stop] == y:
		stop += 1
	return Vector2(left + (start + stop) / 2.0, y)


## Runs in reading order joined into spans: a run that opens a line continues the span the last
## run of the line before closed when both read the same text, as a span that wraps does; a span
## aims where its first run does.
static func joined(runs: Array) -> Array:
	var joined_spans: Array = []
	for index in runs.size():
		var run: Dictionary = runs[index]
		var previous: Dictionary = runs[index - 1] if index > 0 else {}
		var wraps: bool = (
			not previous.is_empty()
			and int(previous["line"]) + 1 == int(run["line"])
			and previous["text"] == run["text"]
		)
		if wraps:
			(joined_spans.back()["rects"] as Array).append(run["rect"])
		else:
			joined_spans.append({"text": run["text"], "rects": [run["rect"]], "aim": run["aim"]})
	return joined_spans


## Copies of spans with every rect and the aim mapped through xform.
static func placed(given: Array, xform: Transform2D) -> Array:
	var moved: Array = []
	for span: Dictionary in given:
		var rects: Array = []
		for rect: Rect2 in span["rects"]:
			rects.append(xform * rect)
		moved.append(
			{"text": span["text"], "rects": rects, "aim": xform * (span["aim"] as Vector2)}
		)
	return moved


## The index of the span spec names among the spans of the label at path: by index, or the one
## whose text, trimmed, is spec's text trimmed; or a String saying why there is none. texts is
## the resolver or its script, whose rect_text prints the rects.
static func pick(path: String, given: Array, spec: Dictionary, texts: Variant) -> Variant:
	if spec.has("index"):
		return _pick_index(path, given, int(spec["index"]), texts)
	var wanted: String = str(spec["text"]).strip_edges()
	var matches: Array[int] = []
	for index in given.size():
		if str(given[index]["text"]).strip_edges() == wanted:
			matches.append(index)
	if matches.size() == 1:
		return matches[0]
	if matches.is_empty():
		return NO_SPAN % [path, wanted, listed(given, texts)]
	return AMBIGUOUS_SPAN % [path, matches.size(), wanted, _listed_matches(given, matches, texts)]


static func _pick_index(path: String, given: Array, index: int, texts: Variant) -> Variant:
	if index < 0 or index >= given.size():
		return NO_SPAN_INDEX % [path, index, given.size(), listed(given, texts)]
	return index


## Spans as a refusal lists them: at most MAX_LISTED as "index N 'text' at x,y,w,h" (the first
## rect), joined with ", ", and … when there are more; "none" when there are none.
static func listed(given: Array, texts: Variant) -> String:
	if given.is_empty():
		return "none"
	var shown := PackedStringArray()
	for index in mini(given.size(), MAX_LISTED):
		var rect: String = texts.rect_text(given[index]["rects"][0])
		shown.append("index %d '%s' at %s" % [index, given[index]["text"], rect])
	if given.size() > MAX_LISTED:
		shown.append("…")
	return ", ".join(shown)


static func _listed_matches(given: Array, matches: Array[int], texts: Variant) -> String:
	var shown := PackedStringArray()
	for index: int in matches.slice(0, MAX_LISTED):
		shown.append("index %d at %s" % [index, texts.rect_text(given[index]["rects"][0])])
	if matches.size() > MAX_LISTED:
		shown.append("…")
	return ", ".join(shown)


## NEAR_MISS for the span nearest point among the spans of the label at path, all in one space;
## "" when there are none. texts is the resolver or its script, whose num, point_text and
## rect_text print the numbers.
static func near_miss_warning(path: String, point: Vector2, given: Array, texts: Variant) -> String:
	var nearest: int = -1
	var distance: float = INF
	for index in given.size():
		for rect: Rect2 in given[index]["rects"]:
			var away: float = distance_to(rect, point)
			if away < distance:
				nearest = index
				distance = away
	if nearest < 0:
		return ""
	var first: String = texts.rect_text(given[nearest]["rects"][0])
	var facts: Array = [texts.point_text(point), path, nearest, given[nearest]["text"], first]
	return NEAR_MISS % (facts + [texts.num(distance), nearest])


## PROBE_STOPPED for the label at path when its probe stopped ({point, samples, ms}, the point
## local and mapped through xform); "" when it did not (stopped null). texts prints the point, as
## near_miss_warning.
static func stopped_warning(
	path: String, stopped: Variant, xform: Transform2D, texts: Variant
) -> String:
	if stopped == null:
		return ""
	var at: String = texts.point_text(xform * (stopped["point"] as Vector2))
	return PROBE_STOPPED % [path, int(stopped["samples"]), int(stopped["ms"]), at]


## text and then sentence, joined with "; ", either alone when the other is empty.
static func and_then(text: String, sentence: String) -> String:
	if sentence.is_empty():
		return text
	if text.is_empty():
		return sentence
	return "%s; %s" % [text, sentence]


## How far point lies from rect, 0 inside it.
static func distance_to(rect: Rect2, point: Vector2) -> float:
	var dx: float = maxf(maxf(rect.position.x - point.x, point.x - rect.end.x), 0.0)
	var dy: float = maxf(maxf(rect.position.y - point.y, point.y - rect.end.y), 0.0)
	return Vector2(dx, dy).length()
