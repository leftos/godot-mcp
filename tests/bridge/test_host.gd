extends "res://gd_test.gd"
## The warm headless host's pure parts (headless/host.gd): reading frames off the wire, the reply
## to each command, and the start slice every headless reply carries in its engineErrors.

const HOST_SCRIPT := "../../headless/host.gd"
const HEADLESS_SCRIPT := "../../headless/operations.gd"

var _host: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(HOST_SCRIPT).simplify_path()
)
var _ops: GDScript = load(
	ProjectSettings.globalize_path("res://").path_join(HEADLESS_SCRIPT).simplify_path()
)
## The error log operations.gd keeps its entries in, and a request answers from.
var _error_log: GDScript = _ops.get_script_constant_map()["ErrorLog"]


func test_a_whole_frame_is_taken() -> void:
	var taken: Dictionary = _host.take_frames(_frame('{"id": 1}'))
	assert_eq(taken["frames"], ['{"id": 1}'], "the frame's text")
	assert_eq(taken["rest"].size(), 0, "nothing left over")
	assert_eq(taken["error"], "", "no error")


func test_a_frame_split_across_reads_waits_for_its_rest() -> void:
	var bytes: PackedByteArray = _frame('{"id": 2, "command": "ping"}')
	var first: Dictionary = _host.take_frames(bytes.slice(0, 6))
	assert_eq(first["frames"], [], "no frame from its first six bytes")
	assert_eq(first["rest"], bytes.slice(0, 6), "the bytes kept for the next read")
	var buffer: PackedByteArray = first["rest"]
	buffer.append_array(bytes.slice(6))
	var second: Dictionary = _host.take_frames(buffer)
	assert_eq(second["frames"], ['{"id": 2, "command": "ping"}'], "the frame once its rest arrived")
	assert_eq(second["rest"].size(), 0, "nothing left over")


func test_two_frames_in_one_read_are_both_taken_in_order() -> void:
	var bytes: PackedByteArray = _frame('{"id": 3}')
	bytes.append_array(_frame('{"id": 4}'))
	bytes.append_array(_frame('{"id": 5}').slice(0, 3))
	var taken: Dictionary = _host.take_frames(bytes)
	assert_eq(taken["frames"], ['{"id": 3}', '{"id": 4}'], "both whole frames, in order")
	assert_eq(taken["rest"].size(), 3, "the third frame's start kept")


func test_a_frame_over_the_limit_stops_the_reading() -> void:
	var bytes: PackedByteArray = _frame('{"id": 6}')
	var header := StreamPeerBuffer.new()
	header.big_endian = true
	header.put_u32(_host.MAX_FRAME_BYTES + 1)
	bytes.append_array(header.data_array)
	var taken: Dictionary = _host.take_frames(bytes)
	assert_eq(taken["frames"], ['{"id": 6}'], "the frame before it still taken")
	assert_eq(
		taken["error"], "a frame of %d bytes is over the limit" % (_host.MAX_FRAME_BYTES + 1), "why"
	)


func test_ping_answers_ok_with_the_id_as_an_int() -> void:
	var answered: Dictionary = _host.answer('{"id": 7.0, "command": "ping"}', _error_log.new(), 0)
	assert_eq(answered["reply"], {"id": 7, "ok": true}, "the reply")
	assert_true(answered["reply"]["id"] is int, "the id echoed as an int")
	assert_eq(answered["quit"], false, "the host keeps serving")


func test_shutdown_answers_ok_then_quits() -> void:
	var answered: Dictionary = _host.answer('{"id": 8, "command": "shutdown"}', _error_log.new(), 0)
	assert_eq(answered["reply"], {"id": 8, "ok": true}, "the reply")
	assert_eq(answered["quit"], true, "the host quits after it")


func test_an_unknown_command_is_refused_by_name() -> void:
	var answered: Dictionary = _host.answer('{"id": 9, "command": "dance"}', _error_log.new(), 0)
	var expected: Dictionary = {"id": 9, "ok": false, "error": "unknown command 'dance'"}
	assert_eq(answered["reply"], expected, "the refusal")
	assert_eq(answered["quit"], false, "the host keeps serving")


func test_a_frame_without_an_id_gets_no_reply() -> void:
	var answered: Dictionary = _host.answer('{"command": "ping"}', _error_log.new(), 0)
	assert_eq(answered["reply"], {}, "no reply")


func test_a_headless_request_answers_what_the_operation_returned() -> void:
	var answered: Dictionary = _host.answer(_validate_request(10), _error_log.new(), 0)
	var reply: Dictionary = answered["reply"]
	assert_eq(reply["id"], 10, "the id")
	assert_eq(reply["ok"], true, "the wire's ok")
	assert_eq(reply["result"]["ok"], true, "the operation's ok")
	assert_eq(reply["result"]["result"]["checked"], 0, "validate's result")
	assert_eq(reply["result"]["engineErrors"], [], "nothing logged")


func test_each_headless_reply_carries_the_start_slice_and_no_earlier_request_s_errors() -> void:
	var log: Object = _error_log.new()
	_logged(log, "an autoload's error at start")
	var start: int = log.count()
	var first: Dictionary = _host.answer(_validate_request(11), log, start)["reply"]
	_logged(log, "an error the first request left behind")
	var second: Dictionary = _host.answer(_validate_request(12), log, start)["reply"]
	var slice: Array = ["an autoload's error at start"]
	assert_eq(_messages(first["result"]["engineErrors"]), slice, "the first reply's start slice")
	assert_eq(_messages(second["result"]["engineErrors"]), slice, "the second reply's, and only it")


func _validate_request(id: int) -> String:
	var request: Dictionary = {
		"id": id, "command": "headless", "params": {"op": "validate", "params": {"targets": []}}
	}
	return JSON.stringify(request)


func _frame(text: String) -> PackedByteArray:
	var payload: PackedByteArray = text.to_utf8_buffer()
	var stream := StreamPeerBuffer.new()
	stream.big_endian = true
	stream.put_u32(payload.size())
	stream.put_data(payload)
	return stream.data_array


## One error entry in log, as the engine logs one.
func _logged(log: Object, message: String) -> void:
	var none: Array[ScriptBacktrace] = []
	log._log_error("", "res://boot.gd", 0, "", message, false, Logger.ERROR_TYPE_ERROR, none)


func _messages(entries: Array) -> Array:
	return entries.map(func(entry: Dictionary): return entry["message"])
