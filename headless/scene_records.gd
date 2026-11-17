extends RefCounted
## A packed scene's node records as `_bundled` holds them: each record's head fields, its (name
## index, value index) pairs into names and variants, and its groups, read and rewritten as 4.7.2
## packed_scene.cpp get_bundled_scene writes them (L1811-1831).

## A packed node record's fields before its property count: parent, owner, type, name, instance.
const RECORD_HEAD := 5
## The bits of a stored property's name index that index names; bit 30 marks a deferred node path.
const PROPERTY_NAME_MASK := (1 << 30) - 1


## bundled with each node record's (name index, value index) pairs replaced by what rewrite
## (bundled, record index, the record's head fields, its pairs) returns for them; the rest of each
## record as it was.
static func with_pairs(bundled: Dictionary, rewrite: Callable) -> Dictionary:
	var nodes: PackedInt32Array = bundled["nodes"]
	var rewritten: PackedInt32Array = []
	var at: int = 0
	for record in int(bundled["node_count"]):
		var head: PackedInt32Array = nodes.slice(at, at + RECORD_HEAD)
		var pairs_start: int = at + RECORD_HEAD + 1
		var groups_start: int = pairs_start + nodes[at + RECORD_HEAD] * 2
		var groups_end: int = groups_start + 1 + nodes[groups_start]
		var pairs: PackedInt32Array = rewrite.call(
			bundled, record, head, nodes.slice(pairs_start, groups_start)
		)
		rewritten.append_array(head)
		rewritten.append(int(pairs.size() / 2.0))
		rewritten.append_array(pairs)
		rewritten.append_array(nodes.slice(groups_start, groups_end))
		at = groups_end
	bundled["nodes"] = rewritten
	return bundled


## pairs with a pair for property holding value, placed before the script's pair: instantiation
## sets the pairs in order, and an engine property set once the script is attached reaches the
## script's member instead.
static func with_pair(
	bundled: Dictionary, pairs: PackedInt32Array, property: String, value: Variant
) -> PackedInt32Array:
	var pair: PackedInt32Array = [name_index(bundled, property), variant_index(bundled, value)]
	var at: int = pair_at(bundled["names"], pairs, "script")
	if at < 0:
		return pairs + pair
	return pairs.slice(0, at) + pair + pairs.slice(at)


## The offset in pairs of the pair whose name is property, or -1.
static func pair_at(names: PackedStringArray, pairs: PackedInt32Array, property: String) -> int:
	for at in range(0, pairs.size(), 2):
		if names[pairs[at] & PROPERTY_NAME_MASK] == property:
			return at
	return -1


## The index of property in bundled's names, appended when absent.
static func name_index(bundled: Dictionary, property: String) -> int:
	var names: PackedStringArray = bundled["names"]
	var index: int = names.find(property)
	if index < 0:
		index = names.size()
		names.append(property)
		bundled["names"] = names
	return index


## The index of value appended to bundled's variants.
static func variant_index(bundled: Dictionary, value: Variant) -> int:
	var variants: Array = bundled["variants"]
	variants.append(value)
	return variants.size() - 1
