---
name: godot-mcp
description: Use when running, seeing, driving, inspecting or editing a Godot project through the `godot` MCP server (`mcp__godot__*` tools) — launching or attaching to a game, clicking, dragging, typing or pressing pad buttons in it, screenshotting it, waiting for an effect, reading or changing live nodes, stepping frames, recording a clip, checking a visual baseline, or editing .tscn files headless. Also before the first godot tool call of a session, and when a godot tool call is refused or returns something unexpected.
---

# Driving Godot through the godot MCP server

The `godot` server (`mcp__godot__*`, source at github.com/leftos/godot-mcp) runs a Godot 4.7 project with a bridge injected, so you can see it, drive it, inspect it and edit its scenes.

This is the walkthrough; every tool's full arguments, edges and result are in the reference, `TOOLS.md` beside this file, which ships with the installed server and matches its version (online, for the latest unreleased state: https://github.com/leftos/godot-mcp/blob/main/docs/TOOLS.md). Read a tool's section there before its first use in a session when its edges matter (input, time, headless edits).

An agent whose godot tools are deferred (its `tools:` list holds `ToolSearch`) loads every tool a step needs in one call, `ToolSearch` with `select:mcp__godot__run_project,mcp__godot__get_ui_elements,mcp__godot__click,…`, rather than one search per tool.

## The loop

A drive is always the same five moves: **start → look → act → wait → check**, then **stop**.

1. **Start.** `run_project {projectPath}` (the folder holding `project.godot`) returns once the bridge is connected; note the `session` and pass it to every later call, and, in a C# project, the `prep` (a stale assembly was built, imports were run). It starts **quiet**: off-screen, unfocused, silent, real input shut out.

   `scene` picks another scene; `userArgs` go after `--` (the game reads them with `OS.get_cmdline_user_args()`), `engineArgs` before it (`["--resolution", "1280x720"]`). A `godot-mcp.json` beside `project.godot` holds per-project defaults and named `presets` (`options.preset`), and `prepWrapper`, the project's gate the prep builds and imports through; `prep.buildLog` / `importLog` name the logs.

   A server or backend the game needs beside it starts with `run_process {projectPath, process}` when the file's `processes` section declares it; note that session too. The call waits for the process's `ready` line when the file declares one (or `options.ready {output, timeoutMs}` gives one), and `wait_for {output}` reads any later line.
