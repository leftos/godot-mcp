# Using the godot-mcp tools

For an agent driving a Godot project through this server: which tool fits a job, what each one does, and the edges that bite. Words such as session, quiet run, prep, baseline and headless run are defined in the [glossary](./README.md#glossary). Each tool's wire-level result shape and bridge command are in the tool table of [ARCHITECTURE.md](./ARCHITECTURE.md#tools); the tool's own schema in your client has every argument.

## Which tool for which job

| Job | Reach for | Instead of |
|---|---|---|
| Start the game and drive it | `run_project`, then the runtime tools | `attach_project`, which is for a game started some other way |
| Start a server or backend the game needs beside it | `run_process`, for a process the project declares in `godot-mcp.json` | a background shell job, or launching it by hand |
| Drive a game you (or the editor) started | `attach_project` | `run_project` |
| Look at a game a launcher or a player already has running | `arm_project` before it starts, then `attach_project` at any time | a delayed launcher timed against `attach_project` |
| See one scene without playing | `preview_scene` | `run_project` with `scene` plus `take_screenshot` plus `stop_project` |
| Play a project's scratch scenes step by step and get a verdict each | `run_scratches` | a `Run-Scratches.ps1` script parsing a `--scratch-auto` log |
| Run scratch scenes for many minutes without holding a call open | `run_scratches` with `options.background`, then a `Monitor` on its log, `scratch_status` to read or stop it | a foreground `run_scratches` call that outlives the client's tool timeout |
| Pick a thing to click | `get_ui_elements` (Controls, with viewport rects) | guessing coordinates from a screenshot |
| Click, drag, type | `click`, `drag`, `type_text`, `key` | `simulate_input`, which is for event sequences the gestures cannot make |
| Pick an option from a dropdown or a menu (`OptionButton`, `MenuButton`, `PopupMenu`) | `click` with `{element: <the button>, item: {text}}`, which opens the popup first | setting `selected` and emitting `item_selected` by hand; clicking the button, then coordinates read off a screenshot |
| Hover a Control, see its tooltip | `hover` (waits for the tooltip, returns its text and rect) | a `mouse_button` release as a hover, a `wait_for` as a sleep |
| Scroll a list or container, wheel-zoom | `scroll` (wheel notches or a trackpad pan) | a `run_script` that parses wheel events |
| Drive with a pad | `gamepad_button`, `gamepad_stick`, `gamepad_axis` | `simulate_input` joypad events |
| Land a check on an exact frame | `frame_control` (`pause`, `step`), then `wait_for` with `timeoutMs: 0` | `wait_for` with a timeout on a running game |
| Freeze the game on the frame a condition becomes true | `frame_control step {count: 1000, options: {until: <wait_for condition>}}` | a `wait_for` then a `pause`, which lands frames late |
| Watch a value change over frames | `watch {action: "run"}` with a property or expression track | a loop of `inspect_node` calls |
| See what several values did while you drive (a dialog leaving, the focus moving, a counter stepping) | `watch` `start`, then any tools, then `stop` | a sampling `run_script`, which keeps every other call from reaching the game while it runs |
| Find the frame that hitched, and what the game was doing then | `watch` with `tracks.monitors: ["frame_ms"]` beside the signals or values of the moment | a `run_script` timing `process_frame` gaps by hand; Godot's `time/process`, which holds the last second's longest frame |
| Check that a sound played (which player, which stream, when it started and ended) | `watch` with `tracks.audio.players` (`start`, then the click or step, then `stop`), or `run` with a one-frame window for what plays now; `tracks.audio.buses` for whether it reached a bus | a `run_script` reading `playing` after the call, which misses any sound shorter than the gap between calls |
| Frames at set moments of an animation or a timed sequence | `capture_frames` | a `take_screenshot` per moment, whose timing drifts |
| Press an InputMap action | `simulate_action` | `key` on a key bound to it |
| Find the input a game cannot survive | `stress_input` | a hand-written loop of random `simulate_input` calls |
| Wait for something to happen | `wait_for` | polling `inspect_node`, `run_script` or `get_debug_output` |
| Read or change a live node | `inspect_node`, `set_property`, `call_method` | `run_script`, which is for anything those three cannot say |
| Read a game's own state (turn, hit points, the cell a player stands on) | `get_game_state`, when the game marks nodes | a screenshot, or a sampling `run_script` |
| See everything an action changed in a subtree | `snapshot_subtree`, act, `diff_snapshots` | `inspect_node` on each node before and after |
| Learn a class's members | `describe_class` | guessing names, then reading `call_method` errors |
| Learn a C# object's or type's members, private ones and overloads included | `cs_members` | `describe_class`, which shows only what Godot's call can reach |
| Read or change a C# member Godot's `get`/`set` cannot reach (a private record field, a `List<T>`, a static) | `cs_get`, `cs_set` | `inspect_node`/`set_property`, which read such a member as null (a private field of a Godot type, such as a `float`, they do reach by name) |
| Run several C# steps in the game at once: build a record and pass it, loop, await | `run_csharp` | a chain of `cs_call`s, or `run_script`, which cannot name C# types |
| Find and call a game's own cheats and debug commands | `list_game_tools`, then `call_game_tool` | reading the game's source for its cheat methods, then `cs_call` with positional arguments |
| Call a C# method Godot's call cannot reach (overloads, generics, records, statics, an async `Task`) or construct a C# object | `cs_call` | `call_method`, which marshals only Variant types |
| Find a live node | `get_scene_tree` | `get_scene_file_tree`, which reads a scene file |
| Know what went wrong | the `errors` in each result, then `get_errors`, then `get_debug_output` | reading stdout first |
| Replay a known sequence with checks | `batch_drive` | one tool call a step |
| Turn what a person (or a drive) did into a replayable sequence | `capture_input` start, play, stop, then `simulate_input` with its `events` | writing the events by hand from a description |
| Catch a visual regression | `save_screenshot_baseline` once, `compare_screenshot` or a `screenshot` assertion after | comparing screenshots by eye |
| Keep a video of a bug | `run_project` with `options.record`, `record_mark`, `stop_project` | an OS screen recorder |
| Record the window as a player sees it, on the wall clock | `record_mark` start and stop on a not-quiet session launched without `options.record` | `options.record`, whose clock is fixed, or an OS screen recorder |
| Check scripts and scenes load | `validate` | launching the game to see if it errors |
| Edit a scene file | the headless scene tools, several edits at once through `batch_scene_operations` | writing `.tscn` text by hand |
| Set a project's launch defaults | a `godot-mcp.json` beside `project.godot` | repeating `engineArgs` on every `run_project` |

## Rules every tool shares

- **Arguments are checked by name.** An argument or options key a tool does not take is refused (`run_project has no argument 'optoins'; it takes: …`, `options has no 'prepar'; it takes: …`), as is a value of the wrong kind (`options.prepare takes a string, not true.`, followed by that option's description) and a missing required one (`run_project needs the argument 'projectPath'.`).

  Free-form values (`add_node`'s `options.properties`, `set_node_properties`' `value`) take any keys. `batch_drive` and `batch_scene_operations` steps are checked the same way, behind their step prefix.
- **Session.** Every runtime tool, and `restart_project`, `stop_project`, `detach_project` and `get_debug_output`, takes a last `session` argument; `run_process` takes it as `options.session`. Pass the session `run_project`, `attach_project` or `run_process` returned on every call from the start: another agent's game can start on the same server at any time, and there is no default session.

  It may be left out while only one session exists; with several live ones and no name the call is refused and lists the live ones, with a count of the stopped. A name no session has is refused the same way.

  A new session is named after its project folder, fitted to the name rule (1 to 64 letters, digits, `.`, `_`, `-`; any other character becomes `_`, so `My Game` becomes `My_Game`). When a live session on another folder already holds that name (two worktrees of one repo), the new one takes `<name>-2`, `-3`, …, cut to fit 64 characters; read it from the result's `session`.

  A process session (`run_process`) is a third kind beside a run and an attach: it has no bridge, joins no folder bookkeeping and writes no `override.cfg`, so the folder's quiet and shut-out rules do not bind it. Only `wait_for` with an output condition, `get_debug_output`, `restart_project`, `stop_project` and `list_sessions` reach it; every other runtime tool refuses it.

  Two sessions on one folder need distinct names: pass `options.session` to `run_project` or `attach_project`. The headless tools and `preview_scene` take `projectPath` instead and no session.
- **Errors ride along.** A runtime tool's result carries `errors` (file, line, stack) for every error the game raised while the call ran, and the call still succeeds, except `run_script` and `call_method` as their sections say. The key is present only when the game raised an error during the call: its absence means none. Read `errors` in every result before the next step.
- **Viewport coordinates.** Every `x`, `y` an input tool takes is in root-viewport coordinates, as `get_ui_elements` reports rects; the bridge maps them to the window whether it is stretched or letterboxed.

  A screenshot's pixels are the window's, which differ from the viewport's when the project stretches (a 640x360 viewport in a 1000x900 letterboxed window captures 1000x562), so a point read off a screenshot is not a viewport point; a `crop` is in viewport coordinates, so a rect `get_ui_elements` or `hover` reports crops as it is, at the window's scale. Aim at `{element}` where a Control or a world node exists.
- **Paging.** Long lists come back one page at a time with `total`, `offset` and, while more remain, `next`: pass `next` as the next call's `offset`. A value longer than its budget comes back as `{valuePreview, valueLength}`.
- **Quiet by default.** `run_project` starts the game quiet: unfocused, off-screen, click-through, silent, with no real input reaching it, and capped at 60 fps unless the project sets its own `application/run/max_fps` (a recording run keeps its fixed 60).

  Pass `options.quiet: false` only when the user wants to watch or play along, with `options.mute: true` when they want to watch without sound. Sessions on one folder must agree on quiet and on `shutOutRealGamepads`, across godot-mcp servers too; a new session that differs is refused, naming the other server's pid when it is another server's, and so is one whose install injects another bridge.

  `run_scratches` sessions are exempt, being headless: they always run quiet with real pads live and unmuted, neither bind nor are bound by the agreement within their own server, and a scratch session alone never refuses that server's run, attach, restart or arm. Another server's sessions on the folder still bind and are bound through `override.cfg`'s values, scratch ones included.
- **Headless tools and live sessions.** Every headless tool (`validate`, `get_scene_file_tree` and the scene tools) runs beside a live session on the project folder, of this server or another: the game keeps running and keeps its `override.cfg`, and the headless run's bridge stays off. A running game reads a scene only when it loads it, so see a headless edit with `restart_project` or `preview_scene`. `preview_scene` is not headless and runs beside a live session too.
- **Paths.** Headless and preview paths are `res://` paths or paths relative to the project folder, inside it, and must match the case of the file on disk; a case variant is refused with the right spelling. A node path in a scene file is relative to the scene's root (`.` for the root, `Boss/Sprite`), never `/root/...`.

  A live node (runtime tools) is an absolute path (`/root/Main/Button`), a path under the root (`Main/Button`) or a bare name found breadth first, or a unique name: `%Rows` alone is looked up in every scene under `/root` (several scenes holding it is refused, naming their owners), `%Rows/Label` reads on from it, and `Glossary/%Rows` after its owner's path; the same rule in the GDScript tools, the `cs_*` tools' `{node}` and `run_csharp`'s `Node(path)`.

  A path that names no node is refused with the base it was read from, the deepest node on it that exists, the name that node lacks and up to 10 of its children (`(+n)` for the rest): `No node 'Main/Missing' in the running game: a path is read from /root, and /root/Main has no child 'Missing' (children: HUD, Board); …`; a bare name says none was found anywhere under `/root`.

  Every tool that names a node refuses this way, the input tools' `{element}`, `wait_for`'s signal wait, `watch`'s tracks and a `{"$node": path}` argument to the C# tools included; the closing hint points to `get_ui_elements` for an input target and to `get_scene_tree` elsewhere.
- **C# projects.**

  A launch, a restart and every headless tool run the prep first: a stale C# assembly is built and missing or outdated imports are run (an asset whose source bytes changed since its import, read from Godot's `.godot/imported/*.md5` only when the source is newer, or whose `.import` settings changed since the server last saw them, recorded in `.godot/godot-mcp/import-fingerprints.json`; a settings change made before the server first prepped the folder needs the editor opened once); the result's `prep` says what was done, and `buildLog` / `importLog` name the full log of a build or import that ran (the next one overwrites it).

  An import that is due while another session's game runs on the folder is refused, not run under the live game: stop those sessions, or pass `options.prepare: "never"` to go on without it.

  A failed build refuses a launch with its compiler errors, naming the configuration built (`The Debug C# build of …`).

  The headless write tools will not save a scene that uses C# scripts while the build fails (nor `attach_script` a `.cs`, nor `connect_signal` to a C# method), and the refusal quotes up to 20 compiler errors, then `(and N more)`; its "Godot logged:" section leaves out the lines the missing assembly causes, counting them as `(and N lines a missing C# assembly causes, left out: the build failed)`.

  While the build fails, every headless scene tool's result carries `csharp: {build: "failed", errors, errorsOmitted?}`, and `errors` leaves out the lines a missing project assembly causes (a C# autoload that "does not inherit from 'Node'", a C# script whose class could not be found): `csharp` is their cause. A GDScript autoload's failure still reports. `options.prepare: "never"` skips the prep on the tools that take it.
- **Scene edits keep the file's own text.** The in-place scene tools (`add_node`, `delete_nodes`, `duplicate_node`, `move_node`, `attach_script`, `load_sprite`, `set_node_properties`, `connect_signal`, `disconnect_signal`, `batch_scene_operations`) rewrite only the sections they add, change or delete; every other line of the `.tscn` stays as it was, so the diff shows the edit.

  A reparent keeps the original property lines of the moved node and of every node under it, in their order, rather than Godot's pack form of them: only the tag line's `parent=`, a rewritten `NodePath` line and a line whose value the move changed (a `keepGlobalTransform` position) differ, and a Control moved into or out of a Container is judged at its new place, so the save stays spliced.

  An edited node's header keeps its `unique_id` as it was (none in a scene that has none); only an added node gets one. When that cannot be done, the file is saved in Godot's full 4.7 form and the result carries a `warning` saying why.

  An in-place `save_scene` writes the full form: use it to bring a file to 4.7's form on purpose; a `save_scene` save-as splices against the source like an edit. No save adds a property line the source did not store for a node when the node loads the same without it (Godot's own pack writes `layout_mode = 0` on a Control subclass in position mode); when leaving such a line out would change the loaded scene, the save keeps every line and its `warning` says which property.

  The other way round, an edit or a save-as keeps a line the source stored that Godot's pack leaves out as a default (a `Label`'s `size_flags_vertical = 4`, an instance's anchors equal to its scene's) while the node still reads that value; an in-place `save_scene` drops it.
- **A script with no `.uid` gets one.** When a scene tool writes a reference to a `.gd`, `.cs`, `.gdshader` or `.gdshaderinc` file that has no `.uid` beside it, it writes the one the editor's scan would (the same id), and the result's `uidFilesWritten` lists the `.uid` files: commit them with the scene.

  Each uid a scene tool creates (a `.uid` file's, a new scene's own) is also added to `.godot/uid_cache.bin` when the project has one, so a Godot run started outside the server knows it; if an editor open on the project drops it again, the next `run_project` imports.
- **A C# field named like an engine property does not reach the file.** Headless, a C# script runs as a real instance, so a field such as `private float scale` on a `Node2D` answers for `Node2D.scale`.

  Every headless save (the in-place tools, `create_scene`, `save_scene`) stores the engine's value instead, as the editor does, and the result's `warning` names each field (`MoteTrail.scale (a C# field) hides Node2D.scale; the file stores the engine's value`); `get_node_properties` and `set_node_properties` read and set the engine property.

  This holds for an instanced or inherited node too: its scene's override (`scale = Vector2(2, 2)` on an instance) is kept, read and duplicated, and a save adds no line the instanced scene already supplies. In the running game the field still hides it: renaming the field is the fix.
- **Timeouts are load-adjusted.** Every timeout and ceiling below, the ones you pass (`timeoutMs`, `waitSeconds`) included, counts load-adjusted time: wall time on an idle machine, slower while other work (a build, a test run, another game) loads it, so a busy machine stretches a wait instead of failing it.

  Each one also ends at 5 x its value in wall time, and its error then adds `; that is 5 x its <n> s ceiling in wall time, the backstop (load-adjusted <a> s, machine free <f>% on average)`. A build, import, headless run or clip cut that neither logs a line nor uses CPU for 120 s is killed as stalled, and its error says `did not finish (<why>)`. The pings and the debugger check stay in wall time. `stop_project`'s kill wait is 5 s of load-adjusted time, with the same 5 x wall backstop.

## Start and stop a game

### `run_project`

