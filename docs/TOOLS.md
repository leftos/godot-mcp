# Using the godot-mcp tools

For an agent driving a Godot project through this server: which tool fits a job, what each one does, and the edges that bite. Words such as session, quiet run, prep, baseline and headless run are defined in the [glossary](./README.md#glossary). Each tool's wire-level result shape and bridge command are in the tool table of [ARCHITECTURE.md](./ARCHITECTURE.md#tools); the tool's own schema in your client has every argument.

## Which tool for which job

| Job | Reach for | Instead of |
|---|---|---|
| Start the game and drive it | `run_project`, then the runtime tools | `attach_project`, which is for a game started some other way |
| Drive a game you (or the editor) started | `attach_project` | `run_project` |
| See one scene without playing | `preview_scene` | `run_project` with `scene` plus `take_screenshot` plus `stop_project` |
| Pick a thing to click | `get_ui_elements` (Controls, with viewport rects) | guessing coordinates from a screenshot |
| Click, drag, type | `click`, `drag`, `type_text`, `key` | `simulate_input`, which is for event sequences the gestures cannot make |
| Hover a Control, see its tooltip | `hover` (waits for the tooltip, returns its text and rect) | a `mouse_button` release as a hover, a `wait_for` as a sleep |
| Drive with a pad | `gamepad_button`, `gamepad_stick`, `gamepad_axis` | `simulate_input` joypad events |
| Land a check on an exact frame | `frame_control` (`pause`, `step`), then `wait_for` with `timeoutMs: 0` | `wait_for` with a timeout on a running game |
| Watch a value change over frames | `monitor_property` | a loop of `inspect_node` calls |
| Press an InputMap action | `simulate_action` | `key` on a key bound to it |
| Find the input a game cannot survive | `stress_input` | a hand-written loop of random `simulate_input` calls |
| Wait for something to happen | `wait_for` | polling `inspect_node` or `run_script` |
| Read or change a live node | `inspect_node`, `set_property`, `call_method` | `run_script`, which is for anything those three cannot say |
| See everything an action changed in a subtree | `snapshot_subtree`, act, `diff_snapshots` | `inspect_node` on each node before and after |
| Learn a class's members | `describe_class` | guessing names, then reading `call_method` errors |
| Learn a C# object's or type's members, private ones and overloads included | `cs_members` | `describe_class`, which shows only what Godot's call can reach |
| Read or change a C# member Godot's `get`/`set` cannot reach (a private record field, a `List<T>`, a static) | `cs_get`, `cs_set` | `inspect_node`/`set_property`, which read such a member as null |
| Call a C# method Godot's call cannot reach (overloads, generics, records, statics, an async `Task`) or construct a C# object | `cs_call` | `call_method`, which marshals only Variant types |
| Find a live node | `get_scene_tree` | `get_scene_file_tree`, which reads a scene file |
| Know what went wrong | the `errors` in each result, then `get_errors`, then `get_debug_output` | reading stdout first |
| Replay a known sequence with checks | `batch_drive` | one tool call a step |
| Turn what a person (or a drive) did into a replayable sequence | `capture_input` start, play, stop, then `simulate_input` with its `events` | writing the events by hand from a description |
| Catch a visual regression | `save_screenshot_baseline` once, `compare_screenshot` or a `screenshot` assertion after | comparing screenshots by eye |
| Keep a video of a bug | `run_project` with `options.record`, `record_mark`, `stop_project` | an OS screen recorder |
| Check scripts and scenes load | `validate` | launching the game to see if it errors |
| Edit a scene file | the headless scene tools, several edits at once through `batch_scene_operations` | writing `.tscn` text by hand |
| Set a project's launch defaults | a `godot-mcp.json` beside `project.godot` | repeating `engineArgs` on every `run_project` |

## Rules every tool shares

- **Session.** Every runtime tool, and `restart_project`, `stop_project`, `detach_project` and `get_debug_output`, takes a last `session` argument. It may be left out while only one session exists; with several and no name the call is refused and lists them. A new session is named after its project folder, fitted to the name rule (1 to 64 letters, digits, `.`, `_`, `-`; any other character becomes `_`, so `My Game` becomes `My_Game`). Two sessions on one folder need distinct names: pass `options.session` (`run_project`) or `session` (`attach_project`). The headless tools and `preview_scene` take `projectPath` instead and no session.
- **Errors ride along.** A runtime tool's result carries `errors` (file, line, stack) for every error the game raised while the call ran, and the call still succeeds, except `run_script` and `call_method` as their sections say. The key is present only when the game raised an error during the call: its absence means none. Read `errors` in every result before the next step.
- **Viewport coordinates.** Every `x`, `y` an input tool takes is in viewport coordinates, as `get_ui_elements` reports rects; the bridge maps them to the window whether it is stretched or letterboxed. A screenshot's pixels are the window's, which differ from the viewport's when the project stretches (a 640x360 viewport in a 1000x900 letterboxed window captures 1000x562), so a point read off a screenshot is not a viewport point. Aim at `{element}` where a Control exists.
- **Paging.** Long lists come back one page at a time with `total`, `offset` and, while more remain, `next`: pass `next` as the next call's `offset`. A value longer than its budget comes back as `{valuePreview, valueLength}`.
- **Quiet by default.** `run_project` starts the game quiet: unfocused, off-screen, click-through, silent, with no real input reaching it, and capped at 60 fps unless the project sets its own `application/run/max_fps` (a recording run keeps its fixed 60). Pass `options.quiet: false` only when the user wants to watch or play along. Sessions on one folder must agree on quiet and on `shutOutRealGamepads`; a new session that differs is refused.
- **Headless tools and live sessions.** Every headless tool (`validate`, `get_scene_file_tree` and the scene tools) is refused while a session is live on the project folder: `stop_project` or `detach_project` it first. `preview_scene` is not headless and runs beside a live session.
- **Paths.** Headless and preview paths are `res://` paths or paths relative to the project folder, inside it, and must match the case of the file on disk; a case variant is refused with the right spelling. A node path in a scene file is relative to the scene's root (`.` for the root, `Boss/Sprite`), never `/root/...`. A live node (runtime tools) is an absolute path (`/root/Main/Button`), a path under the root (`Main/Button`) or a bare name found breadth first.
- **C# projects.** A launch, a restart and every headless tool run the prep first: a stale C# assembly is built and missing imports are run; the result's `prep` says what was done. A failed build refuses a launch with its compiler errors. The headless write tools will not save a scene that uses C# scripts while the build fails. `options.prepare: "never"` skips the prep on the tools that take it.

## Start and stop a game

### `run_project`

- **Does:** launches the project with the bridge injected and returns once the bridge connects: `{session, projectPath, processId, quiet, recording?, prep}`.
- **Use:** `projectPath` (the folder holding `project.godot`); `scene` for a scene other than the main one; `userArgs` (after `--`, read with `OS.get_cmdline_user_args()`); `engineArgs` (before `--`, e.g. `["--resolution", "1280x720"]`); `options {quiet, shutOutRealGamepads, session, prepare, preset, record}`.
- **Edges:** a live session name is refused; a new name starts a second session alongside. `options.preset` names a preset from `godot-mcp.json` and fails when the file or preset is missing. By default the machine's real gamepads stay live and feed the same actions as the gamepad tools; `shutOutRealGamepads: true` keeps them out, at the cost of focus-out notifications to the game (a game that pauses on focus loss will, and every `Popup` closes as it opens). `record` changes game timing: see Recording.

### `attach_project`

- **Does:** injects the bridge plus a one-use attach file and blocks until a game started on the project after that connects: `{session, projectPath}`.
- **Use:** for a game `run_project` does not start: a second client, a `--server` run, a smoke script, the editor's Play button. Start the launch in the background delayed a second or two (`Start-Sleep 2; godot --path <project>`) just before this call, or launch within `waitSeconds` (1 to 600, default 60) after it. Also takes `shutOutRealGamepads` and `session`.
- **Edges:** a game already running when the files are written never attaches. An attached session has no captured output (`get_debug_output` refuses it), cannot be restarted, is never quiet, and ends with `detach_project`, not `stop_project`.

### `detach_project`

- **Does:** ends an attached session, closes the connection and removes the injected `override.cfg` unless another live session uses the folder; the game keeps running with an idle bridge.
- **Use:** to finish with a game `attach_project` joined.

### `stop_project`

- **Does:** asks the game to quit, kills it after 3 s (30 s for a recording run), removes the override when no other live session uses the folder: `{session, projectPath, exitCode, killed, overrideRemoved, recording?}`.
- **Use:** at the end of every `run_project` session, and before a headless tool on the same folder.
- **Edges:** a game that does not answer a ping in 2 s is killed at once. A game a debugger is attached to is stopped all the same, and `warning` says its debug session ended with it. For a recording run, `recording` holds `{path}` (no marks: the full movie) or `{clips}` (one file a mark pair; the full movie deleted), plus `error` when a cut failed.

### `restart_project`

- **Does:** relaunches a run session with the scene, arguments and options it was started with, keeping its name, its error `seq` and its debug output (a marker line separates the two games).
- **Use:** after editing code or scenes, to pick up the change without losing the session; `options {prepare}`.
- **Edges:** the prep runs while the old game still runs, so a failed C# build is an error and leaves the old game running. To change the scene or arguments, `stop_project` then `run_project`. An attached session cannot be restarted. The old game is stopped as `stop_project` stops it, with its `warning` when a debugger was attached to it. A recording run records the new game to a new file and returns the old one's outcome as `previousRecording`.

### `list_sessions`

- **Does:** lists every session by name: project folder, kind (`run` or `attach`), whether it is live, `processId` (the process `run_project` started, the `Godot_console` wrapper on Windows that `stop_project` ends; null for an attach), `gameProcessId` (the game's own process, the one a debugger attaches to; null until the bridge has connected), and a recording's state.
- **Use:** when a call is refused for want of a session name, or to find a session a previous turn started; `gameProcessId` for a debugger's attach. A stopped run stays listed until its name is reused, with its last pids.
- **Edges:** while a debugger is attached to the game, every runtime call first pings it for 500 ms and fails at once when the game does not answer: "The game (pid <pid>) did not answer within 0.5 s while a debugger is attached: it is most likely paused at a breakpoint. Continue it in the debugger, or retry if it was only busy." A call that times out on such a game says it is paused under a debugger rather than stuck.

### `get_debug_output`

- **Does:** the newest stdout and stderr lines of a session's run (the current or last one under its name), whether it still runs, and its exit code.
- **Use:** when the game crashed or quit, or a tool reports the connection ended. `limit` (1 to 500, default 100); `before` (a line number) pages back, starting from the returned `stdoutFirstLine` or `stderrFirstLine`.
- **Edges:** the server keeps the last 500 lines of each stream; lines over 1000 characters are cut. Structured errors are better read from `get_errors`.

## See the game

### `take_screenshot`

- **Does:** captures the game's next drawn frame to a PNG under `.godot/godot-mcp/screenshots/` and returns its path and size, plus an image.
- **Use:** `responseMode` `path_only`, `preview` (default, at most `previewMaxWidth` wide, 480 by default) or `full`; `crop {x, y, width, height}` in the screenshot's pixels.
- **Edges:** the crop is in screenshot pixels, not viewport coordinates (see Rules). `path_only` saves context when only the file matters. The capture is the root viewport's texture: embedded popups and tooltips (Godot's default) are in it, but in a project that sets `display/window/subwindows/embed_subwindows=false` each popup is its own OS window and is missing.

### `get_ui_elements`

- **Does:** lists the game's Controls depth first: path, name, class, `rect` in viewport coordinates, visible, and where they apply `text`, `disabled` and `tooltip`.
- **Use:** before any click, to find a target by path; `filter` keeps one engine class and its subclasses (`BaseButton`); `visibleOnly` (default true) skips hidden subtrees; `offset` and `limit` (1 to 500, default 100) page.
- **Edges:** Controls only; `get_scene_tree` lists every node.

### `preview_scene`

- **Does:** starts the project quiet on one scene in its own short-lived session, pauses it before the scene's first frame, waits two drawn frames, screenshots it and stops: `{scene, path, width, height, previewPath?, cameraAdded, prep, errors?}` and the image.
- **Use:** after a headless scene edit, to see the result. `projectPath`, `scene` (ending `.tscn`, `.scn`, `.escn`, `.res` or `.tres`), `options {resolution, prepare}` (`resolution` as `WIDTHxHEIGHT`), `responseMode`, `previewMaxWidth`.
- **Edges:** the scene's `_ready` and the project's autoloads still run; when one changes scenes, the result's `scene` says what was shown. A 3D scene with no current camera gets a temporary one framing its visible geometry (`cameraAdded: true`). The scene's ending is checked case-sensitively. Prep, launch and capture share 60 s. It cannot run inside `batch_drive`.

## Drive input

All input tools answer `{pointer, heldButtonMask}` once the gesture has ended and two more frames have run; `click`, `drag` and `mouse_button` also say what they hit, each Control as `{path, class}` or null over none, a popup's included. One input call plays at a time per session. A target is `{element}` (a Control's path or name; its rect's centre) or `{x, y}` in viewport coordinates, never both.

### `click`

- **Does:** moves the pointer to `target`, presses, releases a frame later: `pressedOn` and `releasedOn` name the Control under the press and the release (with `doubleClick`, the second click's).
- **Use:** `target`, `button` (`left`, `right`, `middle`; default `left`), `doubleClick`.
- **Edges:** a showing tooltip is closed before the press (as `drag` and a `mouse_button` press do). The engine closes it on the press too (4.7.2 `viewport.cpp` L2011), but its removal lands a frame late, and the tooltip, not the Control beneath, would be reported as hit. A raw `simulate_input` press leaves the closing to the engine.

### `drag`

- **Does:** presses at `from`, moves in a straight line to `to` over `durationMs` (default 300, at least 3 frames), each motion carrying the held button, releases at `to`. `pressedOn` and `releasedOn` name the Control under the press and under the release point (not the one Godot sends the release to, which is always the pressed one), `guiDragStarted` whether Godot's GUI began a drag, `dropAccepted` whether a Control took the drop.
- **Use:** drag and drop, sliders, panning; `button` as `click`.
- **Edges:** Godot starts a GUI drag only once the path passes `gui/common/drag_threshold` (10 px by default); a shorter drag is a click.

### `type_text`

- **Does:** types `text` into the focused Control, one key press and release a character on a US layout; `\n` is Enter, `\t` Tab.
- **Use:** after a `click` on a LineEdit or TextEdit gives it focus.

### `key`

- **Does:** taps, presses or releases one key: `key` is a Godot Key constant without `KEY_` (`Enter`, `Escape`, `Space`, `A`, `F1`, `Up`), `action` `tap` (default), `press` or `release`, `modifiers` any of `shift`, `ctrl`, `alt`, `meta`.
- **Use:** shortcuts, `ui_accept`/`ui_cancel`, holding a movement key (`press`, then later `release`).
- **Edges:** a printable key carries the character it types unless `ctrl`, `alt` or `meta` is held.

### `mouse_button`

- **Does:** moves to `target`, then presses or releases `button` (`action` `press` default, or `release`), or with `action` `move` only moves, pressing nothing and closing no tooltip (`button` ignored); held buttons stay in later motions' `button_mask`. A press answers `pressedOn`, a release `releasedOn`, a move `hoveredOn`.
- **Use:** a drag by hand: `mouse_button` press, `simulate_input` `mouse_motion` events, `mouse_button` release; for paths `drag` cannot draw. `move` to hover without waiting for a tooltip; `hover` waits for it.

### `hover`

- **Does:** moves the pointer to `target` (an element or a point) pressing nothing, held buttons kept in the motion, then, when the Control under it has a tooltip, waits for the tooltip to show. Returns `{pointer, heldButtonMask, hoveredOn, tooltip, warning?}`: `hoveredOn` the Control under the pointer (`{path, class}`, null over none), `tooltip` `{text, x, y, width, height}` in viewport coordinates (`text` null for a custom tooltip with no Label), or null.
- **Use:** checking a tooltip's text or look: hover, then `take_screenshot` with `crop` around the returned rect. `options {tooltip, timeoutMs}`: `tooltip: false` answers right after the move; `timeoutMs` 0 to 10000, by default `gui/timers/tooltip_delay_sec` plus 1 s.
- **Edges:** Godot starts a tooltip's timer only from a motion over a Control that can process (4.7.2 `viewport.cpp` L2117, L2136), so over a pausable Control in a paused game `hover` answers at once with `tooltip: null` and the warning "the game is paused and <path> cannot process, so its tooltip timer never starts; resume, hover, then pause". A timer once started fires even if the game pauses after, so hover, then `frame_control pause`, keeps the tooltip up. A tooltip due but not shown in time answers the warning "no tooltip showed within <n> ms". Over a Control with no tooltip it answers at once, no warning.

### `simulate_action`

- **Does:** injects an InputMap action: `{pointer, heldButtonMask}` as `simulate_input` returns.
- **Use:** driving a game by its actions (`jump`, `ui_accept`) instead of the keys bound to them. `action`, `options {mode, strength}`: `mode` `tap` (default: press, a frame, release, as `key` taps), `press` or `release`; `strength` 0 to 1 (default 1), carried by the press.
- **Edges:** an action missing from the project's InputMap is refused. A pressed action stays held until released. It goes through `simulate_input`'s `action` event, so a raw sequence can still mix actions with other events there.

### `capture_input`

- **Does:** `mode: "start"` begins capturing the session's input; `mode: "stop"` ends it and returns `{events, count, truncated, ended?}`, the events in `simulate_input`'s format (`key`, `mouse_button`, `mouse_motion`, `joypad_button`, `joypad_motion`, `action`), with a `wait {ms}` before any event 20 ms or more after the one before, timed on the game's own clock. Feed `events` to `simulate_input` to replay them.
- **Use:** `options {sources, motion}` with start only: `sources` any of `real` (a person's input to the game's window) and `sent` (every event the server's input tools play, pads included), both by default; `motion` keeps mouse motions with no button held (false by default; a drag's motions are always kept, so it replays as a drag). To record a person playing, run with `options.quiet: false` or attach, start, let them play, stop.
- **Edges:** a quiet run gets no real input, so it captures `sent` only and start's result says so in `warning`. A gesture comes back as its raw events (a `drag` as a press, motions and a release; `type_text` as keys). Capture stops at 2000 events, waits included, with `truncated: true`. A capture survives its game: after a stop, restart or crash, `capture_input stop` naming the session returns what was captured before, with `ended` (`stop`, `restart`, `exit`). Only one capture runs per session; a second start is refused. Mouse wheel and extra buttons are not captured, since `simulate_input` cannot play them.

### `simulate_input`

- **Does:** sends raw `events`, one frame apart: `key`, `mouse_button`, `mouse_motion`, `joypad_button`, `joypad_motion`, `action` (an InputMap action with `pressed?`, `strength?`), `click_element`, `wait {ms}`.
- **Use:** input actions directly, curved mouse paths, precise event timing. An omitted `pressed` on a key, mouse or pad button is a press and a release a frame apart; a motion's `relative` and `button_mask` default from the pointer and held buttons.
- **Edges:** `x`, `y` are viewport coordinates as everywhere.

### `stress_input`

- **Does:** fires `count` random inputs, one a bridge call, each drawn uniformly and seeded from `pool`, and reports `{survived, seed, iterations, stoppedAt?, drawn {actions, keys, elements}, errors, skipped}`. An action is tapped as `simulate_action` taps it, a key as `key` taps it, an element clicked as `click` clicks it. `errors` lists each new error once, keyed by message, file and line, with the `iteration` it first appeared at and its `count`.
- **Use:** `pool {actions?, keys?, elements?}` (1 to 200 entries in all), `count` (1 to 1000, default 100), `seed` (0 or more; left out, one is drawn and returned), `options {gapMs}` (0 to 5000 real milliseconds after each input, default 0). The same seed and pool replay the same sequence, so a failing run is reproduced by passing its `seed` back.
- **Edges:** a draw the bridge refuses (an action missing from the InputMap, an element hidden at that moment, an unknown key) does not end the run: it is counted under `skipped` with the bridge's reason, the iteration it was first refused at and its count. The run stops at the first input the game does not answer, with `survived: false` and `stoppedAt`; otherwise `survived` means the game still runs and answers a ping at the end. It cannot run inside `batch_drive` (a thousand inputs outrun its deadline), and its result has no ordinary `errors` key: the run's own `errors` replace it.

### `gamepad_button`

- **Does:** taps, presses or releases one pad button as a pad's driver would: `button` a JoyButton without `JOY_BUTTON_` (`A`, `B`, `START`, `DPAD_DOWN`, `LEFT_SHOULDER`...), `action` `tap` (default), `press` or `release`, `device` 0 to 15. Without `device`, all three pad tools (and `simulate_input`'s `joypad_*` events without one) inject on the lowest id no connected real pad holds, keep that id for the game's life while it stays free, and report it as `device`; claim that pad in a game's pad-selection step as a player would.
- **Use:** actions bound to pad buttons; the d-pad moves GUI focus through the default `ui_*` bindings.
- **Edges (all three pad tools):** the injected pad never shows in `Input.get_connected_joypads` (only the real pads do), so a game that waits for a connected pad needs an OS-level virtual pad. An explicit `device` a real pad holds is injected as asked, with a `warning` naming the pad; with real pads on all 16 ids and no `device`, the call is refused. Real pads also feed the game unless the session was started with `shutOutRealGamepads`, which closes every `Popup` (menus, `OptionButton` lists) as it opens: the bridge's re-sent focus-out is what a popup closes on.

### `gamepad_axis`

- **Does:** moves one axis: `axis` (`LEFT_X`, `LEFT_Y`, `RIGHT_X`, `RIGHT_Y`, `TRIGGER_LEFT`, `TRIGGER_RIGHT`), `value` (-1 to 1 for sticks, Y down-positive; 0 to 1 for triggers), `device`, `options {durationMs, release}` to sweep and then let go.
- **Use:** triggers, analogue strength past a deadzone.

### `gamepad_stick`

- **Does:** pushes `stick` (`left` or `right`) to `position {x, y}`, both axes each frame; `device`, `options {durationMs, release}`.
- **Use:** menu navigation with a stick, character movement.
- **Edges:** a push moves GUI focus once, on the change from released to pressed; the next move needs a release first (`options.release: true`, or a push to `0, 0`).

## Time

### `frame_control`

- **Does:** `action` `pause` or `resume` sets `SceneTree.paused`; `step` advances exactly `count` (1 to 1000, default 1) drawn frames, or physics ticks with `options.unit: "physics"`, and leaves the game paused; `time_scale` sets `Engine.time_scale` to `scale` (above 0, at most 100). Returns `{paused, timeScale, processFrames, physicsFrames}`.
- **Use:** freezing the game to inspect it; `step` with `options.screenshot: true` to capture the exact frame reached; `time_scale` to fast-forward a slow sequence.
- **Edges:** nodes whose `process_mode` ignores pause keep running. A step counts drawn frames, so it is refused while the window is minimized or in low-processor mode, and a step whose frames stop being drawn fails at its deadline. A step fails if the game pauses itself midway. While a step or a `monitor_property` runs, other steps, `pause` and `resume` are refused.

### `monitor_property`

- **Does:** samples a live node's property once per frame for `samples` frames and returns the changes: `{samples: [{frame, value}], requested, droppedDuplicates, elapsedMs, pausedAtFrame?}`, `frame` counting from 0 at the first sample.
- **Use:** seeing how a value moves over time (a tween, a velocity, a counter) in one call. `node`, `property` (subproperty paths like `position:x`), `options {samples, unit, changesOnly}`: `samples` 1 to 600 (default 60), `unit` `process` (default) or `physics`, `changesOnly` (default true) drops a sample equal to the last kept one (numbers within 1e-6) and counts it in `droppedDuplicates`; the first is always kept.
- **Edges:** each sample, the first included, is taken when the next frame starts, before the nodes process it. Refused while the game is paused, and while a step or another monitor runs; while it runs, `frame_control`'s `pause`, `resume` and `step` are refused. A missing node or property is refused up front; a node freed partway samples as null. A game that pauses itself partway ends the monitor there: it returns the samples so far with `pausedAtFrame` (numbered like `frame`; that frame is not sampled), still a success, `requested` unchanged. It ends at 10 s plus 100 ms a sample, saying how many frames it got.

### `wait_for`

- **Does:** waits until `condition` holds, checked each frame, exactly one of `{node, exists}`, `{node, property, equals}`, `{node, signal}`, `{expression}` or `{uiChanged: true}`: `{met, elapsedMs, frames, value}` (`args` for a signal), or on timeout `{met: false, ..., last}`.
- **Use:** after an input, to wait for its effect instead of sleeping; `timeoutMs` 0 to 120000, default 10000. `{uiChanged: true}` waits for a UI change nobody can name in advance: it compares the visible Controls (as `get_ui_elements` lists them), the focus owner and the top popup with the baseline the bridge snapshots when the first input gesture since launch, or since the last met `uiChanged` wait, starts. Later gestures keep that baseline, so a press and a separate release, or a batch's several inputs, all count; a met wait uses it up and a timeout keeps it. Its `value` is `{appeared, disappeared, appearedCount, disappearedCount}` (node paths, at most 20 in each list, the counts full), plus `focus` and `popup` `{before, after}` when those changed; on a timeout `last` is null. `property` takes subproperty paths (`position:x`); `equals` compares as `run_script` returns values, numbers within 1e-6. An `expression` sees `node`, `root`, `tree`, `Input` and `Engine`. `options.screenshot: true` captures the frame the condition was met on, as `take_screenshot` does, adding `screenshot {path, width, height, previewPath, previewWidth, previewHeight}` and a preview image at most 480 px wide: for a scene that changes faster than a following `take_screenshot` can catch.
- **Edges:** a timeout is a result (`met: false`), not an error. While the game is paused only a signal wait or a check-once wait (`timeoutMs: 0`) is accepted; a check-once wait is refused for a signal. `equals: null` is not supported: use an expression (`node.target == null`). With `options.screenshot`, the condition is checked inside each frame's draw, so the image shows the state the check saw; a timed-out wait captures nothing, a check-once or signal wait captures the draw of the frame running when it is met, which in a running game can already show nodes that processed after the check (pause first for an exact image), and a frame not drawn (a minimized window, low-processor mode) answers a `warning` and no image. A property the node lacks fails the call; an expression that does not parse fails it. `uiChanged` takes no `node` and is refused when no gesture has taken a baseline since launch or since the last met `uiChanged` wait; a gesture the bridge refuses (an unknown element or key) still takes one. Tooltips, drag previews and the bridge's own nodes never count as a change; the change may already have landed during the gesture's settle frames, so the wait can be met at frame 0.

## Inspect and change the running game

### `get_scene_tree`

- **Does:** lists the live nodes depth first: path, name, class, script, groups, childCount.
- **Use:** `root` (a node to list from), `className` (with subclasses), `group`, `options {maxDepth, offset, limit}`.
- **Edges:** the bridge's own nodes are left out. `className` and `group` filter the listing, not the walk.

### `inspect_node`

- **Does:** reads a live node: `{path, class, script, properties}`, by default its script variables and the inspector's properties.
- **Use:** `node`, and `properties` to read exactly those names (a name the node lacks fails).
- **Edges:** values over 2000 characters come back as `{valuePreview, valueLength}`.

### `set_property`

- **Does:** sets one property of a live node, converting the JSON `value` by the property's declared type, and reads it back: `{path, property, before, after}`.
- **Use:** putting the game into a state to test (`{x, y}` for a Vector2, `"#rrggbb"` or `{r, g, b, a}` for a Color).
- **Edges:** fails, putting the old value back, when the read-back differs (a read-only property, a clamping setter). Typed arrays and dictionaries (`Array[int]`, `Dictionary[String, int]`), exported or not, take a JSON array or object; one whose elements, keys or values are Objects or Resources (`Array[Node2D]`, `Array[Texture2D]`) is refused, and the error says why.

### `call_method`

- **Does:** calls `method` on a live `node` with `args` converted by the parameters' declared types, awaiting a coroutine: `{path, method, value}`.
- **Use:** triggering game logic directly (spawn, damage, load a level); C# methods, `internal` ones included, are reached. `options {timeoutMs}` (1 to 120000, default 10000).
- **Edges:** fails when the method is missing, the argument count does not fit, an argument does not convert (an array of Objects says why), or Godot refuses the call. A GDScript error inside it ends it with a null `value` and comes back in `errors`; the call itself succeeds, so read `errors`.

### `cs_members`

- **Does:** lists the C# members of a `target`, exactly one of `{node}` (a path or bare name; the node must have a C# script), `{type}` (a full name with its namespace, e.g. `CsProbe.Tally`: statics and constructors) or `{handle}`: `{type, members: [{kind, name, signature, static}], total, offset, next?}`. Kinds are `constructor`, `method`, `property`, `field`, `event`; every overload is its own entry, spelled as C# declares it (`internal string Hit(int amount)`). `options {name, nonPublic, offset, limit}`: `name` a case-insensitive part of the member name, `nonPublic` true by default, paged 100 at a time (at most 500), sorted by name then signature.
- **Use:** before calling into a C# game object whose members Godot's own call cannot reach or show: private fields, overloads, generics, static classes. Reads only: no getter or constructor runs.
- **Edges:** lists the game's own types down to, not including, the first Godot class (`describe_class` lists Godot's API); accessors, backing fields, record plumbing and the members Godot's source generator adds are left out. A node without a C# script is refused, naming its Godot class; so are the bridge's own nodes, a type no loaded assembly has, and a handle from an earlier run. The first C# call of a game loads the helper into it (about 2 s cold); a project with no C# assembly, or an unbuilt one, is refused before the game is asked.