2. **Look.** `get_ui_elements` lists Controls with their rects in viewport coordinates; `get_scene_tree` lists every live node; `take_screenshot` returns an image (a 480 px preview by default; `responseMode: "path_only"` when only the file matters).
3. **Act.** Aim at a Control by path: `click {target: {element: "Main/Menu/Play"}}`. Use `{x, y}` only where no Control exists, and then in **viewport** coordinates, never pixels read off a screenshot (a stretched or letterboxed window's screenshot is not the viewport).
4. **Wait.**

   `wait_for` a condition after every act instead of sleeping: `{node, exists}`, `{node, property, equals}`, `{node, signal}`, `{expression}` or `{text}` (a caption: exactly one visible Control shows that text, `under` scoping it, `exists: false` waiting for it to go; prefer it to polling `run_script`) (`options.screenshot: true` captures the very frame it was met on, when the scene moves on faster than a following `take_screenshot`); when the game exposes its own state (`get_game_state`), wait on a key of it instead of a screenshot: `{node: "Board", state: "turn", equals: 2}` (a key path such as `seats[0].hp`, or `$` for the whole state; the node must be in the `mcp_state` group; a key the state lacks is not met); before a press on something that moves (a card hand re-sorting, a tween), wait for it to hold still with `{node: "Hand", stable: 10}` (its rect, transform or named `property` unchanged for 10 frames; `options.then` presses in the settled frame); when you cannot name what the input will change (a menu, a dialog), `{uiChanged: true}` reports which Controls appeared or disappeared and whether focus or the top popup moved (it compares Controls' paths, focus and the top popup, never label text); a wait in a parallel group beside an input in another session has no gesture of its own to take the baseline, so it asks `{uiChanged: "now"}`, which snapshots when the wait starts.

   A met bridge wait's `metAt` is the drive time of the frame that met it.

   When a run prints a line (a join notice, a debug line), `{output: "<a .NET regex>", stream?, after?}` matches its captured stdout/stderr, stdout first for `any`, and only lines numbered after `after` when given (numbers carry over a restart); the server answers it, so it works while paused, and an attached session, which captures no output, is refused.

   Its `value.at` is the drive time the server read the line, not when the game printed it, so a line found with `after` can carry one from before the call.

   Every stamp is on the drive clock, one clock for every session: `metAt` is converted from the game's frame, with `clockErrorMs` the error of that conversion, while a `batch_drive` entry's `startMs` and `endMs` are exact, so `metAt - startMs` orders a guest's reaction to a host's click.

   A timeout is a result (`met: false` with `last`), not an error, and never a pause: `timeoutMs` is load-adjusted, so an `expression: "false"` wait stretches under load. To hold for game time (a shot mid-effect), wait `{gameMs: N}` or `{frames: N}`, which count the game's own scaled delta or unpaused frames.

   To act in the very frame a condition comes true (slow the game for a shot, call a method), add `options.then {call?, timeScale?, pause?}`: it runs once in that frame and the result says which. `pause: true` holds the game on that frame for later reads; add `options.screenshot: true` when a crop must match the values, since a plain wait pauses before the frame's `_process`.
5. **Check.** Every runtime result carries `errors` the game raised during the call, with file and line. The key is absent when there were none. Read them before the next step: a handler that threw still "succeeds".
6. **Stop.** `stop_project` at the end of every `run_project` session. For a game joined with `attach_project`, `detach_project` leaves it running and `stop_project` quits it. A game `run_project` started also quits by itself when its server exits or is stopped (an install, a crash), running its exit work; an attached game keeps running.

   A process session is stopped by name, never with `projectPath`: `stop_project {session}` ends the program and everything it started at once, while `stop_project {projectPath}` never stops one.

## Every tool, by job

**Sessions.**

`run_project` starts a game; `attach_project` joins one started some other way (a second client, a `--server` run, the editor's Play button: start it within `waitSeconds` after the call, never before; `options.quiet: true` for one a debugger launches, which also mutes it; `options.mute: true` silences a watched game); to join a game that is already running (a launcher script, a windowed smoke, a state a player reached), `arm_project` the folder first: every game with a window started on it from then on waits dormant (a `--headless` one is left alone), `attach_project` joins it (`options.pid` when several wait; `list_sessions`' `armed` lists them), `detach_project` returns it to waiting, and `disarm_project` ends that.

A game started before the arm has no bridge and cannot be joined; `restart_project` relaunches a run session with the same scene and arguments after you change code, keeping its name and output; `stop_project` and `detach_project` end them; `list_sessions` names the live ones (`includeStopped: true` adds runs that ended, still reachable by name); `get_debug_output` reads a run's stdout and stderr when it crashed or quit, and the whole output is in the result's `log` (`run_project` and `restart_project` return it too).

`run_process` starts a companion a project declares in `godot-mcp.json`'s `processes` section (a game server, a mock backend) as a process session: it has no bridge, `list_sessions` lists it with `kind: "process"` and a null `gameProcessId`, `get_debug_output` and `wait_for {output}` read what it prints (also written to `.godot/godot-mcp/process-<session>.log`), and `stop_project {session}` ends it and everything it started at once.

A process that declares a `prep` (a command, like its `command`) runs it first, through the project's `prepWrapper` when there is one, and again on `restart_project`; a failed prep refuses the start, and the result's `prep` holds its `{command, exit, ms, log}`.

Every other runtime tool refuses it, except `restart_project`, which stops it and starts its stored command and ready line again; `batch_drive` runs a `wait` with `output` on it and refuses a tool step. Stop it by name, never with `projectPath`.

`stop_project`'s `alreadyExited` says the game had ended before the stop, and `gameExitCode` is the game's own code (`exitCode` is the console wrapper's). Several sessions can run at once, on one folder or several (a host and a headless guest of one project): name them with `options.session`, and launch a headless client with `engineArgs: ["--headless", "--resolution", "<the project's viewport size>"]`, since a headless root window is 64x64.

Pass the `session` `run_project` returned to every call from the first one: another agent's game can start on the same server at any time, and a call without a name is then refused.

**Seeing.** `take_screenshot` (read-only, saves under `.godot/`; with `crop` in viewport coordinates, so a rect from `get_ui_elements` or `hover` crops as it is; for a 2x crop, launch at twice the project's size with `--resolution`, which is given exactly), `get_ui_elements` (filter by class, e.g. `BaseButton`), and `preview_scene`, which shows one scene without playing it: it starts the scene quiet, screenshots its first frames and stops, beside any live session.

`save_screenshot` takes `take_screenshot`'s arguments plus a `path` and writes the PNG to that project file, e.g. a tracked picture, or into the git checkout's `.tmp` folder above the project (`../../.tmp/shot.png`); it refuses an existing file without `options.overwrite`, and is not batchable.

**Scratch scenes.**

A project whose scratch scenes implement `PlayStep(int)`, `GetStepCount()`, `GetStepName(int)` and `GetStatus()` runs them with `run_scratches {projectPath}` (or `scenes: [...]` for a few) in place of its `Run-Scratches.ps1`: each scene plays in a fresh headless game, each step paced in game time, and a red scene names its failing step with the errors from the error feed (identical ones folded with a count) and the matching output lines, cut to a small budget (`options.details` adds the green steps' detail and a larger budget), and stops there unless `options.keepGoing` plays on so `failures` lists every red step, and `options.steps` plays only some steps (indices, names or ranges like `"2-5"`; an object `{"<Scene>": [...]}` gives each scene its own list and a scene it leaves out plays every step), plus the steps the scene's `scratch.setup` entry declares, which come back as `setup` beside `selected`.

A scene whose root also has a public `GetStepUntil(int)` returns, per step, a `wait_for` condition (an empty dictionary, or null, for a paced step): the step is then played inside one `wait_for` on it, `PlayStep` as its `options.call`, and ends when the condition holds instead of after the full pace.

The pace is the most game time such a step may take: a condition not met in it is red and stops the scene, even with `options.keepGoing`, and the step's detail carries `until {met, gameMs, elapsedMs, frames}` in place of `gameMs`.

A step whose work runs on after `PlayStep` returns (an awaited task, a tween) needs a `GetStepUntil` condition: a paced step's next `PlayStep` comes one pace later whether or not that work is done, `keepGoing` or not. Only errors (`push_error`, `GD.PushError`, a throw from `PlayStep`) and lines the scene's patterns match turn a step red; `GetStatus()` text and `printerr` never do.

For a windowed look at one moment, run the scene with `run_project` and play its step by the name `run_scratches` reports: `call_method` on the scene root with method `PlayStep` and the name as its one argument (or the same as an `options.call`), with no index to count.

`options.timeScale` (0.01 to 100, default 1) runs every scene's game that many times faster: a pace stays game time and takes pace / timeScale s of wall time (the wait after the last step lasts the longer of its pace in game time and in wall time), and the kill bound scales with it.

An until that counts frames or physics ticks (`frames`, `settled`) sees that many times fewer frames per pace, and at a high scale one frame's delta can exceed a short pace, so such a scene may need a longer pace. A game that sets `Engine.time_scale` itself overrides the launch's scale and has its long steps cut short with a "clock stood still" error: that scene opts out with `"timeScale": 1` in its profile pace object (`{seconds, reason, timeScale: 1}`).

Its folder, paces, ceilings, setup steps (`setup {"<scene>": [step, ...]}`, an index or an exact step name, for a partial run whose selected steps need earlier ones), known-red scenes and patterns live in `godot-mcp.json`'s `scratch` section (a scene is killed at a ceiling derived from its paces; `options.ceilings` or `scratch.ceilings` `{"<scene>": seconds}` gives a scene its own, in load-adjusted seconds from its first step, not scaled by `timeScale`, the call's winning); `get_debug_output {session}` with the scene's `session` reads its whole output afterwards.

A run of many minutes goes in the background: `run_scratches` with `options.background: true` returns `{runId, statusFile, logFile, scenes}` at once and plays on in the server (one background run per project; `run_scratches` on it is refused until it ends).

Wait on its log with a `Monitor` running `timeout <limit> tail -n +1 -F <logFile>` and matching `^godot-mcp scratch run \S+ finished:` (the completion line: `done, passed …`, `stopped`, `interrupted` or `failed: …`) rather than holding a call open.

The limit is the scenes' paces (each divided by the time scale, the last step's pace once more, in wall time above scale 1) plus a minute or two: a server killed outright (an install, a crash) never writes the line, and `scratch_status` then answers `live: false` with `state: running`.

Never drop the `timeout`: on Windows a bare `tail -F` outlives the Monitor's shell when the Monitor ends, and runs on for good.

Then read the verdict with `scratch_status {projectPath, runId}`; `stop: true` ends it. A big run is cheaper to read with `only: "red"`, which keeps every scene's line but lists only the red steps in `details` (a foreground `run_scratches` takes it as `options.only`; a background one refuses it, so pass it here). Its sessions are skipped by calls that name no `session`, so name one to drive or read it.

**Input.**

`click`, `drag` (a GUI drag starts only past 10 px), `type_text` (into the focused Control: click the LineEdit first), `key` (`Enter`, `Escape`, `A`, with `action` `press`/`release` to hold one), `mouse_button` (a press or release by hand, for paths `drag` cannot draw; `action: "move"` only moves), `hover` (the pointer over a Control, waiting for its tooltip and returning its text and rect; hover while the game runs, since a paused Control never starts its tooltip timer, then pause if needed), `scroll` (wheel notches at a target, `notches` and `options.factor` for how far, `options.via: "pan"` for a trackpad pan; a ScrollContainer moves an eighth of its page per notch, and a child with mouse filter Stop swallows the wheel unless it sets `force_pass_scroll_events`; a click on a tab scrolled out of a tab bar is refused naming the `scroll` call, or the arrow clicks, that bring it in).

Pads: `gamepad_button` (`A`, `START`, `DPAD_DOWN`), `gamepad_stick` (push, then `options.release: true` before the next push moves focus again), `gamepad_axis` (triggers, analogue values). `simulate_input` sends raw event sequences, InputMap `action` events included, when no gesture fits.

The injected pad never shows in `Input.get_connected_joypads()`. Without `device`, pad input goes to the lowest id no real pad holds, reported as `device` in the result: claim that pad in the game's pad-selection step.

**Time.**

`frame_control` pauses, resumes, steps exact drawn or physics frames (`step` leaves the game paused; `options.screenshot: true` captures the frame reached; `options.until`, any `wait_for` condition but `frames` or `output`, stops the step on the first frame that meets it and leaves the game paused there, `count` then the most frames, 1000 by default: `step {count: 1000, options: {until: {...}}}` from a running game freezes it on that frame, and an `until` never met answers `met: false` with `last`; `options.input` sends `simulate_input`'s raw events inside chosen stepped frames, each with a `frame`, so a press lands in an exact frame: a key or button with `pressed` left out is released one frame later, an `until` met early reports `inputNotSent`, and `held` lists what is still pressed, so release it) and sets `time_scale`.

While paused, `wait_for` accepts a signal wait, an output wait or a check-once wait (`timeoutMs: 0`): that set is how you assert on an exact frame. A condition already true is met at frame 0: `wait_for` with `options.edge: true` (an exists, property or expression wait with a timeout) waits for it to turn true after being false.

`watch` records how properties and expressions moved frame by frame, and the signals fired meanwhile (`tracks.signals`): `start` it, drive with any tools (input, waits, steps), `stop` it for each track's change points and range; `run` does it in one call over a `window` of `frames` or `gameMs`. Every point, event and voice ends with `at`, its drive-clock ms, comparable with `wait_for`'s `metAt` and `batch_drive`'s `startMs`. One watch per game run: always `stop` the one you started.

**Inspecting and changing the live game.**

`inspect_node` reads a node's properties (a C# node's `[Export]` members by default; name a private field in `properties` to read it, as `set_property`, `wait_for` and `watch` do); `set_property` sets one from JSON by its declared type and reads it back (`{x, y}` for a Vector2, `"#rrggbb"` for a Color), putting the old value back when the read-back differs; `call_method` calls a method (C# ones too) and awaits a coroutine; `list_game_tools` lists the game's own cheat and debug methods marked `[GodotMcpTool]`, with their owner and argument schema: read it before a C# game's docs or source for a cheat, then `call_game_tool {name, args: {<parameter>: value}}` calls one by name, its arguments checked against the schema before the game is asked.

`cs_members` lists a C# node's or type's members, private ones and every overload included, that Godot's call cannot show; `cs_get` and `cs_set` read and write a C# property or field by name or dotted path (`Pending.Options[0]`), private ones and statics included, where Godot's own `get` reads null (`cs_set` reads back and puts the old value back as `set_property` does; `cs_get`'s `options.keep` returns a handle for a later `{handle}` target); `cs_call` calls a C# method or constructor (`.ctor` on a `{type}`) with JSON arguments, choosing among overloads (`options.signature` when several fit), closing generics (`options.typeArgs`) and awaiting a `Task`; `run_csharp` runs a C# method body in the game (`var t = Node<Duel>("Duel"); return t.Bind(new Update(...));`), with `Node`, `Get`/`Set`/`Call` (private members), `await` (`await ToSignal(Tree, SceneTree.SignalName.ProcessFrame)` waits a frame) `Cancellation` (the token its timeout cancels; pass it to awaits so a timed-out snippet stops) and `Log(value)` (a line kept in the run's log, returned beside the value and printed in a throw's failure text) in scope, refused when the game runs an older build than the one on disk (`restart_project`); `run_script` runs a GDScript `extends RefCounted` with `func execute(scene_tree: SceneTree) -> Variant` for anything those tools cannot say, but in a C# project a C# member Godot does not marshal (a `List<T>`, a record, a private field) is out of its reach: read it with `cs_get`, `cs_call` or `run_csharp`.

`get_errors` lists logged errors and warnings since a `seq` (`since` = the last call's `next`).

**Regression checks.** `batch_drive` replays up to 100 tool steps and assertions (`property`, `expression`, `wait`, `no_errors`, `screenshot`, `fired`) in one call and stops at the first failure.

A step `{parallel: [steps]}` runs 2 to 8 tool or assertion steps at once, each on its own `session`, started in listed order and ended together: put a `wait` before the click that should satisfy it so the wait is armed first, and a failed member cancels the rest (an input already sent plays to its end). Every entry reports `startMs` and `endMs`, and a met bridge wait's result its `metAt` with the `clockErrorMs` of its conversion.

In a batch over several sessions, a top-level entry's `errorsElsewhere` lists the errors the other sessions raised since the entry before, so a fault on the guest shows on the host step that caused it, and `no_errors {sessions: "all"}` (or a list of names) fails on any of them; `"all"` covers only the sessions another step of the batch addresses, never a process session, and a pass lists the names it checked.

An error raised inside a parallel member's call shows only in that member's own `errors`, and one landing between two steps can show on no entry, so end a multi-session batch with `no_errors {sessions: "all"}`. A `fired {node, signal, equals?}` assertion checks the `fired` list of the nearest earlier click, key, drag, mouse_button or type_text step, matching `node` by its trailing path segments and `equals` against the emission's whole argument array.

A `wait` assertion takes any `wait_for` condition, `{output}` included: the server answers it from the run's captured output, so it works while paused and refuses an attached session.

`save_screenshot_baseline` stores a cropped frame once; `compare_screenshot` diffs a later frame against it. A game that animates draws a different frame each time, so pause it with `frame_control` before saving a baseline, before comparing, and around a `take_screenshot` before/after pair (pause, shot, change, shot, resume).

**Recording.** `run_project {options: {record: true}}` records a movie at a fixed 60 fps (game time then no longer matches wall time; `wait_for`'s `timeoutMs` and gesture durations count clip time, so an `expression: "false"` wait of 1500 ms holds a 1.5 s still); `record_mark` `start`/`stop` pairs become `.mp4` clips when the run ends.

Add `dropIdle: true` beside `record` to cut the idle frames drawn between your tool calls out of each clip (the clip then plays just the action, silent); leave it out when a still moment or the sound matters.

A game that keeps its own clock is recorded another way: on a not-quiet windowed session launched without `record` (`run_project {options: {quiet: false}}`, or an attach), `record_mark {mark: "start", options: {fps?, maxSeconds?, audio?}}` records the window in real time, as a player sees it, and `record_mark {mark: "stop"}` returns `clip: {path, seconds, frames, averageFps, width, height, audio}`.

A real-time recording ends with its session too: `stop_project` and `detach_project` return its clip as `recording: {mode: "realtime", clips: [path], startedSeconds, warning?}` and `restart_project` as `previousRecording`, so a recording left running is not lost (the new game after a restart does not record until you `start` again); one that ended on its own (`maxSeconds`, the game quitting) is returned once, by whichever of `record_mark stop`, `stop_project`, `detach_project` and `restart_project` comes first.

A clip that could not be finished comes back as `recording.error` and does not fail the stop, restart or detach; a `start` while the session is ending is refused until the game has restarted.

A real-time recording needs ffmpeg and the capture helper, records the game's own sound as an audio track unless `options.audio: false` (a muted session records no track and warns; a game that mutes itself after the start gives a silent one; the system volume and mute do not change what it records; the sound needs Windows build 20348 or later), and is refused on a quiet or headless session; keep the window unminimized, since a minimized window holds its last frame (the stop result warns, naming each span).

**Headless scene editing** (no game runs; a live session on the folder keeps running, and sees an edit only after `restart_project`).

`validate` loads scripts, scenes and resources and reports errors, C# build included (a `.cs` target gets its own errors and warnings and the scenes that attach it); `get_scene_file_tree` lists a scene file's nodes (paths from its root: `.`, `Boss/Sprite`); `create_scene`, `save_scene` (save-as), `add_node`, `delete_nodes`, `duplicate_node`, `move_node`, `attach_script`, `load_sprite`, `set_node_properties` (a Control's anchors need `layout_mode` 1 first), `get_node_properties`, `get_node_signals`, `connect_signal`, `disconnect_signal` edit or read one scene, an edit leaving the rest of the `.tscn` as it was (a `warning` says when it could not; `save_scene` rewrites the whole file in 4.7 form; a result's `uidFilesWritten` names `.uid` files written for scripts that had none: commit them with the scene); `export_mesh_library` builds a GridMap MeshLibrary from a 3D scene; `batch_scene_operations` runs many edits on one scene in one Godot start and saves once, all or nothing.

`move_node` to another parent rewrites the stored `NodePath` properties of the scene's nodes that point into or out of the moved subtree so they resolve to the same node, and lists each in its `rewrittenPaths`; animation track paths and `NodePath`s inside resources are not rewritten.

Prefer it for any change of more than one edit, then `validate`, then `preview_scene` to see the result.

## Rules that bite

- **Viewport coordinates everywhere** an input tool takes `x, y`. Prefer `{element}` targets (full paths from `get_ui_elements` when names repeat): an element may be a 2D or 3D world node too (an `Area2D`, a die on a table), aimed at its origin or at `offset` from it through its camera, with `aimedAt` in the result saying where; aim at the node instead of projecting it by hand or emitting its signal.

  A button or a card is aimed at by the text it shows with `{text: "New Game"}` (`under` narrows it when several show it; `match: "prefix"` or `"contains"` finds a Control by the start or a part of its text, a multi-line label by its first line's opening words, and `wait_for`'s `text` condition takes it too; several matches are still refused, and `item` text always matches exactly), and a tab, a list row or a tree item with `{element: "Tabs", item: {text: "Alex"}}` (`index`, or `path` for a Tree), and a dropdown or menu entry with `{element: "Difficulty", item: {text: "Hard"}}` on the `OptionButton`, `MenuButton` or open `PopupMenu` (a `click` opens a closed popup first, `aimedAt.opened`; an entry with a submenu is only hovered until the submenu opens, and `aimedAt.item.submenu` names the `PopupMenu` to target in the next call), never by setting `current_tab` or `selected` and emitting the signal by hand, before computing its position (a `[hint]` or tooltip link in a `RichTextLabel` is `{element: "Log", item: {text: "<its tooltip text>"}}`, or `item: {index: 0}`; a `hover` that finds no tooltip over such a label names the nearest span and its index): a Button's text can differ from its name, and a miss names the node called that and what it shows.

  An element that is hidden, being freed, named by several nodes, covered at its centre by another Control, or with its centre outside the viewport is refused with nothing pressed: the error names the cover (or the viewport) and the rects, so click by `{x, y}` in the visible part, bring it into view, or wait until it clears.
- **A condition `expression` is Godot's `Expression`, not GDScript**: no lambdas, `if`/`else`, `is`, `as` or `not in` (write `not (a in b)`), and both sides of `and` are evaluated, so guard an index with a separate wait or a method of the game's (the engine error such an index raises is kept out of `errors` and counted in `mutedErrors`).

  A wait that is met after checks that errored says so in `failedChecks`: a condition that holds too early (a label's placeholder text) reads as met, so wait on the value you need, not on "not empty".
- **A misnamed or mistyped argument is refused by name**, with the keys the tool takes or the kind the value needs: read the refusal and fix the call, since nothing ran.
- **One input at a time per session**, each answering once its gesture has ended and two frames have run.
- **Headless tools run beside a live session** on the same folder; the game sees a scene edit only after `restart_project`.

  On a GDScript-only project one headless Godot stays up per folder between calls and holds the folder: before removing or renaming a worktree or folder it serves, run `stop_project {projectPath}` on it (it also stops by itself after 5 minutes idle); `list_sessions` shows the hosts under `headlessHosts`. A C# import that is due is refused while another game runs on the folder: stop it, or pass `options.prepare: "never"`.
- **Headless node paths are relative to the scene root** (`.`, `HUD/Score`); live node paths are absolute (`/root/Main/Button`), under the root (`Main/Button`), a bare name, or a unique name as the scene and source write it (`%Rows`, looked up in every scene; `Glossary/%Rows` after its owner), in the C# tools as in the GDScript ones; a miss names the deepest node that exists and its children, so read the refusal before calling `get_scene_tree`.

  A `run_csharp` snippet names the game's `internal` members directly; privates go through `Get`/`Set`/`Call`.
- **C# projects:** a launch, a restart and every headless tool build a stale assembly first; a failed build refuses the launch with the compiler errors in the result; a headless tool reports it as a `csharp` block (`build: "failed"` and the errors) and refuses a save that needs C#, quoting them. A C# autoload error under a red build is the missing assembly, not your scene: fix the build first.
- **Nothing lands in the project's tracked files.** The bridge rides in an `override.cfg` beside `project.godot`, hidden from git by `.git/info/exclude` and removed at stop or detach, or by `disarm_project` while the folder is armed; a user's own `override.cfg` is refused, never overwritten. Screenshots, baselines and recordings go under `.godot/godot-mcp/`.

  After a drive, with no other drive and no arm on the folder, `Test-Path <project>/override.cfg` is false; if a crashed run left one, `stop_project {projectPath}` clears it once every server it names has exited (or delete it by hand; its first line is `; godot-mcp: bridge injection, removed when the run stops`; a file without that line is the project's own, never delete it).
- **Quiet by default** (off-screen, silent, capped at 60 fps): pass `options.quiet: false` only when the user wants to watch or play along, and `options.mute: true` beside it to watch without sound.
- **A `--headless` game draws no frames**: `take_screenshot`, `save_screenshot`, the baseline tools, `capture_frames` and a stepped screenshot refuse it ("a headless game draws no frames…"), and a `wait_for` screenshot comes back as a `warning`. Read state with `get_game_state`, `get_ui_elements` or `run_script`, or run the scene windowed. This holds for a `run_scratches` session and a headless game you attached.
- **Real gamepads reach even a quiet game.** A connected pad's drifting stick can move GUI focus, and a focus move between a click's press and release drops the click. When clicks seem to vanish now and then, or a drive counts presses, launch with `options.shutOutRealGamepads: true`.
- **The bridge keeps the hover on the injected pointer every frame.** A game's own re-pick reads the OS cursor, which is garbage on a quiet run's hidden desktop. When a drive needs the game's own hover handling to decide what is hovered, launch with `options.repickHover: false`: the bridge then re-picks only after a scene change and at the end of a GUI drag (a drop or a cancel).

## When something goes wrong

- A call refused for want of a session: `list_sessions`, then pass `session`, on this call and every later one.
- The game crashed or "the connection ended": `get_debug_output`, then `get_errors`.
- "Paused under a debugger": a debugger holds the game at a breakpoint; continue it there (the `debug-live` skill), never restart it. A debugger attaches to `list_sessions`' `gameProcessId`, not `processId`; `stop_project` ends its debug session too.
- A class you do not know (an engine node, or the game's own `class_name`): `describe_class` lists its properties, typed method signatures and signals, from the running game or headless, and suggests close names for a typo.
- A value that moves over time (a tween, a velocity, the focus owner): `watch {action: "run", tracks: {properties: [{node, property}]}, window: {frames: 60}}` samples it each frame in one call and returns only the changes, with `min`/`max` and the frames they came at; an expression track (`{name, expression: "root.gui_get_focus_owner()"}`) watches what is no property, and `minDelta` thins a value that moves every frame.

  To line up two games (a host and a guest), `watch` each and compare the items' `at`: it is on the drive clock, as `metAt` and `startMs` are, and `clockErrorMs` bounds its error.

  To see what happened while you acted (a dialog leaving after a click, a value across stepped frames), `watch start` first and `watch stop` after. A hitch: add `monitors: ["frame_ms"]` to the tracks, and its `spikes` name the frames over budget beside the signals and values of the moment (a hitch is one long frame, then one near 0).

  A sound: `tracks.audio.players` (`{players: {under?}}`) lists each voice's `start` (stream, bus), `pause`, `resume` and `end` (`finished`, `stopped` or `freed`) on the same frame clock as the click that caused it, which a `run_script` read after the call misses for a short sound; a `start` says the game asked for the sound (a quiet run plays on the Dummy driver), and an `end` with no `start` is a voice `start`'s `players.playing` listed.

  Whether it reached a bus: `tracks.audio.buses` (`["Effects"]`, or `["*"]` for every bus) answers per bus `heard` ranges (frames whose peak passed `thresholdDb`, default -60), `peakMaxDb` and `volumeDb`/`mute` change points; a sound under about 81 ms in a quiet run may never reach a peak read, so a voice's `start` says it fired, and with the bridge's `mute` Master reads -200 while the buses that send to it still read.

  Frames at set moments (0.5 s and 1.2 s into a step, or every 0.1 s for 2 s): `capture_frames {at}` or `{options: {every, for}}` grabs them in one call on game time, following `time_scale`.

  When the moments count from a method that starts the effect (a scratch's `PlayStep`), give it as `options.call {node, method, args}` (`PlayStep` takes the step's name there and in `call_method`, as `run_scratches` reports it), or a C# game's own tool by name as `{tool, args: {...}}`: the bridge calls it in the frame the clock starts, so no round trip between a `call_method` and the capture shifts the points.

  When the moments count from a condition (a tray showing, then slow motion), give it as `options.start {node, property, equals | exists | expression, edge?, timeoutMs?, then?, call?}`, laid out as `wait_for`'s: the points count from the frame it is met, `start.then {call?, timeScale?}` runs in that frame before `options.call`, and a start that times out answers `stopped: true` with `start: {met: false, last}`.

  A change an earlier tool call made can rise and fall before your next call's first check (that call reaches the game 0.2 s to seconds later), so put the call that causes the change in the same request: `start.call` (shaped as `options.call`; the bridge takes the start's baseline check, then calls, so the start sees its own call's rise), or `wait_for`'s `options.call`. An `every` series always has a point at 0, taken one frame into `options.call`'s motion, not before it.

  `wait_for` takes the same `options.call` with `{gameMs}`, `{frames}`, `{node, exists}`, `{node, property, equals}` or `{expression}`: the call runs in the frame the checks start and the first check follows it, so a step and its condition share a clock; a `signal`, `uiChanged` (`true` or `"now"`) or `output` wait refuses it.

  A game driven by InputMap actions: `simulate_action {action}` taps one. To hunt a crash no scripted drive finds: `stress_input {pool, count, seed}` fires random actions, keys and element clicks and reports the new errors by the iteration they appeared at; pass a failing run's `seed` back to replay it.
- A game that marks nodes with the `mcp_state` group and `_mcp_state()` (in C#, `_McpState()` of any accessibility, returning a record or anything else `cs_get` can write): `get_game_state` reads their state (turn, hit points, a cell) in one frame, cheaper than a screenshot and exact; `options.keys` narrows it, and it runs in `batch_drive`.

  To see what an action changed in that state, `get_game_state {options: {keep: true}}`, act, then `diff_snapshots {beforeId: <stateId>}`: one `changed` entry per leaf that moved (`seats[1].hp`).
- An action whose effects you cannot list: `snapshot_subtree` first, act, then `diff_snapshots {beforeId}` (no `afterId` re-captures now) for every node added, removed or changed. Ids die with a stop or restart, and a `batch_drive` step cannot read an earlier step's id.
- What a `click`, `drag`, `mouse_button`, `key` or `type_text` set off: read its `fired` (the signals emitted on the pressed node's chain, or a key's focus chain, so Enter in a LineEdit lists `text_submitted`, in handler order, each with its `listeners` count; `listeners: 0` on an engine signal means no Godot connection, null a C# `[Signal]`), instead of emitting the signal by hand: a hand emission skips the others the press fires.

  A `warning` says why a press did nothing (a disabled button, a paused game); a `watch` with signal tracks catches what is off the chain (`listenedOn`).
- A click that seems to do nothing: read its `pressedOn` (the Control it actually hit; a drag's `guiDragStarted` and `dropAccepted` say whether a GUI drag and drop happened), check `errors` in its result, then `get_ui_elements` for `disabled`/`visible`, then `wait_for` its effect rather than screenshotting at once.
- A bug a person can reproduce by hand but you cannot script: run with `options.quiet: false` (or attach), `capture_input {mode: "start"}`, let them play, `capture_input {mode: "stop"}`, then replay its `events` with `simulate_input`; the capture survives a crash, so stop it after the game dies too.
- Something not drawn, or drawn wrong (a collision shape, a path, a mesh the camera should show): launch flags such as `--debug-collisions` through `engineArgs` or a preset, and `set_property` on `/root`'s `debug_draw` for 3D draw modes, as "See why something is not drawn" in [TOOLS.md](TOOLS.md) says.
- A tool that is missing, confusing, slow or wrong for the job: that is friction with a tool we own. File it as "Filing friction" below says, then carry on with the task.

Game-specific drive lessons (a project's scenes, launch arguments, a known flaky screen) belong in that project's own docs (its DEVELOPMENT.md), not here.

## Filing friction

Friction is filed with `gh` as it happens, one issue per friction, never through a godot tool: the server stays closed to the outside world. An agent sent a brief (an implementer) does not post: it puts a line starting `godot-mcp friction:` in its report, and the session that sent it files it.

1. **Search first**: `gh issue list -R leftos/godot-mcp --state all --search "<tool name> <two or three words>"`. An open issue that matches gets a comment (`gh issue comment <N> -R leftos/godot-mcp --body-file <file>`) with your case, rather than a new issue; a closed one that matches is a regression, filed new and citing it.
2. **Write the body to a file** (the Write tool, under the project's `.tmp/`; a heredoc is refused), in this shape:

   ```
   > 🤖 Posted by Claude Code on behalf of @leftos.

   **Tool:** `<tool>` with `<arguments, as sent>`
   **What happened:** <the result or error, quoted, cut to what matters>
   **What was needed:** <the outcome you were after>
   **Workaround:** <what you did instead, e.g. a run_script, or "none">
   **Project:** <the game repo>
   **Version:** <the `version` of a `run_project`/`attach_project` result, else `%LOCALAPPDATA%\godot-mcp\VERSION`, else "unversioned build">
   ```

   The version (`0.1.0+<sha>`) names the build that ran, so an issue already fixed on `main` is recognised; the result's `version` is the one to trust, since the file describes the last install.

3. **File it**: `gh issue create -R leftos/godot-mcp --title "<tool>: <the friction in a few words>" --body-file <file>`, and carry on with the task. The title says what is wrong, not what to build.
