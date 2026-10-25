# Changelog

What changed in each version of the godot-mcp server, newest first. The version is `VersionPrefix` in `Directory.Build.props`, and the build stamps the commit after a `+` (`0.3.1+<sha>`). Builds before 0.1.0 carry no version.

## Unreleased

### Added

- `PlayStep` called through `call_method` or an `options.call` takes a scratch step's name, as `run_scratches` reports it, in place of its index.
- `stop_project` with a `projectPath` and no session clears a dead server's leftover `override.cfg` and `armed.json`, reporting what it removed and why any file stayed.
- `run_scratches` takes `engineArgs` in its options and the profile's `scratch` section, placed on each scene's command line before `--`.
- `scratch_status` and `run_scratches` take `only: "red"` to list only each scene's red steps, keeping every scene's verdict line.

### Fixed

- A released `call_method`, `run_script`, `wait_for` or watch whose server stalled now returns the game's answer to its cancel, not a hang report.
- `cs_set` reads an enum name sent wrapped in JSON quotes as the name, and `cs_set` and `set_property` say a string value is given as itself.

## 0.21.2 - 2026-10-25

### Fixed

- `run_scratches` waits the last step's pace in wall time before quitting a faster-scaled game, so its exit leak check matches a 1x run's.
- `validate` reads `.cs` diagnostics through a `prepWrapper` that rewrites or prefixes the command's output, or is a PowerShell script.
- `stop_project` and scratch runs give a game that closed its bridge after the quit 10 s to finish its teardown before killing it.

## 0.21.1 - 2026-10-24

### Changed

- `save_screenshot` is in the `drive` class beside `save_screenshot_baseline`, so agents that drive the game get it without the scene-editing tools.

## 0.21.0 - 2026-10-21

### Added

- `run_scratches` takes `options.timeScale` to run the scenes' games faster or slower, dividing the kill bound; a scene's pace entry can opt out.

### Fixed

- `stop_project` waits for a killed game on load-adjusted time and warns when it still runs, instead of returning while it holds the project folder.
- A stop's `warning` keeps its kill or unkillable text when a debugger was attached, instead of showing only the debugger's.
- `get_ui_elements` reports a Control's rect in root-viewport coordinates inside SubViewports, windows and canvas layers, centred where a click on it lands.
- A `wait_for` with `screenshot` no longer keeps its state alive in the game after it ends.
- A real-time recording no longer fails to start, or kills its capture helper at a stop, when the helper is reading its deadline file at that moment.
- `run_scratches` no longer marks a step red with a marker timeout when a busy machine delays reading the step's end lines.
- A background `run_scratches` run's status file no longer misses an update while another process is reading it.
- Servers starting together on a busy machine no longer write a project's `override.cfg` without holding the shared folder list.

## 0.20.0 - 2026-10-20

### Added

- `run_project` takes `options.repickHover: false` to stop re-picking the hover every frame, so a drive sees whether the game's own re-pick moves it.

### Fixed

- A scene edit whose splice would give two nodes one `unique_id` now falls back to the full save with a warning.
- `get_game_state` stops at the 5000-value budget, ending a cut collection with one `"<size limit>"`, and a C# Godot collection counts once.
- Mouse moves, drags and hovers now carry a velocity, so a game reading `event.velocity` sees the pointer move.
- `capture_frames` encodes its PNGs on worker threads, so capturing no longer slows the game's clock; files carry the frame number.

## 0.19.0 - 2026-10-20

### Added

- `run_scratches` takes `options.background`: it returns a run id at once and plays on, writing a status file and a log ending in a completion line.
- `scratch_status` reads a background scratch run's progress and result, or stops it.
- `batch_drive` entries carry `errorsElsewhere`, the errors the batch's other sessions raised meanwhile, and `no_errors` takes `sessions: "all"` or a list.
- `wait_for` results carry `metAt` and `clockErrorMs` on the drive clock, re-pinging a loose clock first; an output wait reports when the server read its line.
- `watch` points, events and voices end with `at`, their time on the drive clock, with one `clockErrorMs`, so two games' watches line up.

### Changed

- A tool call that names no session never picks a session a background scratch run is playing.

## 0.18.0 - 2026-10-19

### Added