### `cs_get`

- **Does:** reads a C# property or field of a `target` (as `cs_members`: `{node}`, `{type}` or `{handle}`), private and internal ones included: `{value, type}`, `type` the value's runtime full name (the member's declared type for a null). `member` is a name or a dotted path through properties, fields, list indexes and dictionary keys (`Pending.Options[0]`, `Scores["key"]`); a `{type}` target starts at a static member. `options {maxDepth, keep}`: nested objects written 8 levels deep by default (1 to 32); `keep` also returns a `handle` usable as `{handle}` in a later C# call.
- **Use:** for game state Godot's own `get` returns null for: private fields, records, `List<T>`, statics of a plain C# class.
- **Edges:** a getter runs game code. A Godot object comes back as `{"$node": path}`, `{"$object": class, id}` off the tree, or `"<freed object>"`; a value over 20000 characters as `{valuePreview, valueLength}`. `keep` on a null returns no handle and a `warning`; on a value type, a handle to a copy, with a `warning` that a set through it does not reach the source. Fails on a method name, on an instance member named from a `{type}`, on a `{handle}` to a freed Godot object, and when a getter throws, with the exception's type, message and stack.

### `cs_set`

- **Does:** sets a C# property or field of a `target` and reads it back: `{member, before, after}`. `member` is a name or dotted path whose last segment is a property, field, list index or dictionary key (`Pending.Count`, `Items[2]`, `Scores["key"]`); `value` is JSON converted by the member's type: a record or class from an object, an enum by name or number, a list from an array, an interface or abstract member from `{"$handle": h}` or `{"$node": path}`.
- **Use:** to put a C# game object into a state for a test when no method gets it there.
- **Edges:** writes what reflection allows: non-public and `init` setters and readonly instance fields; refuses a property with no setter (or no getter, since it cannot be read back), a `const`, a `static readonly` field, and a path through a struct, whose set would change a copy (set the struct whole). A list index or dictionary key must already exist. When the member reads something else after the set (a setter that clamps), the old value is put back and the call fails with both (each cut at 2000 characters); a read-back that throws puts the old value back too, and a put-back that fails says what the member now reads. `before` and `after` over 20000 characters come back as `{valuePreview, valueLength}`. A setter that throws fails with the exception's type, message and stack.