- **Does:** launches the project with the bridge injected and returns once the bridge connects: `{session, projectPath, processId, quiet, version, recording?, prep, godot, window, log?, warning?}` (`log` the absolute path of `.godot/godot-mcp/run-<session>.log`, the game's whole output, left out when the file could not be opened; see `get_debug_output`; `window` `{width, height}` the game window's size in pixels; `version` is the server's, `0.1.0+<sha>`, to quote in an issue; `godot` is the Godot executable it launched: `GODOT_PATH`, else a `Godot*console*.exe` found on `PATH`, the newest-named in the first folder that has one; with neither, the launch is refused with the `claude mcp add` line that sets `GODOT_PATH`).

  `restart_project`'s result carries `godot` and `log` too.
- **Use:** `projectPath` (the folder holding `project.godot`); `scene` for a scene other than the main one; `userArgs` (after `--`, read with `OS.get_cmdline_user_args()`); `engineArgs` (before `--`, e.g. `["--resolution", "1280x720"]`; the last `--resolution`, after `godot-mcp.json`'s `resolution`, gives the window exactly that size, even one larger than the screen, 1 to 16384 pixels a side, and a window that still differs adds `warning`: "--resolution asked for a WxH window and the game's window is wxh: the system did not give it the size asked for, so screenshots and input work in the window it has." A `--resolution` without a value, not `WIDTHxHEIGHT` or out of range is refused before launch. For a 2x capture of a `canvas_items` project, launch at twice its size); `options {quiet, shutOutRealGamepads, mute, session, prepare, preset, record, dropIdle, repickHover}`; `mute: true` silences a watched run (`quiet: false`) by muting its Master bus for the whole session, again whenever the game unmutes it, and `restart_project` keeps it; `mute: false` on a quiet run is refused, since a quiet run is always silent.
- **Edges:** a live session name is refused when you pass it, or when the default name's live holder runs on the same folder; a default name held live by another folder becomes `<name>-2`, `-3`, …; a new name starts a second session alongside.

  `options.preset` names a preset from `godot-mcp.json` and fails when the file or preset is missing. By default the machine's real gamepads stay live and feed the same actions as the gamepad tools; `shutOutRealGamepads: true` keeps them out, at the cost of focus-out notifications to the game (a game that pauses on focus loss will).

  `record` changes game timing: see Recording. `dropIdle: true` (only with `record`; refused without it) makes each cut clip drop every frame identical to the one before, so the wall-clock time between tool calls, when nothing moves, leaves the clip and the action plays at real speed; such clips have no audio, and a deliberate still (a held pose, a dialog to read) collapses to one frame too.

  `repickHover: false` (default true) stops the bridge re-picking the hover at the injected pointer every frame: see the hover section. It is a run option only, so `attach_project` and `arm_project` take none and a preset in `godot-mcp.json` cannot carry it.
- **Shutdown:** once the server's exit has begun, `run_project`, `restart_project`, `preview_scene` and `run_scratches` are refused with "The godot-mcp server is shutting down; no game can start now.", before any build or import runs.

### `attach_project`

- **Does:** on an armed folder with a dormant game, joins that game at once; otherwise injects the bridge plus a one-use attach file and blocks until a game started on the project after that connects: `{session, projectPath, quiet, version, window, joinedPid?}` (`window` `{width, height}` the game window's size in pixels; `joinedPid` the dormant game joined).
- **Use:** for a game `run_project` does not start: a second client, a `--server` run, a smoke script, the editor's Play button. Start the launch in the background delayed a second or two (`Start-Sleep 2; godot --path <project>`) just before this call, or launch within `waitSeconds` (1 to 600, default 60, load-adjusted) after it.

  For a game already running, `arm_project` the folder before it starts, then call this at any time. `options {quiet, shutOutRealGamepads, mute, session, pid}`: `pid` picks one of several dormant games; `mute` (left out: the arm's, else false) silences the game by muting its Master bus, and `quiet` mutes it too. After a detach the game goes back to the arm's mute, or to its own when it goes idle.
- **Edges:** with one dormant game and no `pid`, that game is joined rather than a new launch waited for; with several and no `pid`, and with a `pid` that is not dormant, the call is refused with the dormant games listed.

  Joins of different dormant games wait at once, beside each other and beside a plain attach; a second join of the same pid is refused naming the session that waits for it. A game started before `arm_project`, or on a folder not armed, has no bridge and never attaches: relaunch it while the folder is armed.

  `quiet: true` creates the window unfocused, then parks it off-screen and caps it at 60 fps, but only a launcher can put a game on the hidden desktop, so it shows on the primary screen for a moment at start (longer when a breakpoint holds it before the bridge's `_ready`); a joined dormant game is parked at the join. A quiet attach shares a folder only with quiet sessions.

  On a folder this server armed, `quiet` and `shutOutRealGamepads` left out take the arm's values, and a value that differs from the arm is refused. An attached session has no captured output (`get_debug_output` refuses it), and cannot be restarted; `detach_project` ends it leaving the game running, `stop_project` ends it and quits the game. The error feed starts at the attach: errors a dormant game raised before it are not reported.

### `detach_project`

- **Does:** ends an attached session and closes the connection; removes the injected `override.cfg` unless another live session uses the folder or it is armed. The game keeps running: on an armed folder it goes dormant again, letting go of any key, button or action the drive left held, so a later `attach_project` joins it again (a window a quiet join parked comes back on screen); otherwise its bridge goes idle.
- **Use:** to finish with a game `attach_project` joined and leave it running; `stop_project` quits it instead.
- **Edges:** a real-time recording (`record_mark`) of the game is ended at the detach and its clip finished before the call returns, reported as `recording: {mode: "realtime", clips: [path], startedSeconds, warning?}`, or `{mode: "realtime", startedSeconds, error}` when the clip could not be finished, which does not fail the detach (a finish that failed or was cancelled by a fault gives `error`, `The real-time recording ended without a clip.` for a cancelled one, else the fault's message, and the session is released); `recording` is left out when none ran, its clip was already returned, or its `start` is in flight.

  A `start` still in flight is ended at once and reports no recording; a `start` after the end begins is refused (see `record_mark`).

### `arm_project`

- **Does:** writes the injection `override.cfg` and `.godot/godot-mcp/armed.json`, so every game with a window started on the folder from now on (by a script, a launcher, by hand) carries a dormant bridge that `attach_project` can join while it runs; a `--headless` game (a smoke, a test runner) is left alone, so it is never joined by mistake: `{projectPath, quiet, shutOutRealGamepads, mute, dormant: [{pid, startedAt}]}`.
- **Use:** before launching games you may want to look at later (a smoke, `dtd.ps1 launch`, a player reaching a state), instead of a delayed launcher. `options {quiet, shutOutRealGamepads, mute}`, all false by default; every session on the folder but a `run_scratches` one must agree with `quiet` and `shutOutRealGamepads`, while an attach's `mute` may differ. A dormant game waits muted when the arm is `mute` or `quiet`, and gets its own mute back when disarmed.
- **Edges:** a game already running at the call has no bridge and is never listed. Arming again with the same options returns the state; with different ones it is refused (disarm first), as is arming a folder another godot-mcp server armed with other options.

  The arm lasts until `disarm_project` or the server exits. A quiet arm starts every game's window unfocused and parks it off-screen, a hand-launched one included. A dormant game checks for a join twice a second, even while paused, and registers no error logger until it is joined.

### `disarm_project`

- **Does:** ends an arm: `armed.json` is removed, and `override.cfg` too unless a live session uses the folder: `{projectPath, overrideRemoved}`. Dormant games stop waiting (a window a quiet arm parked comes back on screen); attached sessions stay attached, and their games go idle at their detach.
- **Use:** when you are done joining games on the folder.

### `stop_project`

- **Does:** (a game `run_project` started also quits by itself, running its exit work, when its server exits or is stopped; an attached game keeps running) asks the game to quit, kills it after 3 s (30 s for a recording run) of load-adjusted time, or 10 s when it acknowledged the quit and closed its bridge within the 3 s (the teardown grace: it is in the engine's own teardown), so a busy machine waits longer in wall time (at most 5 x), removes the override when no other live session uses the folder and it is not armed: `{session, projectPath, exitCode, killed, killReason?, overrideRemoved, alreadyExited, gameExitCode, leftRunning?, quitMs?, recording?, headlessHostStopped}`.

  `projectPath` in place of `session` frees a folder: it stops the warm headless host there (waiting for a headless call running on it) and the session running there, if one does; with `session` too both must name the same folder, and several live sessions there are refused, naming them.

  With no session running there, `projectPath` also clears a dead server's leftover `override.cfg` and `.godot/godot-mcp/armed.json`, and the result is `{projectPath, headlessHostStopped, overrideRemoved?, armedRemoved?, warning?}`: `headlessHostStopped` can be false, and `overrideRemoved`, `armedRemoved` (booleans) and `warning` are left out when false or null.

  Each file is removed only when every godot-mcp server its owners line names has exited (a file with no owners line counts as stale; an owner entry that cannot be parsed counts as live), and an `override.cfg` without godot-mcp's marker is never touched. `attach.json`, join, dormant and scratch status files and the `.git/info/exclude` line are left alone.

  A file left in place is named in `warning`, the reasons joined with `; `: `override.cfg is the project's own (no godot-mcp marker), left in place`; `<file> is open in another process (a running Godot?), left in place` (reported, never failing the stop); `<file> names a live owner (pid <pid>), left in place` (another server's process); `<file> belongs to this server's own session or arm, left in place`; `<file> names an owner this server cannot read (<entry>), left in place`; `<file> changed while it was judged, left in place`; `the owners list could not be opened (busy, or see the server's stderr), nothing removed` (then neither file is touched).

  With nothing stopped or removed the call is refused: `No Godot session or warm headless host runs in <dir>.` followed by the same reasons, capitalised and ending in a period.

  A removal that throws is refused too, naming only what was done: `Stopped the warm headless host and removed override.cfg in <dir>, then clearing armed.json failed: <message>`, or `Clearing <file> in <dir> failed: <message>`, followed by the reasons for any file left in place, capitalised and ending in a period; the original exception is logged to the server's stderr.

  Stopping a session also stops its folder's warm host when that host is idle, and `headlessHostStopped` says whether one was stopped.

  A process session (`run_process`) is stopped by name only: `stop_project {session}` closes its job and ends the program and everything it started at once, returning `{session, projectPath, kind: "process", exitCode, killed, alreadyExited}` (`exitCode` the program's only when it had already exited, null when the stop had to end it; `killed` true only then), and the session stays listed as stopped.

  `stop_project {projectPath}` never stops a process session, and naming one beside `projectPath` is refused (`session '<name>' is a process; stop it by name with stop_project, without projectPath.`).

  `exitCode` is the process the server started (on Windows the `Godot_console` wrapper); `gameExitCode` is the game's own, null when it could not be read and whenever the game was killed (the code read after a kill is not the game's).

  The grace watches the game itself: a game that quits in time is not killed even when it left processes running, which `leftRunning` lists (`["PING.EXE (pid 1234)"]`) and which end with the wrapper, so `exitCode` is then -1. A kill says why in `killReason`: no answer to a ping, no answer to the quit request, still shutting down after the grace, or no bridge connection to ask.

  A game whose bridge connection is still open at 3 s is killed then, as before; one that closed it exits within the next 7 s unkilled (`killed: false`, `quitMs` set), and a recording run keeps its 30 s with no teardown phase.

  A game still running after the 10 s is killed with a `killReason` reading "the game acknowledged the quit but was still shutting down after 10 s of load-adjusted time (…); the bridge's connection had closed … ms after the quit was acknowledged, so the time went into the engine's or .NET's teardown." `restart_project` and a scratch scene's quit wait the same way.

  A kill after the grace also gives the grace in wall time and the machine's free share (`after 3 s of load-adjusted time (wall 8.4 s, machine free 36% on average)`), whether the bridge's connection was still open (the scene tree not yet freed, so a node's `_exit_tree` or the last frame held it) or had closed (the time went into the engine's or .NET's teardown), and the game process's CPU, threads and main-thread state over its last second, with its last stderr lines for a run.

  A kill ends the game's own process and the wrapper with everything each started, then waits for both together for 5 s of load-adjusted time (25 s of wall time at the backstop); the game's exit code is not read afterwards, so `gameExitCode` stays null and `exitCode` stays -1.

  A game still running after that wait adds to `warning`: `The killed game (pid <pid>) was still running after <spent>; its project folder stays in use until it is gone.` With the game gone but the wrapper not, it adds `The killed console wrapper (pid <pid>) was still running after <spent>.` instead. `<spent>` is `5 s of load-adjusted time (wall 5.2 s, machine free 90% on average)`, or at the backstop `25 s of wall time, 5 x its 5 s kill wait (load-adjusted 3 s, machine free 10% on average)`.

  `warning` joins its parts with one space, in the order debugger, kill wait, window size; a debugger's sentence never replaces the kill wait's, in a run or an attach.

  A game that quit gives `quitMs`, the wall milliseconds from the quit request until its process was gone: one near the grace is a quit about to become a kill.
- **Use:** at the end of every `run_project` session, and to quit a game `attach_project` joined. An attached game is stopped the same way through its own process (from the bridge's hello): `exitCode` is null, `gameExitCode` is the game's, `leftRunning` never appears, and the session is gone afterwards, not listed as stopped. A bridge that sent no process id is only asked to quit, and `warning` says it may still be running.
- **Edges:** `alreadyExited` is true when the run had ended before the stop: the game quit, crashed or was killed from outside (a debugger's stop); the exit code, not the flag, tells a kill from a quit, and a debugger picks that code. A game that does not answer a ping in 2 s is killed at once. A game a debugger is attached to is stopped all the same, and `warning` says its debug session ended with it.
  For a recording run, `recording` holds `{mode: "movie", path}` (no marks: the full movie) or `{mode: "movie", clips}` (one H.264 `.mp4` a mark pair; the full movie deleted), plus `error` when a cut failed. A clip whose encode failed (an ffmpeg without libx264, or the encode timing out) is a stream-copied `.avi` of the same frames instead, idle kept, and `error` says which; the other clips are still `.mp4`.
  In any other session a real-time recording (`record_mark`) is ended before the game is asked to quit, so its clip ends at the stop, and `recording` holds `{mode: "realtime", clips: [path], startedSeconds, warning?}`, or `{mode: "realtime", startedSeconds, error}` when the clip could not be finished, which does not fail the stop: a finish that failed or was cancelled by a fault gives `error` (`The real-time recording ended without a clip.` for a cancelled one, else the fault's message) and the session is released. The helper gets 10 s of load-adjusted time to exit after the end; one still running is killed and `warning` says `the capture helper did not exit within 10 s of the recording's end and was killed; the clip may end earlier or later than the recording did`.
  `startedSeconds` is the whole seconds from the current game's launch (a restart resets it) or the attach to the recording's start.
  A recording that ended on its own (`maxSeconds`, or the game quitting) is returned by the next `stop_project`, unless `record_mark` `stop`, `restart_project` or `detach_project` took it first. A clip is reported once: `recording` is left out when it was already returned or its `start` is in flight. A `start` still in flight is ended at once and reports no recording; a `start` after the end begins is refused (see `record_mark`).
  Every `recording` carries `mode`, `"movie"` or `"realtime"`.

### `restart_project`

- **Does:** relaunches a run session with the scene, arguments and options it was started with, keeping its name, its error `seq` and its debug output (a marker line separates the two games). The run's `log` file goes on too: the old game's lines, then the marker `[godot-mcp] restarted: the previous game (pid N) was stopped; output below is from pid M.` once, then the new game's; the result's `log` names it.

  On a process session (`run_process`) it stops the program and starts its stored command and arguments again, waiting on the same ready line and keeping the session and its output buffers; `options.prepare` does not apply, but the process's own `prep`, when the profile declares one, runs again before the start.
  Its result is `{session, projectPath, process, processId, previousProcessId, previousExitCode?, command, log, ready?, prep?}`, `previousExitCode` left out when the kill-on-close job ended the old process, `ready` when the launch waited on none and `prep` (as `run_process`'s) when the process declares a prep.
  A session that never started is refused (`No process has been started in this session, so there is nothing to restart.`); a start that fails is refused as `run_process`'s is and leaves the session stoppable, and one after the server's shutdown has begun is refused as a run restart is.
- **Use:** after editing code or scenes, to pick up the change without losing the session; `options {prepare}`.
- **Edges:** the prep runs while the old game still runs, so a failed C# build is an error and leaves the old game running. To change the scene or arguments, `stop_project` then `run_project`. An attached session cannot be restarted.
  The old game is stopped as `stop_project` stops it, with its `warning` when a debugger was attached to it or the killed game or wrapper was still running after the kill wait, joined in `stop_project`'s order (debugger, kill wait, window size); `previousAlreadyExited` and `previousGameExitCode` (left out when unknown) say how it had ended, as `stop_project`'s `alreadyExited` and `gameExitCode` do, and `previousKillReason`, `previousLeftRunning` and `previousQuitMs` (each left out when empty) say how it was stopped, as its `killReason`, `leftRunning` and `quitMs` do.
  A recording run records the new game to a new file and returns the old one's outcome as `previousRecording`.
  A real-time recording of the old game is ended before it is stopped and its clip returned as `previousRecording` in `stop_project`'s real-time shape (a failed finish is its `error`, as there, and does not fail the restart); the new game does not record until `record_mark` `start`.
  `window {width, height}` is the relaunched game's window, as `run_project` reports it, and a window that differs from `--resolution` adds `run_project`'s size text to `warning`, after the debugger's.

### `run_process`

- **Does:** starts a program the project's `godot-mcp.json` declares, leaving it running: `{session, projectPath, process, processId, command, log, ready?}` (`process` its name in the file, `processId` the program's own pid, `command` the argv it started with, `log` the file its output is also written to, `ready` the line it waited on when the call or the profile declares one, `prep` `{command, exit, ms, log}` when the profile declares a `prep`: the argv run, its exit code, its milliseconds and its log `.godot/godot-mcp/process-<session>.prep.log`).

  It returns once the program starts, or once its ready line matches.
- **Use:** a game server or a mock backend a drive needs beside its games. `projectPath` is the folder holding `project.godot`, `process` a name under the file's `processes` section, and `args` are appended to its `command`. Without `options.session` the session is the process's name fitted to the name rule, as a folder name is; a live session of that name on the same folder refuses the start as a run's does, and one on another folder takes `<name>-2`, `-3`, ….

  `options {session, ready}`; `options.ready {output, timeoutMs}` gives the line to wait for, replacing the profile's `ready` whole.

  The folder must have a `godot-mcp.json` declaring the process: a missing file or an unknown name is refused, the latter listing every process it declares. `options.prepare` does not apply (`dotnet run` builds itself), but a process that declares a `prep` in the profile runs it once before the start, as a finite step through the project's `prepWrapper` (unwrapped when there is none); the process itself is never wrapped.

  A prep that exits non-zero refuses the start and leaves no session (`The prep of process <name> exited <code>, so it was not started. Its log: <path>`), and a wrapper's exit 124 is reported as the gate's stop, naming the prep's log and the wrapper's. A process with no `prep` starts as before.

  `ready` is a .NET regular expression a line is matched against before the call returns, stdout first and then stderr. `timeoutMs` is 1 to 600000 of load-adjusted time, 60000 when left out.

  The wait scans only the lines the program prints after it starts, so a restart never matches the replaced process's line, and the result's `ready` is `{line, stream, ms}`: the matched line cut as `get_debug_output` cuts it, the stream it came from, and the real elapsed milliseconds.

  The process is stopped and the start refused when it exits first (`The process '<name>' exited (code <n>) before a line matched '<pattern>'; its last stderr lines:` its last 20, or `(none)`), or when its timeout passes (`The process '<name>' printed no line matching '<pattern>' within <n> s of load-adjusted time, so it was stopped; its last output lines:` the last 20 of each stream).

  A line the pattern takes over 1 s to match refuses it too: `The ready pattern '<pattern>' took over 1 s to match a line; simplify the pattern.` To wait for a later line, use `wait_for`'s `output` condition.
- **A process session:** it has no bridge. `list_sessions` reports `kind: "process"`, its `processId` (the program itself) and a null `gameProcessId`, live while it runs and still listed as stopped after it ends.

  Its stdout and stderr fill the same last-500-lines buffers `get_debug_output` reads (lines cut at 1000 characters), its output is also written to `.godot/godot-mcp/process-<session>.log` (fresh on each start), and `wait_for` with an `output` condition, `after` included, reads those buffers.

  Every other runtime tool refuses it: `Session '<name>' is a process (<program>), not a Godot game: only wait_for with an output condition, get_debug_output, restart_project, stop_project and list_sessions reach it.` `batch_drive` refuses a tool step on a process before any step of the batch runs (`{passed: false, steps: [], failedAt: {index, reason}}`), while a `wait` assertion with an `output` condition works.
- **Stop:** `stop_project {session}` terminates the process and everything it started at once, and the server's exit ends process sessions with the runs. A process session is stopped by name only; `stop_project {projectPath}` never stops it.
- **Edges:** parsing the section is strict: an unknown key, a wrong type, an empty `command`, a `prep` that is not a non-empty array of non-empty strings, a `ready.output` that is not a valid .NET regular expression, a duplicate name and a `cwd` that is absolute or leaves the project folder are each refused with the file's path and the fix. A process session needs Windows, since its process tree lives in a Windows job object; elsewhere the start is refused and nothing starts.

  A call's `options.ready` with no `output`, an invalid pattern or a `timeoutMs` outside 1 to 600000 is refused before the start.

### `list_sessions`

- **Does:** lists the live sessions by name (`includeStopped: true` adds the stopped ones): project folder, kind (`run`, `attach` or `process`), whether it is live, and a recording's state.

  `processId` is the process `run_project` started, the `Godot_console` wrapper on Windows that `stop_project` ends; for a process session the program `run_process` started; null for an attach. `gameProcessId` is the game's own process, the one a debugger attaches to; null until the bridge has connected, and always null for a process.

  A process session is live while its program runs and is still listed as stopped once it ends, so `get_debug_output` still reads it.
  `recording` is `{mode: "movie", path}` for a movie run while it runs, `{mode: "realtime", path, startedSeconds}` for a real-time recording, nothing while its `start` is in flight, and once a recording has ended on its own (the game quit) what `stop_project` will return for it, until `stop_project`, `restart_project`, `detach_project` or `record_mark` `stop` takes it.
  `armed` lists the folders `arm_project` armed, each with its `quiet`, `shutOutRealGamepads` and `dormant` games (`{pid, startedAt}`) that `attach_project` can join; `headlessHosts` lists the warm headless hosts by folder, `{projectPath, pid, startedAt, requests, idleSeconds}` (`idleSeconds` 0 while one serves a call).
- **Use:** when a call is refused for want of a session name, or to find a session a previous turn started; `gameProcessId` for a debugger's attach. A stopped run is kept until its name is reused, with its last pids and output (`get_debug_output` and `restart_project` still reach it by name), and listed only with `includeStopped: true`.
- **Edges:** while a debugger is attached to the game, every runtime call first pings it for 500 ms of wall time and fails at once when the game does not answer: "The game (pid <pid>) did not answer within 0.5 s while a debugger is attached: it is most likely paused at a breakpoint. Continue it in the debugger, or retry if it was only busy." A call that times out on such a game says it is paused under a debugger rather than stuck.

### `get_debug_output`

- **Does:** the newest stdout and stderr lines of a session's run (the current or last one under its name), whether it still runs, and its exit code. A process session's program answers too, from the output `run_process` captured.
- **Use:** when the game crashed or quit, or a tool reports the connection ended. `limit` (1 to 500, default 100); `before` (a line number) pages back, starting from the returned `stdoutFirstLine` or `stderrFirstLine`.
- **Edges:** the server keeps the last 500 lines of each stream; lines over 1000 characters are cut. Structured errors are better read from `get_errors`. A run session's whole output is also in `.godot/godot-mcp/run-<session>.log`, both streams interleaved in arrival order, lines as-is, with no size cap; the result's `log` is its absolute path (left out for a process session, whose own `log` names its file, and when the file could not be opened) and is still reported after the run has ended.

  The file is truncated at a fresh launch and appended to on a restart. It is closed when the run's output ends, on cancel, by the time `stop_project` returns, and when the session is disposed, so a stopped session never holds a file in the project folder. Every run session writes one, a `run_scratches` scene's included; an attached session has no output. A process session's output is also in `.godot/godot-mcp/process-<session>.log`.

## See the game

### `take_screenshot`

- **Does:** captures the game's next drawn frame to a PNG under `.godot/godot-mcp/screenshots/` and returns its path and size, plus an image.
- **Use:** `responseMode` `path_only`, `preview` (default, at most `previewMaxWidth` wide, 480 by default) or `full`; `crop {x, y, width, height}` in viewport coordinates, mapped to the screenshot's pixels: in a window stretched to twice the project's size, the PNG is twice the rectangle's size.
- **Edges:** the crop is in viewport coordinates (see Rules); a letterboxed window crops the same content, and a crop wholly outside the viewport fails as "the crop (x, y, w, h) lies outside the (W, H) viewport". `path_only` saves context when only the file matters. To keep the shot as a project file (a tracked picture), use `save_screenshot`: this tool writes only under the ignored `.godot/`.

  The capture is the root viewport's texture with every visible popup and tooltip on it: embedded ones (Godot's default) are in the texture, and in a project that sets `display/window/subwindows/embed_subwindows=false`, where each is its own OS window, they are pasted where the window shows them, at any stretch mode and window size. Every capture (baselines, frame steps, `wait_for`, `preview_scene`) does the same.
- **A headless game:** a game started with `--headless` (a `run_scratches` session, or one the user attached) draws no frames, so this tool fails with "a headless game draws no frames, so there is nothing to capture; read state with get_game_state, get_ui_elements or run_script, or run the scene windowed." No engine error reaches `get_errors` and nothing is awaited. `save_screenshot`, `save_screenshot_baseline`, `compare_screenshot` and `capture_frames` fail the same way.
- **A before/after pair:** a game that animates (a flickering light, a moving sprite) draws a different frame each time, so two shots of one state differ outside the region under test. Pause it first: `frame_control {action: "pause"}`, shoot, make the change (`set_property`), shoot again, then `frame_control {action: "resume"}`. A paused game still draws a fresh frame for each shot, since the capture waits for the next `RenderingServer.frame_post_draw`.

  Pausing does not stop everything: a shader's `TIME` is "affected by time_scale but not by pausing" ([CanvasItem shaders](https://docs.godotengine.org/en/4.7/tutorials/shaders/shader_reference/canvas_item_shader.html)), and a node whose process mode is Always keeps processing ("Paused or not, this node will process", [Pausing games](https://docs.godotengine.org/en/4.7/tutorials/scripting/pausing_games.html)), so crop those out.

### `save_screenshot`

- **Does:** `take_screenshot`'s capture, written to `path`, a file in the project (such as a tracked picture) or in the git checkout's `.tmp` folder above it, in one call. Returns `take_screenshot`'s result with `path` the named file and `capturePath` the copy under `.godot/godot-mcp/screenshots/`, beside which any preview stays, plus the image unless `responseMode` is `path_only`.
- **Use:** `path` (project-relative, `res://` or absolute); `crop` as `take_screenshot`'s; `options {overwrite, responseMode, previewMaxWidth}`, the last two as `take_screenshot`'s `responseMode` and `previewMaxWidth`. Missing folders are created.
- **Edges:** `path` must end in `.png`, resolve inside the project or the checkout's `.tmp` folder and not lie under the project's `.godot/`; it is checked before the shot is taken.

  The checkout is the nearest folder at or above the project holding a `.git` folder or file (a worktree counts); its `.tmp` folder is reached by an absolute path or a project-relative one (`../../.tmp/shot.png`), while `res://` stays project-only. A real `<checkout>/.tmp/.godot/x.png` is accepted: only the project's own `.godot/` is refused.

  An existing file is refused unless `options.overwrite` is true.

  The refusals read "path must end in .png: <path>", "path <path> resolves outside the project <project root> and the checkout's .tmp folder <tmp folder>; save inside either" (with no checkout above the project, or for a `res://` path: "path <path> resolves outside the project <project root>; save inside the project"), "path <path> is under .godot/, which the editor owns; pick a tracked folder" and "<path> exists; pass options.overwrite true to replace it"; a failed copy reads "saving the screenshot to <full path> failed: <message>".

  A link (junction or symlink) on the way is followed, every entry from the checkout down (the `.tmp` folder itself included) must finally lead inside `.tmp` or the project, and a position inside the project is held to the project's `.godot/` rule; an existing target file that is a link to outside is refused even with `options.overwrite`.

  A `:` after the drive (an NTFS stream) and an 8.3 short name (`GODOT~1`) are refused, as is a `\\?\` path, in the `.tmp` folder as in the project.

  Those refusals read "path must not contain ':' after the drive: <path>", "path <path> uses a short (8.3) name; use the folder's full name", "path <path> leads through a link to <resolved>, outside the project <project root> and the checkout's .tmp folder <tmp folder>; save inside either" (for a path in the project whose link leads out of it: "…, outside the project; save inside the project"; or "…, under .godot/, which the editor owns; pick a tracked folder") and "path <path> leads through a link at <link path> that cannot be followed: <message>".

  It is in the `drive` class beside `save_screenshot_baseline`, not `read`, so an agent that only reads does not gain this power, and `batch_drive` refuses it as a step.

### `get_ui_elements`

- **Does:** lists the game's Controls depth first: path, name, class, `rect` in root-viewport coordinates, visible, and where they apply `text`, `disabled` and `tooltip`.

  The `rect` is the bounding box of the Control's rect carried out through every `SubViewportContainer`, embedded window, CanvasLayer, Camera2D and rotation as an input tool's aim is, so its centre is where a click on it lands (`aimedAt`); a Control in the root viewport with no rotation or canvas offset reports its global rect.

  `text` is the text the Control shows, the one a `{text}` target matches: a Button's (CheckBox, OptionButton and MenuButton included), a Label's and a LinkButton's translated text (an `uppercase` Label upper-cased), a LineEdit's placeholder while it is empty (a `secret` one's secret character, once per character), a RichTextLabel's text without BBCode.

  An `ItemList`, `TabBar`, `TabContainer` (on its own entry) and `Tree` add `items`, the texts an `item` target matches: `{index, text, hidden?: true, disabled?: true}` for a flat list or tab bar (every entry `hidden` when its drawing list is not visible, a `TabContainer` with `tabs_visible` off included), `{path, text}` for each Tree item an `item.path` can reach (none hidden or under a collapsed item), at most 50, with `itemsTotal` beyond.

  A listed path or text can still be ambiguous to an item target when a hidden or collapsed item shares it. A RichTextLabel's tooltip spans are not listed: finding them samples the label pixel by pixel, which `item` does on demand.
- **Use:** before any click, to find a target by path or by the text it shows; `filter` keeps one engine class and its subclasses (`BaseButton`); `visibleOnly` (default true) skips hidden subtrees; `offset` and `limit` (1 to 500, default 100) page.
- **Edges:** Controls only; `get_scene_tree` lists every node.

### `preview_scene`

- **Does:** starts the project quiet on one scene in its own short-lived session, pauses it before the scene's first frame, waits two drawn frames, screenshots it and stops: `{scene, path, width, height, previewPath?, cameraAdded, prep, errors?}` and the image.
- **Use:** after a headless scene edit, to see the result. `projectPath`, `scene` (ending `.tscn`, `.scn`, `.escn`, `.res` or `.tres`), `options {resolution, prepare}` (`resolution` as `WIDTHxHEIGHT`, given exactly as `run_project`'s `--resolution` is), `responseMode`, `previewMaxWidth`.
- **Edges:** the quiet and pad settings come from the folder's non-scratch sessions, else its arm, else quiet with live pads, never from a `run_scratches` session. The scene's `_ready` and the project's autoloads still run; when one changes scenes, the result's `scene` says what was shown.

  A 3D scene with no current camera gets a temporary one framing its visible geometry (`cameraAdded: true`). The scene's ending is checked case-sensitively. Prep, launch and capture share 60 s of load-adjusted time. It cannot run inside `batch_drive`.

### See why something is not drawn

There is no tool for it: Godot's own debug drawing answers, through tools that exist.

- **Launch flags:** `--debug-collisions` draws collision shapes, `--debug-paths` paths, `--debug-navigation` navigation and `--debug-avoidance` avoidance, and `--debug-canvas-item-redraw` flashes what redraws in low-processor mode.

  Pass them as `run_project {engineArgs: ["--debug-collisions"]}`, or keep them in a `godot-mcp.json` preset (see "Project profiles") and launch with `options.preset`: `"presets": {"collisions": {"engineArgs": ["--debug-collisions"], "description": "collision shapes drawn"}}` draws what the flag does.

  With the flag at launch, a shape added after the game started draws too. The class reference says the SceneTree's hints are "not designed to be changed at run-time", and `set_property` cannot reach them (`Node '/root' has no property 'debug_collisions_hint'.`), so to show shapes already in the scene, `stop_project` then `run_project` with the flag. A `run_script` that sets `get_tree().debug_collisions_hint = true` draws only the shapes added after it, not those already in the tree.
- **Draw modes:** `set_property {node: "/root", property: "debug_draw", value: 3}` sets the root viewport's `Viewport.debug_draw`: 1 unshaded, 2 lighting, 3 overdraw, 4 wireframe, 5 normal buffer, and 0 puts it back (`inspect_node` reads it). They act on 3D only: a 2D scene does not change under any value or renderer.

  Overdraw draws the meshes dark and translucent on a black clear colour, with the 2D controls unchanged, and replaces every material, custom shaders included.

  Normal buffer needs Forward+: under Compatibility it falls back to the normal draw, and `engineArgs: ["--rendering-method", "forward_plus"]` tries it on a Compatibility project. Wireframe under Compatibility draws meshes normally, not nothing, unless the game calls `RenderingServer.set_debug_generate_wireframes(true)` before its meshes load.

  Unshaded under Compatibility sticks: after `debug_draw` is set back to 0 the meshes stay unshaded, and only a restart draws them normally. Forward+ restores fully, and so does overdraw under Compatibility.
- **A node with no point on screen:** an input tool's world aim says why, with its refusals for a node off-screen, behind the camera, with no current camera or hidden (see "Drive input"); `hover` reports the point and moves the pointer.
- **Baselines:** a full-screen baseline taken in one draw mode fails in another, since overdraw and normal buffer change the clear colour (about 84% of a 3D frame); crop the baseline to a 2D region and it is unaffected.
- **Cost:** `--debug-collisions` draws every shape every frame, which can move a watch's `frame_ms`; a quiet run's 60 fps cap hides it (one shape read 16.66 ms with and without), so measure it on an uncapped run.

## Scratch scenes

### `run_scratches`

- **Does:** plays each scratch scene in its own fresh headless game session, `parallel` at a time (default one after another), walking its steps itself, and gives each a verdict: `{passed, green, red, known, noSteps, killed, timeScale?, prep: {build, buildMs?, buildLog?, import, importMs?, importLog?, note?}, scenes: [{scene, verdict, steps: {played, total}, selected?, setup?, stepList?, pace, paceReason?, timeScale?, seconds, session, failedAt?: {index, name, error, status, pattern?: {pattern, reason?}}, failures?: [{index, name, error}], alone?, aloneFailedAt?, details?: [{index, name, ok, gameMs?, until?: {met, gameMs, elapsedMs, frames}, status, error?, errors?: [{message, file, line, count?}], errorsOmitted?, lines?, cut?}], exit: {code, leaked?, lines?, error?, killed?, killReason?, warning?, missing?}, known?}]}`.

  A scratch scene's root implements the **scratch protocol**: `PlayStep(int)`, `GetStepCount()`, `GetStepName(int)` and `GetStatus()`. Per step the server calls `PlayStep`, waits the scene's **pace** in game time (`wait_for {gameMs}`), then reads `GetStatus()` (its second line is `status`).

  Outside `run_scratches`, `PlayStep` called with one String argument on a node that has `GetStepCount` and `GetStepName` plays the step whose `GetStepName` equals that String exactly (ordinal, case-sensitive), so a windowed look plays a step by the name `run_scratches` reports. The names are read from the game on every call; a String is always a name, so a step named `"7"` is reached by name, and an int is still an index.

  It holds for `call_method`, every `options.call` (`capture_frames`, `watch`, `wait_for`, `then.call` included) and `batch_drive`'s steps; a node without `GetStepName` is called as before. A name matching no step is refused with `PlayStep "<name>" matches no step; its steps are 0 a, 1 b` (a hidden step reads `1 (hidden) b`; `it has no steps` for an empty list), and one matching several with `PlayStep "<name>" names N steps (i, j); call it by index`.

  A lookup that cannot read the names refuses too, ahead of those: `PlayStep "<name>": reading its step names raised: <first error>` when `GetStepCount` or `GetStepName` raised an error, `PlayStep "<name>": GetStepCount returned <value>, not a step count`, and `PlayStep "<name>": GetStepName(<index>) returned <value>, not a name`, the value written as a GDScript value (`null`, `"text"`).

  With `options.timeScale` N the game runs N times faster, so a pace of p s of game time takes p / N s of wall time; the pace itself stays game time, except the wait after the last played step, which lasts the longer of that step's pace in game time and in wall time (above scale 1 the pace in wall time, so the exit leak check gets the wall time a 1x run gives it; at 1 and below the pace in game time; a scene that needs longer sets its last step's pace).

  The top-level `timeScale` is in the result when the call's scale is not 1, and a scene whose profile pace opted out carries its own `timeScale: 1`, a refused or killed scene too.

  A step's until that counts frames or physics ticks (`frames`, `settled`) sees N times fewer frames per pace at scale N, and at a high N one frame's delta can exceed a short pace: such a scene may need a longer pace or the opt-out.

  A game that sets `Engine.time_scale` itself (an autoload, a scene) overrides the launch's scale, and the divided kill bound then cuts its long steps short with the "clock stood still" message below: such a scene should opt out with `"timeScale": 1` in its profile pace.

  A root that also has a public `GetStepUntil(i)` (C# or GDScript; an optional fifth method, found by `has_method`) gives each step a `wait_for` condition, as `{node, property, equals}` or `{expression}` would be written (an empty dictionary, or null, for a paced step, which plays as above).

  Such a step is played inside one `wait_for` on that condition with `PlayStep(i)` as the wait's `options.call`, so the wait's clock starts in `PlayStep`'s frame and ends when the condition holds, with the step's pace as the most game time it may take; then `GetStatus()` is read as usual. A `state` condition on a C# node is read through the C# helper, as `wait_for` reads one.

  A root may also have a public `GetStepHidden(i)` (optional, found by `has_method`): true marks step i hidden, false or null leaves it shown, and any other answer refuses the scene with `GetStepHidden(<i>) returned <json>, not true or false`. A hidden step keeps its index and plays by index or name like any other.

  A paced step is not waited out: the next `PlayStep` comes one pace after this one, whether or not work it started (an awaited task, a tween, a timer) is still running, with or without `options.keepGoing`. Give such a step a `GetStepUntil` condition on the state that says it is done.

  A step turns red only on a call error, an error-feed entry (`push_error`, `GD.PushError`, an exception `PlayStep` throws) or a line the scene's patterns match: `GetStatus()` text and a `printerr` or `GD.PrintErr` line never do, so a scene reports a step's failure with `push_error`.

  A condition not met in the pace's game time, one `wait_for` refuses (`options.call` included), and an answer that is not a `wait_for` condition are each red and stop the scene, even with `options.keepGoing`, since the next `PlayStep` would start on a step still running. The step's detail carries `until {met, gameMs, elapsedMs, frames}` in place of `gameMs`: `gameMs` the game time from `PlayStep`'s frame to the met or limit frame, `elapsedMs` and `frames` as `wait_for` reports them.

  The errors read `step <i> '<name>': its until was not met within its pace of <p> s; give it a longer scratch.pace, or check the condition GetStepUntil gives`, `step <i> '<name>': its until was refused: <why>`, `GetStepUntil(<i>) returned <value>, not a wait_for condition`, `step <i> '<name>': its until was not met within <r> s: the game's clock stood still (is it paused?)` (the real-time backstop of the pace / time scale + 10 s ended the wait before the game counted the pace) and `step <i> '<name>': its until's wait failed: <message>` (the connection or session failed).

  A `PlayStep` that throws inside an until reads as its feed entry, `<message> (<file>:<line>)`, as a paced step's does.

  A step fails on an error from the call, an engine error, `push_error` or C# exception in the error feed since the step began (never a text search), or a stdout or stderr line in the step's window matching one of the project's `scratch.patterns`; the scene stops at the first failed step unless `options.keepGoing`.

  `failedAt` is the first failed step and `failures` lists every failed step in order (`index`, `name`, `error`; one entry without `keepGoing`), both absent for a scene with no failed step, and each failed step in `details` carries its `error`.

  A killed scene's `failedAt` names the step the kill cut off, also the last entry of `failures`. After the last step and that step's pace once more (the longer of its game time and wall time) the game is stopped gracefully and its stderr read for the leak line (`N ObjectDB instances were leaked at exit`).

  A scene must also print each line the profile expects of it (`scratch.expect` and `scratch.expectFor`, below). Every line of its output, from the launch through the steps to the exit, is searched, and `exit.missing` lists one entry per pattern no line matched, as "`<pattern>` never appeared in the scene's output (<reason>)", followed by `; <n> output lines were dropped, so it may have been printed` when the output buffer dropped lines and `; it took over 1 s on a line, so simplify it` when the pattern timed out on one (a timeout counts as no match on that line).

  `exit.missing` is absent when every expected line appeared, when the scene expects none and when it was not checked. A scene whose steps are all green but whose `exit.missing` is listed is `red` with no `failedAt`; a red step keeps its `failedAt` and `exit.missing` is still listed. A killed, refused, boot-red or `no-steps` scene, and one `options.steps` plays only part of, is not checked.

  Verdicts: `green` (every step clean, exit 0, no leak, every expected line printed), `red`, `no-steps` (`GetStepCount()` is 0; red at index -1 under `options.steps`), `known` (a `known` scene that went red, or that played only some steps, all green), `known-now-green` (every step played and green; counts as red until its `known` entry goes), `killed` (past its ceiling or stuck).

  `passed` is true when nothing is red or killed. `details` comes for a red or killed scene, or for every scene with `options.details`, and lists every played step as `{index, name, ok, gameMs?, until?, status}` (`gameMs` the pace, or for a step played on an until `until` in its place); only a failing step carries `error`, `errors` and `lines`, and `options.details` adds those for the green steps too, and keeps their `[scratch]` notes (a failing step keeps its own always).

  A step's `errors` are folded by message, file and line into `{message, file, line, count?}` entries as `get_errors` reports them (`count` only above 1); distinct entries past the cap are counted in `errorsOmitted`, which also counts errors the size budget dropped.

  Without `options.details` a failing step carries its `error` cut at 500 characters, at most 3 `errors` (messages cut at 500) and 5 `lines` then `… N more`, within 1500 characters. With `options.details` each step gets 20 `errors` (messages cut at 2000), 20 `lines` and 4000 characters.

  Past its budget a step drops lines, then `errors` from the end, and ends with a `cut` note naming the session whose `get_debug_output` holds the full log; the first `errors` entry is always kept, and `cut` is set only when something was dropped. `failures[].error` is cut at 200 characters without `options.details` and 500 with; `failedAt.error` and a step's `error` are cut at 500.

  A step's `status`, `failedAt.status` and each of its `lines` are cut at `options.detailChars` (1 to 20000, default 500; anything else is refused with "options.detailChars must be at least 1 and at most 20000; got <n>."), and above 500 each step's character budget (4000 with `options.details`, 1500 without) grows by the extra characters, the `cut` note naming the grown budget.

  The failures index and the error messages keep their cuts. The limit is taken when the call runs, and a background run's stored result keeps it.

  The lines a scene's run reads are themselves cut at the larger of 1000 and `options.detailChars`, so with the default the `lines` patterns, the exit lines and the launch and pace lines see no more than their first 1000 characters, and a call with `detailChars` above 1000 sees, matches and can return lines up to that length. Nothing else caps the whole result.
- **Use:** in place of a project's `Run-Scratches.ps1`. `projectPath`, `scenes` (scene names in `scratch.folder`, or `res://` paths; left out: every `.tscn` directly in `scratch.folder`, in name order), `options {pace, timeScale, userArgs, engineArgs, ceilings, prepare, details, detailChars, parallel, session, keepGoing, steps, background, only}`: `pace` in seconds (above 0, at most 120) for every step of every scene of this call, step paces of the profile included,

  `timeScale` from 0.01 to 100 (default 1; anything else is refused with "options.timeScale must be at least 0.01 and at most 100; got <n>."): each scene's game launches with `--headless --time-scale <n>` (at 1, `--headless` alone), so a pace of p s of game time takes p / timeScale s of wall time and the kill bound scales with it.

  It applies to foreground, background, `steps` and folder runs. A scene whose profile pace entry has `"timeScale": 1` runs at normal speed whatever the option says, and `options.pace` keeps that opt-out.

  `parallel` 1 to 4 (overrides the profile's), `userArgs` appended after the profile's, `engineArgs` (an array of strings) placed on each scene's Godot command line after the runner's own `--headless` and `--time-scale` and the profile's `scratch.engineArgs`, before `--` and the user arguments, `prepare` `auto` (default) or `never`.

  `engineArgs` is checked before any scene runs, each source on its own: it refuses `--display-driver` and `--audio-driver` (Godot keeps the last driver argument it reads, so the scene would open a window or play sound), and a `--resolution` Godot cannot read, with the parser's own refusal plus `It came from <source>.`; a `--resolution` at the end of one source whose value sits in the other is refused as having no value.

  An `engineArgs` entry that, with its surrounding spaces trimmed as Godot trims every argument, equals `--` or `++` (Godot reads both as the separator), or equals or starts with `--headless`, `--time-scale` or `--scratch-auto`, is refused before any scene runs (an `options.engineArgs` null entry is refused too, naming its source): "run_scratches sets --headless and --time-scale itself and puts the scene's own arguments after --, so engineArgs cannot hold <arg>; it came from <source>.", the source being `options.engineArgs` or the profile file's path.

  Any other engine argument passes through as given, one that undoes `--headless` included: a later `--display-driver` or `--audio-driver` wins in Godot, so the scene opens a window or plays sound, which is the caller's choice.

  `ceilings {<scene>: seconds}` gives a scene its own ceiling (see the ceiling paragraph below): each value above 0 and at most 3600, in load-adjusted seconds from the scene's first step (its boot is not counted), not scaled by `timeScale`.

  It replaces the ceiling derived from the paces, lower or higher (a value below the paces' sum is allowed), and wins over the profile's `scratch.ceilings` for the same scene; a scene in neither keeps the derived ceiling. A key naming no scene of the run is refused before any scene runs.

  `keepGoing` true (default false; no profile key) plays a scene's next step after one that failed on a pushed error or a matching line, so independent red steps are all reported; a step whose own call failed (the game gone or stuck) still stops the scene. Steps are usually stateful and sequential, so off is the default. With `keepGoing` the wait after the last step runs and the exit is judged too.

  `steps` (an array of tokens, or an object mapping a scene name to such an array; left out, `null` or `{}`: every step) plays only some steps, once each, in ascending order. An array applies to every scene of the call; an object gives each scene it names its own list, and a scene it leaves out plays every step.

  A number is a step index. A string is read as a plain index first (as `scratch.pace` `steps` keys are, below the scene's step count), then as an exact step name, then as an inclusive range `"2-5"` of ASCII digits; `03` and `+3` are names, `"02-05"` is a range, and a name wins over a range reading.

  A `steps` that is neither is refused with `steps is a {kind}; it must be an array of step indices, names or ranges, or an object mapping a scene name to such an array.`

  An object key naming no scene of the plan is refused with `options.steps scene '<key>' is not in the plan; its scenes are <names>. Pass one of those; a scene the map leaves out plays every step.` A map value that is not an array reads `steps for <Scene>: it is a <kind>; it must be an array of step indices, names or ranges.`, and each list refusal below starts with `steps for <Scene>: ` for a map.

  An empty array, a token that is neither a number nor a string, a negative or non-integer number, a reversed range (`"5-2"`) and a duplicate token (`3` and `"3"` are one token) are refused before launch. A token matching no step (a number past the end, an unknown name, `"١-٢"`), a range reaching past the last step and a name shared by two steps make that scene red at index -1 with no step played, the message listing the scene's steps; the other scenes still play.

  `only` (`"all"` default, or `"red"`) lists in each scene's `details` only its red steps (`ok: false`), each with its full readback, and drops `details` from a scene left with none (a green scene, or a red one with no red step, such as a boot error, a failed exit or a kill).

  Every other scene field and the counts stay, so a big run is read for its failures alone. With `background: true` it is refused with "options.only is not read by a background run; pass only to scratch_status, which returns the run's result". Any other value is refused with `only must be "all" or "red", not "<value>"`.

  Steps not named never run, except the scene's setup steps (`scratch.setup`, below), which a partial run plays beside the selected ones; a later step that depends on a skipped step not declared as setup is the caller's choice.

  A scene's `stepList` is one string per step, `"<index> <name>"` with ` (hidden)` after a hidden step's index (`"0 (hidden) fight setup"`), present only when the scene has a hidden step or the run played part of it (`selected` present).

  A scene's `selected` lists the indices the call's `steps` named, only when `steps` is given and resolved in that scene; `setup` lists the declared setup steps the run added to that selection, ascending, and is absent when there are none; like `selected`, it says what the run set out to play, so a scene stopped earlier still lists them.

  A `scratch.setup` key is resolved only on a run with `steps`, so a bad key turns its scene red only then. `steps.played` below `steps.total` with `selected` marks a partial run. A `known` scene played in part (by `selected` alone) with every played step green stays `known` with its reason and counts as passed.

  The project's `godot-mcp.json` holds the rest in a `scratch` section, parsed strictly: `folder` (project-relative or `res://`, exact case; required when `scenes` is left out), `userArgs` (used in place of the top-level `userArgs` for scratch runs), `engineArgs` (an array of strings, read for `run_scratches` only, with no fallback to the top-level `engineArgs`; the same refusals as `options.engineArgs`),

  `pace {<Scene>: seconds | {seconds, reason, timeScale?}}` (default 0.5; the object form takes both keys and an optional `"timeScale": 1`, the opt-out from `options.timeScale` (any other value is refused: "a pace entry's "timeScale" can only be 1, which keeps the scene at normal speed"; a bare number cannot carry it), and an optional `steps {<step>: seconds | {seconds, reason}}` of paces for single steps, each key a step's index written plainly (tried first; `03` or `+3` is a name) or its name, so one slow step does not make every step wait;

  it says why the scene needs that pace, and the reason comes back as `paceReason` on a `red` or `killed` scene whose pace came from the profile, the failed step's own reason when that step has a pace of its own (none when its pace is a bare number), since a step that ran into the next is usually a pace set too short),

  `ceilings {<Scene>: seconds}` (the same map as `options.ceilings`: above 0, at most 3600; a key naming a scene outside the run is ignored, and `options.ceilings` wins for the same scene),

  `setup {<Scene>: [step, ...]}` (the steps a scene's partial run needs for the state the selected steps build on; each step is an index written plainly or a step's exact name, resolved as a `pace.steps` key is; with `options.steps` they play with the selected steps, ascending, and a setup step is judged like any other, `keepGoing` included; without `options.steps` every step plays and `setup` changes nothing; a duplicate entry is deduped),

  `known {<Scene>: reason}`, `patterns` (case-sensitive regexes, each a string or `{pattern, reason}` with both keys; a step failed by a matching line names the first matching entry as `failedAt.pattern`, with its reason; default `^SCRIPT ERROR|^ERROR:|ObjectDB instances? (was|were) leaked`; `[]` turns line matching off),

  `expect` (an array of lines every scene's output must print, each item a regex string or `{pattern, reason}` compiled as `patterns` are; an object item's reason must be a non-empty string saying why the scene must print the line, and `exit.missing` repeats it) and `expectFor {<Scene>: [same items]}` (a scene's own; a scene's list is `expect` then its `expectFor` entry, not deduplicated; a scene named twice is refused with `the scene "A" appears twice in "expectFor"; keep one`),

  `parallel` (1 to 4, default 1).

  For a step played on an until, the pace is the most game time the step may take before its condition holds, and the ceiling below still counts it in full (divided by the time scale).

  A `steps` key matching no step, a name matching more than one step, or two keys naming one step makes the scene red before it plays (names are known only after launch). A `setup` entry matching no step, or a name two steps share, does the same, with `setup "<key>" of <Scene> matches no step; its steps are …` or `… names 2 steps (1, 2); key it by index`; a malformed `setup` value is refused when the profile loads.

  The wait after the last step uses the longer of that step's pace in game time and in wall time, and the ceiling sums each step's pace (divided by the time scale) plus 10 s, plus the last step's pace in wall time for that wait (pace / min(1, time scale)) and 10 s for the markers.

  With `options.steps` the last step is the last one played (setup steps included) and the ceiling sums only the played steps; the kill reason says "its 1 step's pace" or "each of its N steps' pace", N the number played.

  Scenes start `parallel` at a time: those listed in `scenes` in that order, a folder run's largest pace (a scene's or a step's) divided by the scene's time scale first (equal values in name order), so a slow scene does not start last and end the run alone; the result keeps the order given, name order for a folder run.

  With `parallel` above 1, a `red` or `killed` scene (not `known`, not one red before any step played, a boot error included) is played once more alone after the others finish: `alone: true` when that replay is green (the entry is the replay's and counts as green), else `alone: false` with the first run's entry and the replay's failure as `aloneFailedAt`; `seconds` covers both runs. An alone replay plays the scene again with the same `keepGoing`; `aloneFailedAt` is its first failed step.
- **Background:** `options.background: true` returns `{runId, statusFile, logFile, scenes}` at once, once the plan is checked (`scenes` the names in play order), and plays the run on in the server, so a run of many minutes holds no call open. It ends only by `scratch_status {stop: true}`, by finishing, or by the server's exit, never by the call's end.

  One background run is live per project per server: while it is, every `run_scratches` on that project, background or not, is refused with "background scratch run <runId> is still running on this project; read it with scratch_status, or end it with scratch_status {stop: true}". Read and end the run with `scratch_status`.

  The run's files are in `<project>/.godot/godot-mcp/`.

  `scratch-<runId>.json` is its status, rewritten whole at each scene's start and end and each step's start; `scratch-<runId>.log` is one line per event, each starting `godot-mcp scratch run <runId> `: `scene <scene> start` (`start alone` for a replay), `scene <scene> step <i>/<total> <name>` (`i` 0-based, as in `options.steps`, `total` the scene's step count), `scene <scene> <verdict> in <seconds> s`, and last the completion line. The run id is `<UTC yyyyMMddTHHmmss>-<4 hex>`.

  The log's last line is the completion line: `finished: done, passed <true|false>, green <n>, red <n>, killed <n>, known <n>`, `finished: stopped`, `finished: interrupted` or `finished: failed: <one line>`.

  Wait for it with a `Monitor` on the log (`timeout <limit> tail -n +1 -F` the file, matching `^godot-mcp scratch run \S+ finished:`; on Windows a bare `tail -F` outlives the Monitor's shell and runs on for good), or poll `scratch_status` until `live` is false, instead of holding a call open. A reader opens, reads and closes the files, never holds them open.

  The run's scene sessions are listed like a foreground run's, but an unnamed tool call never resolves to a session a live background run holds; name it (`session`) to drive or read one. The server's exit interrupts the run within the shutdown cap: the files say `interrupted`. A server killed outright writes no completion line and leaves `running` in the status file, with its `serverPid`; `pwsh run.ps1 install` kills running servers this way.

  So give the wait a time limit: the scenes' paces (each divided by the time scale, the last step's pace of each scene once more, pace / min(1, time scale); a scene with a given ceiling counts that ceiling) plus a minute or two for launch and prep. When it passes with no completion line, call `scratch_status`; `live: false` with `state: running` means the server that played the run is gone, and the run must be started again.
- **A headless session draws no frames:** a frame tool (`take_screenshot`, `save_screenshot`, `capture_frames`, a screenshot baseline or comparison, a stepped screenshot) sent to a scratch session gets the headless refusal and does not turn the step red; `get_game_state`, `get_ui_elements` and `run_script` work.
- **Edges:** the prep runs once for the whole run, so a stale C# assembly is built, and a red build refuses the whole run; `prep` is the block `run_project` reports (`build` and `import` `skipped` with `prepare: never`).

  A scratch session is exempt from the folder's agreement on quiet and `shutOutRealGamepads`: the game always runs quiet, real pads live and unmuted, copying nothing from a live session or the arm, and the scene is never refused against the arm or the folder's other sessions.

  While scratch sessions hold the folder, a non-scratch run, attach, preview or arm writes only its own values into `override.cfg`; after a windowed drive ends the file keeps its values until the scratch sessions end, so another server's quiet session is refused meanwhile.

  Each scene is a game session named `<folder>.scratch-<scene>`, or `<options.session>.scratch-<scene>` when `options.session` names the prefix (give each worktree's agent its own, so parallel runs are told apart in `list_sessions`), (numbered `-2`, `-3`, … when another session holds that name), still listed as stopped afterwards so `get_debug_output {session}` reads its output; its stdout and stderr carry the runner's own `[godot-mcp] scratch … end <nonce>` marker lines, which end each window and are never judged.

  The root is the game's current scene once started, whatever it renamed itself to; a root missing a protocol method, a scene with no current scene, or a launch that fails is `red` at `failedAt.index` -1, even for a `known` scene.

  The **boot** (from the launch to the first step) is judged by the error feed alone: an engine error, `push_error` or C# exception there (in the scene's `_ready`, say) makes the scene `red` at `failedAt {index: -1, name: "boot", error: "<message> (<file>:<line>)"}` with no step played and no `details`, even with `options.details`, even for a `known` scene, and it is never played again alone; a scene with no steps that errors at the boot is `red`, not `no-steps`.

  A warning at the boot is not; lines printed before the first step are never judged, and `scratch.patterns` never apply to them.

  A scene's ceiling is each step's pace / time scale + 10 s, the last step's pace once more for the wait after it (its wall time, pace / min(1, time scale)), and 10 s for the markers, in load-adjusted time (the fixed 10 s are not divided); at a scale other than 1 the kill reason reads "each of its N steps' pace at time scale <n> + 10 s, the last step's pace once more ("in wall time" is added above 1), and 10 s for the markers"; past it, or when the game stops answering, the scene is `killed` and its game stopped.

  A scene given a ceiling by `options.ceilings` or `scratch.ceilings` is killed at that many seconds of load-adjusted time from its first step instead, and the kill reason reads `the scene passed its ceiling of <N> s of load-adjusted time, set by options.ceilings, so its game was stopped` (`set by the profile's scratch.ceilings` for the profile's; the backstop clause comes before `, so its game was stopped` when the backstop fired). The derived ceiling's reason is unchanged.

  An error or a failed wait in the pace after the last step, or a stop that had to kill the game, turns an otherwise green scene red, with the reason in `exit`.

  A pattern that takes over 1 s on a line fails that step naming the pattern. A project with `application/run/flush_stdout_on_print.debug` off buffers stdout, so the markers never arrive: the error says so. `--scratch-auto` is refused in any user arguments: the server walks the steps. It cannot run inside `batch_drive`.

### `scratch_status`

- **Does:** reads a background scratch run (`run_scratches` with `options.background`), or with `stop: true` ends it.

  Returns `{runId, state, serverPid, startedAt, updatedAt, scenes: [{scene, state, step?, stepName?, total?, elapsedSeconds?, remainingSecondsAtMost?, verdict?, timeScale?}], timeScale?, result?, error?, live}`; `only: "red"` filters `result` as `run_scratches`' `options.only` does; `timeScale` is the run's scale when not 1 and a scene's own `timeScale: 1` when its profile pace opted out.

  `state` is `running`, `done`, `stopped`, `interrupted` (the server exited) or `failed` (with `error`). `scenes` are in play order, each `queued`, `playing` (with its step index, name and the scene's step count, `remainingSecondsAtMost` the wall seconds the paces still to come take at the scene's time scale plus the last pace's wait after it, launch left out) or `done` (with its `verdict`), or the run's final word for a scene the end cut off mid-step.

  `result`, once `done`, is the object `run_scratches` returns without background. `startedAt` and `updatedAt` are UTC ISO 8601.
- **Use:** `projectPath`, `runId` (as `run_scratches` returned it), `stop` (default false), `only` (`"all"` default, or `"red"`: `result.scenes[].details` keeps only the steps with `ok: false`, and a scene left with none drops the key; every other field stays; a status with no `result`, a running run's, is returned as it is; the status file on disk is never changed, so a later read with `"all"` sees every step; any other value is refused as in `run_scratches`).

  `live` is true while this server still plays the run, until its final write has ended; the status may already show the final state while `live` is still true. A run of this server is read from memory; any other run's status file is returned as it is, with `live` false: one a server killed outright left says `running`, and its `serverPid` tells whose it was.
- **Edges:** `stop: true` ends a live run of this server as a cancelled call ends (its games stopped), writes `stopped` and the completion line, and returns the final status once the run has ended; the first end reason wins, so a run that has already finished is returned unchanged.

  A stop of a run read from its status file is refused, naming the process id the file gives: another server's run, or one this server no longer keeps. A `runId` not of the form `20261019T222144-3f2a` is refused, and so is one with no status file under `projectPath`. It is not read-only: it can stop a run, so it is in the drive class.

## Drive input

All input tools answer `{pointer, heldButtonMask}` once the gesture has ended and two more frames have run (after a mouse button or wheel event, first one physics tick when none has run since, so a world node's `input_event` and any error its handler raises are in that call's own reply); `click`, `drag`, `mouse_button` and `scroll` also say what they hit, each Control as `{path, class}` or null over none, a popup's included.

One input call plays at a time per session. A target is `{element}` (a node's path, name or `%UniqueName`, as under "Paths"), `{text}` (the visible Control showing that text, as `get_ui_elements` reports it: trimmed, case-sensitive; `under` names a node that scopes the search to itself and its descendants) or `{x, y}` in viewport coordinates, exactly one of them.

A `{text}` target takes `match`: `"exact"` (the default), `"prefix"` or `"contains"`. Both sides are trimmed and compared case-sensitively against the whole shown text, so `prefix` matches a multi-line label by its first line's opening words.

A bad value is refused (`match is "x"; it must be "exact", "prefix" or "contains".`), and so is `match` without `text` (`match applies only to a text condition or target; this one has no text.`). `match` does not reach an `item` (`item.text`, a popup item or a tooltip span): those match exactly.

A text target that matches nothing is refused as `no visible Control shows 'Host'` (`no visible Control's text starts with 'Host'` for `prefix`, `no visible Control's text contains 'Host'` for `contains`), followed by up to five near misses (equal ignoring case, or containing) and each node named `Host` with what it shows (`…/Host is named 'Host' and shows 'New Game'`); one that several Controls match, in any mode, is refused listing up to ten with their rects, `narrow it with under` (there is no nth and no first match).

A text match is aimed at as its Control would be, and `aimedAt` gains `matched {by: "text", text}`.

A matched Control with an `owner` that does nothing on a press (one that ignores the mouse, or any `Label` or `RichTextLabel`, such as a card's title, whatever its mouse filter) takes a press landing on a Control of its own scene instance in the same window (its `owner`, or a Control that owner owns that is not itself the root of another scene instanced inside), and `pressedOn` names that Control.

A match that acts on a press itself (a Button) and is covered is refused as covered, as is any match covered by a Control with no `owner` (one a script made with `new`) or by a popup's Control.

Beside `element` (or `text`), `item {text | index | path, column?}` aims at an item drawn inside an `ItemList`, a `TabBar`, a `TabContainer` (its tab bar) or a `Tree`, at the item's rect centre (a Tree's column 0, for an item that folds, past the fold arrow's indent, so it is selected, not folded, and refused when the indent fills the column; a right-to-left ItemList where Godot mirrors it): `text` is the item's text as drawn (translated as the Control's auto-translate mode says; exact, trimmed, case-sensitive; in a Tree, at any depth in `column`), `index` its index (an ItemList item or a tab; not a Tree), `path` a Tree item's column-0 texts from the first shown level down, visible items only, every branch reading a step followed (only several matches at the last step are ambiguous), `column` a Tree's cell (default 0); exactly one of `text`, `index`, `path`, and a blank `text` is refused.

Before anything is pressed the point is checked back through the Control's own lookup (`get_item_at_position`, `get_tab_idx_at_point`, a Tree's item and column at the point); `aimedAt` then has `kind: item`, the list Control's `path` and `class`, and `item {index, text, path?, disabled?}` (a Tree item's `index` is its place among its siblings; a disabled item or tab is still pressed and says `disabled: true`).

Refused with nothing pressed: another class (`… is a Label; item targets take an ItemList, TabBar, TabContainer, Tree, RichTextLabel, OptionButton, MenuButton or PopupMenu`), `path` or `column` off a Tree, `index` on a Tree, no such item (listing up to ten shown texts), several items with that text (listing each index and rect, `narrow with item.index`, or each Tree path, `narrow with item.path`; Tree siblings sharing one path are told to aim with `{x, y}` inside a listed rect), a hidden tab or Tree item (and a TabContainer whose tabs are hidden), an item under a collapsed Tree item (naming the outermost one: expand it first), an item scrolled outside its list's visible rect, which leaves out a Tree's column-title row and any visible scroll bar (both rects given: scroll the list first), an item whose point lies outside the viewport, such as a tab past a `clip_tabs`-off bar's edge (its rect, point and the viewport's size given: bring it into view first), a tab scrolled out of a clipped tab bar's drawn range (`… is scrolled out of the tab bar: tab 7 lies after the drawn tabs (0 to 2); scroll over the tab strip, scroll {target: {x, y}, direction: "down", notches: 5}, then try again`: follow it, repeating while the tab is not drawn, since a wheel notch moves the bar's offset by one tab; for a TabContainer the point is its tab strip's; a bar with `scrolling_enabled` off names its increment or decrement arrow's point and how many clicks instead), and a tab or item not laid out yet (`has no drawn rect`).

The press must land on the Control that draws the item (the tab bar for a TabContainer), never on one of its scroll bars.

Beside an `element` (or `text`) naming an `OptionButton`, a `MenuButton` or an open `PopupMenu` (an element names a `PopupMenu` only with `item`, and `offset` there is refused: `offset aims inside a world node; <path> is a PopupMenu.`), `item {text | index}` aims at an item of its popup: `index` counts separators, as `OptionButton.selected` does; `text` matches the drawn text as above, a miss listing up to ten item texts (separators left out) and several matches listing their indices (`<popup> has 2 items reading 'Save': index 1, index 4; narrow with item.index`); `path` or `column` is refused (`<path> is a <class>; a popup item takes text or index, not item.path`).

In the refusals below `<popup>` is the `PopupMenu`'s path, or `the popup of <button path>`, and an item is its text quoted, or `item <index>` when it has none. A `PopupMenu` exposes no item rect, so the point is found by real mouse motions over the popup's items, bisecting on y and reading `get_focused_item()` after each (at most 2 x ceil(log2(items)) + 4 motions); the pointer ends on the item, highlighted as a player's pointer leaves it.

A `click` or `mouse_button` press on an item of a closed `OptionButton` or `MenuButton` popup first clicks the button (a real press and release at its centre, hit-checked as any click), waits up to 1000 ms for the popup to show, then aims, and `aimedAt` gains `opened: true`; an open popup is never clicked again, since a press on its button would close it.

A popup item's `aimedAt` has `kind: item`, the named node's `path` and `class`, and `item {index, text, submenu?}`, with no `viewport` when the named node is the `PopupMenu` itself; `pressedOn` names the internal `PopupMenuItems` Control that draws the items.

An item with a submenu takes two calls: `click`, `hover` and a `mouse_button` press or move hold the pointer on it until the submenu shows (within its `submenu_popup_delay` plus 1000 ms), press nothing, and report the submenu `PopupMenu`'s path as `aimedAt.item.submenu`; the next call names that path as `element` with its own `item`. A `mouse_button` release on a submenu item is sent, so no button stays held.

Beside an `element` (or `text`) naming a `RichTextLabel`, `item {text | index}` aims at a tooltip span: a run of its text that answers a tooltip of its own (a `[hint=…]`, a `[url tooltip=…]` or an image's tooltip; the label's own `tooltip_text` is not a span, nor is a `[url]` without a tooltip). `text` is the span's tooltip text, not the text it shows (exact, trimmed); `index` counts the spans in reading order among the lines shown, from 0.

Godot lists no spans, so the bridge samples the label pixel by pixel to find them, which costs more on a long label; text scrolled out of the label's visible area has no span. The aim is the centre of the span's first line, and `aimedAt.item` is `{index, text, rect, rects}`: `rect` the first line's, `rects` one per line the span wraps onto, each `{x, y, width, height}` in viewport coordinates. It works for `hover` (the tooltip then shows), `click` and every tool that takes `item`.

Refused with nothing pressed: no span reading the text (`<path> has no tooltip span 'Gold'; spans: index 0 'Coins' at x,y,w,h, …`, up to ten, `none` when the label has none), an index out of range (`<path> has no tooltip span 3; it has 2: …`), several spans reading the text (`<path> has 2 tooltip spans reading 'Gold': index 0 at x,y,w,h, index 1 at …; narrow with item.index`), and `path` or `column` (`<path> is a RichTextLabel; item.path is for a Tree only`).

Spans come only from what a player sees: the label's drawn glyphs and images inside the viewport and every ancestor that clips, so a span scrolled out of a ScrollContainer or not yet revealed by a typewriter is neither listed nor indexed. A span's text is stripped of surrounding whitespace, and its aim is a point a sample confirmed.

The probe stops at 20,000 samples or 250 ms, whichever comes first, and the refusal or hover warning then ends with `the tooltip span probe of <path> stopped after <n> samples in <ms> ms, so spans past (x, y) were not looked for.`: on a very long log each sample walks the label's paragraphs, so the deepest lines may go unsearched.

Refused with nothing pressed on the item: a separator (`'---' in <popup> is a separator; Godot ignores a press on it`); a disabled item (`'Load' in <popup> is disabled; Godot ignores a press on it`), since `get_focused_item()` reads -1 over it and the aim cannot be confirmed; a native popup (`<popup> is a native menu, an OS window of its own, and input events cannot reach a native menu; a popup is embedded only while gui_embed_subwindows is on (display/window/subwindows/embed_subwindows) and its force_native is off`); a `PopupMenu` named itself that is not open (`<popup> is not open; open it first`); a popup filtered by its search bar, a submenu of a filtered popup, and a popup whose hidden search bar still holds text (`<popup> filters its items by the search 'lo'; hidden items cannot be told apart, clear the search first`, naming the popup whose search filters; a search is the only thing that hides a popup item, since `PopupMenu` binds no item visibility); a disabled `OptionButton` or `MenuButton` whose popup is closed (`<path> is disabled; it cannot open its popup`); an item of a closed popup aimed by `hover`, `scroll` or a `mouse_button` move or release (`the popup of <path> is closed; click the button first`); a closed popup while another popup is open (`the popup of <path> is closed and <open popup path> is open, so the press that would open it only closes that one; close it first`); a drag from or to a popup item (`from: an item in a popup cannot start or end a drag; click it instead`); a popup that does not show (`clicking <path> did not open its popup within 1000 ms`); a probe that does not find the item (`could not find 'Save' in <popup> within 8 motions; get_focused_item read [0, 2, -1, …]`, ending `; it may lie below the popup's visible items: scroll the popup first`, or `above`, when every item read lies before it, or after it); a popup that closes while the pointer looks (`<popup> closed while the pointer looked for 'Save'`); a popup with no items inside the viewport (`<popup> draws no items inside the viewport`); and a submenu that does not show (`'Recent' in <popup> did not open its submenu <path> within 1200 ms`).

A refusal that comes after the gesture opened the popup ends with `; the popup is open now`, and the popup stays open.

An element is a Control, aimed at its rect's centre where it shows (through its CanvasLayer, a Camera2D's canvas transform, a `SubViewportContainer` and an embedded window's offset), or a 2D or 3D world node, aimed at its origin, or at `offset {x, y}` (a 2D node, local pixels) or `offset {x, y, z?}` (a 3D node, local units) from it, through its CanvasLayer or Camera2D, or projected through its viewport's current Camera3D; a node inside a SubViewport is carried out through its `SubViewportContainer` to the root.

Every element target adds `aimedAt {x, y, kind: control | node2d | node3d, path, class, viewport?}` (`viewport` when the node is not in the root viewport; a `drag` answers `aimedAt {from, to}`, a point end null).

A Control whose centre lies outside the viewport is refused, nothing pressed, with its rect and centre (bring it into view, or click by `{x, y}` inside its visible part), and one whose centre lies outside its SubViewport or embedded window with that level's rect; a Control in a native window is never refused so.

A world node is refused, nothing pressed, when it projects outside its viewport's visible rect or its embedded window, sits behind its camera, has no current Camera3D, lives in a SubViewport shown without a `SubViewportContainer` (only a container forwards input) or in a native window, or when an embedded window, or a Control whose mouse filter is Stop on the hovered chain, would take the event before physics picking (a `scroll` passes a Control whose `force_pass_scroll_events` is on, the default); a press, drag, scroll or `mouse_button` press or release into a SubViewport whose `gui_disable_input` is on is refused, and `hover` or a `mouse_button` move may still aim at a world node there.

Physics picking needs the node's viewport to have `physics_object_picking` on: the root takes the project setting, a SubViewport defaults to off; a project with `display/window/subwindows/embed_subwindows` off picks only once the window system has seen the pointer over the window.

A handler that calls `accept_event()` on the way cannot be foreseen. `pressedOn` names the `SubViewportContainer` for any target inside it. An `{element}` is checked before any event is sent: a hidden node, a node being freed, or a bare name that several nodes share (listing their paths) is refused.

Then, after the gesture's first motion to the aim point, the Control Godot hovers there must be the target, one of its children or, for a target that ignores the mouse, the ancestor that takes its clicks.

Otherwise nothing is pressed and the call fails naming what covers it, with both rects. This applies to a click, a drag's start, a hover, a scroll and a `mouse_button` press or move; a drag's end may be covered. A tooltip showing over the centre counts as covering it. Aim by `{x, y}` inside the visible part instead.

`click`, `drag`, `mouse_button`, `key` and `type_text` also answer what the gesture set off, as `fired [{node, signal, args?, frame, listeners, count?}]`: the signals emitted on the pressed node's chain (the hovered Control and its ancestors up to the root's child, a popup's `PopupMenu` but not its internal Controls, a target's own chain, a pressed `ButtonGroup` button's other buttons, and the root's `gui_focus_changed`), from the press until the settle frames end, in handler order.

`frame` counts process frames since the gesture's first mouse button, wheel or key event; a signal emitted inside a game handler is listed before the signal that handler answered, so a card's own script signal comes before its button's `pressed`.

A run of one signal on one node folds into one entry with `count`, the run's first `frame` and its last `args`; `args` is left out when empty, and an argument over 200 characters of JSON becomes `{valuePreview, valueLength}`.

`listeners` counts the Godot connections the signal had besides the bridge's and the engine's own wiring, and is null for a C# `[Signal]` (its `+=` handlers are invisible to Godot); a C# `+=` on an engine signal such as `Pressed` is a Godot connection and is counted.

`listeners: 0` on an engine signal means no connection, not that nothing reacts, since a game may poll `button_pressed`. Layout, drawing and pointer signals (the noise list), signals whose names start with `_`, tree signals and an `input_event` carrying a mouse motion are not listed.

`firedDropped` is the number of entries past the first 50 (and past 200 raw ones), present only above 0; `listenedOn` always names the bottom node of each chain listened on (`[]` when none), so an empty `fired` says where nobody emitted; `leftTree` (only when non-empty) names the topmost listened nodes that left the tree, such as a screen swap.

A `drag` listens on the pressed Control's chain, refreshed from the hovered Control after each motion and at the release, so a drop target joins; a `to` target that is a world node or a text match adds its own chain, so a world `Area2D`'s release `input_event` is listed. A drop that frees the Control under the pointer is no bridge error: `releasedOn` still names it and `leftTree` lists it.

A `key` or `type_text` listens on the focus chain: the focus owner and its ancestors in the window that holds key focus (the first embedded window that has focus, else the root; the owner may sit inside a `SubViewport` under that window), refreshed after each key. When a key moves focus, even from none, the new owner's chain joins as the focus changes, so its `focus_entered` is listed, inside a dialog or a `SubViewport` too.

With nothing focused and nothing gaining focus, `listenedOn` and `fired` are `[]`.

For a key gesture `frame` counts from the first key event, no `warning` is added, and `type_text`'s `text_changed` folds into one entry with `count` and the last `args`. `simulate_input`'s raw keys list nothing.

Two things are missed: an emission inside the very dispatch that changed the hover, for the newly hovered nodes (for keys, the follow on `gui_focus_changed` catches the new owner's `focus_entered`), and anything off the chain (an autoload, a sibling screen), for which `watch` takes signal tracks around the call.

A `warning` is added after any other when the pressed Control is a disabled `BaseButton` (`<path> is disabled, so the press set off none of its own signals`) or cannot process in a paused game (`the game is paused and <path> cannot process, so it received no input; resume, or step with frame_control`; its `fired` still lists `gui_focus_changed` and `focus_entered`).

### `click`

- **Does:** moves the pointer to `target`, presses, releases a frame later: `pressedOn` and `releasedOn` name the Control under the press and the release (with `doubleClick`, the second click's); `fired` lists the signals it set off.
- **Use:** `target`, `button` (`left`, `right`, `middle`; default `left`), `doubleClick`.
- **Edges:** on an item of a closed `OptionButton` or `MenuButton` popup, the button is clicked first (`aimedAt.opened`), and on an item with a submenu nothing is pressed (`aimedAt.item.submenu`), as "Drive input" says. A showing tooltip is closed before the press (as `drag` and a `mouse_button` press do).

  The engine closes it on the press too (4.7.2 `viewport.cpp` L2011), but its removal lands a frame late, and the tooltip, not the Control beneath, would be reported as hit. A raw `simulate_input` press leaves the closing to the engine.

### `drag`

- **Does:** presses at `from`, moves in a straight line to `to` over `durationMs` (default 300, at least 3 frames; in a recording, clip time: 60 movie frames a second, however slowly the game runs), each motion carrying the held button, releases at `to`.

  `pressedOn` and `releasedOn` name the Control under the press and under the release point (not the one Godot sends the release to, which is always the pressed one), `guiDragStarted` whether Godot's GUI began a drag, `dropAccepted` whether a Control took the drop, `fired` the signals it set off on the pressed Control's chain and the drop target's.
- **Use:** drag and drop, sliders, panning; `button` as `click`.
- **Edges:** Godot starts a GUI drag only once the path passes `gui/common/drag_threshold` (10 px by default); a shorter drag is a click.

### `type_text`

- **Does:** types `text` into the focused Control, one key press and release a character on a US layout; `\n` is Enter, `\t` Tab.
- **Use:** after a `click` on a LineEdit or TextEdit gives it focus.
- **Result:** `fired` on the focus chain, with `text_changed` folded into one entry.

### `key`

- **Does:** taps, presses or releases one key: `key` is a Godot Key constant without `KEY_` (`Enter`, `Escape`, `Space`, `A`, `F1`, `Up`), `action` `tap` (default), `press` or `release`, `modifiers` any of `shift`, `ctrl`, `alt`, `meta`.
- **Use:** shortcuts, `ui_accept`/`ui_cancel`, holding a movement key (`press`, then later `release`).
- **Edges:** a printable key carries the character it types unless `ctrl`, `alt` or `meta` is held.
- **Result:** `fired` on the focus chain; a Tab lists `gui_focus_changed` and the new owner's `focus_entered`.

### `mouse_button`

- **Does:** moves to `target`, then presses or releases `button` (`action` `press` default, or `release`), or with `action` `move` only moves, pressing nothing and closing no tooltip (`button` ignored); held buttons stay in later motions' `button_mask`. A press answers `pressedOn`, a release `releasedOn`, a move `hoveredOn`; every action answers `fired`, a separate release on the chain of the press it follows (kept until the next press, a freed node skipped).
- **Use:** a drag by hand: `mouse_button` press, `simulate_input` `mouse_motion` events, `mouse_button` release; for paths `drag` cannot draw. `move` to hover without waiting for a tooltip; `hover` waits for it.

### `hover`

- **Does:** moves the pointer to `target` (an element or a point) pressing nothing, held buttons kept in the motion, then, when Godot has a tooltip to show for the pointer, waits for it to show. That tooltip is the hovered Control's, or else the nearest ancestor's, climbing no further than a Control whose mouse filter is Stop or which is top-level (4.7.2 `viewport.cpp` L1566-1596).

  Returns `{pointer, heldButtonMask, hoveredOn, tooltip, warning?}`: `hoveredOn` the Control under the pointer (`{path, class}`, null over none), `tooltip` `{text, x, y, width, height, owner}` in viewport coordinates (`text` null for a custom tooltip with no Label; `owner` the Control whose tooltip it is, `{path, class}`), or null.
- **Use:** checking a tooltip's text or look: hover, then `take_screenshot` with the returned rect as its `crop`. `options {tooltip, timeoutMs}`: `tooltip: false` answers right after the move; `timeoutMs` 0 to 10000, by default `gui/timers/tooltip_delay_sec` plus 1 s; in a recording, clip time (60 movie frames a second).
- **Edges:** Godot starts a tooltip's timer only from a motion over a Control that can process (4.7.2 `viewport.cpp` L2117, L2136), so over a pausable Control in a paused game `hover` answers at once with `tooltip: null` and the warning "the game is paused and <path> cannot process, so its tooltip timer never starts; resume, hover, then pause". A timer once started fires even if the game pauses after, so hover, then `frame_control pause`, keeps the tooltip up.

  A tooltip due but not shown in time answers the warning "no tooltip showed within <n> ms". Over a Control with no tooltip, and none on the ancestors that climb reaches, it answers at once, with no warning, except over a `RichTextLabel` that has tooltip spans: the warning is then `no tooltip at (x, y) on <path>; its nearest tooltip span is index N '<text>' at x,y,w,h, D px away; aim at it with item {index: N}`.

  A `RichTextLabel` with no spans stays silent. A `MenuBar` title's tooltip and an embedded `PopupMenu` item's are read, with a point target or an item target on the open popup (a `PopupMenu`'s items are drawn by its internal `PopupMenuItems` Control, which an element target cannot name, and which `tooltip.owner` reports, the `PopupMenu` two parents up); a native, non-embedded `PopupMenu` is out of reach.

  The hover stays on the injected pointer, from `hover`, `mouse_button` or a `drag`'s end, through the game's own `update_mouse_cursor_state`, a drop's end and a scene change, until a real mouse motion hands it back; a game that re-picks every frame sees one `mouse_exited`/`mouse_entered` pair per frame, and a physics `input_event` sees the bridge's re-pick motions as it sees the engine's own hover pick.

  With `run_project`'s `options.repickHover: false` the bridge no longer re-picks every frame, so only the game's own re-picks (which pick at the OS cursor, unreadable on a quiet run's hidden desktop), a scene change and the end of a GUI drag (a drop or a cancel) move the hover. The bridge then re-picks at the injected pointer once after a scene change and once at the end of a GUI drag, and still only when it owns the pointer with no gesture playing, no button held and the mouse not captured.

### `scroll`

- **Does:** closes a showing tooltip, moves the pointer to `target` (hit-checked as a press is, held buttons kept in the motion), then plays `notches` (1 to 100, default 1) notches one frame apart in `direction` (`up`, `down` default, `left`, `right`).

  A wheel notch is a press and a release of the wheel button in the same frame, both carrying `factor`, as Godot's Windows display server sends one (4.7.2 `display_server_windows.cpp` L6472-6499, L6577-6585); the wheel never joins `heldButtonMask`. Returns `{pointer, heldButtonMask, scrolledOn}`: the Control under the point (`{path, class}`, null over none).
- **Use:** scrolling a `ScrollContainer`, a long `ItemList` or log, or a game's wheel zoom. `options {factor, via}`: `factor` more than 0 and at most 10 (default 1, one standard wheel notch); `via` `wheel` (default) or `pan`, which sends each notch as one `InputEventPanGesture` whose delta is the direction times `factor` (down and right positive), as a trackpad does.
- **Edges:** a `ScrollContainer` moves an eighth of its page per notch times `factor`, or per pan delta of 1 (4.7.2 `scroll_container.cpp` L185-230, L311-320). A child with mouse filter Stop swallows wheel events unless its `force_pass_scroll_events` is on (4.7.2 `viewport.cpp` L1763), so `scrolledOn` may name that child while nothing scrolls; a pan gesture goes to the Control under the point whatever its filter.

### `simulate_action`

- **Does:** injects an InputMap action: `{pointer, heldButtonMask}` as `simulate_input` returns.
- **Use:** driving a game by its actions (`jump`, `ui_accept`) instead of the keys bound to them. `action`, `options {mode, strength}`: `mode` `tap` (default: press, a frame, release, as `key` taps), `press` or `release`; `strength` 0 to 1 (default 1), carried by the press.
- **Edges:** an action missing from the project's InputMap is refused. A pressed action stays held until released. It goes through `simulate_input`'s `action` event, so a raw sequence can still mix actions with other events there.

### `capture_input`

- **Does:** `mode: "start"` begins capturing the session's input; `mode: "stop"` ends it and returns `{events, count, truncated, ended?}`, the events in `simulate_input`'s format (`key`, `mouse_button` with wheel notches as `wheel_*` buttons and their `factor`, `mouse_motion`, `pan_gesture`, `joypad_button`, `joypad_motion`, `action`), with a `wait {ms}` before any event 20 ms or more after the one before, timed on the game's own clock. Feed `events` to `simulate_input` to replay them.
- **Use:** `options {sources, motion}` with start only: `sources` any of `real` (a person's input to the game's window) and `sent` (every event the server's input tools play, pads included), both by default; `motion` keeps mouse motions with no button held (false by default; a drag's motions are always kept, so it replays as a drag). To record a person playing, run with `options.quiet: false` or attach, start, let them play, stop.
- **Edges:** a quiet session (a run, or an attach with `quiet: true`) gets no real input, so it captures `sent` only and start's result says so in `warning`. A gesture comes back as its raw events (a `drag` as a press, motions and a release; `type_text` as keys). Capture stops at 2000 events, waits included, with `truncated: true`.

  A capture survives its game: after a stop, restart or crash, `capture_input stop` naming the session returns what was captured before, with `ended` (`stop`, `restart`, `exit`). Only one capture runs per session; a second start is refused. The extra mouse buttons and gestures other than a pan are not captured, since `simulate_input` cannot play them.

### `simulate_input`

- **Does:** sends raw `events`, one frame apart: `key`, `mouse_button` (`button` `left`, `right`, `middle`, or a wheel notch `wheel_up`, `wheel_down`, `wheel_left`, `wheel_right` with `factor?`, default 1), `mouse_motion`, `pan_gesture {x, y, delta_x?, delta_y?}` (a trackpad pan, deltas 0 when left out), `joypad_button`, `joypad_motion`, `action` (an InputMap action with `pressed?`, `strength?`), `click_element`, `wait {ms}`.
- **Use:** input actions directly, curved mouse paths, precise event timing. A `wait {ms}` is game time that `time_scale` does not stretch, so in a recording it lasts its length in the clip however slowly the game runs. An omitted `pressed` on a key, mouse or pad button is a press and a release a frame apart, on a wheel button both in one frame as `scroll` sends them; a wheel button never joins the held buttons. A motion's `relative` and `button_mask` default from the pointer and held buttons.
- **Edges:** `x`, `y` are viewport coordinates as everywhere.

### `stress_input`

- **Does:** fires `count` random inputs, one a bridge call, each drawn uniformly and seeded from `pool`, and reports `{survived, seed, iterations, stoppedAt?, drawn {actions, keys, elements}, errors, skipped}`. An action is tapped as `simulate_action` taps it, a key as `key` taps it, an element clicked as `click` clicks it. `errors` lists each new error once, keyed by message, file and line, with the `iteration` it first appeared at and its `count`.
- **Use:** `pool {actions?, keys?, elements?}` (1 to 200 entries in all), `count` (1 to 1000, default 100), `seed` (0 or more; left out, one is drawn and returned), `options {gapMs}` (0 to 5000 milliseconds after each input, default 0, of game time `time_scale` does not stretch: in a recording, clip time). The same seed and pool replay the same sequence, so a failing run is reproduced by passing its `seed` back.
- **Edges:** a draw the bridge refuses (an action missing from the InputMap, an element hidden at that moment, an unknown key) does not end the run: it is counted under `skipped` with the bridge's reason, the iteration it was first refused at and its count. The run stops at the first input the game does not answer, with `survived: false` and `stoppedAt`; otherwise `survived` means the game still runs and answers a ping at the end.

  It cannot run inside `batch_drive` (a thousand inputs outrun its deadline), and its result has no ordinary `errors` key: the run's own `errors` replace it. In a recording, each input's reply is waited on in clip frames, as the input tools' is, so a slow recorded game does not end a gapped run early.

### `gamepad_button`

- **Does:** taps, presses or releases one pad button as a pad's driver would: `button` a JoyButton without `JOY_BUTTON_` (`A`, `B`, `START`, `DPAD_DOWN`, `LEFT_SHOULDER`...), `action` `tap` (default), `press` or `release`, `device` 0 to 15.

  Without `device`, all three pad tools (and `simulate_input`'s `joypad_*` events without one) inject on the lowest id no connected real pad holds, keep that id for the game's life while it stays free, and report it as `device`; claim that pad in a game's pad-selection step as a player would.
- **Use:** actions bound to pad buttons; the d-pad moves GUI focus through the default `ui_*` bindings.
- **Edges (all three pad tools):** the injected pad never shows in `Input.get_connected_joypads` (only the real pads do), so a game that waits for a connected pad needs an OS-level virtual pad. An explicit `device` a real pad holds is injected as asked, with a `warning` naming the pad; with real pads on all 16 ids and no `device`, the call is refused.

  Real pads also feed the game unless the session was started with `shutOutRealGamepads`, which costs focus-out notifications to the game (a game that pauses on focus loss will) and leaves popups (menus, `OptionButton` lists) open, as `run_project`'s edges say.

### `gamepad_axis`

- **Does:** moves one axis: `axis` (`LEFT_X`, `LEFT_Y`, `RIGHT_X`, `RIGHT_Y`, `TRIGGER_LEFT`, `TRIGGER_RIGHT`), `value` (-1 to 1 for sticks, Y down-positive; 0 to 1 for triggers), `device`, `options {durationMs, release}` to sweep and then let go.
- **Use:** triggers, analogue strength past a deadzone.

### `gamepad_stick`

- **Does:** pushes `stick` (`left` or `right`) to `position {x, y}`, both axes each frame; `device`, `options {durationMs, release}` (`durationMs` in a recording counts clip time, 60 movie frames a second, however slowly the game runs).
- **Use:** menu navigation with a stick, character movement.
- **Edges:** a push moves GUI focus once, on the change from released to pressed; the next move needs a release first (`options.release: true`, or a push to `0, 0`).

## Time

### `frame_control`

- **Does:** `action` `pause` or `resume` sets `SceneTree.paused`; `step` advances exactly `count` (1 to 1000, default 1) drawn frames, or physics ticks with `options.unit: "physics"`, and leaves the game paused; `time_scale` sets `Engine.time_scale` to `scale` (above 0, at most 100). Returns `{paused, timeScale, processFrames, physicsFrames}`, and a step adds `frame`, `Engine.get_process_frames()` where it stopped.
- **Use:** freezing the game to inspect it; `step` with `options.screenshot: true` to capture the exact frame reached; `time_scale` to fast-forward a slow sequence.

  `step` with `options.until`, any `wait_for` condition but `frames` or `output` (a `text` one takes `match` too), stops on the first stepped frame that meets it and stays paused there, so every read tool, a screenshot included, sees that frame. With `until`, `count` is the most frames the step runs, 1000 when left out (still 1 without `until`; 1 to 1000).

  The condition is checked after each stepped frame, never before the first (so an `until: {node, stable: N}` counts stepped frames and needs a `count` above N), so a step runs at least one frame even when the condition already holds: at the frame's draw with the process unit, as the next physics tick starts with `options.unit: "physics"`.

  The result adds `met` with `value` (`args` for a signal), or `last` when not met (a signal has none), and `failedChecks` and `mutedErrors` when expression checks failed or raised muted engine errors, as `wait_for` reports them.

  An `until` never met runs `count` frames, stays paused and answers `met: false` with `last`: a result, not an error. It works from a running game too: "run until X, then freeze" is `frame_control step {count: 1000, options: {until: X}}`; a stop further than 1000 frames (about 17 s) away takes a `wait_for` on an earlier sign first.
- **Edges:** nodes whose `process_mode` ignores pause keep running. A step counts drawn frames, so it is refused while the window is minimized or in low-processor mode, and a step whose frames stop being drawn fails at its deadline (10 s + 100 ms a frame, load-adjusted), leaving the game paused.

  A step fails if the game pauses itself midway, unless its `until` is met on that frame or tick. While a step or a `capture_frames` runs, other steps, `pause` and `resume` are refused. On a headless game a step with `options.screenshot` fails with `take_screenshot`'s headless refusal, in place of the no-draw refusal a step without a screenshot still gets.

  `until` is refused on `pause`, `resume` and `time_scale` (`options.until applies to step only.`; `options.input` likewise, `options.input applies to step only.`), and with a `frames` condition (`frame_control step's until takes any wait_for condition but frames: count is the most frames a step runs.`) or an output one.

  The output refusal reads `frame_control step's until takes any wait_for condition but output: a step waits on the game's frames, not its printed lines.` Its other conditions are refused as `wait_for` refuses them, `uiChanged` with no pending baseline included, before any frame runs.

  A condition that cannot be met (an ambiguous unique name) fails the step with `wait_for`'s text, the game left paused at that frame. A `state` until (`until: {node, state, equals}`) reads the node's state after each stepped frame as `wait_for` does, a C# node's through the C# helper, so it stops on the frame a game's own key reaches a value. A signal `until` is connected when the request arrives, so a signal emitted before the first stepped frame is met at that first frame.

  `step` with `options.input` sends `simulate_input`'s raw events (`key`, `mouse_button`, `mouse_motion`, `pan_gesture`, `joypad_button`, `joypad_motion`, `action`; at most 100) inside chosen stepped frames: each entry takes `frame`, the 0-based stepped frame it is sent in (0 when left out), and runs under the session's input gate as an input tool does.

  A `wait` or a `click_element` entry is refused (a step's input is placed by frame), as is any `options.input` with `unit: "physics"`, a `frame` that is not a whole number 0 or more or lies past the step's last frame, and an unknown type (`simulate_input`'s `events[<i>] has type …`).

  Each event is sent at its frame's `process_frame`, before that frame's nodes process, so `_input`, `gui_input` and `is_action_just_pressed` in `_process` see it in that frame. `_physics_process` sees a press at the next physics tick, one frame later than a real event, which the engine flushes between frames.

  A riding click reaches a world node's `input_event` one physics tick late as well, so place a world click no later than the step's last frame minus 1. Events of one frame are sent in list order; a frame the window does not draw sends nothing and does not move the schedule on.

  A `key`, `mouse_button` (not a wheel notch) or `joypad_button` with `pressed` left out is a press at `frame` and a release at `frame + 1`, which must fall inside the step (otherwise refused naming `pass pressed: true and a release event, or raise count`).

  With `pressed` given, the event is sent as it is; an `action` without `pressed` presses and never releases. A step with input takes the `uiChanged` baseline first when none is pending, so `until: {uiChanged: true}` stops on the frame that input's effect shows; a refused step leaves no baseline pending.

  When `until` is met before the last frame, the result adds `inputNotSent`, the events left unsent (a press-and-release entry counts as two). A step with input adds `held: {keys, mouseButtons, padButtons, actions}` when anything is still pressed, presses made before the step included, all four lists present; release them with `simulate_input` or the matching tool.

  A pad event on a connected real pad adds `simulate_input`'s warning, `Device N is a connected real pad (<name>); its own input mixes with what is injected there.`, as `warning` (several are joined with a space).

  An event the game cannot play (an unknown key, an action not in the InputMap) fails the step at its frame with `event <i>: <reason>`, leaving the game paused there. Any failed step whose input leaves something held (a pause changed by the game, a stall, a condition that cannot be met, a screenshot that cannot be saved, an event failure) ends its error with `; held: ` and the `held` object.

### `capture_frames`

- **Does:** saves the game's frame at each of a list of moments in one call, with no round trip per frame: `{folder, files, width, height, points: [{at, frame, file, gameSeconds, late}], shared, stopped?, missed?, call?, start?}`, paths only, no images.

  `folder` is the directory of the PNGs and `files` names each distinct one once, in the order first taken; a point's `file` is its index into `files`, `frame` the engine frame it was taken in, and `shared` counts the points that took a file an earlier point took (a game running slower than the points are spaced shares frames). A capture stopped before its first point has empty `files` and `points` and no `folder`, `width` or `height`.
- **Use:** frames at set points of an animation or a timer-driven sequence. `at` (1 to 1000 ascending seconds, each 0 to 120), or `options {every, for}` for evenly spaced points, always including one at 0 (`every` 0.5 with `for` 1 gives 0, 0.5 and 1.0, and with `for` 1.2 the same; the point at 0 counts against the 1000-point cap, so 1001 is refused with "makes 1001 points", and an `every` longer than `for` is refused); `options.crop` as `take_screenshot`'s.

  The seconds are game time from the call's start, the sum of each frame's delta, so they follow `Engine.time_scale` as the game's own timers do. `options.call {node, method, args?}` calls a method in the clock's first frame, before the nodes process it, so the points count from the method's entry with no round trip between: capture the frames of an effect the method starts. A scratch root's `PlayStep` takes a step's name there (the scratch protocol under `run_scratches`).

  The point at 0 is taken in the clock's first frame, after `options.call` has run, so it is one frame into the motion, not the pose before the call; a frame from before the call needs `take_screenshot`, or `start.call` with the start's clock. A coroutine is not awaited (it runs to its first `await`), and the result adds `call: {value}`, its return value as `call_method` returns it (`{valuePreview, valueLength}` past 2000 characters).

  Or `options.call {tool, args?}` calls a game tool, a C# method the game marks `[GodotMcpTool]`, by its name as `list_game_tools` lists it, with `args` an object keyed by parameter name as `call_game_tool` takes it; the result's `call` is then `call_game_tool`'s `{tool, value, type, build?}`, and a tool that returns a `Task` is not awaited: `call` reads `pending: true` with a null `value`.

  A call refused as `call_method` or `call_game_tool` refuses it, or an error it raises, fails the capture with no frames.

  `options.start {node, property, equals | exists | expression, edge?, timeoutMs?, then?, call?}` is a condition laid out as `wait_for`'s, of kind `exists`, `property` or `expression`, that the clock waits for: the points count from the frame it is met, not from the call, so a round trip after a `wait_for` does not shift them.

  `start.then {call?, timeScale?}` runs in the met frame as `wait_for`'s `options.then` does (`call` first, then `timeScale`), and the capture's own `options.call` runs after it, in that frame, where the clock starts. A start already true at the call starts the clock at the next process frame, as a capture without one.

  `start.call` takes `options.call`'s shape (a method call or a game tool) and makes the call that causes the change the start waits for: the bridge waits one frame, takes the start's baseline check (for an `edge` start, its first false check), then makes the call, so the start sees the rise its own call makes.

  The clock starts in the frame the start is met, which with a call can be the call's own frame, and `start.timeoutMs` counts from after the call. `start.call` is allowed without `edge`, and `options.call` still runs after the start is met.

  A change made by an earlier tool call can rise and fall before the next call's first check, since that call reaches the game 0.2 s to seconds later: put the call that causes the change in the same request (`start.call`, or `wait_for`'s `options.call`).

  A `start.call` the bridge refuses, or that fails, fails the capture with its error and takes no frames.

  Another kind is refused: `options.start takes an exists, property or expression condition; this condition is {kind}.` (a start given `uiChanged`, `true` or `"now"`, is of kind `uiChanged`, one given `text` or `under` of kind `text`, one given `stable` of kind `settled`, one given `state` of kind `state`, and one given `output` of kind `output`, whatever else it carries) A bad `start.timeoutMs` or `start.then` is refused with `wait_for`'s texts (`timeoutMs must be between 0 and 120000.`, `options.then needs call, timeScale, pause or a mix.`).

  The result adds `start: {met: true, frame, call?, then?, failedChecks?, mutedErrors?}`, `call` being `{value}`, the start call's return value, reported when the start is met and when it times out; `frame` being `Engine.get_process_frames()` where the start was met.

  `start.timeoutMs` counts real time (not load-adjusted, and not clip time in a recording), defaults to 10 s (0 checks once, at the call) and is added to the capture's allowance, at most 600000 ms in all. A start that times out is still a success: `stopped: true`, empty `points` and `files`, `missed` every point, and `start: {met: false, last, call?}`.
- **A headless game** is refused as `take_screenshot` refuses it, before the busy and paused refusals below.
- **Edges:** a point is taken in the first frame at or after it, and `late` says by how many seconds; points due in the same frame share one file. Refused while the game is paused (its game time does not advance; step it with `frame_control` and `options.screenshot` instead) and while a `frame_control` step runs; while it runs, `pause`, `resume`, a step and another capture are refused.
- **Cost:** each frame is encoded and written to PNG on a worker thread, at most 8 at once (past that, on the game's main thread), so the game clock keeps its pace; the main thread still pays the frame's read-back, about 4-11 ms at 1920x1080. The reply comes once the last file is complete, and a file is named `<UTC stamp>-<pid>-<process frame>.png`. `options.crop` makes each frame cheaper still.

  It ends at `options.timeoutMs` (default the last point + 10 s + 100 ms a point, load-adjusted; 1 to 600000) and then answers the frames taken with `stopped: true` and `missed`, the points not reached, still a success: under a small `time_scale`, raise `timeoutMs`.

  A game that pauses itself partway stops its clock, so it ends the same way. A call refused as `call_method` refuses it, or a method that raises an error (a GDScript runtime error or C# exception cannot be caught, so the bridge's error feed is read across the call), fails the capture with that error and no frames; an unrelated error another node raises in that same frame fails it too.

### `watch`

- **Does:** samples properties and expressions of the running game once a frame over a window, beside every other tool, and returns each track's change points.

  `action` `start` begins a watch and answers after its first frame with `{startFrame, tracks, call?}` (and the signal, monitor and audio keys below), each track as resolved (`name?`, `node` as an absolute path, `property?`; a named property track keeps its `node` and `property` beside `name`); `stop` ends the watch, or collects one whose window has ended, and answers its timeline; `run` starts one, waits out its window and answers the timeline in one call.

  The timeline: `{startFrame, frames, gameMs, wallMs, clockErrorMs?, paused?: [[from, to]], stopped?, call?, warning?, tracks: [{name?, node?, property?, first, last, changes, points: [[frame, gameMs, value, at]], dropped?, cut?, min?, max?, minAt?, maxAt?}]}`, plus the signal, monitor and audio keys below when those tracks are watched.

  `startFrame` is `Engine.get_process_frames()` at the watch's first frame (`get_physics_frames()` with `unit: "physics"`), so it lines up with `get_game_state`'s `frame` and `frame_control`'s `processFrames`; every `frame` counts from 0 there, paused frames included, and a point's `gameMs` is the game time since then.

  `at` ends every point, signal event and audio voice: its time on the drive clock in ms (to 0.1), the clock `wait_for`'s `metAt` and `batch_drive`'s `startMs` and `endMs` are on, so a watch's items compare with them directly, and the watches of two games (a host and a guest) line up on `at`. A point's and a voice's `at` is the start of its frame, shared by every item of that frame; a signal event's is its own, mid-frame. `at` is null when the session's clock cannot convert it.

  `clockErrorMs` is the half-width of the clock's bounds, in ms: one value for the whole timeline, since every item is converted from one reading of them, and present only when at least one `at` converted. Before converting, a `stop` or `run` on bounds wider than 2 ms, or missing, sends one extra clock ping, as a met `wait_for` does: about a frame, up to 0.5 s if unanswered. `start`'s reply carries no `at`.

  A watch has no clip time: in a recorded session `at` is still drive time. It samples unpaused frames only, so a gap in `at` between two points includes paused wall time that `gameMs` does not. Monitor `spikes`, `minAt`/`maxAt` and `heard` ranges stay frame-only.

  `frames` and `gameMs` count the unpaused frames sampled and their game time, `wallMs` the real time from the first frame to the window's end. `stopped` is `stop` when a `stop` ended the window early and `deadline` when its deadline did; left out, the window ran full. `call` is the run's `options.call` result, which takes `{node, method, args?}` or a game tool's `{tool, args?}` and answers as `capture_frames`' does, a `PlayStep` name included.
- **Use:** to learn how values moved over a stretch of play: `run` for one value or several in one call; `start`, drive with any tools, `stop` when the stretch is your own input, a `wait_for` or a step. `wait_for` waits for one condition and says when it held; `capture_frames` keeps images; a watch keeps numbers and values. `tracks {properties: [{node, property, name?, minDelta?}], expressions: [{name, expression, node?, minDelta?}]}`, 1 to 32 tracks together.

  A property is a name or a path into one (`position:x`, `modulate:a`), read with `get_indexed`, so a non-exported C# field Godot can marshal reads too.

  An expression is a Godot `Expression` with `wait_for`'s inputs, `root`, `tree`, `Input`, `Engine` and, when the track names a `node`, `node` (also its base instance): `{name: "focus", expression: "root.gui_get_focus_owner()"}`, `{name: "screens", node: "Main/Safe/Screens", expression: "node.get_children()"}`; a Node in a value reads as its path. A track is keyed by its `name`, else by `node:property`; the keys must be unique.

  `window` is exactly one of `{frames}` (1 to 7200 unpaused frames, physics ticks with `unit: "physics"`) or `{gameMs}` (1 to 120000 ms of game time, the sum of each unpaused frame's scaled delta, so it follows `Engine.time_scale` as the game's own timers do); `start` defaults to `{frames: 600}`, `run` needs one.

  `options {call, unit, budgetMs}`: `call {node, method, args?}` is called in the watch's first frame, before its first sample, so the window counts from the method's entry (a coroutine is not awaited), and `start`'s and `run`'s replies add `call: {value}`; `unit` is `process` (the default) or `physics`.

  `minDelta` (above 0, numeric tracks only) keeps a sample only when it moved at least that far from the last one kept (an int or float by its difference, a vector when any component did): `minDelta: 1` on a tween's `position:y`. Between `start` and `stop` any tool runs: input, `wait_for`, `capture_frames`, `get_game_state`, `run_script`, `frame_control`'s `pause`, `step` and `resume`.

  In `batch_drive`, `start` and `stop` are steps like any other (a later step cannot read an earlier one's result), a `run` step holds the batch for its window inside its 300 s, and a watch the batch leaves running keeps running.
- **Signals:** `tracks.signals: [{node, signal} | {group, signal}]` (at most 16, connecting at most 200 nodes) records every emission as `events: [[frame, gameMs, node, signal, args, at]]`, in emission order across tracks, plus `eventCounts: {"<node>:<signal>": n}` counting all of them.

  A group is resolved once at start: a member without the signal is listed in `skipped` (in `start`'s reply and the timeline), and a node joining later is not followed. `start` lists `signals: [{node | group, signal, connected}]`; a node and signal two tracks name is connected and counted once, under the first track, so a later track can show `connected: 0`.

  A signal is stamped with the frame it fires in, so one emitted beside a value set in frame N's `_process` reads N while the value shows at sample N+1; an emission before the first sample (an `options.call`'s) is frame 0, one in a paused frame is still recorded, and a freed emitter's signals just stop.

  `events` keeps at most 300, shared fairly across signal tracks, each keeping its earliest, with `eventsDropped` counting the rest; an argument over 200 characters comes back as `{valuePreview, valueLength}`. A C# `[Signal]` is a Godot signal and is watched by its name without the `EventHandler` suffix; a plain C# `event` is not.
- **Monitors:** `tracks.monitors: ["frame_ms", "object/nodes", "<custom id>"]` (at most 16; monitors alone are a watch) reads performance monitors each sampled frame and answers `monitors: [{name, samples, p50, p95, p99, max, maxAt, mean, spikes?, custom?, nonNumeric?, over?}]`, summarised by the game at the end, so a 7200-frame series costs no more on the wire than a short one.

  `frame_ms` is the wall time between one frame's start and the next's (`Time.get_ticks_usec()`), in ms to 3 decimals: physics, process, draw and the frame cap's wait together, so a 60 fps quiet run idles near 16.7. Its `over: {budget, count, frames}` counts the readings over `options.budgetMs` (0 to 10000; by default 1.5 frames of the game's `max_fps`, else of 60 fps: 25 ms), `frames` being how many readings there were, and its `spikes` are the readings over budget.

  A built-in monitor is named by Godot's own name for it (`Performance.get_monitor_name`): `object/nodes`, `object/orphan_nodes`, `raster/total_draw_calls`, `raster/total_objects_drawn`, `video/video_mem`, `pipeline/compilations_draw`, `physics_2d/active_objects`, `audio/driver/output_latency`, … (59 in all); its `spikes` are its highest readings, left out when it never changed.

  A custom monitor is the id the game gave `Performance.add_custom_monitor` (`custom: true`), and a built-in name wins over a custom id of the same name. Percentiles are nearest-rank; `maxAt` is the frame of the first maximum; `spikes` are `[frame, value]`, at most 20, highest first. `start` lists `monitors: [{name, custom?}]`.
- **Audio:** `tracks.audio: {players: {under?}}` follows every `AudioStreamPlayer`, `AudioStreamPlayer2D` and `AudioStreamPlayer3D` at or under `under` (default the root), found at start or added during the window, and answers `audio: {voices: [[frame, gameMs, event, path, {stream?, bus?, maxPolyphony?, position?, reason?}, at]], voiceCounts: {"<path>": {starts, ends}}, voiceCountsCut?, voicesDropped?, voicesCut?, playersFollowed?, playersSkipped?}`.

  `tracks.audio: {buses: ["Effects", "Music"] | ["*"], thresholdDb?}` reads bus levels instead of, or beside, `players` (see Buses below).

  `audio` with neither `players` nor `buses`, or an empty `under`, is refused, and an `under` that does not exist gets the not-found text every tool uses. `start`'s reply adds `audio: {driver, mixRate, bridgeMuted, players?: {count, playing: [{path, stream, bus, ...}]}, playersSkipped?, buses?, busesSkipped?}` (the `players` keys with a players track): what plays at that moment, so `watch run` with a one-frame window is the read of "what is playing now".
  - **Events.** `start` is a new playback (a voice), carrying its `stream` (its resource path, `res://x.tscn::<id>` for one embedded in a scene, or `{class}` when it has none), its `bus`, `maxPolyphony` when above 1, and for a 2D or 3D player its global `position`, not the playback position, null outside the tree.

    `pause` and `resume` are `stream_paused` flipping, which follows the player's oldest voice, so a new voice on a player whose oldest is paused reads no `pause` and a later `resume` can come with none before it. `end` is the player holding no playback, with `reason` `finished` (the sound ran out), `stopped` (no `finished` signal and the player still there: a `stop()`) or `freed` (the player was freed).

    A `finished` voice and a `stopped` one are told apart by the signal the player emitted since the last sample. Voices are read in paused frames too.
  - **Offsets.** With `unit: process`, a `play()` in frame N's processing reads as a `start` at N+1 and one in a physics tick at N, as a value set in frame N shows at sample N+1; an `end` is the frame that first finds no playback.

    A `start` says the game asked for the sound, not that it was heard: a quiet run mixes on the Dummy driver (`driver`), where nothing reaches a speaker, and `bridgeMuted` says the bridge holds the Master bus muted.
  - **Counts.** A voice already playing at `begin` is in `start`'s `players.playing`, and its later `end` counts (`starts: 0, ends: 1`): an `end` with no `start` is a voice `start`'s `players.playing` listed. A polyphonic restart (`max_polyphony` above 1) is a second `start` with no `end` between.

    A path is fixed when the player is first followed, so a pooled player moved elsewhere keeps its first path. A player already playing that is moved into `under` from outside it, during the window, reads a `start` for its old voice.
  - **Caps.** At most 200 players are live at a time (a freed one makes room); the rest are counted in `playersSkipped`, and `playersFollowed` counts only those added during the window.

    The bridge keeps at most 300 voices per player and 1000 per watch; the server keeps 300 voices shared fairly across paths, each keeping its earliest, the earliest players first, with `voicesDropped` counting every voice left out (the bridge's cap and the share) and `voicesCut` the loss to the result's 40000-character budget.

    `voiceCounts` keeps the 100 paths with the most starts and ends and counts the rest in `voiceCountsCut` (present only above 0). A stream value over 200 characters comes back as `{valuePreview, valueLength}`. A player made, played and freed within one frame is never seen.
  - **Buses.** `buses` names the buses to read (at most 16, each once, as the Audio bus layout shows them) or `["*"]` alone for every bus at start, Master included, the first 16 by index, the rest counted in `busesSkipped` (a bus added later is not followed). Each unpaused sampled frame reads the largest left or right peak over a bus's channels, its `volumeDb` and `mute`; paused frames are not read, as property tracks are not.

    A name is resolved at every sample: a bus missing at start is listed `{name, missing: true}` in `start`'s reply and read from any sampled frame it exists in, and one removed or renamed during the window is skipped while it is gone, so `sampledFrames` counts the frames it was read.

    `start`'s reply lists each bus as `{name, index, volumeDb, mute, solo, send, effects: [class]}`. The timeline's `audio.buses` is `[{name, heard: [[fromFrame, toFrame]], heardFrames, sampledFrames, heardCut?, peakMaxDb?, peakMaxAt?, volumeDb?: {first, last, changes}, mute?: {first, last, changes}}]`, plus `busesSkipped?`; `volumeDb`, `mute`, `peakMaxDb` and `peakMaxAt` appear once the bus has been read. A buses-only watch has no `voices`, `voiceCounts` or `voiceTotal`.

    `heard` lists the runs of sampled frames whose peak was above `thresholdDb` (-100 to 0, default -60, Godot's `audio/buses/channel_disable_threshold_db`), broken by a paused frame, at most 50 a bus (the first 25 and the last 25, the rest counted in `heardCut`, which the 40000-character budget can also raise). Peaks are rounded to 0.1 dB and volumes to 0.01 dB; `peakMaxAt` is the frame of the first maximum.

    The peak is read after the bus's volume, mute and solo, so a bus that is not soloed while another is reads -200, and `heard` follows what reached the mix. With the bridge's `mute`, Master reads -200 while the buses that send to it still read their own levels: watch those by name. In a quiet or headless run audio is mixed in bursts of about 93 ms, so a sound shorter than about 81 ms may never reach a peak read: a voice's `start`, not `heard`, says whether a sound fired.
- **Edges:** each frame is sampled as it starts, before the nodes' `_process` (at `process_frame`, or `physics_frame` with `unit: "physics"`), so a value set in frame N shows at sample N+1. A frame that finds the game paused is neither sampled nor counted toward the window, and is listed in `paused`; a `frame_control` step's frames are unpaused, so a watch started on a paused game and then stepped samples exactly the stepped frames.

  `points` are the change points, each `[frame, gameMs, value, at]`: a sample is kept when it differs from the last one kept (numbers within 1e-6, or by `minDelta`; containers member by member), the first always.

  A track keeps its first 200 and last 50 change points and counts the ones between in `dropped`; `changes` counts every change point after the first, dropped and cut ones included; `first` is the first point's value (null when nothing was sampled) and `last` the last sample's; `min`, `max`, `minAt` and `maxAt` (frames) cover every sample of a numeric track, per component for a vector (`{x, y}`).

  A value over 200 characters of JSON, in a point, `first` or `last`, comes back as `{valuePreview, valueLength}`. A timeline over 40000 characters (`at` counted) loses points from the middle of its longest tracks first, each track counting its loss in `cut` and keeping at least its first and last point.

  A property track whose node is freed reads `{"$freed": true}` once and stops. An expression that fails reads `{"$error": "<text>"}` (`its node <path> was freed` once its node is gone), kept again only when the text changes, and goes on sampling, so it recovers when the thing comes back; its failures are not in the call's `errors`.

  An engine error raised directly by a native method an expression calls (`get_child(0)` before the child exists) is kept out of `errors` too (a `wait_for` or step-until expression is muted the same way, but counts what it dropped in `mutedErrors`), and the track reads the null the method returns; an error from game code the expression calls (a `push_error`, a script or C# error, an engine check failing inside a game method) still lands, as does any error raised outside the expression; a GDScript game's `push_error` lands only in a build that records call stacks (any debug build, or a release export with `debug/settings/gdscript/always_track_call_stacks`).

  Refused at start: a missing node, with the not-found text every tool uses; `'<path>' has no property '<name>'.`; `The expression of track '<name>' does not parse: <error>`; a `minDelta` track whose first read is neither a number nor a vector, `minDelta applies to a numeric track (an int, a float or a vector); track '<key>' reads <type>.` (so a `minDelta` expression runs once more at start); `Two tracks are keyed '<key>'; give one of them a different name.`; no track, or more than 32; `window needs exactly one of frames or gameMs.`; `stop takes no tracks, window or options: it ends the watch that runs and returns its timeline.` A `call` refused as `call_method` refuses it, or raising an error, fails the `start` or `run` and ends the watch before it samples.

  One watch runs per game run: `A watch is already running on this game; end it with watch {action: "stop"} before starting another.`; `The last watch's window has ended and its timeline is not collected yet; collect it with watch {action: "stop"} before starting another.`; while a `run` waits, a `start` or `stop` gets `A watch run is still running on this game; it answers when its window ends, and no other watch can start or stop until then.` A `stop` with nothing to collect says why: `No watch runs: none has started in this game run (restart_project and stop_project drop a watch); start one with watch {action: "start"}.`, `No watch runs: the last one's timeline has already been returned; …` or `No watch runs: the last one was dropped when the connection to the server ended; …`.

  A `stop` that lands before a starting watch's first frame makes that `start` fail with `The watch ended before its first frame; start it again.` The deadline is the window's allowance (10 s + 100 ms a frame, or `gameMs` + 10 s, at most 600 s): a started watch holds it in real time, lengthened by the time it spends paused (up to 600 s more), and a `run` is released when it passes in load-adjusted time; either way the watch ends with what it has and `stopped: "deadline"`.

  `restart_project`, `stop_project` and a `detach_project` drop a watch. Expressions and `options.call` run game code, an expression every sampled frame, so write expressions that only read; the tool is annotated destructive for that, and agents keep it in the `read` class.

  A `run_script` that holds the main thread holds the watch's frames with it. Monitors: `frame_ms` has no reading for the watch's first frame, a paused stretch does not read as one long frame, and an `options.call` that stalls its frame shows in the next reading. A hitch reads as one long frame (a 300 ms stall reads about 301-303) followed by one near 0, since Godot skips the next frame's wait to catch up.

  `raster/*` counts 2D draws as well as 3D, and `memory/static` moves while a watch records because the watch's own buffers grow. `pipeline/compilations_*` are cumulative and stay 0 under the Compatibility renderer. A custom monitor whose first read is not a number is refused at start; a later non-number, or an id the game removed, counts in `nonNumeric`.

  Refused at start: `'time/fps' is set once a second, so read per frame it repeats one value and hides which frame was slow; watch 'frame_ms' instead.` (and `time/process`, `time/physics_process`, `time/navigation_process`); `frame_ms measures process frames; drop options.unit "physics" or the frame_ms monitor.`; `options.budgetMs applies to the frame_ms monitor; add "frame_ms" to tracks.monitors or drop budgetMs.`; an unknown name, `No monitor '<name>': not one of Godot's 59 built-in monitors (such as object/nodes or raster/total_draw_calls), not frame_ms, and no custom monitor has that id; a game registers one with Performance.add_custom_monitor.`; more than 16 monitors, or one named twice.

  `warning` says when a reading misleads: frame_ms in a Movie Maker recording, however it was started (it measures rendering speed, not what a player sees), a `raster/*` monitor while no frame was drawn (minimized, headless or low-processor mode), and sampling that averaged over 2 ms a frame, which slows the frames it watches.

  An audio track adds a warning when Master was read while the bridge muted it (the buses that send to it still read) and when a sampled frame ran at an `Engine.time_scale` other than 1, which audio does not follow, so a voice's length in `gameMs` is its length in seconds times `time_scale`; the warnings are joined with `; `.

  Refused at start for audio: `tracks.audio needs players or buses: …`; an empty `buses`, an empty bus name, a bus named twice, more than 16 buses, `"*"` beside another name (`tracks.audio.buses takes "*" alone: …`); a `thresholdDb` outside -100 to 0 (`tracks.audio.thresholdDb must be between -100 and 0; got <value>.`) or without `buses`.

### `wait_for`

- **Does:** waits until `condition` holds, checked each frame (the server polls an output wait), exactly one of `{node, exists}`, `{node, property, equals}`, `{node, signal}`, `{expression}`, `{uiChanged: true | "now"}`, `{gameMs}`, `{frames}`, `{text, match?, under?, exists?}`, `{node, stable: N, property?}`, `{node, state, equals}` or `{output, stream?, after?}`: `{met, elapsedMs, frames, value}` (`args` for a signal; a met bridge wait adds `metAt`, the drive time of the frame whose check met, with `clockErrorMs`, the half-width of the clock's bounds; a session whose clock has none yet, or a bridge too old to send `ticksUsec`, reports neither; a met wait on clock bounds wider than 2 ms, or missing, first sends one clock ping, costing about one frame and up to 0.5 s when the game does not answer, and `metAt` and `clockErrorMs` still come back from the bounds held if it fails), or on timeout `{met: false, ..., last}`.

  An output wait has no `frames`: its `value` is `{line, stream, lineNumber, groups, at}`, `at` the server's read time of the line on the drive clock, and its timeout `last` is `{stdout, stderr}`.
- **Use:** after an input, to wait for its effect instead of sleeping; `timeoutMs` 0 to 120000, default 10000, load-adjusted: under load it waits longer in wall time.

  So a `timeoutMs` is never a pause: to hold for a span of the game's own time (a shot 0.5 s into an effect), wait `{gameMs: N}` (1 to 120000 ms of process delta over unpaused frames, so it follows `time_scale` and load does not stretch it) or `{frames: N}` (1 to 7200 unpaused process frames), each alone, counted from the first frame after the request; `value` is the game ms (whole) or frames reached, up to one frame past N. Left out, their `timeoutMs` is N + 10000 for `gameMs` and 10 s + 100 ms a frame for `frames`, and a settled wait's is 10 s + 100 ms for each of the `stable` + 1 frames it reads, so it grows with `stable` (10200 for 1, 70100 for 600); each is at most 600000.

  A `gameMs`, `frames`, `exists`, `property`, `expression`, `text`, `settled`, `state` or `signal` wait takes `options.call {node, method, args?}` or `{tool, args?}`, as `capture_frames` does (a `PlayStep` name too, in `then.call` as well): the method or game tool is called in the frame the checks start (the next process frame), so a `gameMs` or `frames` count runs from the call's entry, and another wait's first check follows the call in that frame, its `frames` and `elapsedMs` counted from that check (a wait met there reports `frames: 0`).

  A `signal` wait connects before the call, so a signal the call emits at once is caught, and it does not catch an emission from the frames before the call.

  The result adds `call` as `capture_frames`' does; a refused or erroring call fails the wait. A check-once wait (`timeoutMs: 0`) with a call waits for the next frame, calls, then checks once.

  A `uiChanged` or `output` wait refuses `options.call` (`options.call is taken only by a gameMs, frames, exists, property, expression, text, settled, state or signal wait; this condition is {kind}.`), and `batch_drive`'s wait assertion has none: put a `call_method` step before it.

  In a recording (`run_project` `options.record`) a bridge wait counts clip time instead, 60 movie frames a second, however slowly the game runs, so a timed-out `expression: "false"` wait holds a still of exactly that length in the clip, and the result adds `clipMs`, the clip time waited (`elapsedMs` stays real time).

  `{uiChanged: true}` waits for a UI change nobody can name in advance: it compares the visible Controls (as `get_ui_elements` lists them), the focus owner and the top popup with the baseline the bridge snapshots when the first input gesture since launch, or since the last met `uiChanged` wait, starts.

  `{uiChanged: "now"}` takes its snapshot when the wait starts instead (as its probe is built, before the first check, a check-once wait included), replacing one a gesture left pending: for a wait in a parallel group beside an input in another session, where no gesture on this session takes a baseline.

  Any other value is refused `uiChanged takes true or "now"; got <value>.` (`got false.`, `got "later".`, `got 3.`; in a batch prefixed `step N: `), and `"now"` with a `node` is refused as `true` is.

  A check-once `"now"` (`timeoutMs` 0) is refused, `timeoutMs 0 checks once, which a {uiChanged: "now"} wait cannot meet; give it a timeout.`, since its fresh snapshot is checked in the same instant. The snapshot holds the visible Controls' paths, the focus owner and the top popup, not label text, so a text change alone does not meet either form.

  Later gestures keep that baseline, so a press and a separate release, or a batch's several inputs, all count; a met wait uses it up (a `"now"` one too) and a timeout keeps it. Its `value` is `{appeared, disappeared, appearedCount, disappearedCount}` (node paths, at most 20 in each list, the counts full), plus `focus` and `popup` `{before, after}` when those changed; on a timeout `last` is null.

  `property` takes subproperty paths (`position:x`); `equals` compares as `run_script` returns values, numbers within 1e-6. An `expression` sees `node`, `root`, `tree`, `Input` and `Engine`. `options.screenshot: true` captures the frame the condition was met on, as `take_screenshot` does, adding `screenshot {path, width, height, previewPath, previewWidth, previewHeight}` and a preview image at most 480 px wide: for a scene that changes faster than a following `take_screenshot` can catch.
- **A headless game:** `options.screenshot` on a headless game still returns the wait's result, with `warning` "a headless game draws no frames, so no screenshot was taken; read state with get_game_state, get_ui_elements or run_script, or run the scene windowed." and no engine error.
- **Act in the met frame:** `options.then {call?: {node, method, args?} | {tool, args?}, timeScale?, pause?}`, taken by every condition but `output` and by a check-once wait, runs once in the frame the condition is first found true (the met frame): `call` first, then `timeScale` (above 0, at most 100), which scales the next frame on, then `pause: true`, which pauses the game there as `frame_control pause` does and adds `paused: true` to the result's `then`; `frame_control resume` undoes it, and a `timeScale` set beside it applies on resume.

  A `then` needs at least one of the three (`options.then needs call, timeScale, pause or a mix.`).

  Where a pause lands: a plain wait pauses before the met frame's `_process`, after its physics, so a read afterwards gives the values the wait met but anything the game updates in `_process` (a shader parameter, an animation) may still show the previous frame. Give `options.screenshot: true` when a picture or crop must match the values: that wait pauses after the met frame is drawn. For whole stepped frames from a paused game, `frame_control step {options: {until}}` stops on the met frame too.

  A failed `then.call` does not pause (its error says `the game was not paused`), and neither does a wait whose met frame comes while a `frame_control` step or a `capture_frames` runs: the wait fails, naming the running one. `capture_frames`' `start.then` refuses `pause` (`capture_frames' start.then cannot pause: a paused game adds no game time, so the capture would never advance.`).

  A slow-motion shot of a moment: `wait_for {condition: {expression: "…"}, options: {then: {timeScale: 0.2}}}`, then `capture_frames`, or give `capture_frames` an `options.start` that waits for the condition and counts its points from the met frame. The result adds `then: {frame, call?: {value}, timeScale?, paused?}`, `frame` being `Engine.get_process_frames()` when it ran.

  A plain wait runs it at that frame's start, before the nodes process (a check met at once runs it inside the request); with `options.screenshot` it runs after the met frame is drawn and grabbed, but a check-once wait runs it before its capture, which can then show its effect; a signal wait runs it in the frame the wait notices the emission.

  A refused or erroring `then.call` fails the wait with no screenshot: `The condition was met after 12 frames (frame 4810), but then.call failed, so timeScale was not set: …`. A timeout runs nothing. A recording's 60 fps clip does not follow `timeScale`. `batch_drive`'s wait assertion takes no `then` and no `edge`: use a `wait_for` tool step.
- **Wait for a rise:** `options.edge: true`, with an `exists`, `property`, `expression`, `text` or `state` wait, is met only on a check that finds the condition true after a check that found it false.

  A first check that already finds it true is not met, so a condition already true waits for its next rise (with `options.call`, the baseline check runs in the call's frame before the call, so an effect the call makes at once is met as a rise), and one true for the whole wait times out with `met: false` and `last` the true value.

  A check whose expression fails to run does not count as false. Use it to start a `then` on the event itself, not on a state that held before the wait: a `visibility_changed` signal wait is no substitute, since it fires on hide too. Any other condition is refused (`options.edge applies to exists, property, expression, text and state waits; this condition is {kind}.`), and so is `timeoutMs: 0` (`timeoutMs 0 checks once, which an edge wait cannot meet; give it a timeout.`).
- **Wait for a caption:** `{text: "Deal"}` waits until exactly one visible Control shows that text, matched as a click's `{text}` target is: by default the shown text trimmed equals the given text trimmed, case kept; `match: "prefix"` or `"contains"` (see the input tools' targets) waits for a Control whose text starts with or contains it. Met, `value` is `{path, shown, rect}`, `rect` `{x, y, width, height}` in root-viewport coordinates as `get_ui_elements` gives it; unmet, `last` is null.

  `under` names the node whose visible Controls are scanned (as `node` is named; the whole tree when left out), and a text with a `node` is refused (`a text condition takes no node; scope it with under.`). `exists: false` waits for the text to go: met with `value` null, and `last` the held Control while it shows.

  Several Controls showing the text fail an appear wait (`'Deal' is shown by 2 Controls: /root/LeftBox/Deal at 20,300,120,40, /root/RightBox/Deal at 500,300,120,40; narrow it with under.`) and hold a leave wait until every copy is gone.

  An `under` that is missing or hidden shows nothing (a leave is met); one that several nodes share fails the check (`'<under>' names N nodes: <paths>; pass the full path.`). Each check walks the visible Controls in scope, with no cache, and `edge` (a text's next appearing, or with `exists: false` its next going), `options.call`, `then`, `screenshot`, `timeoutMs: 0` and a `frame_control` `until` all compose with it.

  Refused: `under` without `text` (`condition.under scopes a text condition; give text with it.`), a blank `text` (`condition.text is blank; give the text a visible Control shows.`), a blank `under` (`condition.under is blank; name the node to scope the text to, or leave under out.`), a bad `match` value and a `match` on a condition with no `text` (a property, expression or state wait; the same refusals as a target's).

  Wait for a caption with this rather than polling `run_script` or `get_ui_elements`.
- **Wait for a layout to settle:** `{node, stable: N, property?}` waits until the node's value has held unchanged for N process frames (1 to 600), so a press lands where a sorting container or a tween left a control: `wait_for {condition: {node: "Hand", stable: 10}, options: {then: {call: {node: "Hand/Card", method: "emit_signal", args: ["pressed"]}}}}`.

  It watches a Control's global rect (position and size), a Node2D's or Node3D's `global_transform`, or the `property` named (a path such as `position:x`, compared as the `property` condition compares: numbers within 1e-6); any other node without a `property` ends the wait with an error (`'{node}' is a {class}, which has no rect or transform to watch; name a property with stable.`), which is not listed in `failedChecks`.

  Met, `value` is `{frames, value}`, `frames` the frames held; the first reading counts 0, so N + 1 checks are needed, and any change starts the count again. Unmet, `last` shows the value still moving. A transform is compared and reported as Godot's text form (precision about 1e-6 near zero, coarser far from the origin; a structured form is a later item). 
  A frame counts once however often it is checked, and a paused frame counts too, since the process-frame counter advances while paused. A missing node is not met and resets the count.

  `then` runs in the settled frame. `options.call`, `screenshot`, `then` and a `frame_control` `until` compose with it; `until: {node, stable: N}` counts stepped frames, so its step `count` must be above N.

  Refused: `stable` outside 1 to 600 (`condition.stable must be 1 to 600 frames; got {N}.`), `equals` with it (`a settled condition takes no equals; it waits for the value to hold, whatever it is.`), `options.edge`, `timeoutMs: 0` (`timeoutMs 0 checks once, which a settled wait cannot meet; give it a timeout.`) and, as with every wait but a signal, a game that is paused. Wait for a layout with this rather than a fixed `gameMs`.
- **Wait for a state key:** `{node, state, equals}` waits until the value at the key path `state` in the node's state, the one `get_game_state` reads, equals `equals`: `wait_for {condition: {node: "Board", state: "turn", equals: 2}}` or `{node: "Board", state: "seats[0].hp", equals: 6}`, instead of a screenshot or a polling `run_script`. The node must be in the `mcp_state` group and define `_mcp_state()` (GDScript) or `_McpState()` (C#), as for `get_game_state`.

  `state` takes `get_game_state keys`' grammar: a key (`turn`), or a dotted path with list indexes (`seats[0].hp`, `[0]`, `grid[1][0]`), and `$` for the whole state. `$` is checked first, so a state member literally named `$` cannot be reached by a key path (`get_game_state keys: ["$"]` finds it). A key containing a dot or a bracket is unsupported, as for `keys`, and an index of more than 10 digits is not found.

  `equals` is compared as the `property` condition compares: numbers within 1e-6, `equals: null` refused.

  The state is read at every check, each frame, as `get_game_state` reads it: the node's state method runs each time, so a side effect in it repeats. The read keeps `get_game_state`'s limits (depth 4, 5000 values), so a key deeper than that reads the cut marker and compares unequal, and a C# int above 2^53 loses precision on its way to the bridge. A path the state lacks is not met, with `last` null, since a state changes shape as the game moves; met, `value` is the value at the key.

  A node outside the `mcp_state` group ends the wait with an error (`'<node path>' is not in the mcp_state group; add it to the group to read its state.`), as does one whose state cannot be read (`'<node path>' has no state: <get_game_state's error for it>`, such as no state method, a raising one, a coroutine, or a C# helper that cannot run); a missing node is not met, as for `property`.

  `options.edge`, `options.call`, `then`, `screenshot`, `timeoutMs: 0` (checked once, even while paused) and a `frame_control` `until` all compose with it. A C# node's state is read through the C# helper in the same frame, which the first check loads, and the default timeout covers.

  Refused: a blank `state` (`condition.state is blank; give a key path into the node's state, such as seats[0].hp, or $ for the whole state.`), `property` or `stable` with it (`a state condition takes no property or stable; state names the key path to read.`) and `state` without `equals` (`a state condition needs equals, the JSON value the key must equal; null is not supported.`).
- **Wait for a printed line:** `{output: "<regular expression>"}` waits until a line the run, or a process session's program, printed matches a .NET regular expression, against the captured stdout and/or stderr (`stream`: `stdout`, `stderr` or `any`, the default, which scans stdout first).

  It sends nothing to the bridge, so it works while the game is paused, and an attached session, which captures no output, is refused (`Session '<name>' is attached: the server captures no output from a game it did not start; wait on a node, a property or an expression instead.`). It is the only wait a process session accepts.

  Met, `value` is `{line, stream, lineNumber, groups, at}`: the line as printed, cut at 1000 characters in the answer but matched whole; the stream it came from; its number in that stream, as `get_debug_output`'s totals number it; the pattern's capture groups (the whole match excluded, null for a group that did not match); and `at`, the drive time the server read the line, not when the game printed it. With `after: 0` it is the receipt time of a line that can predate the call.

  A timeout's `last` is `{stdout, stderr}`, each stream's newest line or null, both keys always present; `elapsedMs` is real time and the result has no `frames`.

  `after` starts the scan after that line number on both streams: `after: 0` scans every line still held (the last 500 of each stream), and left out, only lines printed after the call are read. Line numbers carry over a restart, as `get_debug_output`'s do, so a mark taken before one still finds the lines printed since. In a Movie Maker recording `timeoutMs` still counts load-adjusted time: no clip time is involved in a check the server makes.

  It takes no `options.call`, `options.edge`, `then` or `screenshot`: `options.call is taken only by a gameMs, frames, exists, property, expression, text, settled, state or signal wait; this condition is output.`

  An `edge` refusal reads `options.edge applies to exists, property, expression, text and state waits; this condition is output.`; `then` and `screenshot` are refused as `options.then needs a wait that meets on a game frame; an output wait meets on a printed line.` and `options.screenshot needs a wait that meets on a game frame; an output wait meets on a printed line.`

  `timeoutMs` defaults to 10000 and is load-adjusted; `timeoutMs: 0` checks once, and needs `after` (`timeoutMs 0 checks once, which an output wait needs after for: without it there is nothing new to check.`). A pattern that takes over its one-second match timeout ends the wait with `condition.output took over 1 s to match a line; simplify the pattern.`

  Refused: a blank `output` (`condition.output is blank; give a regular expression to match a printed line.`), one that does not compile (`condition.output is not a valid regular expression: <reason>.`), a `stream` other than the three (`condition.stream must be stdout, stderr or any; got <value>.`), a negative `after` (`condition.after must be 0 or more; got <value>.`) and another kind's field beside `output`.
- **Edges:** a timeout is a result (`met: false`), not an error.

  While the game is paused only a signal wait, an output wait or a check-once wait (`timeoutMs: 0`) is accepted; a check-once wait is refused for a signal, `gameMs`, `frames` or settled wait; an output one needs `after` for it; and a pause during a `gameMs` or `frames` wait stops its count, so it runs to its timeout with `last` the progress. `equals: null` is not supported: use an expression (`node.target == null`).

  With `options.screenshot`, the condition is checked inside each frame's draw, so the image shows the state the check saw; a timed-out wait captures nothing, a check-once or signal wait captures the draw of the frame running when it is met, which in a running game can already show nodes that processed after the check (pause first for an exact image), and a frame not drawn (a minimized window, low-processor mode) answers a `warning` and no image.

  A property the node lacks fails the call, as does a property path whose sub-name is missing, for a `property` and a settled wait alike (`'<node path>' has no property '<path up to the missing part>'.`); an expression that does not parse fails it, as does one with text after a complete expression, which Godot's `Expression` would otherwise ignore (`a if b else c` would read as `a`; `x is Node` as `x`): `the expression does not parse: text follows a complete expression, and Expression would ignore it`.

  That refusal, and one whose source uses GDScript syntax `Expression` lacks (`func`, `if`, `else`, `is`, `as`, `return`, `var`, `await`, `not in`), ends with `Godot's Expression is not GDScript: …`, listing what it has.

  An expression that fails while it runs (an index past an empty array) counts as not met for that check, and its error is in `errors`; a result that follows failed checks adds `failedChecks {count, error}` (the last failure's text), so a `met: true` after them shows the expression did not hold all along. `Expression` evaluates both sides of `and` and `or`, so `a.size() > 0 and a[0] == x` fails, rather than short-circuits, on an empty `a`.

  An engine error a native method the expression calls raises on the main thread (`get_child(0)` on a node with no children, which the `and` guard does not prevent) is kept out of `errors` and counted in `mutedErrors {count, error}` (`error` the first one's text), met or not; the same holds for a `frame_control` step's expression `until` and a `capture_frames` expression `start`.

  The game's own errors (a `push_error` in a game method the expression calls) and other threads' errors still land in `errors`, as does the once-per-text `godot-mcp wait_for: the expression failed: …` report.

  `uiChanged` takes no `node`, and `true` is refused when no gesture has taken a baseline since launch or since the last met `uiChanged` wait (`uiChanged has no baseline: …; send the input first, or wait for {uiChanged: "now"} in a parallel group beside an input in another session`; the paused refusal applies to both forms); a gesture the bridge refuses (an unknown element or key) still takes one.

  Tooltips, drag previews and the bridge's own nodes never count as a change; the change may already have landed during the gesture's settle frames, so the wait can be met at frame 0.

## Inspect and change the running game

### `get_scene_tree`

- **Does:** lists the live nodes depth first: path, name, class, script, groups, childCount.
- **Use:** `root` (a node to list from), `className` (with subclasses), `group`, `options {maxDepth, offset, limit}`.
- **Edges:** the bridge's own nodes are left out. `className` and `group` filter the listing, not the walk.

### `inspect_node`

- **Does:** reads a live node: `{path, class, script, properties}`, by default its script variables and the inspector's properties. For a C# script the default holds only its `[Export]` members: the editor build Godot runs leaves the others out of the property list.
- **Use:** `node`, and `properties` to read exactly those names (a name the node lacks fails). A named non-exported C# field is read too, as `set_property`, `wait_for {property}` and `watch`'s property tracks reach it; `cs_members` lists them.
- **Edges:** values over 2000 characters come back as `{valuePreview, valueLength}`.

### `set_property`

- **Does:** sets one property of a live node, converting the JSON `value` by the property's declared type, and reads it back: `{path, property, before, after}`.
- **Use:** putting the game into a state to test (`{x, y}` for a Vector2, `"#rrggbb"` or `{r, g, b, a}` for a Color).
- **Edges:** fails, putting the old value back, when the read-back differs (a read-only property, a clamping setter). Typed arrays and dictionaries (`Array[int]`, `Dictionary[String, int]`), exported or not, take a JSON array or object; one whose elements, keys or values are Objects or Resources (`Array[Node2D]`, `Array[Texture2D]`) is refused, and the error says why.

### `call_method`

- **Does:** calls `method` on a live `node` with `args` converted by the parameters' declared types, awaiting a coroutine: `{path, method, value}`.
- **Use:** triggering game logic directly (spawn, damage, load a level); C# methods, `internal` ones included, are reached. `options {timeoutMs}` (1 to 120000, default 10000, load-adjusted).
- **Edges:** fails when the method is missing, the argument count does not fit, an argument does not convert (an array of Objects says why), or Godot refuses the call. A scratch root's `PlayStep` called with one string plays the step of that name (the scratch protocol under `run_scratches`). A GDScript error inside it ends it with a null `value` and comes back in `errors`; the call itself succeeds, so read `errors`.

  Past `timeoutMs` the call fails, `Engine.time_scale` and `SceneTree.paused` go back to their values at the call's start (the error names what it restored), and the method keeps running on its node: only `restart_project` stops it.

### `cs_members`

- **Does:** lists the C# members of a `target`, exactly one of `{node}` (a path, bare name or `%UniqueName`; the node must have a C# script), `{type}` (a full name with its namespace, e.g. `CsProbe.Tally`: statics and constructors) or `{handle}`: `{type, members: [{kind, name, signature, static}], total, offset, next?}`.

  Kinds are `constructor`, `method`, `property`, `field`, `event`; every overload is its own entry, spelled as C# declares it (`internal string Hit(int amount)`). `options {name, nonPublic, offset, limit}`: `name` a case-insensitive part of the member name, `nonPublic` true by default, paged 100 at a time (at most 500), sorted by name then signature.
- **Use:** before calling into a C# game object whose members Godot's own call cannot reach or show: private fields, overloads, generics, static classes. Reads only: no getter or constructor runs.
- **Edges:** lists the game's own types down to, not including, the first Godot class (`describe_class` lists Godot's API); accessors, backing fields, record plumbing and the members Godot's source generator adds are left out.

  A node without a C# script is refused, naming its Godot class; so are the bridge's own nodes, a type no loaded assembly has, and a handle from an earlier run. The first C# call of a game loads the helper into it (about 2 s cold); a project with no C# assembly, or an unbuilt one, is refused before the game is asked.

### `cs_get`

- **Does:** reads a C# property or field of a `target` (as `cs_members`: `{node}`, `{type}` or `{handle}`), private and internal ones included: `{value, type}`, `type` the value's runtime full name (the member's declared type for a null).

  `member` is a name or a dotted path through properties, fields, list indexes and dictionary keys (`Pending.Options[0]`, `Scores["key"]`); a `{type}` target starts at a static member. `options {maxDepth, keep}`: nested objects written 8 levels deep by default (1 to 32); `keep` also returns a `handle` usable as `{handle}` in a later C# call.
- **Use:** for game state Godot's own `get` returns null for: private fields, records, `List<T>`, statics of a plain C# class.
- **Edges:** a getter runs game code. A Godot object comes back as `{"$node": path}`, `{"$object": class, id}` off the tree, or `"<freed object>"`; a value over 20000 characters as `{valuePreview, valueLength}`.

  A lazy sequence (a LINQ `Select`/`Where`, `Enumerable.Range` or `Repeat`, an iterator, any `IEnumerable` that is neither a dictionary nor a materialised collection) is enumerated and written as an array of its items, at most 1000 of them, then one `"<size limit>"` with nothing further read; a materialised collection has no cap of its own. The cap counts items produced, so a sequence that never produces one (`Where` over an endless source with nothing matching) still never returns. An enumeration that throws, or whose enumerator throws as it is disposed, writes the items read so far, then one `"<threw ExceptionName: message>"` item, and stops, without failing the read. `cs_set`'s `before`/`after`, `cs_call`, `call_game_tool`, `run_csharp` and a C# node's `_McpState()` write a value the same way.

  `keep` on a null returns no handle and a `warning`; on a value type, a handle to a copy, with a `warning` that a set through it does not reach the source. Fails on a method name, on an instance member named from a `{type}`, on a `{handle}` to a freed Godot object, and when a getter throws, with the exception's type, message and stack.

### `cs_set`

- **Does:** sets a C# property or field of a `target` and reads it back: `{member, before, after}`. `member` is a name or dotted path whose last segment is a property, field, list index or dictionary key (`Pending.Count`, `Items[2]`, `Scores["key"]`); `value` is JSON converted by the member's type: a record or class from an object, an enum by name or number, a list from an array, an interface or abstract member from `{"$handle": h}` or `{"$node": path}`.

  A string is given as itself (`"value": "Normal"`), not as JSON text holding a string (`"\"Normal\""`); an enum name sent wrapped in quotes anyway reads as the name between them, and an unknown one is refused showing the value as sent. `set_property` reads its `value` the same way, except that a string's quotes are content there and are written into the property.
- **Use:** to put a C# game object into a state for a test when no method gets it there.
- **Edges:** writes what reflection allows: non-public and `init` setters and readonly instance fields; refuses a property with no setter (or no getter, since it cannot be read back), a `const`, a `static readonly` field, and a path through a struct, whose set would change a copy (set the struct whole). A list index or dictionary key must already exist.

  When the member reads something else after the set (a setter that clamps), the old value is put back and the call fails with both (each cut at 2000 characters); a read-back that throws puts the old value back too, and a put-back that fails says what the member now reads. `before` and `after` over 20000 characters come back as `{valuePreview, valueLength}`. A setter that throws fails with the exception's type, message and stack.

### `cs_call`

- **Does:** calls a C# method or constructor of a `target` (as `cs_members`), private and internal ones included: `{value, type}`, `type` the value's runtime full name, `System.Void` for a void. A `{node}` or `{handle}` calls an instance method, a `{type}` a static one, and `member: ".ctor"` on a `{type}` constructs one.

  `args` is a JSON array in parameter order, each converted as `cs_set` converts a value; a `params` parameter takes its array as one value, an `out` takes null, and what `ref` and `out` parameters hold after the call comes back as `outs {name: value}`. `options {signature, typeArgs, keep, timeoutMs, maxDepth}`.
- **Use:** for C# game methods `call_method` cannot reach: overloads sharing a name and count (`Hit(int)`/`Hit(float)`), generics (`typeArgs: ["int"]` or full names), records and `List<T>` arguments, interface parameters (`{"$handle": h}`), statics of plain C# classes, async methods.
- **Edges:** when several overloads fit, the call fails listing each with its ready-to-paste `options.signature` array (in `cs_members`' spelling). A returned `Task` or `ValueTask` is awaited up to `timeoutMs` (10000 ms by default, at most 120000, load-adjusted); past it the call fails and the `Task` keeps running in the game.

  `keep` returns a handle, with `cs_get`'s warnings; a `Node` made by `.ctor` is freed after the call unless kept, and a kept one is outside the tree, with a warning to free or add it. A thrown exception fails with its type, message and stack. Godot's own methods are refused, pointing to `call_method`; a property or field, pointing to `cs_get`; pointer and `Span`-like parameters are refused. Values and outs over 20000 characters come back as `{valuePreview, valueLength}`.

### `list_game_tools`

- **Does:** lists the game's own C# methods marked `[GodotMcpTool("what it does")]`, an attribute the game declares itself (any namespace; matched by the name `GodotMcpToolAttribute`, with optional `Name`, `When` and `ReadOnly` properties; parameter descriptions from `[System.ComponentModel.Description]`): `{tools: [{name, description, when?, readOnly, on, args?, returns, available, reason?}], total, offset, next?, build?, hint?}`, sorted by name.

  `on` is the owner a call would run on: an autoload's name (`Net`), the current scene root's path (`/root/HandScratch`), `static <Type>` (the full name when two static owners share a short one), or null. `args` is a JSON Schema object of the named arguments, each property with `x-csharp` (the C# type as `cs_members` spells it), `description` and `default`. `options {name, offset, limit}`: `name` a case-insensitive substring, `offset` from 0, `limit` 1 to 500 (default 100).
- **Use:** before reading a game's docs for its cheats and debug commands; a marked tool is the game's own way to jump to a room, set a value or play a scratch step, where `cs_call` would need the method's internals; `call_game_tool` then calls one by its name.
- **Edges:** owners and availability are read from the live tree at each call, so a tool on a scene root is available only while that scene is current. A tool is listed `available: false` with its `reason` when its type is neither an autoload's, the current scene root's nor static (its `args` kept), when a parameter cannot be passed by name (`out`, `ref`, pointers, delegates, a generic method; no `args`), when two marked methods share a name, or when its mark has no description string.

  `build: "stale"` says the game runs an older build than the one on disk: the list is what the running game loaded, and `restart_project` runs the new one. A game that marks nothing gets an empty list and a `hint` on declaring the attribute; a `[Conditional("DEBUG")]` attribute class leaves the marks out of a Release build. A GDScript project is refused, pointing at `call_method`; a project with no built C# assembly is refused as the `cs_*` tools refuse it.

### `call_game_tool`

- **Does:** calls one of the game's own tools, a C# method it marks `[GodotMcpTool]`, by its name as `list_game_tools` lists it, on the owner the listing names: `{name, args?, options?: {timeoutMs, maxDepth}, session?}` → `{tool, value, type, build?, errors?}`.

  `args` is an object keyed by parameter name (`{"enemy": true, "index": 0, "hp": 6}`), each value converted as `cs_set` converts one (`{"$node": path}` for a node, an enum by name or number); a parameter with a default may be left out. `type` is the value's runtime full name, `System.Void` for a void; a returned node reads as `{"$node": path}`. `timeoutMs` 1 to 120000 (10000 by default, load-adjusted), `maxDepth` 1 to 32 (8 by default).
- **Use:** for a game's cheats and debug commands once `list_game_tools` has shown them: `call_game_tool {name: "SetHp", args: {enemy: true, index: 0, hp: 6}}` in place of a positional `cs_call` whose argument meanings live in the game's source. It runs in `batch_drive` like any tool, so a cheat, a `wait_for {frames}` and a read go in one call.
- **Edges:** names match exactly (case-sensitive), and a mark's `Name` replaces the method's. Refused before any game code runs: a name no mark gives (`cs_call` reaches an unmarked member), a tool the listing shows unavailable (with its `reason` word for word), an unknown, missing or wrongly typed argument (`SetHp has no parameter 'hitpoints'; it takes enemy, index, hp.`, with a case hint), `args` that is not an object or names a key twice.

  A returned `Task` is awaited up to `timeoutMs`; past it the call fails and the `Task` keeps running in the game. A thrown exception fails with its type, message and stack. `build: "stale"` says the game runs an older build than the one on disk; the call still runs the code the game loaded, and `restart_project` runs the new build. A value over 20000 characters comes back as `{valuePreview, valueLength}`. A GDScript project is refused, pointing at `call_method`.

### `run_csharp`

- **Does:** compiles `code`, a C# method body (statements, or one expression whose value is returned), in the server and runs it in the game on the main thread: `{value, type}` as `cs_call` answers, `type` the value's runtime full name, null for a null value. `await` works in the body, and an unfinished result is awaited up to `timeoutMs` (load-adjusted).

  In scope, unqualified: `Tree` (the `SceneTree`), `Root`, `ToSignal(source, signal)` as a node's script has it (`await ToSignal(Tree, SceneTree.SignalName.ProcessFrame)` waits one frame, in a paused game too; it takes no token, so a signal that never fires holds the snippet past its timeout), `Node(path)` and `Node<T>(path)`, `Handle(id)` and `Handle<T>(id)`, `Get(target, path)` and `Get<T>`, `Set(target, path, value)`, `Call(target, method, args...)` (private and internal members included; `Type` forms of `Get`/`Set`/`Call` for statics), `Keep(value)`, which answers a handle id for a later `{handle}` target, `Cancellation`, the `CancellationToken` the call's timeout cancels, and `Log(value)`, which adds one line to the run's log (thread-safe; `null` logs as `null`; an entry is cut at 1000 characters with `… (N more characters)`; the first 200 lines are kept and the count past them comes back as `logOmitted`).

  A run that logged answers `log` (the entries) beside `value`, `logOmitted` when lines were dropped, and `errors` when the game raised any. Usings: `System`, `System.Linq`, `System.Collections.Generic`, `System.Threading.Tasks`, `Godot` and the game's root namespace, plus `options.usings`. `options {usings, timeoutMs, keep, maxDepth}`.
- **Use:** what the `cs_*` tools cannot say in one call: building a record and passing it to a method (`t.Bind(new Update(...))`), a loop, several calls whose results feed each other, awaiting a game `Task`.

  Public and internal members of the game's assemblies (and GodotSharp's) are named directly, as code inside those assemblies would (`Node<CsTargets>("CsTargets").Hit(2)` with an `internal Hit`); a private one only through `Call`/`Get`/`Set`, and a `CS0122` compile error says so. .NET's own non-public members stay out of reach: naming one is a `CS0122` before the game is asked.
- **Edges:** a snippet that does not compile is refused with no call to the game: `run_csharp failed: the snippet does not compile:` then one `snippet(line,col): error ID: message` line per error, at most 20. A game running an older build of any of its own dlls, or of the C# helper, than the one on disk is refused, naming `restart_project` (Godot's `GodotSharp` is not checked: the game loads the engine's copy). A snippet declares no types (lambdas, local functions and anonymous types work).

  Inside the snippet `Node` names the method, so a static of Godot's type is `Godot.Node.X`. `Call` picks an overload by the arguments' runtime types with no numeric widening (`1` does not reach `Hit(float)`: pass `1f`), and refuses a generic method (use `cs_call` with `typeArgs`) and `out`/`ref` parameters. The snippet compiles against the .NET runtime the game runs (the one its target framework loads: .NET 8 or 10 on Godot 4.7), so an API newer than the game's is a compile error.

  A thrown exception fails with its type, message and a stack naming `snippet:line N`, then `Log before the failure (N lines):` (`1 line` for one) with each entry on its own line behind a two-space indent (a newline inside an entry is indented too, so a logged value never stands at column 0), then `… M more not kept` when lines were dropped, then `Godot reported:` with the game's error feed as `run_script` gives it; a stale-build or `maxDepth` refusal gets the error feed too.

  It stays an error with no partial value.

  `Log` is a member of the snippet, so a game type named `Log` is written fully qualified inside one (`global::MyGame.Log.Info(...)`), or the call fails with `CS0119`.

  Past `timeoutMs` the call fails as `cs_call`'s does and `Cancellation` is cancelled, so a snippet that passes it to its awaits or checks it in its loops stops; one that ignores it keeps running, and a loop on the main thread that never yields cannot see the cancel; what the snippet does after the timeout is not reported. `keep` and the 20000-character cut as `cs_call`'s; each run loads into its own collectible context, unloaded once its result is written.

### `get_game_state`

- **Does:** reads the game's own state in one frame: every node in the `mcp_state` group, in tree order, with what its `_mcp_state()` returns: `{frame, nodes: [{path, class, state} | {path, class, error}], total, omitted?: {count, paths}, hint?, errors?}`.

  `frame` is the engine's process frame count at the read; `total` counts every marked node (under `node` when given); `omitted` counts the nodes left out past `maxNodes` or the 40000-character budget of the list and names the first 20. A node's JSON keys arrive sorted. `node` reads only the marked nodes at or under it.

  `options {keys, maxNodes, maxDepth}`: `keys` keeps only those keys or dotted paths of each state (`seats[0].hp`), returned flat under the key as given, a missing one left out and one cut by `maxDepth` given as its depth marker; `maxNodes` 1 to 500 (50 by default), counted in tree order before any node is read; `maxDepth` 1 to 8 (4 by default), a deeper Dictionary or Array written as `"<depth limit: Dictionary>"`; past 5000 values written for one node the walk stops: the collection it is in ends with one `"<size limit>"` (a Dictionary maps its next key to it and leaves the rest out), and so does each collection around it, so shared references cannot multiply; `keep` (false by default) also holds the read in the session for `diff_snapshots` and adds `stateId` to the result.
- **Use:** in place of a screenshot or a sampling script, when a game opts in: a GDScript node joins the `mcp_state` group and defines `func _mcp_state() -> Dictionary` returning the few values a player would read off the screen; a C# node joins the group and defines `_McpState()`, an instance method with no parameters, of any accessibility, returning any value (a record, an anonymous type, a `Dictionary<string, object?>`), found on its type or a base up to the GodotSharp types and called by the C# helper in the same frame as the GDScript nodes, its value written as `cs_get` writes one (a Godot object as `{"$node": path}`, past `maxDepth` `"<depth limit: Type>"`, past 5000 values cut at one `"<size limit>"` the same way, a Godot collection counting once).

  A C# node with no `_McpState` but a Godot-visible `_mcp_state` returning a Dictionary is read through it; one with both reads `_McpState` and says so in `warning`. The helper loads only when a marked C# node is within `maxNodes`. It runs in `batch_drive` and inside `frame_control` steps. A key of a state can be waited on: `wait_for {node, state, equals}`, the same key paths as `keys`.
- **Edges:** state methods are game code and run at every read, so a side effect in one repeats.

  A node's `error` says why it has no state: `in the mcp_state group but has no _mcp_state method`, `_mcp_state raised: <the error>` (also in `errors`), `_mcp_state returned a coroutine, which is not awaited`, `_mcp_state raised an error the log no longer holds` (past the error log's 200 entries), a GDScript node with only the C# name `_McpState`; for a C# node, `<Full.Type> has no _McpState() (an instance method with no parameters, any accessibility)`, `<Exception>: <message>` when the method (or writing its value) throws (a sequence whose enumeration throws is no error: its state array carries a `"<threw ...>"` item), `<Full.Type>._McpState returns a Task<int>; a state method must return its value, not a task` (never awaited), `<Type> is a C# script; its _McpState is read by the C# helper, which cannot run: <why>` (several csproj files, an unbuilt assembly, a helper that failed to load or refused), or `…, which this project has no build of`.

  One C# node's failure never blanks the others.

  A `null` return reads as `state: null`. A state over 4000 characters comes back as `{valuePreview, valueLength}`. The bridge's own nodes are never read. A marked node an earlier node's state method freed is skipped, still counted in `total`. A whole-tree read that finds no marked node returns an empty list and a `hint` on opting in; `snapshot_subtree` and `get_ui_elements` read an unmarked game.

  A kept read is held flattened, one value per leaf of each node's state keyed by its dotted path (`seats[1].hp`; list elements `[i]`, a state that is itself a list from `[0]`), with `$` for a whole state that is a scalar, null or empty, an empty nested Dictionary or Array as one leaf `{}` or `[]`, cut marks and a preview's `valuePreview`/`valueLength` as leaves, and an errored node as the one leaf `error`; nodes in `omitted` are not held.

  A member name holding `.` or `[` reads like a path. It shares `snapshot_subtree`'s 16 held snapshots and id sequence.

### `snapshot_subtree`

- **Does:** captures a live subtree for a later `diff_snapshots`: every node under `node` (default the current scene's root), keyed by its path from that node (`.` for itself), with the properties the inspector shows and its groups (sorted, internal `_` ones left out). Returns `{snapshotId, node, nodeCount}`, not the data.
- **Use:** before an action whose effects you cannot list in advance. `options {properties, ignore, maxNodes}`: `properties` narrows each node to those names (read even when the inspector hides them; `groups` only when named), `ignore` drops names, `maxNodes` (default 2000) refuses a bigger subtree with its count.
- **Edges:** snapshots are of the running game only, held per session, the 16 most recently used, `get_game_state`'s kept reads among them; ids (`s1`, `s2`, …) never repeat in a session, and every snapshot is dropped when the run stops, exits or restarts, or when an attached game's connection ends. Inside one `batch_drive`, a later step cannot read an earlier step's `snapshotId`, since arguments are fixed before the batch runs.

### `diff_snapshots`

- **Does:** compares two held snapshots of one kind, two `snapshot_subtree` captures or two `get_game_state` reads kept with `keep`: `{added, removed, changed: [{node, property, before, after}], addedCount, removedCount, changedCount}`, each list at most 200 entries with full counts. A subtree's node paths are relative to its root; a state read's are absolute, and its properties are the leaves of each node's state (`seats[1].hp`, `$`, `error`), so one hit point changing is one `changed` entry.
- **Use:** `beforeId` from `snapshot_subtree`'s `snapshotId` or `get_game_state`'s `stateId`; without `afterId` the live game is taken again now as the before snapshot was: a subtree captured with its root and options, or a state read repeated with its `node`, `keys`, `maxNodes` and `maxDepth`, through the C# helper when the first read was.

  So `get_game_state {options: {keep: true}}`, act, `diff_snapshots {beforeId}` shows what the action changed in the game's own state. Numbers compare within 1e-6, so a float that only round-trips differently is not a change.
- **Edges:** annotated destructive, since the live re-read of a state read runs the game's state methods (game code); agents keep it in the `read` class. A subtree snapshot and a state read are refused together: `Snapshot s1 is a subtree snapshot and s2 is a state read (get_game_state); diff two of the same kind.` Elements of a list are keyed by index, so an element inserted at the head diffs as every later element changed; key a list by id in the state method where that matters.

  A property only one side has is a change whose missing side's key (`before` or `after`) is left out, not null. An id that is not held (evicted, or from a run that stopped or restarted, or an attached game that has gone) is refused with a hint to take a new one. Two ids are not checked to share a root: nodes match by relative path.

### `run_script`

- **Does:** runs GDScript inside the game; the `script` must `extends RefCounted` and define `func execute(scene_tree: SceneTree) -> Variant` (it may await). Returns `{value}` as JSON.
- **Use:** anything `inspect_node`, `set_property` and `call_method` cannot express: loops, several nodes at once, engine singletons. `timeoutMs` default 30000, load-adjusted.
- **Edges:** fails on a compile error, or when `execute` returns null and an error is located in the script itself; a null with errors elsewhere still succeeds.

  GDScript cannot reach a C# member Godot does not marshal (a `List<T>`, a plain C# class, a private member): in a C# project such a failure (Godot's `Invalid access to property or key`, `Invalid assignment of property or key` or `Invalid call. Nonexistent function`) ends with a line pointing to `cs_get`, `cs_call` and `run_csharp`, which reach it.

  A value over 20000 characters comes back as `{valuePreview, valueLength}`. Past `timeoutMs` the script is stopped and the call fails: `execute` never resumes, and `Engine.time_scale` and `SceneTree.paused` go back to their values at the call's start (the error names what it restored), so a timed-out script cannot disturb the next call. A coroutine it awaited on another object, such as a node's own method, keeps running. A script that finishes keeps its changes.

## Errors and debug output

### `get_errors`

- **Does:** the session's logged errors and warnings, oldest first: `{errors: [{seq, type, message, file, line, function, stack}], next, dropped}`.
- **Use:** to see warnings (tool results carry errors only), or what happened between calls. Pass `since` = the previous call's `next` to read only newer entries (`next` equals `since` when nothing new arrived); `limit` 1 to 500, default 50.
- **Edges:** the server keeps the last 500; `dropped` counts those lost. See also `get_debug_output` above.

## Replay with checks

### `batch_drive`

- **Does:** plays 1 to 100 `steps` against a running game in one call and stops at the first failure: `{passed, startedAt, steps: [{index, tool | assert, ok, startMs, endMs, result | error}], failedAt?}`; a met bridge wait's `result` adds `metAt` and `clockErrorMs`, while an output wait's `value` carries `at`.
- **Use:** a regression check or a long repeatable sequence.

  A step is `{tool, args}` (any runtime tool of this server but `batch_drive` and `save_screenshot`, with its own arguments) or an assertion `{assert, ...}`: `property {node, property, equals, timeoutMs?}`, `expression {expression, timeoutMs?}`, `state {node, state, equals, timeoutMs?}` (the value at a key path of the node's state, as `wait_for`'s state condition reads it).

  A `wait` takes any `wait_for` condition, `uiChanged` (`true` or `"now"`), `gameMs`, `frames`, `text` (with `match`, `under`, `exists`), `stable` (with `property`), `state` and `output` (with `stream`, `after`) included, and `wait_for`'s own default timeout, `stable` growing it as it does there; the other assertions are `no_errors {sessions?}`, `screenshot {name, tolerance?, maxChangedRatio?}` and `fired {node, signal, equals?}`.

  A tool step's `{text}` targets take `match` through its args, and a `match` refusal in a batch is prefixed `step N: `. A `match` on a property, expression or state assertion is refused (it has no text), and on a `fired` assertion with `the fired assertion takes node, signal and equals; got match.`

  Any tool or assertion step may carry `session`; the batch's `session` is the default.

  A step may instead be a parallel group `{parallel: [steps]}`: 2 to 8 tool or assertion steps (`wait`, `property`, `expression`, `state`, `screenshot`; not `fired`, `no_errors` or another group), each on its own session.

  The members start in listed order, each once the one before has written its request (a tool's setup request, like the C# helper's first ping, does not count; an output wait counts once its scan baseline is read), so a wait listed before a click is armed when the click lands. The group ends when all members have answered, and a failed member cancels those still running. Members count toward the 100 steps, and the group's time counts once toward the 300 s.
  The result's `startedAt` and every entry's `startMs` and `endMs` are drive-clock milliseconds since the server started, to 0.1 ms. A batch stamp is exact; a converted one, a met bridge wait's `result.metAt`, carries its `clockErrorMs`. A group entry is `{index, parallel: true, ok, startMs, endMs, members: [...]}`, each member an ordinary entry whose `index` is its place in the group, and `failedAt` is `{index, member, reason}`, the member the first to fail by completion order.
  A cancelled member reads `{index, tool | assert, session, ok: false, cancelled: true, sent, startMs, endMs}` with no `result` or `error`; `sent` says whether its request reached the game, and an input gesture once sent plays to its end (input calls get no cancel). A member whose outcome arrives after the cancel reads as cancelled when it failed and keeps its result when it succeeded. A cancelled `frame_control` step leaves its game paused.
  Refusals before any step runs: `step <n>: give exactly one of tool, assert and parallel.`, `step <n>: a parallel group takes parallel alone; got <fields>.`, `step <n>: a parallel group takes 2 to 8 steps; got <k>.`, `step <n>: parallel steps cannot be nested; a group holds tool and assertion steps only.`, `step <n>: parallel step <m>: <fired|no_errors> cannot run in a parallel group; put it after the group.`, a member's own refusal under `step <n>: parallel step <m>: …`, and `step <n>: parallel steps <a> and <b> both address session '<name>'; each step of a group addresses its own session.` Two members that only resolve to one session fail at batch start: `{passed: false, steps: [], failedAt: {index, reason}}`.
- **Edges:** `property`, `expression` and `state` default to `timeoutMs: 0` (checked once, now); `wait` defaults to 10000. A `wait` with `output` is answered by the server from the session's captured run output, so it works while paused and refuses an attached session; `timeoutMs: 0` on it needs `after`, as `wait_for` does.

  On a process session a `wait` with `output` works, and a tool step is refused before any step runs: `{passed: false, steps: [], failedAt: {index, reason}}` with the process refusal as `reason`.

  A `state` assertion missing `node`, `state` or `equals` is refused at validation (`step {index}: the state assertion needs node, state and equals.`), and a failed one reads `the state assertion {...} was not met when checked; wait_for returned {...}`, `last` the value at the key.

  `no_errors` covers errors since the batch started or the previous `no_errors` that checked the same session. `sessions` checks other sessions in place of the step's own: `"all"` (every session another step of the batch addresses, except process sessions) or a list of session names.

  `"all"` covers only the sessions some other step of the batch addresses (a step, a group member or a `no_errors` list), not every live session. A pass with `sessions` returns `{errors: 0, sessions: [names checked]}`, without it `{errors: 0}`; the checks and the failure blocks go in name order, for a list too.

  `sessions` on any other assertion is refused (`step <n>: the <assert> assertion does not take sessions; only no_errors does.`), and so are `sessions` on a tool step (`step <n>: a tool step does not take sessions; only no_errors does.`; a group member gets the `parallel step <m>:` prefix), `session` with `sessions` (`step <n>: no_errors takes session or sessions, not both; sessions replaces its own session.`), a `sessions` that is neither `"all"` nor a list of non-empty strings (`step <n>: sessions takes "all" or a list of session names; got <json>.`), an empty list (`step <n>: sessions is empty; name at least one session, or give "all".`), a repeated name (`step <n>: sessions names '<name>' twice.`) and a process session (the process refusal after `step <n>: `, at batch start).

  A `no_errors` that checks one session fails with `the game raised N error(s) since the batch started or its previous no_errors:` and the summary; one that checks several fails with one block per session that raised errors, `session '<name>' raised N error(s) since the batch started or its previous no_errors:` and the summary.

  A top-level or group entry also carries `errorsElsewhere: {"<session>": [errors]}` and `errorsElsewhereOmitted: {"<session>": n}`: the errors the batch's other sessions raised since the entry before it ended, shaped as a result's `errors` (at most 20 a session, then the omitted count), each error on one entry only; both are left out when there are none.

  A group's are for the sessions no member addresses and, for a member's session, the errors raised outside that member's own call (a cancelled member's session counts as not addressed); its members carry none. A failed entry carries them and a cancelled member does not. An error raised a frame after a step's reply lands on the next entry. A batch of one session never has them. They do not touch `no_errors`: each reads its own cursor.

  An error raised inside a member's call reaches the agent only through that member's `errors`, so a member whose result has no `errors` field (a screenshot assertion, a failed property assertion) does not show it; a final `no_errors {sessions: "all"}` still catches it.

  One gap remains: an error in the next step's own session that lands between one step's end and the next step's call can show on no entry (a short window, GMCP-211); a final `no_errors {sessions: "all"}` catches it. A session restarted mid-batch (`restart_project`) keeps its feed and numbering, so its cursor carries on; one relaunched under the same name is a new session the batch does not follow.

  `take_screenshot` and `compare_screenshot` steps are forced to `path_only` and images dropped. Not batchable: the lifecycle tools, `preview_scene`, `save_screenshot` (it writes a file into the project) and the headless tools. The whole batch stops after 300 s of load-adjusted time.
  A `fired` assertion checks the `fired` list of the nearest earlier tool step (assertion steps between are skipped), so it follows a `click`, `mouse_button`, `drag`, `key` or `type_text`; with no tool step before it the batch is refused at validation (`step {index}: assert 'fired' needs a tool step before it, whose fired list it checks.`).
  `node` matches an entry whose absolute node equals it or ends with `/` + it on a segment boundary: `Ready` and `Main/Ready` match `/root/Main/Ready`, `/Main/Ready` does not (no slash trimming). `signal` is the signal name.
  `equals` is a JSON array compared as JSON with the entry's whole `args` (an entry with no `args` reads as `[]`); absent or null, any args match. A non-array `equals` is refused (`equals must be an array of the emission's arguments; got {json}.`). A folded entry with `count` matches like any other, its args being the run's last.
  A pass answers `{"fired": {node, signal, args?, frame, listeners, count?}}`, the matching entry. A failure reads `the fired assertion wanted {node}.{signal}[ with args {json}] but step {k} fired: ` then up to 10 `node.signal` entries and `, … (N more)`; an entry whose node and signal match but whose args did not shows as `{node}.{signal} with args {json}`; with none it says `nothing fired on {listenedOn}` or `nothing fired and nothing was listened on`; a checked result with `firedDropped` appends `; {firedDropped} more were dropped from the fired list`.
  When the checked step's result has no fired list the assertion fails: `step {i}: the fired assertion checks step {k} ({tool}), whose result has no fired list, so there is nothing to check.`
  Fields other than `assert`, `node`, `signal`, `equals` and `session` are refused (`step {i}: the fired assertion takes node, signal and equals; got {fields}.`), `output`, `stream` and `after` included. `session` is accepted and ignored: the assertion reads the earlier step's result.

## Screenshot baselines

### `save_screenshot_baseline`

- **Does:** stores the next drawn frame as a named baseline under `.godot/godot-mcp/baselines/`: `{name, baselinePath, width, height, crop}`.
- **Use:** once the game shows the state you want to guard; `name` (1 to 64 letters, digits, `.`, `_`, `-`, starting with a letter or digit), `crop`, `options {overwrite}`.
- **Edges:** a headless game is refused as `take_screenshot` refuses it. An existing name is refused without `options.overwrite`. Crop to the region under test: a clock or particle elsewhere on screen makes every comparison fail. Pause an animating game (`frame_control`) before saving and again before comparing, or the animation counts as changed pixels; a shader driven by `TIME` keeps moving while paused (`take_screenshot`'s "A before/after pair").

### `compare_screenshot`

- **Does:** captures the next frame with the baseline's crop and counts pixels whose channel differs by more than `tolerance` (0 to 255, default 2): `{changedPixels, totalPixels, changedRatio, bbox, match, diffPath?, ...}` and a diff image (changed pixels red).
- **Use:** `name`; `options {maxChangedRatio, responseMode, previewMaxWidth}`, `maxChangedRatio` default 0.
- **Edges:** a mismatch is a result, not an error. A missing baseline fails (checked after the headless refusal, which a headless game gets first), and so does a capture of another size (a different `--resolution` or window size from when it was saved). Pause the game (`frame_control`) first for a stable frame.

## Recording

### `record_mark`

- **Does:** in a session launched with `options.record` (Movie Maker), marks the `start` or `stop` of a clip at the movie frame reached: `{mark, frame, seconds, mode: "movie"}`, and takes no `options`. In any other session with a window, `start` records the window in real time and `stop` ends it (below).
- **Use:** bracket the moment worth keeping; when the run ends (`stop_project`, the game quitting, `restart_project`) each start-stop pair is cut to its own file with ffmpeg, and a start left open clips to the end.
- **Edges:** marks alternate start, stop. A recording runs at a fixed 60 fps, so game time advances 1/60 s a frame whatever the wall clock does, and the game runs uncapped.

  `wait_for`'s `timeoutMs` and the gesture durations (`drag`'s `durationMs`, `hover`'s tooltip wait, a gamepad sweep) count clip time, 60 frames a second, and a wait's result adds `clipMs`; a recorded wait is released after 10 s + 100 ms a frame of load-adjusted time, so a long wait in a slow game can meet `batch_drive`'s 300 s deadline.

  A game started with `--write-movie` by hand and then attached keeps wall-clock durations. Capped at 10 minutes of frames; not with `--headless`. Without ffmpeg the full movie is kept and the result reports an error. If the server exits, the movie is lost.
- **Real-time recording:** `record_mark {mark: "start", options?}` on a windowed session not launched with `options.record` films its window as a player sees it, on the game's own clock (durations and `wait_for` stay real time).

  `options {fps, maxSeconds, audio}`: `fps` 1 to 60, default 30; `maxSeconds` 1 to 600, default 600; `audio` (default true) records the game's own sound, its process tree through Windows process loopback, as the clip's AAC audio track, aligned with the picture from its first frame; a lost audio packet leaves a gap of its own length instead of shifting the later sound.
  The picture is the client area, the size `run_project` reports with an odd side padded by a black pixel.
  It answers once ffmpeg has opened its encoder, about 0.1 to 0.3 s after the first frame, and the clip keeps those first frames, so it begins slightly before the answer: `{mark: "start", mode: "realtime", path, width, height, fps, audio, encoder, warning?}`, `path` the final `.mp4`, which exists only after `stop`, and `audio` whether the clip has a track. `stop` answers `{mark: "stop", mode: "realtime", clip: {path, seconds, frames, averageFps, width, height, audio}, warning?}`.
  The encoder is `h264_nvenc`, else `libx264`, else `h264_mf` (`hevc_nvenc` past 4096 px on a side), chosen once per server; one that fails as the recording starts (NVENC with no capable device, or past its session limit) falls back to the next that works, and later starts on that ffmpeg skip it for 60 s, then probe it again. The server's frame copy costs about 3% of one core at 1080p30 (measured with `h264_nvenc`).
- **Real-time edges:** one recording per session; several sessions may record at once. A recording that ended on its own (the window closed, the game quit, `maxSeconds`, a resize) is returned once, by whichever of `record_mark` `stop`, `stop_project`, `detach_project` and `restart_project` comes first, and a new `start` replaces a finished one nobody stopped.

  A `stop` that loses a race for the clip fails with `The real-time recording's clip in session '<name>' was already returned by another call (stop_project, restart_project, detach_project or another record_mark stop).`
  The session's end ends a running capture at that call, before the game is asked to quit, and reports its clip as `recording {mode: "realtime", clips: [path], startedSeconds, warning?, error?}`: `stop_project` and `detach_project` return it as `recording`, `restart_project` as `previousRecording`, and `list_sessions` shows `{mode: "realtime", path, startedSeconds}` while it runs.
  A clip that could not be finished is the `error`, and the stop, restart or detach goes on; the session is released. The helper gets 10 s of load-adjusted time to exit once the end has begun (a stop, restart, detach or a game that quit; `record_mark` `stop` and the server's shutdown keep their own waits), and one still running is killed with the `warning` `the capture helper did not exit within 10 s of the recording's end and was killed; the clip may end earlier or later than the recording did`. A stop, restart or detach during a `start` ends it at once and reports no recording.
  From the moment the session's end begins until a launch, restart or attach, `start` is refused: `Session '<name>' is ending, so a real-time recording cannot start now; start it once the game has restarted.`
  Windows.Graphics.Capture films the window's own surface, so a window covered by another or without focus records fine, but a window minimized mid-recording holds its last frame until restored, and the stop result's `warning` names each span it spent minimized on the clip's clock (a minimized one is refused at `start`), a window partly off the desktop is half stale (`start` warns, naming the window's rect and the desktop's), and a non-embedded popup is a separate OS window the clip does not hold. The yellow capture border shows on screen, not in the clip.
  Refused: a mark other than `start` or `stop`, `fps must be 1 to 60; got <x>.`, `maxSeconds must be 1 to 600; got <x>.`, `options belong to record_mark start; stop takes none.`, a session that is not running or is headless, a window that has closed (the window reader's `window <hwnd> no longer exists.`), one already recording (naming its age), or a `stop` with none running.
  Also refused at `start`: a quiet session (a run or an attach alike: its window is on the hidden desktop or parked, which the capture cannot see; launch it with `options.quiet: false`, and `options.mute: true` to keep it silent), no ffmpeg (`FFMPEG_PATH` naming no file included), no capture helper, an embedded window, a minimized window, the helper's probe refusing the window, the recordings folder not creatable, ffmpeg that cannot be run to test its encoders, a client area that differs from the size the helper measured (`record_mark start` again).
  Also refused at `start`: no frame or an encoder that does not open within 10 s of load-adjusted time (the helper and ffmpeg are stopped, and the refusal quotes both logs).
  And no working encoder (`ffmpeg at <path> has no working encoder for a <W>x<H> clip (tried …); see <log>`, naming the logs the cached verdicts came from).
  A start that does not go ahead deletes its `.deadline` and `.mkv` and keeps its logs.
  The no-track rule: a clip whose game renders no sound has no audio track, never a silent one, and says why in the start's `warning` and the clip's. `options.audio: false` gives no track and no warning.
  Process loopback ignores the system volume and the system mute: a tone's clip peak was -31.15, -31.09 and -31.23 dBFS with the system volume at 0.08, at 0.50 and at 0.50 muted, so a muted machine still records the game's sound. Only the game's own mute (`options.mute`) or a quiet run silences the track.
  The video needs Windows 10 build 19041 (2004) or later and the audio build 20348 or later (Windows 11, Server 2022): on an older build `start` goes ahead with video only and the `no audio:` warning quotes the helper's `process-loopback audio needs Windows build 20348 or later`.

  The three warnings lead the others (joined with `; `): `no audio: session '<name>' renders no sound (options.mute or a quiet run), so the clip has no audio track` (a muted session, `options.mute` or an attach's mute); `no audio: the game's bridge sent no process id, so the clip has no audio track`; and `no audio: {the helper's last line without its "godot-mcp-capture: " prefix and trailing periods}, so the clip has no audio track` when process-loopback audio cannot start, which never refuses the start: the recording goes on with video only.
  A game that mutes itself after the start gives `audio: true` and a silent track: that is not detected. A late audio failure at the start retries it once without audio.

  A helper that ends with exit 5 during the recording because nothing read the audio pipe in full fails `stop` like any failed helper: `The capture helper <how it ended> during the recording: the game's audio did not reach ffmpeg in full: <last lines of its log>. The frames recorded are kept in <the .mkv>; see <capture log> and <ffmpeg log>.`
  Warnings: `the capture got <a> fps of <fps>: the machine was busy, so the clip repeats frames` (an average under 90% of `fps`) and `the window changed size at <t> s; the clip ends there`. A failed helper, ffmpeg or remux fails `stop` instead, naming its log and keeping the `.mkv`; the error text is the one `stop_project` and `list_sessions` show for it, and a later `stop` gets `No real-time recording is running in session '<name>'; record_mark start first.` The server's log gets one Warning line (`RealtimeFinishFailed`) for the failed finish. A `stop` that cannot write the deadline file kills the helper and returns the clip with a `warning` that it ends there.
  Files are in `<project>/.godot/godot-mcp/recordings/`: `<UTC yyyyMMdd-HHmmss-fff>-<session>-realtime.mkv` while recording (playable if the server dies), remuxed to `.mp4` with faststart and the `.mkv` deleted, and, per recording, the logs `<same stamp>-<session>-realtime-capture.log` and `…-realtime-ffmpeg.log` (errors and the remux's output), plus `…-realtime-probe-<guid>.log` when a probe ran.
  Ending the server ends every real-time recording and waits for its `.mp4` within the 10 s shutdown cap.

## Headless scene editing

These run a headless Godot on the project's files with no game started, beside any live session on the folder. The write tools always run the prep, open the scene as the editor does and save it with its uids kept; each edit is all or nothing. The scene edit tools write `.tscn` only; a binary `.scn` can be read but not edited. A node inside an instanced scene is refused (edit that scene's own file), except where a tool below says an editable instance or an instance's root is allowed.

On a GDScript-only project the headless Godot stays up between calls (a warm host, one per folder and server), so consecutive calls skip Godot's start: its first call on a folder costs a start, the next ones a few milliseconds. Any change to the folder's files the host did not make (your own edit, the editor's, another server's), an import, a crash or a ceiling restarts it on the next call, which then costs a start again; nothing is retried.

While it lives it holds the folder as its working directory, so renaming or deleting the folder (removing a worktree) is refused until it stops: run `stop_project {projectPath}` on the folder first, which waits for a headless call running on it.

A host stops by itself after 5 minutes (wall time) with no call; at most 4 run per server, a fifth folder stopping the one idle longest (a busy one is never stopped); it runs at 30 fps while idle and at the engine's headless floor (about 145 iterations a second) for a second after each call; and a call after which a `res://` resource it named stays cached restarts it.

`list_sessions`' `headlessHosts` lists them. A C# project starts a fresh Godot for every call. The host's log is `.godot/godot-mcp/headless-host-<server pid>.log`; a C# project's is `.godot/godot-mcp/headless.log`.

### `describe_class`

- **Does:** lists a class's properties, methods with typed arguments, signals, constants and enums: `{className, inherits, inheritsChain, canInstantiate, isScript, scriptPath?, language?, properties: [{name, type, default?}], methods: [{name, args: [{name, type, default?}], returnType, isVirtual, isStatic}], signals: [{name, args: [{name, type}]}], constants, enums, methodCount, offset, limit, warning?}`.
- **Use:** before `call_method`, `set_property` or a headless edit on a class you do not know, engine (`Button`) or the project's own `class_name`. `projectPath`, `className`, `options {inherited, offset, limit}`: an engine class lists its own members unless `inherited`; methods are sorted by name and paged (`limit` 1 to 500, default 100, `methodCount` the total). When a game runs on the project (or `session` names one) that game answers; otherwise it is read headless.
- **Edges:** an unknown name is refused with up to 5 close names (`Did you mean: …`). Properties the editor neither shows nor stores (`Node.name`, `Node2D.global_position`) are listed without `default`, since Godot keeps none for them. A script class's lists include its base scripts' members; `inherited` adds the native base's.

  A C# script class whose lists come back empty carries a `warning` that the build may be red or stale. Engine enum members appear under `enums`, not `constants`. The editor binary's class list includes editor-only classes, so a suggestion may name one.

### `validate`

- **Does:** loads scripts, scenes and resources without running the game and reports errors: `{valid, checked, results, engineErrors?, csharp?, prep}`; `results` lists only files with errors.
- **Use:** after edits, before a launch. `targets` (1 to 50 files), or none for every git-versioned `.gd`, `.tscn` and `.tres` (at most 500); `options {prepare}`.
- **Edges:** Godot's parser reports only the first parse error in a file: fix it and validate again. A failed C# build comes back as `csharp {build: "failed", errors}` and makes `valid` false while GDScript is still checked. Autoloads' `_init` still runs. Every C# script a target reaches, through instanced scenes and resources however deep, is checked for its class.
- **C# targets:** a `.cs` target is checked through the build and through the versioned `.tscn`/`.tres` files whose `ext_resource` names it (at most 20, checked as targets): `csharp.files` gives each `.cs` target's `{path, errors, warnings, from, scenes}`, each diagnostic `{file, line, column, code, message, severity}`, and `csharp.otherErrors` the errors in the assembly's other files (each list at most 20, the rest counted).

  Errors make `valid` false; warnings never do. With nothing stale no build runs, so the diagnostics come from the last prep build (`from: "last build at <UTC>"`); a project with no saved build is rebuilt once (`--no-incremental`). A `.cs` target is refused in a project with no `.csproj` or several, and when it is outside the csproj's `Compile` items.

### `get_scene_file_tree`

- **Does:** lists a scene file's nodes without running any code, instanced scenes expanded and an inherited scene's base merged: path (from the root, `.`), name, type, script, instance, groups, childCount.
- **Use:** before a scene edit, to learn node paths. `scenePath`, `root`, `options {maxDepth, offset, limit, prepare}`.

### `create_scene`

- **Does:** writes a new `.tscn` holding one root node, with a new uid: `{scenePath, root, uid}`.
- **Use:** `scenePath`, `rootType` (default `Node2D`; a Node class or a script's `class_name`), `rootName` (default the file name in PascalCase), `options {overwrite}`.
- **Edges:** an existing file is refused without `overwrite`; a replaced file keeps its uid.

### `save_scene`

- **Does:** opens a scene and saves it again, in place or to `newPath` (save-as): `{scenePath, savedTo, uid}`. In place it writes Godot's full 4.7 form; a save-as keeps the source's text (its `ext_resource` ids, its tag lines) with the new file's own header.
- **Use:** copying a scene to a new file; `options {overwrite}` for an existing `newPath`.
- **Edges:** a new file gets a new uid; saved over a file, the copy takes that file's uid.

### `add_node`

- **Does:** adds a node named `nodeName` of `nodeType` (a Node class, a script's `class_name`, a script path, or a scene path, added as an instance) and saves: `{path, type, index, instance?, script?}`.
- **Use:** `options {parent, properties, position}`: parent path from the root (default `.`), `{name: value}` set once the node is under its parent, as the editor sets them (so a Control's `layout_mode` takes 0 or 1 under a Control, is 2 under a Container and 3 with no Control parent; `anchors_preset` and a non-zero `anchor_*` need `layout_mode` 1, which is applied first), and a position among its new siblings as `move_node` takes it (default last; in 2D, sibling order is draw order).

  A script path (`res://Foo.cs`, `res://foo.gd`, no `class_name` or `[GlobalClass]` needed) makes a node of the script's base class with the script attached; `type` is that base class and `script` the path.
- **Edges:** a sibling name clash, a parent inside an instanced scene (an instance's own root may be the parent), and a scene instancing itself are refused; a property that does not take adds nothing. A script that does not compile or does not extend a Node class is refused, and a `.cs` script while the project's C# build fails, as `attach_script` refuses it.

### `delete_nodes`

- **Does:** deletes 1 to 100 `nodePaths`, with their children, and saves: `{deleted}`.
- **Edges:** the root, a node inside an instanced scene and a node an inherited scene gets from its base are refused; deleting an instance's root removes the instance. One refusal saves nothing.

### `duplicate_node`

- **Does:** copies `nodePath` and its subtree right after it, or last under `options.parent`, and saves: `{originalPath, newPath}`. The copy and every node it owns get fresh `unique_id`s, as the editor's Duplicate gives them; every other node keeps its own.
- **Use:** `newName`, else the node's name or the editor's serial name (`Sprite` gives `Sprite2`).
- **Edges:** instances stay instances; connections within the copy and out of it are kept, connections into it are not. The root is refused.

### `move_node`

- **Does:** moves `nodePath` among its siblings, or under another parent, and saves: `{path, previousPath, index, rewrittenPaths}`. In 2D, sibling order is draw order: later siblings draw on top.
- **Use:** `options {parent, position, keepGlobalTransform}`, at least one of `parent` and `position`. A position is exactly one of `{index}`, `{before: "<sibling name>"}` or `{after: "<sibling name>"}`; a negative `index` counts from the end (-1 is last). With `parent` and no position the node goes last. A reparented node keeps its global transform unless `keepGlobalTransform` is false, and its subtree stays the scene's own.
- **Edges:** refused, with the file unchanged: the root, a node inside an instance or from a base scene (the file cannot record their move), a parent that is the node or its own child, a name clash under the new parent, an `index` out of range or a sibling name that is missing or is the node itself. `Node`-typed exports and signal connections follow the node, since the save recomputes them.

  A stored `NodePath` property (an Array's or Dictionary's `NodePath`s included) of the scene's nodes, or of an instance's own nodes (editable children), that points into or out of the moved subtree is rewritten to resolve to the same node, as the editor's reparent rewrites it; a path that still resolves is left alone. Animation track paths and `NodePath`s inside resources are not rewritten.

  `rewrittenPaths` lists each rewrite as `{node, property, from, to}` (`node` is the holder's path after the move, `from` and `to` the `NodePath` text, subnames kept), `[]` when none; a value held by an instance's node is saved as an override of that node, and a node inside a non-editable instance may be listed though its write is not saved. An `AnimationPlayer`'s default `..` `root_node` under the moved node is rewritten like any other and becomes a stored value.

  In the file the moved node and every node under it keep their original property lines in their order; only the tag line's `parent=`, any rewritten `NodePath` line and any line whose value the move changed (a `keepGlobalTransform` position) differ: a changed line takes the saved line in place, a line only the save has goes right after the latest saved line before it (or right after the tag line), so a new engine property lands before `script =` and a new script member after it, and one only the original has is dropped.

  The node's `[connection]` `from=`/`to=` and `[editable path=]` change in place, so the section keeps its position. A node added in the same batch at a path the moved node left is written as Godot writes a new node, with no warning.

  A node the same edit created (`add_node`, `duplicate_node`, and every node under it) and then moved is saved as a new section in Godot's form. A node moved onto the path of a node deleted in the same batch keeps its own lines; the deleted node's sections, its children's, its connections and its `editable` entries are dropped, with no full-save fallback.

### `attach_script`

- **Does:** sets `scriptPath` (`.gd` or `.cs`) on `nodePath`, replacing its script, and saves: `{path, script, previous?, kept?, dropped?, uidFilesWritten?}`. The values the previous script stored carry over as the editor carries them: a value is kept when the new script declares its name and the value's type matches (or an object fits the new export's class); `kept` and `dropped` name them.
- **Edges:** the script must compile and extend the node's class. A C# script is refused while the C# build fails. An instance's root and a node from a base scene are allowed, saved as overrides.

### `load_sprite`

- **Does:** sets the `texture` of `nodePath` (Sprite2D, Sprite3D, TextureRect, NinePatchRect, Polygon2D, or any node with a Texture2D `texture`) to `texturePath` and saves: `{path, texture}`.
- **Edges:** an image never imported is imported first by the editor's full scan, which writes `.import` and `.uid` files across the project: expect those in the diff. An image in a folder Godot does not scan (a name starting with `.`, or holding `.gdignore` or a nested project's own `project.godot`) is refused.

### `set_node_properties`

- **Does:** sets 1 to 100 `updates` `{nodePath, property, value}`, reads each back, and saves: `{results: [{nodePath, property, before, after}]}`.
- **Use:** values as `set_property` takes them, typed arrays and dictionaries included; a resource as a `res://` or `uid://` path, `{resource: path}`, `{type, ...properties}` or null; a Node-typed export (GDScript or C#, a `[GlobalClass]` type included) as a path from the scene's root (`"Card"`, `"Board/Card"`, not the `../Card` the saved file holds).
- **Refusals:** a Node-typed export given a non-string or absolute path, a path with no node, or a node of the wrong class is refused with the reason (`no node at "Card" from the scene root`, `"Card" is a Label; the export takes Button`; a node whose script has a class name is named by it, `"Card" is a CardView (Control); …`, and a hint listing several classes reads `takes Node2D or Control`).
- **Edges:** a node from a base scene, or inside an editable instance, may be set (saved as an override); inside another instance it is refused. A non-zero `anchor_*` on a Control in Position layout (`layout_mode` 0) is refused unless the node already holds that value: set `layout_mode` 1 first (a full rect is `layout_mode` 1, `anchors_preset` 15, `grow_*` 2); the editor cannot make a Position node with anchors.

  Under a Container, `anchor_*`, `offset_*`, `grow_*` and `anchors_preset` are refused unless unchanged, since Godot saves none of them there: set `size_flags_*` instead. All or nothing.

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
- **Edges:** refused, saving nothing: a missing node, signal or method; an emitting node inside a non-editable instance; arguments plus binds that do not fit the method; a bind that does not convert (an array of Objects says why; a Node-typed parameter takes a path from the scene root and is refused for the same three reasons as `set_node_properties`); a signal already connected to that method.

### `disconnect_signal`

- **Does:** removes the connection from `signal` on `nodePath` to `target {nodePath, method}`, whatever its binds.
- **Edges:** an inherited connection is refused, naming the scene whose file makes it.

### `export_mesh_library`

- **Does:** builds a GridMap MeshLibrary from a 3D scene by the editor's Import from Scene rule and writes it to `outputPath` (`.tres` or `.res`); the scene is only read: `{outputPath, items: [{id, name, shapes, navigation}], replaced?}`.
- **Use:** `meshItemNames` to keep only some items; `options {overwrite}`.
- **Edges:** every MeshInstance3D with a mesh below the root is an item named after its node; two nodes of one name make one item (`replaced`). No previews are made. A file the scene uses is refused as `outputPath`, whether directly, through an instanced scene or a resource file, or by a node an earlier `batch_scene_operations` step changed; the refusal names the file or node (`through res://cell.tscn`, `through node Grid`).

  A replaced `.res` keeps its uid when the project has an import cache (`.godot/uid_cache.bin`) and gets a new one in a never-imported project (a `.tres` keeps its own).

### `batch_scene_operations`

- **Does:** runs 1 to 100 scene edits on one scene in a single headless Godot and saves once: `{passed, steps, failedAt?, uid?}`.
- **Use:** any change of more than one edit: each step is `{tool, args}`, `tool` one of `delete_nodes`, `attach_script`, `duplicate_node`, `move_node`, `load_sprite`, `add_node`, `set_node_properties`, `connect_signal`, `disconnect_signal`, `export_mesh_library`, `args` that tool's own arguments without `projectPath` and `scenePath`. Later steps see earlier ones (add a node, then connect its signal).
- **Shapes:** `args` by tool (`?` marks an optional key): `delete_nodes {nodePaths: [path]}`; `attach_script {nodePath, scriptPath}`; `duplicate_node {nodePath, newName?, options?: {parent?}}`; `move_node {nodePath, options: {parent?, position?: {index}|{before}|{after}, keepGlobalTransform?} (parent, position or both)}`; `load_sprite {nodePath, texturePath}`; `add_node {nodeType, nodeName, options?: {parent?, properties?: {name: value}, position?: {index}|{before}|{after}}}`; `set_node_properties {updates: [{nodePath, property, value}]}`; `connect_signal {nodePath, signal, target: {nodePath, method, binds?: [value]}}`; `disconnect_signal {nodePath, signal, target: {nodePath, method}}`; `export_mesh_library {outputPath, meshItemNames?: [name], options?: {overwrite?}}`.

  A step with an unknown or missing top-level argument is refused with the tool's shape appended (`set_node_properties has no argument 'nodePath'; it takes: updates. Its shape: {updates: [{nodePath, property, value}]}.`).
- **Edges:** every step is checked before Godot starts. At the first failed step nothing is saved and no library written. One Godot start for the lot, so it is much faster than the tools one by one.

## Project profiles: `godot-mcp.json`

A `godot-mcp.json` beside `project.godot` sets launch defaults for every `run_project` on the folder. Top-level keys: `scene`, `userArgs`, `engineArgs`, `resolution` (`WIDTHxHEIGHT`, lowercase `x`), `quiet`, `presets`, `prepWrapper`, `processes`.

Each preset under `presets` takes the same keys but `presets`, plus `session` and `description` (what the preset is for; an unknown `options.preset` is refused listing each preset as `name (description)`). The file may carry `//` and `/* */` comments and trailing commas, so a value can sit beside a note on why it is set.

`run_project`'s `options.preset` layers a preset over the top level, and the call's own arguments override both: `scene`, `resolution` and `quiet` are replaced by the later layer, `userArgs` and `engineArgs` append. The session name is `options.session`, else the preset's `session`, else the folder's.

Parsing is strict: an unknown key, a wrong type or a bad resolution refuses the launch with the file's path and the fix. `restart_project` reuses the merged launch and does not re-read the file. The profile is not a tool and applies to `run_project` only, except `prepWrapper`.

`prepWrapper` (an array of strings: a program, then its arguments) runs the prep's processes, the C# build, its Compile-items listing, the import and a process's `prep`, through a project's own wrapper such as a gate script, for every tool that preps: launch, restart, preview and the headless tools.

Each runs as the wrapper followed by the process's absolute path and arguments, from the project folder; `{log}` in any element becomes the step's log (`.godot/godot-mcp/build.log`, `import.log`) and `{ceiling}` the prep's ceiling in seconds (300).

A program with a folder in it is relative to the project folder, a bare name is found on `PATH`. When the wrapper takes `{log}`, it writes that log and its own output goes to `build.wrapper.log` / `import.wrapper.log` beside it, and an import's second pass writes `import.2.log`.

`{log}` may hold anything the wrapper writes, such as every line prefixed, because the server reads MSBuild's own files first.

The C# build's diagnostics come from MSBuild's quiet file logger (`.godot/godot-mcp/build.diagnostics.log`, from `-fl` and `/flp:LogFile=…;Verbosity=quiet`) and the Compile-items listing from MSBuild's result file (`compile-items-<guid>.json`, from `/getItem:Compile` and `/getResultOutputFile:…`), so `validate`'s C# errors and warnings name their source file under a prefixing wrapper.

The switches are on the command line in MSBuild's slash form: a PowerShell wrapper (`pwsh -File`) keeps `/name:value` whole where it splits `-name:value` at the colon, and a gate that routes the build to another machine sees and rewrites the path after the colon.

When MSBuild's file is missing (a routed build's diagnostics file stays on the builder) or a failed build's file logger holds no error (MSBuild's early errors such as MSB1009 reach only the console log), the server reads the captured `build.log` (for the listing, the listing's log) instead, and under a prefixing wrapper those lines keep its prefix.

`build.log` and `build.diagnostics.log` are deleted before each build, so a build whose command never ran (a wrapper exiting 124) leaves no earlier build's log or diagnostics. A listing's `.log` and `.json` are deleted once it succeeds and kept when it fails.

The import is read differently: its `ERROR:` check reads the wrapped import output, so an import error a prefixing wrapper rewrites is not detected.

A wrapped process has no stall kill (a gate waiting for a slot is silent), the ceiling still holds, and a wrapper exit of 124 refuses the prep with `the prep wrapper stopped the build (exit 124); its log: …`. `prep.note` says `ran through prepWrapper (<program>)`. A malformed `godot-mcp.json` refuses any prep that runs a process.

A process's `prep` (under `processes.<name>`, a non-empty array of non-empty strings: a program, then its arguments, shaped like `command`) is wrapped the same way, but its log is `.godot/godot-mcp/process-<session>.prep.log` (the wrapper's own output in `process-<session>.prep.wrapper.log` when it owns `{log}`), and a bare program is left to the wrapper's `PATH`.

It runs in the process's `cwd`, wrapped or not (so the wrapper starts there too, and a relative path in the `prepWrapper` command must also resolve from that `cwd`; an absolute one always does), with the process's `env`, so a build reads the same variables (an MSBuild `Configuration`, a `CARGO_TARGET_DIR`) as the process it builds for. A prep that runs past its ceiling is stopped with its whole process tree and refuses the start, naming its log.

```json
{ "prepWrapper": ["gate", "run", "--log", "{log}", "--timeout", "{ceiling}", "--slot", "heavy", "--"] }
```

```jsonc
{
  "resolution": "1280x720",
  "presets": {
    // Two Godot runs of the same game on one folder, each a session of its own;
    // both must take the same quiet and shutOutRealGamepads.
    "host": { "userArgs": ["--host"], "session": "host", "quiet": false },
    "guest": { "userArgs": ["--join=127.0.0.1"], "session": "guest", "quiet": false },
  }
}
```

### `processes`

A `processes` object declares the programs `run_process` starts, each keyed by the name a call passes as `process`:

```jsonc
"processes": {
  "server": {
    "command": ["dotnet", "run", "--project", "../Delve.Server", "--", "--dev-cheats"],
    "cwd": ".",
    "env": {"Delve__DataDir": "../../.tmp/drive-runs"},
    "ready": {"output": "Now listening on", "timeoutMs": 120000},
    "description": "the game server; pass --port <n> as args"
  }
}
```

`command` (required) is a non-empty array of non-empty strings, the program and then its arguments: a program with a folder in it is relative to the project folder, a bare name is found on `PATH`, and `run_process`'s `args` append after them.

`cwd` is the folder the program runs in, relative to the project folder (an absolute path or one that leaves it is refused); left out, the project folder. `env` sets variables over the server's environment, with no way to remove one. `description` says what the process is, shown when a call names one the file does not declare.

`ready {output, timeoutMs}` is the line `run_process` waits for before returning: `output` must compile as a .NET regular expression matched against stdout and then stderr, and `timeoutMs` is 1 to 600000 of load-adjusted time, 60000 when left out.

A call's `options.ready` replaces the entry whole, and `restart_project` waits on it again; to wait for a later line, use `wait_for`'s `output` condition.

The section is parsed strictly with the rest of the file: an unknown key, a wrong type, an empty `command`, a bad `ready.output`, a duplicate process name and a `cwd` that is absolute or leaves the project folder are each refused with the file's path and the fix. A process is not a preset and not a `run_project`: presets still launch Godot only.

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

1. `list_sessions`: a live session on the folder can keep running; it sees the edits only after `restart_project`.
2. `create_scene {projectPath, scenePath: "res://ui/hud.tscn", rootType: "Control"}`.
3. `batch_scene_operations {projectPath, scenePath: "res://ui/hud.tscn", steps: [{tool: "add_node", args: {nodeType: "Label", nodeName: "Score", options: {properties: {text: "0", position: {x: 16, y: 16}}}}}, {tool: "attach_script", args: {nodePath: ".", scriptPath: "res://ui/hud.gd"}}]}`: `passed: true` and a `uid`; on failure `failedAt` names the step and nothing was saved.
4. `validate {projectPath, targets: ["res://ui/hud.tscn", "res://ui/hud.gd"]}`: `valid: true`, or the first error per file.
5. `get_scene_file_tree {projectPath, scenePath: "res://ui/hud.tscn"}`: confirm the node paths.
6. `preview_scene {projectPath, scene: "res://ui/hud.tscn", options: {resolution: "1280x720"}}`: look at the image; `errors` holds anything `_ready` raised.

### Drive a gamepad menu

1. `run_project {projectPath, options: {shutOutRealGamepads: true}}`: so a pad on the desk cannot move the focus under you.
2. `get_ui_elements {filter: "Button"}`: learn the menu's buttons.
3. `gamepad_button {button: "DPAD_DOWN"}`, or `gamepad_stick {stick: "left", position: {x: 0, y: 1}, options: {release: true}}`: without `release` a second push does not move focus again.
4. `wait_for {condition: {expression: "root.gui_get_focus_owner().name == \"Options\""}, timeoutMs: 1000}`: `met: true` confirms where focus went.
5. `gamepad_button {button: "A"}`, then `wait_for` on the screen it opens.

### Record a clip around a bug

1. `run_project {projectPath, options: {record: true, dropIdle: true}}`: `recording.path` is the movie in progress. `dropIdle` keeps the clip to the action: without it the clip also holds every idle frame drawn while you were between tool calls. Leave it out when a still moment or the sound matters.
2. Drive to just before the bug. `record_mark {mark: "start"}`: note `frame`.
3. Trigger the bug with the input tools. The run is at a fixed 60 fps, so millisecond durations no longer match game time: wait on conditions with `wait_for`, not on durations.
4. `record_mark {mark: "stop"}`.
5. `stop_project`: `recording.clips` lists one `.mp4` for the pair, and the full movie is gone; `recording.error` means ffmpeg was missing or failed and `recording.path` is kept instead.

### Record a clip as a player sees it

1. `run_project {projectPath, options: {quiet: false}}` (add `mute: true` to keep it silent), or attach to a game that is not quiet. Leave out `record`: the game keeps its own clock.
2. `record_mark {mark: "start"}`: it answers once the first frame is in, with `path`, the `.mp4` the clip becomes, and the `width` and `height` of the window.
3. Drive with the input tools; durations and `wait_for` are real time, so waits count as they play.
4. `record_mark {mark: "stop"}`: `clip` has `path`, `seconds`, `frames`, `averageFps` and `audio` (whether it has the game's sound); read `warning` for a busy machine, a resize, a minimized span or a missing audio track. Keep the window unminimized meanwhile.

### A `batch_drive` regression check against a baseline

1. Once, in a known-good build: drive to the state, `frame_control {action: "pause"}`, `save_screenshot_baseline {name: "hud-idle", crop: {x: 0, y: 0, width: 400, height: 80}}`.
2. After a change: `run_project`, then one call:

   `batch_drive {steps: [{tool: "click", args: {target: {element: "Main/Menu/Play"}}}, {assert: "wait", node: "Hud", exists: true}, {tool: "frame_control", args: {action: "pause"}}, {assert: "property", node: "Hud/Score", property: "text", equals: "0"}, {assert: "screenshot", name: "hud-idle", maxChangedRatio: 0.001}, {assert: "no_errors"}]}`

3. Read `passed`. On failure `failedAt {index, reason}` names the step; for a screenshot failure the step's error carries the comparison, and `compare_screenshot {name: "hud-idle"}` outside the batch returns the diff image to look at.

### Drive a host and a headless client

1. Pick a loopback port both runs take, and give the game an argument that names it.
2. `run_project {projectPath, scene: "res://Lobby.tscn", userArgs: ["--host=<port>"], options: {session: "host"}}`: the host is a windowed game, so it can be screenshotted.
3. `run_project {projectPath, scene: "res://Lobby.tscn", engineArgs: ["--headless", "--resolution", "<the project's viewport size>"], userArgs: ["--join=<port>"], options: {session: "guest"}}`: the guest is a run like any other, driven and read by every runtime tool. `--resolution` is the project's viewport size because a headless root window is 64x64; without it the guest lays its Controls out for a 64x64 screen. Both sessions on the folder must take the same `quiet` and `shutOutRealGamepads`.
4. Wait for the join before driving: `wait_for {condition: {output: "<the line the guest prints when it joins>", stream: "stdout"}, session: "guest"}` answers once the guest prints that line, from the run's captured output; an attached session has none and is refused. Then `wait_for {condition: {node: "<the host's node>", property: "<its connected flag>", equals: true}, session: "host"}` for the host's side.
5. One `batch_drive` acts in one and checks the other: `batch_drive {steps: [{tool: "click", args: {target: {element: "Main/Lobby/Start"}}, session: "host"}, {assert: "wait", node: "Main/Hud/Status", property: "text", equals: "started", session: "guest"}]}`. `passed: true` means the host's click reached the guest's label.

   For an effect that lasts one frame, arm the wait first in a parallel group: `batch_drive {steps: [{parallel: [{assert: "wait", node: "Main/Hud/Status", property: "text", equals: "started", session: "guest"}, {tool: "click", args: {target: {element: "Main/Lobby/Start"}}, session: "host"}]}]}`. The wait has written its request before the click starts, and the group ends when both have answered; order the host's click and the guest's met wait by `metAt - startMs`, two numbers of one result.

   A wait for any UI change takes the same place: `{assert: "wait", uiChanged: "now", session: "guest"}` in the group snapshots the guest's UI as it starts, which no gesture in that session would, and is met when the host's click changes it; a plain `uiChanged: true` there is refused with no baseline.
6. `list_sessions` lists both names, and `stop_project {session: …}` stops each; the folder's `override.cfg` goes with the last one.

Wait on state that persists: a step after the other session's input misses a one-frame effect (measured 30 of 30 in the spike). A guest that must be screenshotted is a quiet windowed run instead, since a headless one has no picture and no `window` in its result.