- `wait_for` and `batch_drive` waits take `output`: a regex matched against a run's printed lines, with `stream` and `after`, working while paused.
- `run_process` starts a process a project's profile declares, a game server or mock backend, and `stop_project` ends it and everything it started.
- `run_process` takes `options.ready`: it returns once the process prints a matching line, and refuses a start whose process exits first or misses its timeout.
- `restart_project` restarts a process session, stopping the program and starting its stored command again, waiting on its ready line.
- `batch_drive` takes parallel groups: up to 8 steps on different sessions at once, each sent before the next starts, a failure cancelling the rest.
- `batch_drive` results carry `startedAt`, and every step's `startMs` and `endMs`, on one clock across sessions.
- `wait_for {uiChanged: "now"}` takes its UI snapshot when the wait starts, so a parallel group's wait sees another session's input change it.
- `run_scratches` ends a step once the condition its scene's `GetStepUntil(i)` gives holds, within the step's pace of game time.
- `wait_for` signal waits take `options.call`, connecting before the call, so a signal the call emits at once is caught.

## 0.17.1 - 2026-10-17

### Fixed

- The installed godot-mcp skill carries its own `TOOLS.md`, matching the installed server, instead of pointing agents at a source checkout's path.

## 0.17.0 - 2026-10-13

### Added

- `click`, `mouse_button`, `drag`, `key` and `type_text` answer `fired`, the signals the gesture set off on its node or focus chain, with listener counts.
- `record_mark` records a windowed session without `options.record` in real time into an MP4 clip with the game's sound, which `stop`, `stop_project`, `restart_project` or `detach_project` returns.
- `frame_control` step takes `options.input`, raw events sent inside chosen stepped frames, and reports what stays held and what went unsent.
- `capture_frames` takes `options.start.call`, run after the start's first check, so an edge start sees the change its own call makes.
- `run_scratches` takes `options.steps`, indices, names or ranges, and plays only those steps, reporting them as `selected`.
- `watch` takes `tracks.audio`: `players` reports each sound's start, pause, resume and end, and `buses` reports when each bus carried sound.
- `batch_drive` takes a `fired` assertion, checking that the nearest earlier gesture step set off a node's signal, optionally with given arguments.
- `run_csharp` snippets take `Log(value)`, returned as `log` with the result and inside a thrown exception's error, beside the game's errors.
- `wait_for`, `frame_control` until and `batch_drive` waits take `{text, under?}`, met when exactly one visible Control shows that text, or none with `exists: false`.
- `wait_for`, `frame_control` until and `batch_drive` waits take `{node, stable}`, met once the node's rect, transform or named property has held still that many frames.
- `wait_for`, `frame_control` until and `batch_drive` take `{node, state, equals}`, met when a key of the node's `get_game_state` state equals the value.

### Changed

- `capture_frames` with `every` always takes a point at 0, which counts against the 1000-point cap.
- `run_scratches` lists every step by its status line and keeps a failing step's detail short and folded unless `options.details` is set.
- A refused node-typed export in `add_node` or `set_node_properties` now says why: not a path from the scene root, no such node, or the wrong class.
- `batch_scene_operations` describes each step's argument shape, and an unknown step argument's refusal ends with that tool's shape.
- Every recording result, movie or real-time, carries `mode`, and a real-time one carries `startedSeconds` and any `warning` or `error`.
- `save_screenshot` also accepts a path in the git checkout's `.tmp` folder above the project.

### Fixed

- An error raised in a world node's `input_event` handler now lands in the gesture's own reply, even when frames outrun physics ticks.
- Engine errors from a `wait_for` or `frame_control` until expression's unguarded call stay out of `errors`, counted in `mutedErrors` instead.
- A `wait_for` property condition whose path names a missing sub-property, such as `position:q`, now fails naming it instead of timing out.

## 0.16.0 - 2026-10-09

### Added

- `wait_for` takes `options.edge`, met only when its condition turns true after a false check, so a condition already true waits for its next rise.
- `run_scratches` paces single steps from the profile's `steps` map, by index or name, so one slow step no longer slows every step.
- `wait_for`'s `exists`, `property` and `expression` waits take `options.call`, run in the frame their checks start, so a step and its wait share one clock.
- `save_screenshot` writes a shot straight to a named PNG in the project, such as a tracked picture, in one call.
- `run_scratches` takes `options.keepGoing`, playing on past a step that pushed an error and listing every red step in `failures`.
- `capture_frames` takes `options.start`, a condition whose met frame starts the capture's clock, so shots count from an event rather than the call.
- `wait_for`'s `options.then` takes `pause`, holding the game on the met frame for later reads until `frame_control resume`.
- `frame_control` step takes `options.until`, a `wait_for` condition, and stops paused on the first frame that meets it; step results carry `frame`.
- `hover`, `click` and every `item` target reach a RichTextLabel's tooltip spans by tooltip text or index; a hover just off one warns naming the nearest.

