# godot-mcp: our own Godot MCP server and in-game bridge

## Context

Agents drive the user's two Godot projects (opening-hand and delve-the-dungeon, both C#, Godot 4.7.2) through the third-party `godot-mcp-runtime` 3.4.0 (npx, TypeScript, MIT). Its costs are recorded across both repos:
- **The bridge is written into `project.godot`**, and `stop_project` has left it there. There is a commit rule in both repos and in the user-level Godot conventions because of it.
- **Simulated input misses.** Motion events carry no `button_mask`, so a drag does nothing (2026-09-25). Clicks land in window pixels while `get_ui_elements` reports viewport pixels, so a letterboxed window misses small buttons. Typed text arrives in lower case.
- **`run_project` passes no arguments**, so delve uses a launch-then-attach workaround.
- **One command at a time**, no request ids, and JSON parsed out of stdout by finding the first bracket.

The user asked for our own plugin and MCP server for all their Godot projects. Godot's source is at `F:\Godot\repo`, but that checkout is detached at 4.5.1, while the Godot in use is 4.7.2 (the research read the 4.7.2 tags upstream). The reference server's source is in the npx cache.

## Decided (user, 2026-09-25)

1. **Fresh code.** `godot-mcp-runtime` (MIT) and Godot's source are read for reference only; nothing is forked.
2. **The bridge is injected and never committed.** A run loads it through `override.cfg` in the project folder, which is hidden from git through `.git/info/exclude`.
   - Verified 2026-09-25 on 4.7.2: an `override.cfg` autoload pointing outside the project loaded, and `project.godot` was untouched.
   - Godot's source: game runs read `override.cfg`; the editor, `--import` and `--export` never do (`main/main.cpp:2107`, `p_ignore_override`).
3. **C# / .NET 10 server, GDScript bridge.**
4. **Its own repo at `D:\godot-mcp`.**
5. **First-version must-have: faithful input**, offered as gestures plus raw events. Gamepad input is part of it: buttons, sticks and triggers per device (user, 2026-09-25, step 4b).
6. **The 16 headless scene and node tools are included.**
7. **`run_project` takes pass-through arguments.**
8. **Cutover at parity**, in every project at once; the old registration is removed.
9. **Sixteen friction features, all before the cutover** (user, 2026-09-25, choosing every option offered and "all before cutover" over building only the structural ones first):
   - Time: frame control, `wait_for`, `restart_project`, fresh-worktree prep.
   - Inspection: the running scene tree, `inspect_node`/`set_property`, `call_method`, an error feed on every result, screenshot baselines.
   - Scale: several sessions, a hang watchdog, quiet by default, in-engine recording.
   - Ergonomics: tool annotations, a batch drive tool, compact outputs, project profiles.

   This reverses decision 5's "several sessions" and "in-engine recording" as later work. The order is in `MAIN.md` (steps 7-15, structural first).
