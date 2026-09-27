---
name: godot-mcp
description: Use when running, seeing, driving, inspecting or editing a Godot project through the `godot` MCP server (`mcp__godot__*` tools) — launching or attaching to a game, clicking, dragging, typing or pressing pad buttons in it, screenshotting it, waiting for an effect, reading or changing live nodes, stepping frames, recording a clip, checking a visual baseline, or editing .tscn files headless. Also before the first godot tool call of a session, and when a godot tool call is refused or returns something unexpected.
---

# Driving Godot through the godot MCP server

The `godot` server (`mcp__godot__*`, source and full reference at `D:\godot-mcp`, public at github.com/leftos/godot-mcp) runs a Godot 4.7 project with a bridge injected, so you can see it, drive it, inspect it and edit its scenes. This is the walkthrough; every tool's full arguments, edges and result are in the reference, `D:\godot-mcp\docs\TOOLS.md` (online: https://github.com/leftos/godot-mcp/blob/main/docs/TOOLS.md). Read a tool's section there before its first use in a session when its edges matter (input, time, headless edits).

## The loop

A drive is always the same five moves: **start → look → act → wait → check**, then **stop**.

1. **Start.** `run_project {projectPath}` (the folder holding `project.godot`) returns once the bridge is connected; note the `session` and, in a C# project, the `prep` (a stale assembly was built, imports were run). It starts **quiet**: off-screen, unfocused, silent, real input shut out. `scene` picks another scene; `userArgs` go after `--` (the game reads them with `OS.get_cmdline_user_args()`), `engineArgs` before it (`["--resolution", "1280x720"]`). A `godot-mcp.json` beside `project.godot` holds per-project defaults and named `presets` (`options.preset`).
2. **Look.** `get_ui_elements` lists Controls with their rects in viewport coordinates; `get_scene_tree` lists every live node; `take_screenshot` returns an image (a 480 px preview by default; `responseMode: "path_only"` when only the file matters).
3. **Act.** Aim at a Control by path: `click {target: {element: "Main/Menu/Play"}}`. Use `{x, y}` only where no Control exists, and then in **viewport** coordinates, never pixels read off a screenshot (a stretched or letterboxed window's screenshot is not the viewport).
4. **Wait.** `wait_for` a condition after every act instead of sleeping: `{node, exists}`, `{node, property, equals}`, `{node, signal}` or `{expression}`. A timeout is a result (`met: false` with `last`), not an error.
5. **Check.** Every runtime result carries `errors` the game raised during the call, with file and line. Read them before the next step: a handler that threw still "succeeds".
6. **Stop.** `stop_project` at the end of every `run_project` session (and before any headless tool on that folder). A game joined with `attach_project` ends with `detach_project` instead.

## Every tool, by job

**Sessions.** `run_project` starts a game; `attach_project` joins one started some other way (a second client, a `--server` run, the editor's Play button: start it within `waitSeconds` after the call, never before); `restart_project` relaunches a run session with the same scene and arguments after you change code, keeping its name and output; `stop_project` and `detach_project` end them; `list_sessions` names them; `get_debug_output` reads a run's stdout and stderr when it crashed or quit. Several sessions can run at once (a server and two clients): name them with `options.session`, and pass `session` to every call once there is more than one.

**Seeing.** `take_screenshot` (with `crop` in screenshot pixels), `get_ui_elements` (filter by class, e.g. `BaseButton`), and `preview_scene`, which shows one scene without playing it: it starts the scene quiet, screenshots its first frames and stops, beside any live session.

**Input.** `click`, `drag` (a GUI drag starts only past 10 px), `type_text` (into the focused Control: click the LineEdit first), `key` (`Enter`, `Escape`, `A`, with `action` `press`/`release` to hold one), `mouse_button` (a press or release by hand, for paths `drag` cannot draw). Pads: `gamepad_button` (`A`, `START`, `DPAD_DOWN`), `gamepad_stick` (push, then `options.release: true` before the next push moves focus again), `gamepad_axis` (triggers, analogue values). `simulate_input` sends raw event sequences, InputMap `action` events included, when no gesture fits. The injected pad never shows in `Input.get_connected_joypads()`.

**Time.** `frame_control` pauses, resumes, steps exact drawn or physics frames (`step` leaves the game paused; `options.screenshot: true` captures the frame reached) and sets `time_scale`. While paused, `wait_for` accepts only a signal wait or a check-once wait (`timeoutMs: 0`): that pair is how you assert on an exact frame.

**Inspecting and changing the live game.** `inspect_node` reads a node's properties; `set_property` sets one from JSON by its declared type and reads it back (`{x, y}` for a Vector2, `"#rrggbb"` for a Color), putting the old value back when the read-back differs; `call_method` calls a method (C# ones too) and awaits a coroutine; `run_script` runs a GDScript `extends RefCounted` with `func execute(scene_tree: SceneTree) -> Variant` for anything those three cannot say. `get_errors` lists logged errors and warnings since a `seq` (`since` = the last call's `next`).

**Regression checks.** `batch_drive` replays up to 100 tool steps and assertions (`property`, `expression`, `wait`, `no_errors`, `screenshot`) in one call and stops at the first failure. `save_screenshot_baseline` stores a cropped frame once; `compare_screenshot` diffs a later frame against it (pause first for a stable frame).

**Recording.** `run_project {options: {record: true}}` records a movie at a fixed 60 fps (game time then no longer matches wall time: wait on conditions, not durations); `record_mark` `start`/`stop` pairs become clips when the run ends.

**Headless scene editing** (no game runs; refused while a session is live on the folder). `validate` loads scripts, scenes and resources and reports errors, C# build included; `get_scene_file_tree` lists a scene file's nodes (paths from its root: `.`, `Boss/Sprite`); `create_scene`, `save_scene` (save-as), `add_node`, `delete_nodes`, `duplicate_node`, `attach_script`, `load_sprite`, `set_node_properties`, `get_node_properties`, `get_node_signals`, `connect_signal`, `disconnect_signal` edit or read one scene; `export_mesh_library` builds a GridMap MeshLibrary from a 3D scene; `batch_scene_operations` runs many edits on one scene in one Godot start and saves once, all or nothing. Prefer it for any change of more than one edit, then `validate`, then `preview_scene` to see the result.

## Rules that bite

- **Viewport coordinates everywhere** an input tool takes `x, y`. Prefer `{element}` targets.
- **One input at a time per session**, each answering once its gesture has ended and two frames have run.
- **Headless tools refuse a live session** on the same folder: `stop_project` or `detach_project` first.
- **Headless node paths are relative to the scene root** (`.`, `HUD/Score`); live node paths are absolute (`/root/Main/Button`), under the root (`Main/Button`) or a bare name.
- **C# projects:** a launch, a restart and every headless tool build a stale assembly first; a failed build refuses the launch with the compiler errors in the result.
- **Nothing lands in the project's tracked files.** The bridge rides in an `override.cfg` beside `project.godot`, hidden from git by `.git/info/exclude` and removed at stop or detach; a user's own `override.cfg` is refused, never overwritten. Screenshots, baselines and recordings go under `.godot/godot-mcp/`. After a drive, `Test-Path <project>/override.cfg` is false; if a crashed run left one, `stop_project` it or delete it (its first line is `; godot-mcp: bridge injection, removed when the run stops`; a file without that line is the project's own, never delete it).
- **Quiet by default:** pass `options.quiet: false` only when the user wants to watch or play along.

## When something goes wrong

- A call refused for want of a session: `list_sessions`, then pass `session`.
- The game crashed or "the connection ended": `get_debug_output`, then `get_errors`.
- A click that seems to do nothing: check `errors` in its result, then `get_ui_elements` for `disabled`/`visible`, then `wait_for` its effect rather than screenshotting at once.
- A tool that is missing, confusing, slow or wrong for the job: that is friction with a tool we own. File it (`gh issue create -R leftos/godot-mcp`, after `gh issue list -R leftos/godot-mcp --search "<words>"`), with the tool, the arguments, what happened against what you needed, and the workaround you used, then carry on.

Game-specific drive lessons (a project's scenes, launch arguments, a known flaky screen) belong in that project's own docs (its DEVELOPMENT.md), not here.