### Fixed

- A quiet run started by a server under ssh or a service no longer fails before its game starts; the hidden desktop is named in the server's own window station.
- A headless scene edit no longer writes a `unique_id` into an edited node's header in a scene that has none; an edited node keeps its own id.
- An engine error raised by a method a `watch` expression calls no longer lands in the call's `errors`; the track reads null.
- A screenshot or capture tool on a headless game, such as a `run_scratches` session, now refuses with a reason instead of breaking the scratch replay.
- A game `run_project` started now quits with its exit work when its server exits or dies, instead of being killed or left running.

## 0.15.0 - 2026-10-06

### Added

- `watch` records signals of a node or a group as `tracks.signals`, returning every emission with its frame, game time and arguments.
- `watch` reads performance monitors as `tracks.monitors`: frame time, Godot's built-in monitors and the game's custom ones, summarised with percentiles and the frames that spiked.
- `wait_for` takes `options.then`, a method call and a time scale run once in the frame its condition is met.
- `options.call` in `capture_frames`, `wait_for` and `watch` takes a C# game tool by name with named arguments, as `{tool, args}`.
- A `run_csharp` snippet can `await ToSignal(source, signal)`, so it can wait a frame or a game signal mid-snippet.
- `run_scratches` takes `options.session`, the prefix of its scenes' session names, so parallel runs from several worktrees are told apart.

### Changed

- A `batch_drive` wait refused at its timeout names the wall time that passed beside its load-adjusted budget.
- `monitor_property` is removed: `watch {action: "run"}` with a property track samples one value or several, beside every other tool.
- `take_screenshot`, `save_screenshot_baseline` and `compare_screenshot` say to pause an animating game with `frame_control` for a before/after pair.
- A click on a tab scrolled out of a tab bar is refused naming the `scroll` call, or arrow clicks, that bring the tab into view.

### Fixed

- The launch prep reimports an asset whose source changed since its import, or whose import settings changed since the server last saw them.
- `stop_project` waits its quit grace in load-adjusted time, and a kill after it says whether the scene tree was freed and what the process was doing.

## 0.14.0 - 2026-10-04

### Added

- `watch` records property and expression tracks frame by frame while other tools drive the game, returning each track's change points, range and paused frames.
- Item targets reach `OptionButton`, `MenuButton` and `PopupMenu` items by text or index, opening the popup first and following submenus in two calls.
- `run_scratches` reports its `prep` block (build, import and their logs) as `run_project` does.
- `get_ui_elements` lists the tabs and items of an `ItemList`, `TabBar`, `TabContainer` or `Tree` as `items`, the texts an `item` target matches.
- `wait_for` and `batch_drive` waits report `failedChecks` when the expression errored on earlier checks, so a late `met: true` shows it.

### Changed

- A `run_scratches` folder run starts its slowest-paced scenes first, so a long scene no longer ends the run alone.

### Fixed

- `run_scratches` turns a scene red when it errors before its first step, at `failedAt` boot, instead of passing it.
- Condition expressions in `wait_for`, `batch_drive` and `watch` refuse text that Godot's `Expression` would silently ignore, such as `a if b else c`, and name the GDScript syntax it lacks.
- A `{text}` target on a `RichTextLabel` or a mouse-stopping `Label` now presses through a Control of its own scene instance covering it.
- A hover from `hover` or `mouse_button` now survives the game's own cursor re-picks, a drop's end and scene changes, instead of dropping to the hidden cursor.
- With `shutOutRealGamepads`, a `PopupMenu`, `OptionButton` list or other popup now stays open instead of closing as it opens.
- An item target whose point lies outside the viewport is refused as off-screen, naming the item and its rect, instead of as covered.
- An element or `{text}` target whose Control's centre lies outside the viewport is refused as off-screen, naming its rect, instead of as covered.

## 0.13.1 - 2026-10-01

### Fixed

- Headless scene edits keep a property line the file stored at its default, such as a `Label`'s `size_flags_vertical = 4`, instead of rewriting that node.

## 0.13.0 - 2026-10-01

### Added

- `get_game_state {options: {keep: true}}` holds the read for `diff_snapshots`, which reports each changed state leaf, such as `seats[1].hp`.
- `scratch.pace` entries take `{seconds, reason}`, and `run_scratches` reports the reason as `paceReason` on a red or killed scene.
- `godot-mcp.json` takes `//` and `/* */` comments and trailing commas.
- A `godot-mcp.json` preset takes `description`, listed beside its name when `options.preset` names an unknown preset.
- `scratch.patterns` entries take `{pattern, reason}`, and a step failed by a matching line names the entry as `failedAt.pattern`.