10. **Steps 7-9 shape** (user, 2026-09-25, each the recommended option of a ranked choice):
    - **Session names.** `run_project` and `attach_project` take an optional `session`; without one, the session is named after the project folder. A second session under a live name is refused and asked for a name; a name whose run has exited is replaced. Runs and attaches share one name space. A runtime tool without a name uses the only session, and with several it refuses, listing the names. `list_sessions` (read-only) returns each session's name, project, kind (run or attach), liveness and process id.
    - **Several sessions on one project folder** (delve's server and two clients). The marked `override.cfg` is reference-counted and removed when the last session on that folder ends. A new session whose `shutOutRealGamepads` differs from the live ones' is refused, since the file holds one value. An attach is refused while another attach on the same folder is still waiting for its game.
    - **The five-parameter limit holds.** `session` goes into `run_project`'s `options`; `gamepad_axis` and `gamepad_stick` move `durationMs` and `release` into an `options` object to make room for it.
    - **Error feed.** The bridge registers a `Logger` (`OS.add_logger`, confirmed in 4.7.2's source first) and sends errors with function, file, line and Godot's script backtrace over the wire, so attached games report too. Results carry errors only, in an `errors` array, and the call still succeeds; `get_errors(since)` returns errors and warnings keyed by a per-session sequence cursor.
    - **Screenshots** default to an inline preview at most 480 px wide, plus the full-size path.
    - **Quiet by default** (reshaped by the user on 2026-09-26 after review measured the first build): `run_project` starts quiet unless `options.quiet = false`. The window is created unfocused and off-screen by the quiet run's `override.cfg` (`[display]` `window/size/no_focus` and an off-screen initial position), because Godot creates the main window focused before any script runs (`platform/windows/display_server_windows.cpp` L1970-1973), and un-focusing it later does not release the focus it holds. It stays click-through. Audio is off through `--audio-driver Dummy`, placed before the user's engine arguments, never by touching the game's buses, so the game's own mute stays testable and no muted state leaks into its saved settings. Real input is kept out by the window never receiving it, not by swallowing: an always-on swallow also killed input the game synthesises itself (on-screen keyboards, virtual cursors) and never reached scene `_input` handlers or `Input` polling. The real mouse is still swallowed while a gesture plays, as before. Sessions on one folder must agree on quiet (the override holds one value); an attach counts as not quiet. Attach is otherwise unaffected. Real pads stay opt-in through `shutOutRealGamepads`. Ruled out: marking injected keys with the mouse's device mark (a key's device is 16 and the built-in `ui_*` actions match only 16 or -1: `core/input/input_event.cpp` L669-671, `core/input/input_map.cpp` L147-153).

## Design

**The server** (`src/GodotMcp.Server`, net10.0):
- `ModelContextProtocol` 2.2.0 (current stable, released 2026-08-13; net8/9/10) with `Microsoft.Extensions.Hosting`: `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`. All logging goes to stderr, since stdout is the protocol. Screenshots are returned as `ImageContentBlock.FromBytes(png, "image/png")`.
- It registers as `godot`, keeping the tool names agents already use (`mcp__godot__run_project` …), so the projects' docs keep working after cutover.

**Wire:**
- **The server listens and the bridge dials in.** The server binds `127.0.0.1:0` and keeps the listener open, then passes the port and a session token to Godot through environment variables (`GODOT_MCP_PORT`, `GODOT_MCP_TOKEN`). This removes the reference server's free-port race and its regex-editing of the port into GDScript.
- **Frames** are a 4-byte length plus UTF-8 JSON.
- **Every request carries an id** and every reply echoes it. A reply for a timed-out id is dropped, not matched to the next request.
- **Handshake:** the bridge's hello carries the token and the project path; a mismatch is refused, which catches a stale bridge.

**The bridge** (`bridge/godot_mcp_bridge.gd`):
- The server writes `<project>/override.cfg` with one autoload pointing at the bridge's absolute path, with a first-line marker comment identifying it as ours.
  - An `override.cfg` without that marker is the user's own. The server refuses to launch with a message, never overwriting it.
  - The server adds `override.cfg` to `.git/info/exclude` when it is missing there.
- On stop, and on any crash path, the server deletes its own marked file. Before every launch it removes a stale marked file.
- Commands:
  - `ping`
  - `screenshot`: full-resolution PNG path, an inline preview, and an optional crop rectangle (this replaces `Crop-Screenshot.ps1`). The reference's `force_draw` fallback is kept for an occluded window.
  - `ui_elements`
  - `input`
  - `run_script`: user GDScript with `execute(scene_tree)`; compile diagnostics come from the error feed (step 8).
  - `shutdown`

**Input** (`Input.parse_input_event` with a new event object each time, so `Input` button state stays consistent; the Godot 4.7.2 source behaviour is quoted in the research):
- Every point is in viewport coordinates. The bridge maps it to window coordinates through the viewport's screen transform, which fixes today's miss on a letterboxed window. The step checks this against a stretched fixture window.
- **Gestures:**
  - `click` (an element path or a point; press and release one frame apart)
  - `drag` (from/to points or elements, over N ms, stepped one motion per frame, each carrying `button_mask` LEFT and its `relative`; the total passes the viewport's `gui_drag_threshold`, default 10 px, `viewport.cpp:2049-2052`)
  - `type_text` (unicode with case preserved)
  - `key` and `button` with hold/release
- **`simulate_input`:** a raw event list, done right (motion with mask and relative; press and release separate).
- **Gamepad (step 4b), from Godot 4.7.2's source** (librarian, 2026-09-25; copies fetched under `D:\opening-hand\.tmp\ovr\r\47_*`):
  - **What works.** A parsed `InputEventJoypadButton` updates `is_joy_button_pressed` (`input.cpp:977-987`). A parsed `InputEventJoypadMotion` updates `get_joy_axis`, raw with no deadzone (`input.cpp:989-993`, `601-614`). Actions match through `InputMap::event_get_index`: pressed means `|value| >= deadzone` (default 0.2, `input_map.h:55`), and strength is `inverse_lerp(deadzone, 1, |v|)` (`input_event.cpp:1133-1171`). Joypad events are never merged by accumulated input, and `is_echo()` is always false.
  - **Focus navigation.** The default `ui_left/right/up/down` have d-pad buttons plus the left stick at ±1.0 (`input_map.cpp:490-512`) and all devices (-1), so they react to injected events. A stick push moves focus only on the released-to-pressed change, so the next move needs a 0.0 release first (`viewport.cpp:2340-2380`, `input.cpp:1033-1034`). `ui_accept`, `ui_cancel` and `ui_focus_next` have no joypad binding by default; `ui_select` is Y.
  - **Devices.** The first pad is device 0; ids 0-15 are joypads (`input_event.h:64-67`).
  - **Not reachable from script.** `get_connected_joypads`, `is_joy_known`, `get_joy_name`/guid/info and vibration are filled only by the platform driver's `joy_connection_changed`, and only its signal is bound (`input.cpp:239`, `711`, `747`, `2291-2301`). An injected pad never shows as connected. Neither project calls those queries (checked 2026-09-25). A game that does would need an OS-level virtual pad.
  - **Losing focus can clear pad state.** The setting is `input_devices/joypads/ignore_joypad_on_unfocused_application`, default false (`ProjectSettings.xml` L1671; defined at `input.cpp` L2374).
    - Pad state is cleared only in a project that sets it true, on `NOTIFICATION_APPLICATION_FOCUS_OUT` (`scene_tree.cpp` L934-942, `input.cpp` L1600-1623).
    - While it is true and the application is unfocused, the driver's real pad input is ignored (`input.cpp` L1652, L1684), but events injected through `parse_input_event` still get through.
    - Enum values were checked against `input_enums.h` L68-102.
  - **Shutting real pads out is opt-in** (user, 2026-09-25). This machine has four pads connected, and in the step 4b tests they moved menu focus and pushed an action's strength mid-test.
    - The only script-reachable shut-out is to set the setting true and mark the game unfocused: pad state updates in `Input` before any script sees the event. A real focus-in turns the pads back on (`scene_tree.cpp` L934-942), so the bridge re-sends a focus-out after every focus change and restores held injected pad state.
    - The costs: the game sees application focus-out notifications, and delve mutes on them (`Main.cs:340-352`, when its mute-in-background option is on). Held injected keys release on a real focus change, and held pad buttons fire "just pressed" again.
    - So the default leaves real pads live (the setting written false). `shutOutRealGamepads: true` on `run_project`/`attach_project` turns the shut-out on, and the gamepad tests use it.
    - This reverses a same-day first decision of "shut out by default", made before these costs were measured.
  - **Releases are sent explicitly** (`pressed=false`, or `axis_value=0.0`), and every event is a new object.
- **Built (step 3):** the hold/release gestures are the `key` and `mouse_button` tools. The transform is `get_screen_transform()`, measured against a letterboxed window; the red proof is the three drag tests failing with `button_mask` forced to 0.

**The error feed (step 8), from Godot 4.7.2's source** (librarian, 2026-09-25, tag `4.7.2-stable`):
- `Logger` extends `RefCounted` with `_log_error(function, file, line, code, rationale, editor_notify, error_type, script_backtraces)` and `_log_message(message, error)` (`core/core_bind.h` L125-138); `ErrorType` is ERROR 0, WARNING 1, SCRIPT 2, SHADER 3. `OS.add_logger` catches only what is logged after it; the logging tutorial registers in an autoload's `_init()`. Script loggers are removed at shutdown (`core/object/script_language.cpp` L335-339); the list has no lock, so `remove_logger` while threads log is unsafe and the bridge never calls it.
- `_log_error` runs on the thread that raised the error with no engine lock (`core/error/error_macros.cpp` L125-133, `core/os/os.cpp` L98-151); a per-thread guard sends an error raised inside it to stderr only (`error_macros.cpp` L49, L116-121). So it only appends under a `Mutex`, and the main thread sends.
- Engine `_MSG` errors put the condition in `code` and the message in `rationale`; `push_error`, `push_warning` and GDScript runtime errors put the text in `code` (`core/error/error_macros.h` L426-428, `core/io/logger.cpp` L67-72). `push_error`'s function/file/line are its C++ site (`core/variant/variant_utility.cpp` L1023, L1033; Godot issue #119628), so the script's location is backtrace frame 0.
- GDScript runtime errors reach loggers in debug builds only (`modules/gdscript/gdscript_vm.cpp` L3963-3993); backtraces are filled in debug and editor builds or with `debug/settings/gdscript/always_track_call_stacks` (default false, `gdscript.cpp` L2871-2875). Runtime `load()` parse errors arrive as `function="GDScript::reload"` (`gdscript.cpp` L824-853). Nothing arrives with `application/run/disable_stderr` on, or outside startup-to-shutdown (`main.cpp` L2363-2376, PR #117790). Loggers are additive: the console logger stays (`os.cpp` L829-835).

**Process and session:**
- `run_project(projectPath, scene?, userArgs[], engineArgs[], background?)` spawns Godot with the user arguments after `--`.
- Output is assembled into whole lines across stdout/stderr chunks into ring buffers for `get_debug_output`; errors reach results through the error feed, not stderr.
- Sessions are keyed by name (decision 10): `run_project` under a live name refuses with a message, rather than killing it silently as the reference does.
- `attach_project` / `detach_project` / `stop_project` / `get_debug_output`, and `list_projects` / `get_project_info`.

**Headless tools** (the 16: `create_scene`, `add_node`, `load_sprite`, `save_scene`, `export_mesh_library`, `batch_scene_operations`, the nine node tools, and `validate`):
- Each runs `godot --headless --path <p> --script <repo>/headless/operations.gd` with the request passed as a JSON file path and the result written to a JSON file. This avoids the command-line length limit and stdout scraping.
- `override.cfg` is never written for these, so a headless run loads only the project's own autoloads.

**Out of the first version:** the profiler tools, the autoload-editing tools, the pure file-parsing tools and `launch_editor`, because none has ever been called. C#-aware tools beyond `call_method` also wait.

## Repo and gates

`D:\godot-mcp` holds:
- `GodotMcp.slnx`
- `src/GodotMcp.Server`, `bridge/`, `headless/`
- `tests/GodotMcp.Tests` (xUnit v3: framing, override.cfg writer/cleaner, argument building, line assembly)
- `tests/GodotMcp.IntegrationTests`: launches the real Godot (`$env:GODOT_PATH`, falling back to `F:\Godot\Godot_console.exe`) against `tests/fixtures/InputProbe`, a tiny GDScript project with a drag source, a drop target, a LineEdit and a button, run windowed and hidden.
- `CLAUDE.md` (router), `docs/README.md` (map and glossary), `docs/plans/MAIN.md`, `docs/DEVELOPMENT.md`.
- Gates: warnings as errors, `dotnet format` / csharpier, `prek` hooks, and `gdlint` (gdtoolkit) on the GDScript.

## Steps (each an implementer brief with its proving command)

0. **Scaffold** (me plus implementer): git init, the solution, the gates, the docs skeleton, MAIN.md seeded with these steps.
1. **Wire and lifecycle:**
   - Listener, handshake, `override.cfg` inject/clean/refuse, `.git/info/exclude`.
   - `run_project` (arguments), `stop_project`, `get_debug_output`.
   - Integration: the fixture launches, handshakes, and stops with no file left behind; a user's own `override.cfg` is refused untouched.
2. **Runtime reads:** `take_screenshot` (crop, preview), `get_ui_elements`, `run_script` (diagnostics). Integration against the fixture.
3. **Input:**
   - Gestures plus raw events.
   - Integration: a drag moves the fixture's item to the drop target; `type_text "Hello"` gives "Hello"; a click on a small button in a letterboxed window hits it.
4. **Attach and detach**, with integration.
5. **The 16 headless tools and `validate`**, with integration on a copy of the fixture.
6. **Cutover** (me; load the `agent-tooling-lifecycle` skill first):
   - Register the new server as `godot` in both projects' local config and remove the npx registration.
   - Rewrite what described the old server:
     - both repos' DEVELOPMENT.md registration lines
     - the `project.godot` commit rule in both CLAUDE.md files, both GODOT_CONVENTIONS.md files and the user-level Godot conventions
     - delve's launch-then-attach and input workarounds
     - the `debugger` agent's allow-list
   - Drive one scratch scene in each project end to end: run, screenshot, drag, run_script, stop, with `git status` clean afterwards.
   - The opening-hand MAIN.md line moves to done, pointing at the new repo.

## Verification

- Each step's proving command: unit tests, plus the integration tests against the real Godot 4.7.2 and the fixture.
- **The drag regression is the headline test:** a drag the old server could not perform moves the fixture's item.
- **At cutover:** a real scratch drive in each project, and `git status` clean afterwards in both repos (no `override.cfg` or `.mcp/` left).
