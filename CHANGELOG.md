# Changelog

What changed in each version of the godot-mcp server, newest first. The version is `VersionPrefix` in `Directory.Build.props`, and the build stamps the commit after a `+` (`0.3.1+<sha>`). Builds before 0.1.0 carry no version.

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
