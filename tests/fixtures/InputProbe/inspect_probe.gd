extends Node2D
## The inspection tests' probe, added under the root by the tests themselves: typed variables to
## read and set, and methods to call, one of them a coroutine.

var count: int = 0
var label_text: String = "start"
var offset: Vector2
var numbers: Array[int] = []
var points: PackedVector2Array
## Untyped: set_property keeps the type the value already has.
var target = Vector2.ZERO
var loose = 3
## Clamped to 0-10 by its setter, so a set of 50 reads back 10.
var level: int = 1:
	set(value):
		level = clampi(value, 0, 10)


func probe_add(a: int, b: int) -> int:
	return a + b


func probe_scaled(n: int, factor: int = 2) -> int:
	return n * factor


func probe_sum(values: Array[int]) -> int:
	var total: int = 0
	for value in values:
		total += value
	return total


func probe_long() -> String:
	return "x".repeat(3000)


## Calls a missing method through callv, which logs Godot's "Error calling method from
## 'callv'", and returns 3 all the same.
func probe_inner_callv() -> int:
	callv("probe_missing", [])
	return 3


## Returns twice n a frame after it is called, so a caller gets a coroutine's state first.
func probe_later(n: int) -> int:
	await get_tree().process_frame
	return n * 2
