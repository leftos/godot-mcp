---
name: godot-mcp
description: Use when running, seeing, driving, inspecting or editing a Godot project through the `godot` MCP server (`mcp__godot__*` tools) — launching or attaching to a game, clicking, dragging, typing or pressing pad buttons in it, screenshotting it, waiting for an effect, reading or changing live nodes, stepping frames, recording a clip, checking a visual baseline, or editing .tscn files headless. Also before the first godot tool call of a session, and when a godot tool call is refused or returns something unexpected.
---

# Driving Godot through the godot MCP server

The `godot` server (`mcp__godot__*`, source and full reference at `D:\godot-mcp`, public at github.com/leftos/godot-mcp) runs a Godot 4.7 project with a bridge injected, so you can see it, drive it, inspect it and edit its scenes. This is the walkthrough; every tool's full arguments, edges and result are in the reference, `D:\godot-mcp\docs\TOOLS.md` (online: https://github.com/leftos/godot-mcp/blob/main/docs/TOOLS.md). Read a tool's section there before its first use in a session when its edges matter (input, time, headless edits).

## The loop

A drive is always the same five moves: **start → look → act → wait → check**, then **stop**.

1. **Start.** `run_project {projectPath}` (the folder holding `project.godot`) returns once the bridge is connected; note the `session` and pass it to every later call, and, in a C# project, the `prep` (a stale assembly was built, imports were run). It starts **quiet**: off-screen, unfocused, silent, real input shut out. `scene` picks another scene; `userArgs` go after `--` (the game reads them with `OS.get_cmdline_user_args()`), `engineArgs` before it (`["--resolution", "1280x720"]`). A `godot-mcp.json` beside `project.godot` holds per-project defaults and named `presets` (`options.preset`).
2. **Look.** `get_ui_elements` lists Controls with their rects in viewport coordinates; `get_scene_tree` lists every live node; `take_screenshot` returns an image (a 480 px preview by default; `responseMode: "path_only"` when only the file matters).
3. **Act.** Aim at a Control by path: `click {target: {element: "Main/Menu/Play"}}`. Use `{x, y}` only where no Control exists, and then in **viewport** coordinates, never pixels read off a screenshot (a stretched or letterboxed window's screenshot is not the viewport).
4. **Wait.** `wait_for` a condition after every act instead of sleeping: `{node, exists}`, `{node, property, equals}`, `{node, signal}` or `{expression}` (`options.screenshot: true` captures the very frame it was met on, when the scene moves on faster than a following `take_screenshot`); when you cannot name what the input will change (a menu, a dialog), `{uiChanged: true}` reports which Controls appeared or disappeared and whether focus or the top popup moved. A timeout is a result (`met: false` with `last`), not an error.
5. **Check.** Every runtime result carries `errors` the game raised during the call, with file and line. The key is absent when there were none. Read them before the next step: a handler that threw still "succeeds".
6. **Stop.** `stop_project` at the end of every `run_project` session (and before any headless tool on that folder). A game joined with `attach_project` ends with `detach_project` instead.

## Every tool, by job

**Sessions.** `run_project` starts a game; `attach_project` joins one started some other way (a second client, a `--server` run, the editor's Play button: start it within `waitSeconds` after the call, never before; `quiet: true` for one a debugger launches, with `--audio-driver Dummy` in its args); `restart_project` relaunches a run session with the same scene and arguments after you change code, keeping its name and output; `stop_project` and `detach_project` end them; `list_sessions` names the live ones (`includeStopped: true` adds runs that ended, still reachable by name); `get_debug_output` reads a run's stdout and stderr when it crashed or quit. `stop_project`'s `alreadyExited` says the game had ended before the stop, and `gameExitCode` is the game's own code (`exitCode` is the console wrapper's). Several sessions can run at once (a server and two clients): name them with `options.session`. Pass the `session` `run_project` returned to every call from the first one: another agent's game can start on the same server at any time, and a call without a name is then refused.

**Seeing.** `take_screenshot` (with `crop` in screenshot pixels), `get_ui_elements` (filter by class, e.g. `BaseButton`), and `preview_scene`, which shows one scene without playing it: it starts the scene quiet, screenshots its first frames and stops, beside any live session.

**Input.** `click`, `drag` (a GUI drag starts only past 10 px), `type_text` (into the focused Control: click the LineEdit first), `key` (`Enter`, `Escape`, `A`, with `action` `press`/`release` to hold one), `mouse_button` (a press or release by hand, for paths `drag` cannot draw; `action: "move"` only moves), `hover` (the pointer over a Control, waiting for its tooltip and returning its text and rect; hover while the game runs, since a paused Control never starts its tooltip timer, then pause if needed). Pads: `gamepad_button` (`A`, `START`, `DPAD_DOWN`), `gamepad_stick` (push, then `options.release: true` before the next push moves focus again), `gamepad_axis` (triggers, analogue values). `simulate_input` sends raw event sequences, InputMap `action` events included, when no gesture fits. The injected pad never shows in `Input.get_connected_joypads()`. Without `device`, pad input goes to the lowest id no real pad holds, reported as `device` in the result: claim that pad in the game's pad-selection step.

**Time.** `frame_control` pauses, resumes, steps exact drawn or physics frames (`step` leaves the game paused; `options.screenshot: true` captures the frame reached) and sets `time_scale`. While paused, `wait_for` accepts only a signal wait or a check-once wait (`timeoutMs: 0`): that pair is how you assert on an exact frame.

**Inspecting and changing the live game.** `inspect_node` reads a node's properties (a C# node's `[Export]` members by default; name a private field in `properties` to read it, as `set_property`, `wait_for` and `monitor_property` do); `set_property` sets one from JSON by its declared type and reads it back (`{x, y}` for a Vector2, `"#rrggbb"` for a Color), putting the old value back when the read-back differs; `call_method` calls a method (C# ones too) and awaits a coroutine; `cs_members` lists a C# node's or type's members, private ones and every overload included, that Godot's call cannot show; `cs_get` and `cs_set` read and write a C# property or field by name or dotted path (`Pending.Options[0]`), private ones and statics included, where Godot's own `get` reads null (`cs_set` reads back and puts the old value back as `set_property` does; `cs_get`'s `options.keep` returns a handle for a later `{handle}` target); `cs_call` calls a C# method or constructor (`.ctor` on a `{type}`) with JSON arguments, choosing among overloads (`options.signature` when several fit), closing generics (`options.typeArgs`) and awaiting a `Task`; `run_csharp` runs a C# method body in the game (`var t = Node<Duel>("Duel"); return t.Bind(new Update(...));`), with `Node`, `Get`/`Set`/`Call` (private members), `await` and `Cancellation` (the token its timeout cancels; pass it to awaits so a timed-out snippet stops) in scope, refused when the game runs an older build than the one on disk (`restart_project`); `run_script` runs a GDScript `extends RefCounted` with `func execute(scene_tree: SceneTree) -> Variant` for anything those tools cannot say, but in a C# project a C# member Godot does not marshal (a `List<T>`, a record, a private field) is out of its reach: read it with `cs_get`, `cs_call` or `run_csharp`. `get_errors` lists logged errors and warnings since a `seq` (`since` = the last call's `next`).

**Regression checks.** `batch_drive` replays up to 100 tool steps and assertions (`property`, `expression`, `wait`, `no_errors`, `screenshot`) in one call and stops at the first failure. `save_screenshot_baseline` stores a cropped frame once; `compare_screenshot` diffs a later frame against it (pause first for a stable frame).

**Recording.** `run_project {options: {record: true}}` records a movie at a fixed 60 fps (game time then no longer matches wall time: wait on conditions, not durations); `record_mark` `start`/`stop` pairs become `.mp4` clips when the run ends. Add `dropIdle: true` beside `record` to cut the idle frames drawn between your tool calls out of each clip (the clip then plays just the action, silent); leave it out when a still moment or the sound matters.

**Headless scene editing** (no game runs; refused while a session is live on the folder). `validate` loads scripts, scenes and resources and reports errors, C# build included (a `.cs` target gets its own errors and warnings and the scenes that attach it); `get_scene_file_tree` lists a scene file's nodes (paths from its root: `.`, `Boss/Sprite`); `create_scene`, `save_scene` (save-as), `add_node`, `delete_nodes`, `duplicate_node`, `move_node`, `attach_script`, `load_sprite`, `set_node_properties` (a Control's anchors need `layout_mode` 1 first), `get_node_properties`, `get_node_signals`, `connect_signal`, `disconnect_signal` edit or read one scene, an edit leaving the rest of the `.tscn` as it was (a `warning` says when it could not; `save_scene` rewrites the whole file in 4.7 form; a result's `uidFilesWritten` names `.uid` files written for scripts that had none: commit them with the scene); `export_mesh_library` builds a GridMap MeshLibrary from a 3D scene; `batch_scene_operations` runs many edits on one scene in one Godot start and saves once, all or nothing. Prefer it for any change of more than one edit, then `validate`, then `preview_scene` to see the result.

## Rules that bite

- **Viewport coordinates everywhere** an input tool takes `x, y`. Prefer `{element}` targets (full paths from `get_ui_elements` when names repeat). An element that is hidden, being freed, named by several nodes, or covered at its centre by another Control is refused with nothing pressed: the error names the cover and both rects, so click by `{x, y}` in the visible part or wait until it clears.
- **A misnamed or mistyped argument is refused by name**, with the keys the tool takes or the kind the value needs: read the refusal and fix the call, since nothing ran.
- **One input at a time per session**, each answering once its gesture has ended and two frames have run.
- **Headless tools refuse a live session** on the same folder: `stop_project` or `detach_project` first.
- **Headless node paths are relative to the scene root** (`.`, `HUD/Score`); live node paths are absolute (`/root/Main/Button`), under the root (`Main/Button`) or a bare name, in the C# tools as in the GDScript ones; a miss names the deepest node that exists and its children, so read the refusal before calling `get_scene_tree`. A `run_csharp` snippet names the game's `internal` members directly; privates go through `Get`/`Set`/`Call`.
- **C# projects:** a launch, a restart and every headless tool build a stale assembly first; a failed build refuses the launch with the compiler errors in the result; a headless tool reports it as a `csharp` block (`build: "failed"` and the errors) and refuses a save that needs C#, quoting them. A C# autoload error under a red build is the missing assembly, not your scene: fix the build first.
- **Nothing lands in the project's tracked files.** The bridge rides in an `override.cfg` beside `project.godot`, hidden from git by `.git/info/exclude` and removed at stop or detach; a user's own `override.cfg` is refused, never overwritten. Screenshots, baselines and recordings go under `.godot/godot-mcp/`. After a drive, `Test-Path <project>/override.cfg` is false; if a crashed run left one, `stop_project` it or delete it (its first line is `; godot-mcp: bridge injection, removed when the run stops`; a file without that line is the project's own, never delete it).
- **Quiet by default** (off-screen, silent, capped at 60 fps): pass `options.quiet: false` only when the user wants to watch or play along.
- **Real gamepads reach even a quiet game.** A connected pad's drifting stick can move GUI focus, and a focus move between a click's press and release drops the click. When clicks seem to vanish now and then, or a drive counts presses, launch with `options.shutOutRealGamepads: true`, unless the drive opens a `Popup` (menus, `OptionButton` lists): shut-out closes every popup as it opens.

## When something goes wrong

- A call refused for want of a session: `list_sessions`, then pass `session`, on this call and every later one.
- The game crashed or "the connection ended": `get_debug_output`, then `get_errors`.
- "Paused under a debugger": a debugger holds the game at a breakpoint; continue it there (the `debug-live` skill), never restart it. A debugger attaches to `list_sessions`' `gameProcessId`, not `processId`; `stop_project` ends its debug session too.
- A class you do not know (an engine node, or the game's own `class_name`): `describe_class` lists its properties, typed method signatures and signals, from the running game or headless, and suggests close names for a typo.
- A value that moves over time (a tween, a velocity): `monitor_property` samples it each frame in one call and returns only the changes. Frames at set moments (0.5 s and 1.2 s into a step, or every 0.1 s for 2 s): `capture_frames {at}` or `{options: {every, for}}` grabs them in one call on game time, following `time_scale`. A game driven by InputMap actions: `simulate_action {action}` taps one. To hunt a crash no scripted drive finds: `stress_input {pool, count, seed}` fires random actions, keys and element clicks and reports the new errors by the iteration they appeared at; pass a failing run's `seed` back to replay it.
- An action whose effects you cannot list: `snapshot_subtree` first, act, then `diff_snapshots {beforeId}` (no `afterId` re-captures now) for every node added, removed or changed. Ids die with a stop or restart, and a `batch_drive` step cannot read an earlier step's id.
- A click that seems to do nothing: read its `pressedOn` (the Control it actually hit; a drag's `guiDragStarted` and `dropAccepted` say whether a GUI drag and drop happened), check `errors` in its result, then `get_ui_elements` for `disabled`/`visible`, then `wait_for` its effect rather than screenshotting at once.
- A bug a person can reproduce by hand but you cannot script: run with `options.quiet: false` (or attach), `capture_input {mode: "start"}`, let them play, `capture_input {mode: "stop"}`, then replay its `events` with `simulate_input`; the capture survives a crash, so stop it after the game dies too.
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
