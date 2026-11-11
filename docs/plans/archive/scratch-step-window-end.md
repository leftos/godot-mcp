# GMCP-340: a scratch step's window ends at its pace's end in game time

Design for GMCP-340, with GMCP-537's option (b) folded in so lines and errors split by one rule. The explorer's map follows the decisions; its line numbers are from main at `dac19dcc` and are re-found by name when the brief is written.

## Decisions (orchestrator, not owner rulings; open to the owner)

1. The cut lies at the met check's place in the frame: the `process_frame` emission, after the frame's physics steps and before its node `_process` calls, timers and tweens.
2. Lines and errors from the runner's own calls between steps (GetStatus, the shot, the next step's until read) belong to the next step, or to the exit after the last step. This is a documented behaviour change.
3. Errors are cut by a "mark" sentinel the bridge appends to its logger's pending list under the logger's mutex, never dropped; no per-entry stamps.
4. A shot-condition wait prints the marker only once the pace is reached (`endMarkerAtGameMs`).
5. When the wait fails or no marker is confirmed, the old path runs unchanged: the run_script marker and take-everything.
6. `endMarker` and `endMarkerAtGameMs` are server-only `wait_for` params, as `limitGameMs` is; the MCP tool's schema does not gain them.
7. A print from another thread in the same instant as the marker may fall on either side; documented as unspecified.
8. If the hook in `_poll_and_capture` proves awkward, the marker is printed at the end of `wait_for` (after capture and `then`), documented as the one place the cut lies a draw later.

Build after GMCP-552 lands (both edit `ScratchRun.cs`; no shared function).

### After review (orchestrator, fix round 1)

9. Decision 1 is corrected: a game-time wait is met on `seconds`, which at the `process_frame` emission already holds the frame's delta (Godot 4.7.2 `scene/main/scene_tree.cpp`, `SceneTree::process`: `process_time` set, then `process_frame` emitted, then `_process`, then timers). So a timer firing in the frame the clock crosses the pace runs after the cut and belongs to the next step. A draw-checked wait (a shot condition, a captured until) is met at `frame_post_draw`, and its cut lands at the next `process_frame`.
10. A paced step plays as one `wait_for` whose call is PlayStep, so its pace starts in PlayStep's frame and no server round trip sits inside the window. A shot met before its pace keeps polling in the bridge until the pace, then ends the window; the server sends no remainder wait.
11. A step that stops the walk takes the feed's entries after its cut into its own errors, since no later window exists.
12. An unmet shot wait still ends its window; `endMarkerAtGameMs` gates only a met one.
13. A bridge with no logger prints no marker and sets no `endMarked`, so `endMarked` always means a mark is in the feed.
14. A wait sent with `endMarker` whose window still falls back logs one stderr warning naming the step and why.
15. The exit hold still starts after the last step's GetStatus, shot and window round trips, so how far past the last step's cut an error is still judged moves with latency: booked as its own item, not built here.

## Explorer's map

