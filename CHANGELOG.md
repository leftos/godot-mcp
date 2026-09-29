# Changelog

What changed in each version of the godot-mcp server, newest first. The version is `VersionPrefix` in `Directory.Build.props`, and the build stamps the commit after a `+` (`0.3.1+<sha>`). Builds before 0.1.0 carry no version.

## Unreleased

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