### Changed

- `diff_snapshots` is annotated destructive, since re-reading a kept state read runs the game's state methods; agents keep it as a read tool.

## 0.12.0 - 2026-09-30

### Added

- `prep` in `run_project`, `restart_project`, preview and headless tool results names `buildLog` and `importLog`, the full log of a build or import that ran.
- `godot-mcp.json`'s `prepWrapper` runs the prep's build and import through the project's own wrapper, such as its gate script, with `{log}` and `{ceiling}` tokens.
- Live node paths take `%UniqueName`, alone (looked up in every scene, refused when several hold it) or after its owner's path, in every node-taking tool.
- Input tools aim `element` at 2D and 3D world nodes through their camera, with an optional `offset`, and report the point as `aimedAt`.
- Input tools aim at a Control by the text it shows, `{text}` with an optional `under`, naming near misses when nothing matches.
- Input targets take `item` to press a row, tab or tree item inside an `ItemList`, `TabBar`, `TabContainer` or `Tree` by text, index or path.
- `stop_project {projectPath}` frees a folder by stopping its warm headless host, as stopping a session does when idle; `list_sessions` lists hosts as `headlessHosts`.
- `list_game_tools` lists a C# game's own methods marked `[GodotMcpTool]`, with each one's owner, argument schema and whether it can be called now.
- `get_game_state` reads, in one frame, what each `mcp_state` group node returns from `_mcp_state()` or, in C#, `_McpState()`, with `keys` to narrow it.
- `call_game_tool` calls one of those marked methods by name with named arguments, checked against its schema before any game code runs.
- `run_scratches` plays a project's scratch scenes step by step in fresh headless games, in parallel if asked, with a verdict per scene.

### Changed

- Consecutive headless tool calls on a GDScript-only project answer from one Godot kept running per folder, stopped after 5 minutes idle, at most 4 per server.
- `get_ui_elements` reports each Control's shown text: translated, a LineEdit's placeholder while empty, a RichTextLabel without BBCode, and a LinkButton's too.
- `capture_frames` returns a folder, each distinct file once and points indexing them, with `shared` counting points that shared a frame.

### Fixed

- An `element` inside a `SubViewportContainer`, or in a window opened from one, is now aimed where the root viewport shows it.
- A plain `attach_project` ending on a folder no longer deletes an attach file another server's waiting attach wrote since.
- A value holding a Dictionary or Array that contains itself now reads as `"<cycle: Dictionary>"` instead of leaving the call without a reply.
- `run.ps1 install` links the skills from the installed build, so the skill never documents tools the installed server lacks.
- A headless refusal under a red C# build no longer quotes the errors the missing assembly causes, such as a C# autoload's, under "Godot logged:".

## 0.11.1 - 2026-09-30

### Changed

- `attach_project` joins of different dormant games on one folder now wait at once; a second join of the same pid is refused, naming the waiting session.
- A call to an attached game that closed its connection now names `stop_project` or `detach_project` to end the session.
- A session or arm on a folder another godot-mcp server uses with other `quiet`, `shutOutRealGamepads`, `mute` or bridge is refused, naming that server's pid.

### Fixed

- Two servers ending sessions on one folder at once no longer lose an owner of its `override.cfg`, leaving the other's game without a bridge.
- A headless tool call on a folder this server armed no longer deletes its `override.cfg`, so games started later still get the bridge.
- A game going dormant again after `detach_project` now lets go of keys and input actions a drive left pressed, as it already did mouse and pad buttons.

## 0.11.0 - 2026-09-29

### Added

- `run_project`, `attach_project` and `arm_project` take `options.mute`, muting the game's Master bus so a watched or joined game plays no sound.

### Changed

- A quiet `attach_project` or `arm_project` now mutes the game too, as a quiet run always has.
- `stop_project` ends a session `attach_project` joined and quits its game, killing it after 3 s; `detach_project` still leaves the game running.
- `arm_project` leaves `--headless` games alone, so `attach_project` never joins a smoke or test runner started on the armed folder.

### Fixed

- `duplicate_node` gives the copy and its children fresh `unique_id`s, so a scene never holds two nodes with one id.

## 0.10.0 - 2026-09-29

### Added