PROTOCOL: 1 overlay: none (docs/agents/explore.md absent), read CLAUDE.md rules via the harness (.claude/rules/tool-docs.md seen); 2 README: not re-read (the earlier map's read stands; unmeasured); 3 Task Index: row "A scratch step's feed window" (ARCHITECTURE.md:79) found and used, no row for the line windows or the early reads; 4 design docs: DECISIONS.md grep only (no window or marker decision found), plans/ not read; 5 source: done (ScratchRun.cs, ErrorFeed.cs, bridge time/logger/bridge/signal_wait, Godot 4.7.2 logger.cpp, print_string.cpp, os.cpp, error_macros.cpp); 6 search: used for tests, fixtures, docs; stated below.

All paths under X:\dev\godot-mcp (main, HEAD dac19dcc, includes GMCP-537(a) and GMCP-545). Line numbers are for this commit; the gmcp-552 tree moves ScratchRun.cs lines slightly.

# 0. Answer in short

Recommended single rule: a line or an error belongs to the step whose pace wait had not yet seen the pace elapsed when the game printed or raised it. The bridge ends the wait's window itself: in the frame its wait_for check is met (or the wait ends unmet), before replying, it prints the step's end marker to stdout and stderr and appends a "mark" sentinel to the error logger's own list under the logger's mutex. The server's window cut is then the marker line in each stream and the sentinel's position in the error feed, both fixed in game order; server latency only decides when the server reads, never where the cut falls.

# 1. How a window is closed today

Lines (stdout/stderr):
- `PlayStepAsync` (ScratchRun.cs:1924): read until (:1927), play (PlayPacedStepAsync / PlayInsideUntilAsync, :1928-1931), `ShotAsync` (:1932), `CallAsync(GetStatus)` (:1933), then `CloseWindowAsync($"step {index}")` (:1934), then `TakeErrors` (:1935).
- Pace: `PlayPacedAsync` (:2130-2134) = call PlayStep then `WaitAsync(gameMs)` (:2404-2426, wait_for kind gameMs, returns (Error, Closed)). `GameMs` is at least 1 (ScratchRun.cs:919), so pace 0 is a 1 ms wait. Shot-condition steps: `PlayToShotAsync` (:2105-2127): one wait_for on the shot condition released at the pace (limitGameMs), then `WaitAsync(remaining)` (:2119) when met early; `ShotRemainderMs` (:992).
- Until steps: `PlayInsideUntilAsync` (:2248-2264), one wait_for until the condition holds or the hang guard.
- `CloseWindowAsync` (:2301-2332): `ScriptAsync(MarkerScript(marker))` (:2308), a run_script whose `execute` does print + printerr of `"[godot-mcp] scratch <label> end <nonce>"` (MarkerScript :2361, MarkerPrefix :455); then `WaitForMarkersAsync` (:2340-2359) polls the buffered output every 10 ms (MarkerPoll :508) until both streams hold the marker, within `_bounds.Markers` of load-adjusted time. `Window(stdout, stderr, after, found, nonce)` (:1272) takes each stream's own lines between the previous mark and this marker, dropping marker lines of this nonce; `_mark = found ?? total` (:2314). A failed run_script or a missing marker takes every line so far and returns an error text (:2315-2331).
- So a line belongs to the window whose run_script marker the server's print happens to follow: the marker is printed after the server learns the wait is done, runs GetStatus (and the shot), and sends the run_script. That is the pace plus latency of issue GMCP-340.
- Marker location in the stream: `Markers` (:1263) finds the marker in each stream after the previous mark (`MarkerLine` :1346); both streams must hold it.
- Exit: `PaceAfterAsync` (:2285-2293): `WaitAsync(AfterGameMs)` (the exit hold, else the last step's pace again; AfterGameMs :269), then `CloseWindowAsync("pace")` into `_paceLines` (:2288), `TakeErrors` into `ScratchVerdict.FeedError` (:2289), `_afterError = feedError ?? waitError ?? markerError` (:2292). Exit lines = `_paceLines` + `Window(.., _mark, EveryLine)` (:1418).
- Other windows: "launch" (:1712, boot errors taken at :1713) and "until reads" (`PassOverReadLinesAsync` :2195-2207), both via the run_script marker.

Errors:
- `ErrorFeed` (Session/ErrorFeed.cs): `Receive` (:74) parses the bridge's `{type:"errors", entries, dropped}`, `Add` (:81) assigns Seq in arrival order (bridge's number ignored, :94), `JudgedSince` (:121). `Parse` (:129) maps any type other than "warning" to "error".
- `TakeErrors(feed, ref mark, warnings)` (ScratchRun.cs:1290-1295): everything past `_feedMark` (:543), mark = last Seq taken. So the step takes whatever has arrived by the time of its `TakeErrors`, i.e. after GetStatus, the shot and the run_script marker round trip. The bridge flushes errors on `_process` (bridge.gd:445 `_poll_connection` -> `_flush_errors` :501) and before every reply (`_reply_ok` bridge.gd:968-971), so the errors frame precedes the reply on the socket.
- GMCP-545 (`ReadUntilEarlyAsync` :2217, `_earlySkipped` :550, `WithUnreproducedEarly` :1965 called at :1949, `Unreproduced` :1303): leftovers from the pre-walk reads are appended to the owning step's `Errors` after the window's list; independent of where the window ends.
- Exit hold after the last step: same `PaceAfterAsync` path. Game that quits during a wait: `WaitAsync` returns `Closed=true` for IOException (:2418-2421); `CloseWindowAsync`'s run_script fails, the window takes every line, error text is "the <label> end marker failed" (:2315-2318); the walk goes on to `StopsAfter`.

# 2. What the bridge knows when the pace's wait is met

- `wait_for` (bridge/godot_mcp_time.gd:474-496): fires `_on_godot_mcp_wait_started(kind)` on the group at the start (:477), then kind gameMs goes `_wait_for_game_time` (:545): a clock ticked in `process_frame` (`_tick_game_clock` :580, `advance_game_clock` :589: `seen` is the game time through the PREVIOUS frame, because SceneTree::process emits process_frame before the frame's nodes, timers and tweens, per the comment :585-588 citing scene/main/scene_tree.cpp L712-728), polled by `_poll_capturing` (:610) -> `_poll_and_capture` (:648) -> `_poll` (:681). The check that meets runs in the process_frame emission of frame N after the tick (the tick connected first at :554, before the poll's await at :686). Signal and probe waits go through the same `_poll_capturing` (signal_wait.gd:46) and `_poll` (:69 is the signal "start" poll, not an end).
- At that point the bridge has: `Engine.get_process_frames()` (used by `_frames_of`, bridge.gd:981-990, and in then.call, time.gd:831), the wait's game ms (`clock["seen"]`, `_add_game_ms` :637 only with limitGameMs), `ticksUsec` (:696). The result is returned to `wait_for` then `_reply_ok` -> `_flush_errors` -> send, all in that frame unless a screenshot capture awaits (`_with_capture` :660) or `then` runs (`_finish_then`).
- Can it print the marker to stdout and stderr in that frame before replying? Yes: `print()` and `printerr()` from the bridge script run synchronously on the main thread. Godot 4.7.2: `__print_line` (core/string/print_string.cpp:80-109) calls `OS::print` immediately; `OS::print` -> `_logger->logv` (core/os/os.cpp:108-118) -> `CompositeLogger::logv` (core/io/logger.cpp:245-256) -> `StdLogger::logv` (:224-239): stderr is `vfprintf(stderr)` (unbuffered), stdout is `vprintf` and `fflush(stdout)` only when `_flush_stdout_on_print` (:233-236; the project's `application/run/flush_stdout_on_print.debug`, the requirement TOOLS.md:810 and `MarkerTimeout` already state). Error lines go `OS::print_error` -> `CompositeLogger::log_error` (logger.cpp:258-266) on the raising thread, so a push_error's stderr text and the logger's `_log_error` (godot_mcp_logger.gd:53) both run synchronously in the raising call.
- Ordering guarantee: within one stream the bytes are in call order (stdout/stderr FIFO pipes; the server reads each stream separately). Between the two streams there is NO order guarantee in the captured data, which is why the window takes each stream's own range to its own marker (`Window` :1272, test `AStepsWindowEndsAtEachStreamsMarkerNotAtTheOtherStreamsCount`, ScratchVerdictTests.cs:1013). A marker printed on the main thread is ordered exactly against every main-thread print and error of the game in that stream. Prints from other threads are ordered by the C stdio lock and the logger mutex at their call time: a thread racing the marker within the same instant can land either side (state it as unspecified).
- The bridge logger (`_log_error`) appends to `_pending` under `_mutex` with no stamp (godot_mcp_logger.gd:81-87); a sentinel appended under the same mutex at the marker's instant is totally ordered against every thread's entries. The bridge could also stamp `Engine.get_process_frames()`, but a sentinel needs no clock and no thread-safety reasoning.

# 3. Design proposal

## Recommended option: the bridge ends the wait's window (marker print + error sentinel in the met frame)

Rule: "A step owns every line and error the game printed or raised up to the moment its pace wait saw the pace elapsed: the first process_frame emission after the clock reached the pace. The next step owns everything after it, including what the runner's own calls between steps print (GetStatus, the shot, the next step's GetStepUntil)."

Mechanics:
1. Server sends the step's last wait (the pace wait, the until wait, the shot wait or its remainder, the exit hold) with two new server-only params: `endMarker` (the text `MarkerPrefix + "{label} end {nonce}"`, label `step N` or `pace`) and, for a shot-condition wait only, `endMarkerAtGameMs` (the pace), so a shot met before the pace does not print.
2. Bridge `_poll_and_capture` (time.gd:648), right after `await _poll(...)` returns and before `_finish_then` or a capture await, when `params.endMarker` is set (and `endMarkerAtGameMs` is reached by the wait's seen game ms when present): `print(marker)`, `printerr(marker)`, and `logger.mark(marker)` (new: appends `{"type":"mark","message":marker}` to `_pending` under `_mutex`, never dropped at `MAX_PENDING`); adds `endMarked: true` to the result. It runs on a met, an unmet (timeout, limitGameMs reached, cancel) end alike, so every end of the wait closes the window.
3. `ErrorFeed.Receive` (ErrorFeed.cs:74): an entry of type "mark" is not an `ErrorEntry`; it records `Marks[message] = _lastSeq` (the Seq of the last real entry received before it, in frame order). New `ErrorFeed.TakeThrough(label)`-style read: entries with Seq above the mark and up to the labelled mark's Seq.
4. Server `WaitAsync` returns the reply's `endMarked`. `CloseWindowAsync` gets a variant that skips `ScriptAsync` (the bridge already printed) and goes straight to `WaitForMarkersAsync` (:2309), the one place the server still waits on latency (it waits for its own readers to see lines already printed). Errors: `TakeErrors` takes through the label's sentinel and moves `_feedMark` to the sentinel's Seq (not to the last taken Seq), so later entries stay for the next window; no wait is needed because the errors frame holding the sentinel precedes the wait_for reply on the socket (bridge.gd:968-971). If the reply lacks `endMarked` (the wait failed, was refused, the game closed, or PlayStep failed so no wait ran) the old path runs unchanged: run_script marker, take-everything.
5. The rule is by construction the same for lines and errors: both cut at the same call in the same frame; a line and an error printed in one frame land in the same window unless the cut falls between them, and both then fall on the same side of it.

Frame rule (a line in the very frame the pace is met): the cut is the met check's place in the frame, the `process_frame` emission, after the frame's physics steps and before its node `_process` calls, timers and tweens. So: printed in the frames before, or in that frame's physics step: the step's. Printed by node `_process`, a timer or a tween in the met frame, or later: the next step's. Handlers on `process_frame` itself in that emission: order of connection (unspecified for callers). Because the clock's `seen` lags a frame, a timer set to fire exactly at the pace fires in the crossing frame (the step's, before the check) and one past it in the next (the next step's).

What the server waits on: (a) the wait_for reply (already awaited; it carries `endMarked`, and the sentinel is already in the feed); (b) both marker lines in the output buffers (existing `WaitForMarkersAsync`, bounded by `_bounds.Markers`). Nothing waits on the errors beyond (a).

Cases (details in the CASE TABLE, section 5):
- pace 0: `GameMs` is 1 ms; the wait ends at the first check whose `seen` reaches 1 ms, a frame or two after PlayStep returns; same rule.
- `until` wait: `endMarker` sent on the until wait; ends at met, at limitGameMs unmet, or at the backstop; the step owns everything up to that frame. A signal until goes through the same `_poll_capturing` (signal_wait.gd:46).
- exit hold: `PaceAfterAsync` sends `endMarker` on the hold's wait with label `pace`; the exit window ends at the hold's end, so nothing between the last step's pace end and the hold's start is lost: it belongs to the exit (GetStatus of the last step, the shot, the hold's own start).
- game quits in a step: the wait fails with IOException, no marker, no sentinel; fallback = today's (take everything so far, run_script marker fails, error text). A quit inside the hold: same, `closed` true (`_afterClosed`).
- pace wait times out (the game's clock stood still, e.g. paused): the bridge's `_poll` ends at `timeoutMs`/`backstopMs` unmet and still prints the marker (the wait ended); the server fails the step as `PaceNotMet` (:2416, :2429), window as normal. If the server released the wait itself first (`SendAsync` release path, :2415) the marker arrives with the late answer or never; the existing marker timeout path (`MarkerTimeout`, :2324) covers a missing marker.

## Rejected alternatives
- (R1) Stamp each log line and error with game time / process frame and split in the server. Rejected: stdout/stderr lines carry no stamp (the game's own `print` cannot be stamped; only a marker in the stream can order them), so it would solve errors only and leave lines on latency; a second rule is the thing GMCP-537 (b) folded into 340 to avoid. It also changes `ErrorEntry` (positional record: every `new ErrorEntry(` in tests changes) and the wire.
- (R2) Server sends the run_script marker sooner (before GetStatus, straight after the wait reply). Rejected: it still races the game's frames during the round trip (a run_script is handled in a later frame), so a line printed one frame after the pace lands before or after it by latency; cuts the window by one round trip, not the dependence.
- (R3) Bridge prints the marker at the end of `wait_for` (after capture/then) rather than in `_poll_and_capture`. Rejected: the shot or then.call frames would be charged to the step for windowed capture runs; the met check is the pace's end. (Acceptable fallback if the in-`_poll_and_capture` hook proves awkward; documented as the one place the cut lies a draw later.)
- (R4) Put the marker in a bridge `call_group` hook only (as 537(a)). Rejected: that fires on wait START (the next step), a test aid, not a window end.
- (R5) Cut by frames: the reply carries the end frame, the bridge stamps the frame per error. Rejected for lines (as R1).

# 4. SITE INVENTORY (searched: Grep of `CloseWindowAsync|WaitForMarkersAsync|TakeErrors|_feedMark|_mark\b|MarkerScript|WaitAsync\(` in ScratchRun.cs, `Errors.Receive|OnErrors` in Session, wait_for/_poll/_flush_errors/_log_error in bridge; the ErrorFeed consumers elsewhere (RuntimeTools*.cs, Batch.cs `ErrorsSince`) read the feed with their own marks and are listed once)

Server, Session/ScratchRun.cs:
1. :543 `_feedMark` - changes: after a labelled take it is the sentinel's Seq, not the last taken Seq.
2. :550 `_earlySkipped`, :1965 `WithUnreproducedEarly`, :1303 `Unreproduced`, :2217-2240 `ReadUntilEarlyAsync` - unchanged: they judge by sequence before the walk and by `window` contents; a window that ends earlier only moves later entries on.
3. :1290 `TakeErrors` - changes: gains the labelled form (keep the old one for boot and fallback).
4. :1712-1713 launch window + boot take - unchanged (no wait_for ends it; run_script marker stays).
5. :1924-1957 `PlayStepAsync` - changes: passes the label, takes errors through the sentinel, uses the marker-printed close.
6. :2105-2127 `PlayToShotAsync` - changes: sends `endMarkerAtGameMs` on the shot wait, `endMarker` on the remainder wait; takes `endMarked` from whichever wait ended the step.
7. :2130-2134 `PlayPacedAsync` - changes: its `WaitAsync` carries the marker.
8. :2248-2264 `PlayInsideUntilAsync` - changes: wait params carry the marker.
9. :2404-2426 `WaitAsync` - changes: optional marker, returns whether the bridge printed it.
10. :2285-2293 `PaceAfterAsync` - changes: hold wait carries `pace` marker; errors through its sentinel.
11. :2301-2332 `CloseWindowAsync` - changes: variant that skips the run_script; fallback path kept.
12. :2340-2359 `WaitForMarkersAsync` - unchanged (still polls the output buffers).
13. :2361 `MarkerScript` - unchanged (fallback and boot/until-reads windows use it).
14. :1263 `Markers`, :1272 `Window`, :1282 `Dropped`, :1346 `MarkerLine` - unchanged (cut is still by marker line per stream).
15. :2195-2207 `PassOverReadLinesAsync` - unchanged (before-the-walk window, run_script marker).
16. :1418 `ExitLines` and `_paceLines` - unchanged (exit window still `_paceLines` + the tail after `_mark`).
17. :2450 `Seen` (carries `Steps`, `AfterError`) - unchanged.
Server, other files:
18. Session/ErrorFeed.cs:74 `Receive`, :81 `Add`, :129 `Parse` - changes: "mark" entries recorded as marks, never as entries (Parse would turn one into an error today). :121 `JudgedSince`, :109 `ErrorsSince`, :100 `Since` - unchanged (a mark is not an entry; add a range read).
19. Session/GodotSession.cs:1076 and GodotSession.Attach.cs:298 `connection.OnErrors(Errors.Receive)` - unchanged (same entry point).
20. Wire/BridgeConnection (errors frame dispatch, ARCHITECTURE.md:223) - unchanged.
21. Session/ScratchVerdict.cs (`FeedError`, `UnmetFailure`, `UntilFailure`, `ShotWaitFailure`), Session/ScratchView.cs - unchanged (they judge the list they are given); not re-read this run, unmeasured beyond the call sites at ScratchRun.cs:1936-1939, :2289.
22. Tools/RuntimeTools*.cs, Batch.cs `ErrorsSince` consumers - unchanged (a mark never enters an entry list).
Bridge:
23. bridge/godot_mcp_time.gd:648 `_poll_and_capture` - changes (prints the marker, marks the logger, sets `endMarked`); :474 `wait_for` - unchanged but its doc comment lists the new params; :610 `_poll_capturing`, :681 `_poll` - unchanged (`_poll` also serves the signal "start" poll, so the print is NOT put there).
24. bridge/godot_mcp_signal_wait.gd:46 - unchanged (goes through `_poll_capturing`).
25. bridge/godot_mcp_logger.gd:53 `_log_error` - unchanged; new `mark(label)` beside `take_pending` (changes the file); `MAX_PENDING` cap (:24, :83) must not drop a mark.
26. bridge/godot_mcp_bridge.gd:501 `_flush_errors`, :968 `_reply_ok` - unchanged (the sentinel rides the frame that already precedes the reply); the `_logger` may be null (dormant), so `mark` is skipped then.
27. Tests' stand-in tree `wait_tree` (time.gd:499 `_tree`) - unchanged.
Count: 27 sites, 15 changing (1, 3, 5-11, 18, 23, 25 and the new test fixtures not counted); 12 unchanged for the reason given.

# 5. CASE TABLE (window each lands in under the proposal)

| Case | What the game prints / raises | Lands in |
|---|---|---|
| Normal step, line printed in the 2nd frame of the pace | line | that step |
| Line from a timer at exactly the pace | fires in the frame the clock crosses, after that frame's `process_frame` check already saw the pace (decision 9) | next step (exit for the last) |
| Line from a timer a little past the pace, or any node `_process` in the met frame | after the met check | next step (exit for the last) |
| Line from the met frame's physics step | before the check | that step |
| Line printed by GetStatus, the shot, the next GetStepUntil | after the cut | next step (exit's `pace` window for the last step's GetStatus) |
| Error pushed in PlayStep or its pace | before the cut | that step (and its `UnmetFailure`) |
| Error raised in GetStatus (was the step's) | after the cut | next step; for the last step, the exit (`_afterError`) |
| Error and line in the same frame | same side of the cut together, except where the cut falls between them in that frame's order | same rule |
| Pace 0 (1 ms wait) | cut at the first check whose seen game ms reaches 1 | as above |
| `until` step, until met | cut at the met frame | prints before it: the step; after: next |
| `until` unmet at limitGameMs or backstop | cut at the poll's end, marker still printed | prints before: the step (failed step); after: next |
| Shot step, shot met before the pace | shot wait prints nothing (`endMarkerAtGameMs`); the remainder wait ends the step | by the remainder's cut |
| Shot step, shot met at or after the pace | shot wait prints | by that cut |
| Exit hold (or last pace again) | prints and errors after the last step's cut up to the hold's cut | the exit (`_paceLines`, `FeedError`) |
| After the hold's cut (quit lines, leak report) | after the cut | exit lines tail (`Window(.., _mark, EveryLine)`), unjudged errors as today |
| Game quits in a step | no marker, no sentinel; IOException | fallback: take everything so far, "end marker failed" error, as today |
| Game quits in the hold | same, `Closed` true | fallback as today; clean exit 0 still green |
| Pace wait times out (clock stood still) | bridge ends the poll unmet and prints | up to the poll's end: the step; `PaceNotMet` fails the step |
| Server released the wait before the bridge replied | the server sends a cancel and waits for its answer, so the poll ends and prints | the marked path |
| PlayStep itself failed (no wait ran) | none | fallback run_script marker, take everything |
| Thread print or error in the cut's instant | unspecified | either side |

# 6. Tests

Existing tests that pin the split (all must still pass; none asserts a wall-clock figure):
- Itest: `ScratchRunnerTests.ALateLineFailsTheStepItPrintedIn` (ScratchRunnerTests.cs:282, fixture scratch_late_line.gd: line in the first process frame after PlayStep of step 1, step 1's pace not yet elapsed: stays step 1); `AnErrorInThePaceAfterTheLastStepIsRedInTheExit` (:217, scratch_late_error.gd, 0.5 s timer from the hold's start: exit); `AStepThatPushesAnErrorIsTheFailedStepAndTheRestNeverPlay` (:262); `ScratchPaceRunTests.TheWaitAfterTheLastStepIsThatStepsOwnPace` (:97), `AnExitHoldIsTheWaitAfterTheLastStepInPlaceOfItsPace` (:118), `ASceneThatQuitsWithCodeZeroInsideItsExitHoldIsGreen` (:137); `ScratchRunnerTests` quit test at :326 (scratch_quit.gd) and `ScratchStepsRunTests` :72; `ScratchExpectRunTests` :32 (exit line); the whole `ScratchUntilRunTests` (36 references to the window functions); `ScratchLookRunTests`.
- Unit: `ScratchVerdictTests.AStepsWindowEndsAtEachStreamsMarkerNotAtTheOtherStreamsCount` (:1013), `AMarkerBeforeTheWindowsStartOrFromAnotherWindowIsNotItsEnd` (:1043), `OnlyThisRunsMarkersAreDroppedFromAWindow` (:1058), `AnErrorFlushedBetweenTwoWindowsLandsInExactlyTheNextOne` (:1072), `TakingErrorsWithWarningsOffLeavesTheMarkAtTheLastErrorTaken` (:151), `TakingErrorsWithWarningsOnTakesBothAndMovesTheMarkPastBoth` (:188), `AMissingStdoutMarkerPointsAtBuffering` (:1111); `ScratchUntilTests` `Unreproduced` tests (:534-576); `ScratchBoundsTests.cs:39` MarkerTimeout.
- Change: none needs rewriting if `TakeErrors` keeps its signature and the labelled take is an overload. Fixture comments (scratch_late_error.gd:3-6 etc.) updated to say the margin is gone.
New tests (no sleeps, no wall-clock; each waits on events or asserts on the order of lines/entries):
- gdtest `test_time.gd`: `test_a_wait_with_an_end_marker_prints_it_to_stdout_and_stderr_and_marks_the_logger_when_it_ends` (stand-in tree, the logger's pending list holds a mark entry after the entries logged before the met check and before the entries logged after it); `test_an_unmet_wait_still_prints_its_end_marker`; `test_a_shot_wait_met_before_its_limit_prints_no_marker` (endMarkerAtGameMs); `test_a_wait_without_an_end_marker_prints_nothing`; `test_the_result_says_endMarked`.
- gdtest `test_logger.gd`: `test_mark_is_ordered_among_entries_by_the_mutex`, `test_a_mark_survives_a_full_pending_list` (200 entries then a mark), `test_take_pending_hands_the_mark_with_the_entries`.
- Unit `ErrorFeedTests`: `AMarkEntryIsNotAnErrorAndRecordsTheSeqBeforeIt`, `EntriesAfterAMarkInTheSameFrameAreNotPastIt`... precisely: `TakingThroughAMarkLeavesLaterEntriesForTheNextWindow`, `AMissingMarkTakesEverything`, `TwoMarksCutThreeWindows`.
- Unit `ScratchVerdictTests`/`ScratchRunWindowTests`: `ATakeThroughAMarkMovesTheFeedMarkToTheMarkNotToTheLastTaken`; `AWindowEndsAtThePrintedMarkWhetherOrNotTheRunnerSentIt` (Window on pages built from a marker line placed before further lines).
- Itest, new fixture `scratch_window_cut.gd` (extends scratch_probe; uses the existing wait-start hook only for arming): step 0 plays; its GetStatus prints a line and push_errors once (runner-called, deterministic, after the pace's end, before today's window close); asserts, through `details` with `FullDetails`: step 0's `lines` and `errors` do NOT hold them, step 1's do; a line printed by PlayStep stays in step 0. Test `AFixtureCallTheRunnerMakesBetweenStepsIsTheNextStepsNotTheLast` in ScratchRunnerTests or a new `ScratchWindowRunTests`. This pins the rule with the runner's own call order, so no latency enters.
- Itest `TheLastStepsGetStatusLineIsTheExitsNotItsOwn` (same fixture, last step; asserts `exit.lines`).
- Itest regression for fallback: `AStepWhoseGameQuitsInItsPaceStillGetsAnEndMarkerError` (existing ScratchQuit path, assertion kept).

# 7. Docs

- docs/TOOLS.md `run_scratches`: add the rule in the paragraph at :602 (it says "since the step began" and "in the step's window" with no rule for what lies between steps): "A step owns every stdout or stderr line and every error in the error feed up to the moment its pace wait saw the pace elapsed (the first frame whose process_frame check finds the clock at the pace, before that frame's `_process` calls, timers and tweens); what the game prints or raises after it, including from `GetStatus`, belongs to the next step, or the exit for the last. A line or error from another thread in that same instant may fall on either side." Add the exit hold and `until` cut sentences. Keep the note at :810 (stdout buffering) and add that the marker is printed by the bridge.
- docs/ARCHITECTURE.md: Task Index row at :79 (add `bridge/godot_mcp_time.gd` `_poll_and_capture`, `godot_mcp_logger.gd` `mark`, `ErrorFeed.Receive`, new test names); the errors-frame paragraph at :223 (frame may carry a "mark" entry; `wait_for`'s new `endMarker`/`endMarkerAtGameMs` params and `endMarked` result); describe the window closing wherever the run_scratches runner is described (not located by line in this run; long lines omitted by Grep: unmeasured).
- docs/DECISIONS.md: one entry (the single rule, the sentinel over per-entry stamps, GetStatus/shot to the next step, 537(b) folded in, cross-thread instant unspecified).
- docs/TOOLS.md `wait_for` section: the params are server-only for scratch runs (as `limitGameMs` is); say so or leave the tool schema alone (`endMarker` must not reach the MCP tool's schema).
- skills/godot-mcp/SKILL.md: the line-window rule if the skill describes step lines (per .claude/rules/tool-docs.md: not checked, unmeasured).
- CHANGELOG entry (user-visible: lines printed by GetStatus move to the next step).
- fixtures' header comments (scratch_late_error.gd, scratch_step_pace_after.gd, scratch_exit_hold.gd, scratch_quit_in_hold.gd): the margin text.

# DEPENDS ON
- GMCP-552 (worktree X:\dev\godot-mcp.wt\gmcp-552): file overlap only. `git diff 2924c5c8 -- src/GodotMcp.Server/Session/ScratchRun.cs` there shows hunks at the fields (:530-533 `_memoryPeak`, `_freeMemoryMb`), the doc of `RunAsync` and `ScratchRunLogs.PruneShots` (:611-616), `FreeMemory.AvailableMb` around `LaunchAsync` (:1401), `memory?.FinalRead()` and `_memoryPeak` in the `ReadAndPlayAsync` finally (:1536), `MemoryWatch` (:1547-1572), and `Seen` (`PeakPrivateMb`, `FreeMemoryMb`, :2468). None touches `PlayStepAsync`, `PaceAfterAsync`, `CloseWindowAsync`, `WaitForMarkersAsync`, `TakeErrors`, `WaitAsync`, `PlayToShotAsync`, `PlayInsideUntilAsync`, `ReadUntilEarlyAsync`: no functional conflict; the `Seen` hunk is beside none of the changes; textual conflicts unlikely beyond line offsets. Build 340 after it lands, rebase, and re-find by name.
- GMCP-537: option (b) folded in here; option (a) (wait_started hook) already landed and is kept.
- GMCP-545: landed (`WithUnreproducedEarly`); unaffected.
- Not consumed: GMCP-500/513 (scratch plan/batch code; overlap unmeasured).

# OPEN DECISIONS (recommended answer first)
1. Where the cut lies in the met frame: the process_frame emission of the met check (recommend; after physics, before `_process`, timers, tweens) vs after the frame's nodes. Recommend the first: it is where `_poll` resumes and needs no deferral.
2. Lines and errors the runner's own calls between steps make (GetStatus, shot, next GetStepUntil): next step's (recommend, a documented behaviour change: a push_error in GetStatus now fails the next step, or the exit) vs keep them the step's by closing the window after GetStatus (reintroduces latency for the pace's end).
3. Error cut mechanism: a sentinel entry in the logger's own list (recommend) vs per-entry frame stamps with an end frame in the reply (R1).
4. Marker on a shot wait: conditional `endMarkerAtGameMs` (recommend) vs always run a zero-length remainder wait.
5. Fallback when the wait fails or no marker is confirmed: old run_script marker and take-everything (recommend, unchanged behaviour) vs fail the step.
6. Whether the bridge also prints for waits that are not scratch's: the params are server-only, so no.
7. A mid-instant other-thread print: unspecified (recommend) vs a lock around marker print + sentinel (cannot order against the engine's own stdio).
8. Where to print if the hook proves awkward: after capture/then in `wait_for` (R3) at the cost of a draw's lines being charged to a captured step.

# DOC DRIFT
- docs/TOOLS.md:602 "the error feed since the step began" and "in the step's window": the code's window ends at the server-sent marker after GetStatus; the doc never says which step owns lines between steps. Code is not wrong; owner doc TOOLS.md (the rule above fixes it).
- Fixture headers scratch_late_error.gd:3-6, scratch_exit_hold.gd:1-9, scratch_quit_in_hold.gd:1-6: still say the wait "is sent only after that step's window closed" (true) but not that the window ends at the server's round trip; they read as latency-free. Owner: these fixtures (GMCP-340 rewrites them).
- docs/ARCHITECTURE.md:79 Task Index row lacks `ReadUntilEarlyAsync`, `WithUnreproducedEarly` (the earlier map's drift; still open).

# TASK INDEX GAP
- Task "a line or error lands in the wrong step / the step's window ends too late or too early": `src/GodotMcp.Server/Session/ScratchRun.cs` (`PlayStepAsync`, `PlayPacedAsync`, `PlayToShotAsync`, `PlayInsideUntilAsync`, `WaitAsync`, `PaceAfterAsync`, `CloseWindowAsync`, `WaitForMarkersAsync`, `TakeErrors`), `Session/ErrorFeed.cs` (`Receive`, `Add`, `Parse`), `bridge/godot_mcp_time.gd` (`wait_for`, `_poll_and_capture`, `_poll`), `bridge/godot_mcp_logger.gd` (`_log_error`), `bridge/godot_mcp_bridge.gd` (`_flush_errors`, `_reply_ok`), fixtures `scratch_probe.gd`, `scratch_late_line.gd`, `scratch_late_error.gd`; tests ScratchVerdictTests, ScratchRunnerTests, ScratchPaceRunTests, tests/bridge test_time.gd, test_logger.gd. This answer took about 35 reads and searches beyond the Task Index row and one doc.

# MISSING ENTRY POINT
- none (docs/README.md and docs/ARCHITECTURE.md with a Task Index exist). Terms "window", "pace end" and "end marker" have no glossary entry (not verified against README.md in this run: unmeasured).
