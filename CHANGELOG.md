# Changelog

What changed in each version of the godot-mcp server, newest first. The version is `VersionPrefix` in `Directory.Build.props`, and the build stamps the commit after a `+` (`0.3.1+<sha>`). Builds before 0.1.0 carry no version.

## Unreleased

### Added

- `capture_frames` saves frames at set moments of game time (`at`, or `every` and `for`) in one call, following `time_scale`.

### Changed

- `add_node` and `batch_scene_operations` take a `.gd` or `.cs` script path as `nodeType`, making a node of its base class with the script attached.
- Headless scene edits rewrite only the sections they add, change or delete, keeping the rest of the `.tscn` as it was; a `warning` says when they cannot.

### Fixed

- A timed-out `run_script` is stopped in the game, and `run_script` and `call_method` timeouts restore `Engine.time_scale` and `SceneTree.paused`.
- Screenshots, baselines, frame steps and previews include popups and tooltips in projects that turn `embed_subwindows` off, quiet runs included.
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
