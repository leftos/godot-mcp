# Changelog

What changed in each version of the godot-mcp server, newest first. The version is `VersionPrefix` in `Directory.Build.props`, and the build stamps the commit after a `+` (`0.3.1+<sha>`). Builds before 0.1.0 carry no version.

## Unreleased

### Added

- `prep` in `run_project`, `restart_project`, preview and headless tool results names `buildLog` and `importLog`, the full log of a build or import that ran.
- `godot-mcp.json`'s `prepWrapper` runs the prep's build and import through the project's own wrapper, such as its gate script, with `{log}` and `{ceiling}` tokens.
- Live node paths take `%UniqueName`, alone (looked up in every scene, refused when several hold it) or after its owner's path, in every node-taking tool.
- Input tools aim `element` at 2D and 3D world nodes through their camera, with an optional `offset`, and report the point as `aimedAt`.
- Input tools aim at a Control by the text it shows, `{text}` with an optional `under`, naming near misses when nothing matches.
- `stop_project {projectPath}` frees a folder by stopping its warm headless host, as stopping a session does when idle; `list_sessions` lists hosts as `headlessHosts`.
- `list_game_tools` lists a C# game's own methods marked `[GodotMcpTool]`, with each one's owner, argument schema and whether it can be called now.
- `call_game_tool` calls one of those marked methods by name with named arguments, checked against its schema before any game code runs.

### Changed

- Consecutive headless tool calls on a GDScript-only project answer from one Godot kept running per folder, stopped after 5 minutes idle, at most 4 per server.
- `get_ui_elements` reports each Control's shown text: translated, a LineEdit's placeholder while empty, a RichTextLabel without BBCode, and a LinkButton's too.

### Fixed

- An `element` inside a `SubViewportContainer`, or in a window opened from one, is now aimed where the root viewport shows it.
- A plain `attach_project` ending on a folder no longer deletes an attach file another server's waiting attach wrote since.

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