### `cs_call`

- **Does:** calls a C# method or constructor of a `target` (as `cs_members`), private and internal ones included: `{value, type}`, `type` the value's runtime full name, `System.Void` for a void. A `{node}` or `{handle}` calls an instance method, a `{type}` a static one, and `member: ".ctor"` on a `{type}` constructs one. `args` is a JSON array in parameter order, each converted as `cs_set` converts a value; a `params` parameter takes its array as one value, an `out` takes null, and what `ref` and `out` parameters hold after the call comes back as `outs {name: value}`. `options {signature, typeArgs, keep, timeoutMs, maxDepth}`.
- **Use:** for C# game methods `call_method` cannot reach: overloads sharing a name and count (`Hit(int)`/`Hit(float)`), generics (`typeArgs: ["int"]` or full names), records and `List<T>` arguments, interface parameters (`{"$handle": h}`), statics of plain C# classes, async methods.
- **Edges:** when several overloads fit, the call fails listing each with its ready-to-paste `options.signature` array (in `cs_members`' spelling). A returned `Task` or `ValueTask` is awaited up to `timeoutMs` (10000 ms by default, at most 120000); past it the call fails and the `Task` keeps running in the game. `keep` returns a handle, with `cs_get`'s warnings; a `Node` made by `.ctor` is freed after the call unless kept, and a kept one is outside the tree, with a warning to free or add it. A thrown exception fails with its type, message and stack. Godot's own methods are refused, pointing to `call_method`; a property or field, pointing to `cs_get`; pointer and `Span`-like parameters are refused. Values and outs over 20000 characters come back as `{valuePreview, valueLength}`.

### `snapshot_subtree`

- **Does:** captures a live subtree for a later `diff_snapshots`: every node under `node` (default the current scene's root), keyed by its path from that node (`.` for itself), with the properties the inspector shows and its groups (sorted, internal `_` ones left out). Returns `{snapshotId, node, nodeCount}`, not the data.
- **Use:** before an action whose effects you cannot list in advance. `options {properties, ignore, maxNodes}`: `properties` narrows each node to those names (read even when the inspector hides them; `groups` only when named), `ignore` drops names, `maxNodes` (default 2000) refuses a bigger subtree with its count.
- **Edges:** snapshots are of the running game only, held per session, the 16 most recently used; ids (`s1`, `s2`, …) never repeat in a session, and every snapshot is dropped when the run stops, exits or restarts, or when an attached game's connection ends. Inside one `batch_drive`, a later step cannot read an earlier step's `snapshotId`, since arguments are fixed before the batch runs.

### `diff_snapshots`

- **Does:** compares two snapshots: `{added, removed, changed: [{node, property, before, after}], addedCount, removedCount, changedCount}`, node paths relative to the snapshot's root, each list at most 200 entries with full counts.
- **Use:** `beforeId` from `snapshot_subtree`; without `afterId` the subtree is captured again now, with the before snapshot's root and options. Numbers compare within 1e-6, so a float that only round-trips differently is not a change.
- **Edges:** a property only one side has is a change whose missing side's key (`before` or `after`) is left out, not null. An id that is not held (evicted, or from a run that stopped or restarted, or an attached game that has gone) is refused with a hint to take a new one. Two ids are not checked to share a root: nodes match by relative path.

### `run_script`

- **Does:** runs GDScript inside the game; the `script` must `extends RefCounted` and define `func execute(scene_tree: SceneTree) -> Variant` (it may await). Returns `{value}` as JSON.
- **Use:** anything `inspect_node`, `set_property` and `call_method` cannot express: loops, several nodes at once, engine singletons. `timeoutMs` default 30000.
- **Edges:** fails on a compile error, or when `execute` returns null and an error is located in the script itself; a null with errors elsewhere still succeeds. A value over 20000 characters comes back as `{valuePreview, valueLength}`.

## Errors and debug output

### `get_errors`

- **Does:** the session's logged errors and warnings, oldest first: `{errors: [{seq, type, message, file, line, function, stack}], next, dropped}`.
- **Use:** to see warnings (tool results carry errors only), or what happened between calls. Pass `since` = the previous call's `next` to read only newer entries (`next` equals `since` when nothing new arrived); `limit` 1 to 500, default 50.
- **Edges:** the server keeps the last 500; `dropped` counts those lost. See also `get_debug_output` above.

## Replay with checks

### `batch_drive`

- **Does:** plays 1 to 100 `steps` against a running game in one call and stops at the first failure: `{passed, steps: [{index, tool | assert, ok, result | error}], failedAt?}`.
- **Use:** a regression check or a long repeatable sequence. A step is `{tool, args}` (any runtime tool of this server but `batch_drive`, with its own arguments) or an assertion `{assert, ...}`: `property {node, property, equals, timeoutMs?}`, `expression {expression, timeoutMs?}`, `wait {any wait_for condition, timeoutMs?}`, `no_errors {}`, `screenshot {name, tolerance?, maxChangedRatio?}`. Any step may carry `session`; the batch's `session` is the default.
- **Edges:** `property` and `expression` default to `timeoutMs: 0` (checked once, now); `wait` defaults to 10000. `no_errors` covers errors since the batch started or the previous `no_errors`. `take_screenshot` and `compare_screenshot` steps are forced to `path_only` and images dropped. Not batchable: the lifecycle tools, `preview_scene` and the headless tools. The whole batch stops after 300 s.

## Screenshot baselines

### `save_screenshot_baseline`

- **Does:** stores the next drawn frame as a named baseline under `.godot/godot-mcp/baselines/`: `{name, baselinePath, width, height, crop}`.
- **Use:** once the game shows the state you want to guard; `name` (1 to 64 letters, digits, `.`, `_`, `-`, starting with a letter or digit), `crop`, `options {overwrite}`.
- **Edges:** an existing name is refused without `options.overwrite`. Crop to the region under test: a clock or particle elsewhere on screen makes every comparison fail.

### `compare_screenshot`

- **Does:** captures the next frame with the baseline's crop and counts pixels whose channel differs by more than `tolerance` (0 to 255, default 2): `{changedPixels, totalPixels, changedRatio, bbox, match, diffPath?, ...}` and a diff image (changed pixels red).
- **Use:** `name`; `options {maxChangedRatio, responseMode, previewMaxWidth}`, `maxChangedRatio` default 0.
- **Edges:** a mismatch is a result, not an error. A missing baseline fails, and so does a capture of another size (a different `--resolution` or window size from when it was saved). Pause the game (`frame_control`) first for a stable frame.

## Recording

### `record_mark`

- **Does:** marks the `start` or `stop` of a clip in a session launched with `options.record`, at the movie frame reached: `{mark, frame, seconds}`.
- **Use:** bracket the moment worth keeping; when the run ends (`stop_project`, the game quitting, `restart_project`) each start-stop pair is cut to its own file with ffmpeg, and a start left open clips to the end.
- **Edges:** marks alternate start, stop. A recording runs at a fixed 60 fps, so game time advances 1/60 s a frame whatever the wall clock does: millisecond timeouts and gesture durations no longer match game time. Capped at 10 minutes of frames; not with `--headless`. Without ffmpeg the full movie is kept and the result reports an error. If the server exits, the movie is lost.

## Headless scene editing

These run a headless Godot on the project's files with no game started. All are refused while a session is live on the folder. The write tools always run the prep, open the scene as the editor does and save it with its uids kept; each edit is all or nothing. The scene edit tools write `.tscn` only; a binary `.scn` can be read but not edited. A node inside an instanced scene is refused (edit that scene's own file), except where a tool below says an editable instance or an instance's root is allowed.

### `describe_class`

- **Does:** lists a class's properties, methods with typed arguments, signals, constants and enums: `{className, inherits, inheritsChain, canInstantiate, isScript, scriptPath?, language?, properties: [{name, type, default?}], methods: [{name, args: [{name, type, default?}], returnType, isVirtual, isStatic}], signals: [{name, args: [{name, type}]}], constants, enums, methodCount, offset, limit, warning?}`.
- **Use:** before `call_method`, `set_property` or a headless edit on a class you do not know, engine (`Button`) or the project's own `class_name`. `projectPath`, `className`, `options {inherited, offset, limit}`: an engine class lists its own members unless `inherited`; methods are sorted by name and paged (`limit` 1 to 500, default 100, `methodCount` the total). When a game runs on the project (or `session` names one) that game answers; otherwise it is read headless.
- **Edges:** an unknown name is refused with up to 5 close names (`Did you mean: …`). Properties the editor neither shows nor stores (`Node.name`, `Node2D.global_position`) are listed without `default`, since Godot keeps none for them. A script class's lists include its base scripts' members; `inherited` adds the native base's. A C# script class whose lists come back empty carries a `warning` that the build may be red or stale. Engine enum members appear under `enums`, not `constants`. The editor binary's class list includes editor-only classes, so a suggestion may name one.

### `validate`

- **Does:** loads scripts, scenes and resources without running the game and reports errors: `{valid, checked, results, engineErrors?, csharp?, prep}`; `results` lists only files with errors.
- **Use:** after edits, before a launch. `targets` (1 to 50 files), or none for every git-versioned `.gd`, `.tscn` and `.tres` (at most 500); `options {prepare}`.
- **Edges:** Godot's parser reports only the first parse error in a file: fix it and validate again. A failed C# build comes back as `csharp {build: "failed", errors}` and makes `valid` false while GDScript is still checked. Autoloads' `_init` still runs. Every C# script a target reaches, through instanced scenes and resources however deep, is checked for its class.

### `get_scene_file_tree`

- **Does:** lists a scene file's nodes without running any code, instanced scenes expanded and an inherited scene's base merged: path (from the root, `.`), name, type, script, instance, groups, childCount.
- **Use:** before a scene edit, to learn node paths. `scenePath`, `root`, `options {maxDepth, offset, limit, prepare}`.

### `create_scene`

- **Does:** writes a new `.tscn` holding one root node, with a new uid: `{scenePath, root, uid}`.
- **Use:** `scenePath`, `rootType` (default `Node2D`; a Node class or a script's `class_name`), `rootName` (default the file name in PascalCase), `options {overwrite}`.
- **Edges:** an existing file is refused without `overwrite`; a replaced file keeps its uid.

### `save_scene`

- **Does:** opens a scene and saves it again, in place or to `newPath` (save-as): `{scenePath, savedTo, uid}`.
- **Use:** copying a scene to a new file; `options {overwrite}` for an existing `newPath`.
- **Edges:** a new file gets a new uid; saved over a file, the copy takes that file's uid.

### `add_node`

- **Does:** adds a node named `nodeName` of `nodeType` (a Node class, a script's `class_name`, or a scene path, added as an instance) and saves: `{path, type, instance?}`.
- **Use:** `options {parent, properties}`: parent path from the root (default `.`), and `{name: value}` set before the node is added.
- **Edges:** a sibling name clash, a parent inside an instanced scene (an instance's own root may be the parent), and a scene instancing itself are refused; a property that does not take adds nothing.

### `delete_nodes`

- **Does:** deletes 1 to 100 `nodePaths`, with their children, and saves: `{deleted}`.
- **Edges:** the root, a node inside an instanced scene and a node an inherited scene gets from its base are refused; deleting an instance's root removes the instance. One refusal saves nothing.

### `duplicate_node`

- **Does:** copies `nodePath` and its subtree right after it, or last under `options.parent`, and saves: `{originalPath, newPath}`.
- **Use:** `newName`, else the node's name or the editor's serial name (`Sprite` gives `Sprite2`).
- **Edges:** instances stay instances; connections within the copy and out of it are kept, connections into it are not. The root is refused.

### `attach_script`

- **Does:** sets `scriptPath` (`.gd` or `.cs`) on `nodePath`, replacing its script, and saves: `{path, script, previous?}`.
- **Edges:** the script must compile and extend the node's class. A C# script is refused while the C# build fails. An instance's root and a node from a base scene are allowed, saved as overrides.

### `load_sprite`

- **Does:** sets the `texture` of `nodePath` (Sprite2D, Sprite3D, TextureRect, NinePatchRect, Polygon2D, or any node with a Texture2D `texture`) to `texturePath` and saves: `{path, texture}`.
- **Edges:** an image never imported is imported first by the editor's full scan, which writes `.import` and `.uid` files across the project: expect those in the diff. An image in a folder Godot does not scan (a name starting with `.`, or holding `.gdignore`) is refused.

### `set_node_properties`

- **Does:** sets 1 to 100 `updates` `{nodePath, property, value}`, reads each back, and saves: `{results: [{nodePath, property, before, after}]}`.
- **Use:** values as `set_property` takes them, typed arrays and dictionaries included; a resource as a `res://` or `uid://` path, `{resource: path}`, `{type, ...properties}` or null; a Node-typed export as a path from the scene's root.
- **Edges:** a node from a base scene, or inside an editable instance, may be set (saved as an override); inside another instance it is refused. All or nothing.

### `get_node_properties`

- **Does:** reads 1 to 50 `nodes` `{nodePath, properties?, changedOnly?}` of a scene file; nothing is saved: `{results: [{nodePath, type, script?, properties} | {nodePath, error}]}`.
- **Use:** `changedOnly` for just what the scene file stores for the node; `options {prepare}`.
- **Edges:** the scene is instantiated to read it, so its scripts' `_init` runs (never `_ready`). A missing node fails its own entry alone.

### `get_node_signals`

- **Does:** lists a node's signals and the persistent connections the scene makes from each; nothing is saved.
- **Use:** before `connect_signal` or `disconnect_signal`; `options {prepare}`.
- **Edges:** connections made by `connect()` in code are not listed. `inherited: true` marks a connection from an instanced or base scene, which only that scene's file can change. A C# script's signals are missing while its build fails (`warning` says so).

### `connect_signal`

- **Does:** saves a persistent connection from `signal` on `nodePath` to `target {nodePath, method, binds?}`: `{from, signal, target, method}`.
- **Edges:** refused, saving nothing: a missing node, signal or method; an emitting node inside a non-editable instance; arguments plus binds that do not fit the method; a bind that does not convert (an array of Objects says why); a signal already connected to that method.

### `disconnect_signal`

- **Does:** removes the connection from `signal` on `nodePath` to `target {nodePath, method}`, whatever its binds.
- **Edges:** an inherited connection is refused, naming the scene whose file makes it.

### `export_mesh_library`

- **Does:** builds a GridMap MeshLibrary from a 3D scene by the editor's Import from Scene rule and writes it to `outputPath` (`.tres` or `.res`); the scene is only read: `{outputPath, items: [{id, name, shapes, navigation}], replaced?}`.
- **Use:** `meshItemNames` to keep only some items; `options {overwrite}`.
- **Edges:** every MeshInstance3D with a mesh below the root is an item named after its node; two nodes of one name make one item (`replaced`). No previews are made. A file the scene uses is refused as `outputPath`, whether directly, through an instanced scene or a resource file, or by a node an earlier `batch_scene_operations` step changed; the refusal names the file or node (`through res://cell.tscn`, `through node Grid`). A replaced `.res` gets a new uid (a `.tres` keeps its own).

### `batch_scene_operations`

- **Does:** runs 1 to 100 scene edits on one scene in a single headless Godot and saves once: `{passed, steps, failedAt?, uid?}`.
- **Use:** any change of more than one edit: each step is `{tool, args}`, `tool` one of `delete_nodes`, `attach_script`, `duplicate_node`, `load_sprite`, `add_node`, `set_node_properties`, `connect_signal`, `disconnect_signal`, `export_mesh_library`, `args` that tool's own arguments without `projectPath` and `scenePath`. Later steps see earlier ones (add a node, then connect its signal).
- **Edges:** every step is checked before Godot starts. At the first failed step nothing is saved and no library written. One Godot start for the lot, so it is much faster than the tools one by one.

## Project profiles: `godot-mcp.json`

A `godot-mcp.json` beside `project.godot` sets launch defaults for every `run_project` on the folder. Top-level keys: `scene`, `userArgs`, `engineArgs`, `resolution` (`WIDTHxHEIGHT`, lowercase `x`), `quiet`, `presets`. Each preset under `presets` takes the same keys but `presets`, plus `session`. `run_project`'s `options.preset` layers a preset over the top level, and the call's own arguments override both: `scene`, `resolution` and `quiet` are replaced by the later layer, `userArgs` and `engineArgs` append. The session name is `options.session`, else the preset's `session`, else the folder's. Parsing is strict: an unknown key, a wrong type or a bad resolution refuses the launch with the file's path and the fix. `restart_project` reuses the merged launch and does not re-read the file. The profile is not a tool and applies to `run_project` only.

```json
{
  "resolution": "1280x720",
  "presets": {
    "server": { "userArgs": ["--server"], "session": "server" },
    "client": { "userArgs": ["--connect", "127.0.0.1"], "session": "client-1" }
  }
}
```

## Worked drives

### Reproduce a UI bug and screenshot it

1. `run_project {projectPath}`: note `session`; check `prep` when the project is C#.
2. `get_ui_elements {filter: "BaseButton"}`: find the button's `path` and check `disabled` and `visible`.
3. `click {target: {element: "Main/Menu/Settings"}}`: read `errors` in the result; a handler error shows its file and line here.
4. `wait_for {condition: {node: "SettingsPanel", exists: true}, timeoutMs: 3000}`: `met: false` means the panel never appeared; `last` shows what was seen.
5. `take_screenshot {}`: look at the preview; pass the `path` to the user.
6. `get_errors {since: 0}` for warnings, then `stop_project`.

### Step physics frames and assert with `wait_for`

1. `run_project {projectPath, scene: "res://tests/jump_test.tscn"}`.
2. `frame_control {action: "pause"}`.
3. `set_property {node: "Player", property: "velocity", value: {x: 0, y: -400}}`: check `after` matches.
4. `frame_control {action: "step", count: 10, options: {unit: "physics", screenshot: true}}`: `physicsFrames` is 10; the image shows the frame after the tenth tick. The game stays paused.
5. `wait_for {condition: {node: "Player", expression: "node.position.y < 300"}, timeoutMs: 0}`: a check-once wait, accepted while paused; `met` is the assertion and `value` what the expression returned. A property equality works the same way: `{node: "Player", property: "position:y", equals: 280}`.
6. Repeat steps 4 and 5, then `frame_control {action: "resume"}`.

### Edit a scene headlessly, then preview it

1. `list_sessions`: if a session is live on the folder, `stop_project` it; the headless tools are refused otherwise.
2. `create_scene {projectPath, scenePath: "res://ui/hud.tscn", rootType: "Control"}`.
3. `batch_scene_operations {projectPath, scenePath: "res://ui/hud.tscn", steps: [{tool: "add_node", args: {nodeType: "Label", nodeName: "Score", options: {properties: {text: "0", position: {x: 16, y: 16}}}}}, {tool: "attach_script", args: {nodePath: ".", scriptPath: "res://ui/hud.gd"}}]}`: `passed: true` and a `uid`; on failure `failedAt` names the step and nothing was saved.
4. `validate {projectPath, targets: ["res://ui/hud.tscn", "res://ui/hud.gd"]}`: `valid: true`, or the first error per file.
5. `get_scene_file_tree {projectPath, scenePath: "res://ui/hud.tscn"}`: confirm the node paths.
6. `preview_scene {projectPath, scene: "res://ui/hud.tscn", options: {resolution: "1280x720"}}`: look at the image; `errors` holds anything `_ready` raised.

### Drive a gamepad menu

1. `run_project {projectPath, options: {shutOutRealGamepads: true}}`: so a pad on the desk cannot move the focus under you (a menu that opens a `Popup` needs real pads live instead: shut-out closes popups).
2. `get_ui_elements {filter: "Button"}`: learn the menu's buttons.
3. `gamepad_button {button: "DPAD_DOWN"}`, or `gamepad_stick {stick: "left", position: {x: 0, y: 1}, options: {release: true}}`: without `release` a second push does not move focus again.
4. `wait_for {condition: {expression: "root.gui_get_focus_owner().name == \"Options\""}, timeoutMs: 1000}`: `met: true` confirms where focus went.
5. `gamepad_button {button: "A"}`, then `wait_for` on the screen it opens.

### Record a clip around a bug

1. `run_project {projectPath, options: {record: true}}`: `recording.path` is the movie in progress.
2. Drive to just before the bug. `record_mark {mark: "start"}`: note `frame`.
3. Trigger the bug with the input tools. The run is at a fixed 60 fps, so millisecond durations no longer match game time: wait on conditions with `wait_for`, not on durations.
4. `record_mark {mark: "stop"}`.
5. `stop_project`: `recording.clips` lists one file for the pair, and the full movie is gone; `recording.error` means ffmpeg was missing or failed and `recording.path` is kept instead.

### A `batch_drive` regression check against a baseline

1. Once, in a known-good build: drive to the state, `frame_control {action: "pause"}`, `save_screenshot_baseline {name: "hud-idle", crop: {x: 0, y: 0, width: 400, height: 80}}`.
2. After a change: `run_project`, then one call:

   `batch_drive {steps: [{tool: "click", args: {target: {element: "Main/Menu/Play"}}}, {assert: "wait", node: "Hud", exists: true}, {tool: "frame_control", args: {action: "pause"}}, {assert: "property", node: "Hud/Score", property: "text", equals: "0"}, {assert: "screenshot", name: "hud-idle", maxChangedRatio: 0.001}, {assert: "no_errors"}]}`

3. Read `passed`. On failure `failedAt {index, reason}` names the step; for a screenshot failure the step's error carries the comparison, and `compare_screenshot {name: "hud-idle"}` outside the batch returns the diff image to look at.