- `arm_project` prepares a folder so every game started on it can be joined later by `attach_project` while it runs; `disarm_project` ends that.
- `list_sessions` lists armed folders and the games on them waiting to be joined.

### Changed

- `attach_project` takes `quiet`, `shutOutRealGamepads` and `session` in `options`, beside a new `pid` choosing which waiting game to join.
- A game detached on an armed folder waits to be joined again instead of going idle.

## 0.9.0 - 2026-09-29

### Added

- `scroll` sends mouse wheel notches or trackpad pan gestures at a target, with `notches` and `options.factor` for how far.
- `simulate_input` plays wheel notches as `wheel_*` mouse buttons with `factor`, and trackpad pans as `pan_gesture` events.

### Changed

- `capture_input` records wheel notches and trackpad pans, so a captured scroll replays through `simulate_input`.

## 0.8.1 - 2026-09-29

### Changed

- Headless tools run beside a live session on the project folder instead of refusing; the game keeps running and sees edits after `restart_project`.
- `run_project` and `attach_project` without a session name take `<name>-2`, `-3`, … when a live session on another folder holds the default name.

### Fixed

- A killed server's `override.cfg` is removed at the next server start, and a game run by hand from it meanwhile opens its window on screen.
- One server no longer deletes the `override.cfg` that another server's live session on the same folder still uses.

## 0.8.0 - 2026-09-29

### Added

- `wait_for` and `batch_drive`'s wait take `{gameMs}` and `{frames}`, a pause in the game's own time or frames that machine load does not stretch.
- `capture_frames` and the `gameMs` and `frames` waits take `options.call`, a method called in the frame their clock starts, so they count from it.

### Changed

- `restart_project`'s result reports the relaunched `window` and, in `warning`, a window that differs from `--resolution`, as `run_project`'s does.

### Fixed

- `hover` over an item of an embedded `PopupMenu` reads the item's tooltip instead of null.
- `stress_input` in a recorded run waits for each input in clip time, so a gapped run in a slow recorded game is no longer cut short.

## 0.7.0 - 2026-09-29

### Added

- The install brings the server's tools to every agent file marked with its godot tool classes, through `godot-mcp --sweep-agents`; `--list-tools` prints each tool's class.

### Changed

- `crop` on `take_screenshot`, `capture_frames` and the screenshot baselines is in viewport coordinates, so a window launched at 2x crops at 2x.

### Fixed

- `run_project` gives a `--resolution` larger than the screen exactly, and its result and `attach_project`'s report the window's size.
- In a recorded run, `wait_for` timeouts and drag, hover and gamepad durations count clip time, and a wait adds `clipMs`.
- `hover` reads and waits for the tooltip Godot shows when it belongs to an ancestor of the hovered Control, naming it in `tooltip.owner`.
- `add_node` and `set_node_properties` refuse anchors, offsets, grow and `anchors_preset` under a Container, which Godot drops at save, and take an unchanged anchor.

## 0.6.1 - 2026-09-28

### Changed

- The several-sessions refusal and every `session` description tell agents to pass the session `run_project` returned on every call from the start.
- `add_node` and `set_node_properties` refuse a non-zero `anchor_*` on a Control in Position layout, saying to set `layout_mode` 1 first.

### Fixed

- `load_sprite` refuses a texture inside a nested project folder, which Godot never scans, instead of importing on every request.
- Scene tools record the uids they create in Godot's uid cache, so a run started outside the server no longer logs `invalid UID`.
- `inspect_node`, `set_property`, `wait_for` and `monitor_property` read a named non-exported C# field instead of refusing it as missing.

## 0.6.0 - 2026-09-28

### Added

- A `run_csharp` snippet gets `Cancellation`, a token its timeout cancels, so a snippet that awaits with it stops once the call times out.
- Scene tools write the `.uid` the editor would for a script that has none, list it in `uidFilesWritten`, and `run_project` imports so Godot's cache learns it.

### Fixed

- `attach_script` keeps the values the previous script stored when the new script declares them, as the editor does, and lists them in `kept` and `dropped`.
- Headless saves add no property line the source lacked when the node loads the same, such as `layout_mode = 0`, and a `save_scene` save-as keeps the source's text.
- Headless scene saves store the engine's value when a C# field shares an engine property's name, such as `scale`, on plain and instanced nodes alike, and warn naming the field.

## 0.5.1 - 2026-09-28

### Changed

- `stop_project` gives `quitMs`, how long a game that quit took after the quit request, and `restart_project` gives `previousQuitMs`.
- Every tool, and each `batch_drive` and `batch_scene_operations` step, refuses an argument or options key it does not take, naming the keys it does.

