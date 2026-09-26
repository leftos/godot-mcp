# godot-mcp docs

The map. An MCP server (C#, .NET 10) and an in-game bridge (GDScript) that let agents run, see and drive the user's Godot projects, written to replace the third-party `godot-mcp-runtime`.

- [`plans/MAIN.md`](./plans/MAIN.md): open work, in order.
- [`plans/2026-09-25-first-version.md`](./plans/2026-09-25-first-version.md): the first version's design, the user's decisions and the steps.
- [`ARCHITECTURE.md`](./ARCHITECTURE.md): the components, a request's path, the session lifecycle, every tool and the recipe for a new one.
- [`DEVELOPMENT.md`](./DEVELOPMENT.md): toolchain, commands, gates, test coverage and footguns.

## Glossary

| Term | Meaning |
|---|---|
| Bridge | The GDScript autoload (`bridge/godot_mcp_bridge.gd`) that runs inside a launched game, dials the server and carries out its commands: screenshots, UI listing, input, scripts |
| Hidden desktop | A Windows desktop nobody looks at (`CreateDesktopW`): the server starts quiet runs on its own (`godot-mcp-<pid>`), so their windows never reach the user's screen |
| Injection | How the bridge gets into a run without touching the project: the server writes a marked `override.cfg` in the project folder naming the bridge as an autoload, hides it through `.git/info/exclude`, and deletes it when the run stops. Godot reads `override.cfg` for game runs only, never for the editor or `--import` |
| Marker | The first-line comment that identifies an `override.cfg` as the server's own; one without it is the user's and is never touched |
| Handshake | The bridge's first frame: the session token and the project path, which the server checks so a stale bridge from another run is refused |
| Gesture | A high-level input tool (click, drag, type_text, hold/release) that sends the right sequence of events over frames, as against the raw event list of `simulate_input` |
| Headless tool | A scene or node edit done by a one-off `godot --headless --script headless/operations.gd` run, with its request and result passed as JSON files: `create_scene`, `save_scene`, `delete_nodes`, `attach_script`, `duplicate_node`, `load_sprite`, `add_node`, `set_node_properties`, `get_node_properties`, `get_node_signals`, `connect_signal`, `disconnect_signal`, `export_mesh_library`; `batch_scene_operations` is next |
| Inherited connection | A signal connection a scene gets from a scene it instances or inherits (flag 32, `CONNECT_INHERITED`); it can only be removed in the scene that makes it |
| Editable instance | An instanced scene marked "Editable Children" in its parent scene (`[editable path=…]`): changes to its inner nodes save as overrides, so the property tools allow them |
| Edit state | How `PackedScene.instantiate` builds a scene: `GEN_EDIT_STATE_MAIN`, as the editor opens one, keeps an inherited scene inherited and instances as instances, where the default flattens them into the saved file |
| Inherited scene | A scene made from another (its base) whose root is an instance of the base; it stores only its changes, so a node it gets from the base cannot be deleted in it |
| InputProbe | The fixture Godot project the integration tests launch (`tests/fixtures/InputProbe`): a label, a button, a red square, a drag source, a drop target, a LineEdit and a 12x12 button at known places, at a 640x360 base size stretched `canvas_items`/`keep`, so launching it with `--resolution 1000x900` letterboxes it. For gamepads it has the actions `probe_jump` (A) and `probe_right` (left stick right), a `PadProbe` counting jumps, and a `Menu` column of three buttons with focus on the first. `Main.probe_push_error()` pushes an error from the fixture's own script |
| CsProbe | The C# fixture project (`tests/fixtures/CsProbe`, `Godot.NET.Sdk/4.7.2`, net8.0) whose node has a public `PlayStep` and an `internal` `Secret`, built by the tests to prove `call_method` on C# |
| Attached session, attach file | A session on a game `run_project` did not start: `attach_project` writes the injection `override.cfg` and a one-use `<project>/.godot/godot-mcp/attach.json` ({port, token}) and waits for a game launched after it to dial in; `detach_project` ends it and leaves the game running. It has no captured output |
| Shut out (real gamepads) | The opt-in `shutOutRealGamepads` mode that keeps a machine's real pads from reaching a run, by marking the game unfocused; the bridge half is `bridge/godot_mcp_gamepad.gd` |
| Injected mark | The `InputEvent.device` value `0x6D6370` every injected mouse event carries, so the bridge can swallow the real mouse while a gesture plays |
| Touch twin | The `InputEventScreenTouch`/`ScreenDrag` Godot makes of each left-button mouse event when `emulate_touch_from_mouse` is on (device -1); the bridge swallows real ones while a gesture plays, as it does the real mouse |
| Prep | What a launch does first to make a fresh checkout runnable: build a missing or stale C# assembly and run a Godot import when imported files are missing (`run_project`'s `options.prepare`, `auto` by default, `never` to skip) |
| Session, session name | One run or attached game the server drives, known by a name (by default its project folder's; delve's "server" and "client-1" share one folder); a tool may omit the name while only one session exists. `list_sessions` lists them |
| Error feed | Engine and script errors (with file, line and Godot's stack) raised while a tool call ran, attached to that call's result as `errors` so no failure is silent; `get_errors` reads a session's errors and warnings from a cursor |
| Errors frame | The id-less `{type: "errors", entries, dropped}` frame the bridge sends every frame and before each reply, carrying what its `Logger` caught |
| Compact output | A result held to a size budget: a screenshot as a path or a 480 px preview, a long list or log paged with a total, long lines and values cut with their length |
| Quiet run | The default for `run_project`: the window created unfocused, then moved off-screen and made click-through, on the Dummy audio driver, so it takes no focus, shows nothing, plays nothing and receives no real input; `options.quiet: false` gives a normal window. Sessions on one folder share the setting; attached games are never quiet |
| Tool annotations | The MCP hints each tool declares (`readOnlyHint`, `destructiveHint`, `openWorldHint`) so a client's permission rules can allow the reads |
| Frame control | Pausing, stepping N frames and scaling time in a running game, so a screenshot or check lands on an exact frame (`frame_control`; `wait_for` waits for a node, a property, a signal or an expression) |
| Profile, preset | A project's `godot-mcp.json` beside `project.godot`: its launch defaults, and named launch presets that `run_project`'s `options.preset` layers over them |
| Batch drive | `batch_drive`: one tool call that plays a list of steps (runtime tools and assertions) against a running game and stops at the first failed step |
| Recording, mark, clip | A recording is a run launched with `options.record`, filmed by Godot's Movie Maker from launch to quit into one AVI; a mark (`record_mark` start or stop) notes a movie frame; a clip is the part between a start and its stop, cut out with ffmpeg when the run ends |
| Headless run | A `godot --headless --script headless/operations.gd` run on a project's files, with no game started and the autoloads freed, that backs `validate` and `get_scene_file_tree` |
| Check-once wait | `wait_for` with `timeoutMs: 0`: the condition is checked once, now, even while the game is paused |
| Hang watchdog | The server's report, when a request times out, of whether the game's main thread is still running (it answers a ping) or stuck (it does not), with the stuck process's CPU, threads and last stderr lines, in place of a bare timeout |
| Baseline | A stored screenshot (and its crop) a new capture is compared against, giving the changed pixels, their bounding box and a diff image (`save_screenshot_baseline`, `compare_screenshot`) |
| gdtest | The headless GDScript unit-test run (`pwsh run.ps1 gdtest`, `tests/bridge`): many bridge-logic tests in one Godot process, as against an integration test's launch per test |
| Complexity baseline | `tools/gdcomplexity-baseline.txt`: the GDScript functions still allowed over the complexity or length limit, a list that can only shrink |
| Gate, ceiling | A gate is one command run by `tools/gate.ps1` with its own log; its ceiling is the wall-clock limit after which the gate kills the process tree and exits 124 |
| Parity | The first version covers every godot tool the user's projects call, so it replaces the old server everywhere at once |
