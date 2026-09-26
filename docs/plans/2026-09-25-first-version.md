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
5. **First-version must-have: faithful input**, offered as gestures plus raw events.
6. **The 16 headless scene and node tools are included.**
7. **`run_project` takes pass-through arguments.**
8. **Cutover at parity**, in every project at once; the old registration is removed.

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
  - `run_script`: user GDScript with `execute(scene_tree)`; compile diagnostics are taken from stderr lines marked for that call.
  - `shutdown`

**Input** (`Input.parse_input_event` with a new event object each time, so `Input` button state stays consistent; the Godot 4.7.2 source behaviour is quoted in the research):
- Every point is in viewport coordinates. The bridge maps it to window coordinates through the viewport's screen transform, which fixes today's miss on a letterboxed window. The step checks this against a stretched fixture window.
- **Gestures:**
  - `click` (an element path or a point; press and release one frame apart)
  - `drag` (from/to points or elements, over N ms, stepped one motion per frame, each carrying `button_mask` LEFT and its `relative`; the total passes the viewport's `gui_drag_threshold`, default 10 px, `viewport.cpp:2049-2052`)
  - `type_text` (unicode with case preserved)
  - `key` and `button` with hold/release
- **`simulate_input`:** a raw event list, done right (motion with mask and relative; press and release separate).
- **Built (step 3):** the hold/release gestures are the `key` and `mouse_button` tools. The transform is `get_screen_transform()`, measured against a letterboxed window; the red proof is the three drag tests failing with `button_mask` forced to 0.

**Process and session:**
- `run_project(projectPath, scene?, userArgs[], engineArgs[], background?)` spawns Godot with the user arguments after `--`.
- Output is assembled into whole lines across stdout/stderr chunks into ring buffers, with a mark per call so errors are attributed to it.
- One session at a time: `run_project` with a session already live refuses with a message, rather than killing it silently as the reference does.
- `attach_project` / `detach_project` / `stop_project` / `get_debug_output`, and `list_projects` / `get_project_info`.

**Headless tools** (the 16: `create_scene`, `add_node`, `load_sprite`, `save_scene`, `export_mesh_library`, `batch_scene_operations`, the nine node tools, and `validate`):
- Each runs `godot --headless --path <p> --script <repo>/headless/operations.gd` with the request passed as a JSON file path and the result written to a JSON file. This avoids the command-line length limit and stdout scraping.
- `override.cfg` is never written for these, so a headless run loads only the project's own autoloads.

**Out of the first version:** the profiler tools, the autoload-editing tools, the pure file-parsing tools and `launch_editor`, because none has ever been called. In-engine recording, concurrent sessions and C#-aware tools also wait.

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