### Fixed

- An argument of the wrong kind or a missing one names the argument, the kind it takes and the value given, instead of a bare "An error occurred".

## 0.5.0 - 2026-09-28

### Added

- `capture_frames` saves frames at set moments of game time (`at`, or `every` and `for`) in one call, following `time_scale`.
- `move_node` reorders a node among its siblings or moves it under another parent; `add_node` takes the same `options.position`.

### Changed

- `add_node` and `batch_scene_operations` take a `.gd` or `.cs` script path as `nodeType`, making a node of its base class with the script attached.
- Headless scene edits rewrite only the sections they add, change or delete, keeping the rest of the `.tscn` as it was; a `warning` says when they cannot.
- `restart_project` says how it stopped the old game in `previousKillReason` and `previousLeftRunning`, as `stop_project` does.

### Fixed

- `add_node` takes a Control's `layout_mode` and `anchors_preset` as the editor sets them, and a value the parent forbids says which it allows.
- A timed-out `run_script` is stopped in the game, and `run_script` and `call_method` timeouts restore `Engine.time_scale` and `SceneTree.paused`.
- Screenshots, baselines, frame steps and previews include popups and tooltips, in place at any stretch, in projects that turn `embed_subwindows` off, quiet runs included.
- `stop_project` no longer reports a kill for a game that quit but left processes running; it lists them in `leftRunning`, and a real kill gives `killReason`.

## 0.4.0 - 2026-09-28

### Added

- A build, import, headless run or clip cut idle and silent for 120 s is stopped as stalled; such errors now name the limit reached.
- A frame step, monitor, wait or C# call the server gives up on is cancelled in the game, freeing the bridge for the next call.

### Changed

- Timeouts and ceilings, caller-set ones included, run on load-adjusted time that slows while other work loads the machine, and end at 5x in wall time.
- A git call that runs past 30 s is stopped and treated as a failed git call instead of holding up the tool.

### Fixed

- `stop_project` returns only once a killed game has let go of its project folder, so the folder can be moved or deleted at once.

## 0.3.5 - 2026-09-27

### Changed

- Without `GODOT_PATH`, Godot is found as a console executable on `PATH`, or the refusal says how to set it; `run_project` names the Godot used.
- The installer stops running servers itself and names each Claude session and project to reconnect with `/mcp`.

## 0.3.4 - 2026-09-27

### Added

- A prebuilt download on each GitHub release, with a one-line installer that also offers to install the .NET 10 runtime.

### Changed

- Input targets, `wait_for` signal waits, `monitor_property` and `{"$node"}` arguments that name no node refuse with the base, deepest node and its children.

## 0.3.3 - 2026-09-27

### Fixed

- A `run_csharp` snippet that names a non-public .NET member is refused at compile time instead of failing inside the game.

## 0.3.2 - 2026-09-27

### Fixed

- A headless scene edit refused over a failed C# build quotes the compiler errors and names the configuration built, as a failed launch does.
- Headless scene tools under a failed C# build report the build, not misleading C# autoload errors, in a `csharp` block.

## 0.3.1 - 2026-09-27

### Changed

- A live node path that finds nothing names the base it was read from, the deepest node that exists, and up to ten of its children.
- `run_csharp` snippets call the game's `internal` members directly; private members still go through `Get`, `Set` or `Call`, and a compile error says so.

## 0.3.0 - 2026-09-27

### Added

- The `dropIdle` run option cuts the idle time between tool calls out of each recorded clip, so the clip plays just the action, silently.

### Changed

- Recorded clips are H.264 MP4s instead of MJPEG AVIs; a clip whose encode fails is kept as an AVI copy, and the result says so.

## 0.2.0 - 2026-09-27

### Added

- `validate` on a `.cs` file reports that file's own compiler errors and warnings and loads the scenes that attach it.

## 0.1.2 - 2026-09-27

### Fixed

- An `{element}` input target that is hidden, being freed, shared by several nodes or covered by another control is refused before any press.
- An `{element}` target is aimed correctly inside a CanvasLayer, under a Camera2D and in an embedded popup.

## 0.1.1 - 2026-09-27

### Fixed

- In a C# project, a `run_script` that cannot reach a C# member points to `cs_get`, `cs_call` and `run_csharp`.

## 0.1.0 - 2026-09-27

### Added

- Every build carries a version, reported at the handshake and in `run_project`, `restart_project` and `attach_project` results, to quote in an issue.
