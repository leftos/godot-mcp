# godot-mcp docs

The map. An MCP server (C#, .NET 10) and an in-game bridge (GDScript) that let agents run, see and drive the user's Godot projects, written to replace the third-party `godot-mcp-runtime`.

- [`plans/MAIN.md`](./plans/MAIN.md): open work, in order.
- [`DECISIONS.md`](./DECISIONS.md): the user's decisions and the Godot 4.7.2 facts behind them: why the server exists, the wire, gamepad input, the error feed.
- [`ARCHITECTURE.md`](./ARCHITECTURE.md): the components, a request's path, the session lifecycle, every tool and the recipe for a new one.
- [`DEVELOPMENT.md`](./DEVELOPMENT.md): toolchain, commands, gates, test coverage and footguns.
- [`csharp-runtime-tools.md`](./csharp-runtime-tools.md): the design record of the C# runtime tools and their helper: the options weighed, the spike, the decisions and the build steps.
- [`TOOLS.md`](./TOOLS.md): for an agent using the server: which tool fits a job, each tool's edges, and worked drives.
- [`../skills/godot-mcp/SKILL.md`](../skills/godot-mcp/SKILL.md): the tutorial skill agents in the game repos load: the drive loop, every tool by job, the rules that bite; it points at TOOLS.md for each tool's edges.

## Glossary

| Term | Meaning |
|---|---|
| Bridge | The GDScript autoload (`bridge/godot_mcp_bridge.gd`) that runs inside a launched game, dials the server and carries out its commands: screenshots, UI listing, input, scripts |
| Hidden desktop | A Windows desktop nobody looks at (`CreateDesktopW`): the server starts quiet runs on its own (`godot-mcp-<pid>`), and `run.ps1 itest` runs the integration tests on another (`tools/hidden-desktop.ps1`), so no Godot window reaches the user's screen |
| Injection | How the bridge gets into a run without touching the project: the server writes a marked `override.cfg` in the project folder naming the bridge as an autoload, hides it through `.git/info/exclude`, and deletes it when the run stops. Godot reads `override.cfg` for game runs only, never for the editor or `--import` |
| Marker | The first-line comment that identifies an `override.cfg` as the server's own; one without it is the user's and is never touched |
| Handshake | The bridge's first frame: the session token and the project path, which the server checks so a stale bridge from another run is refused |
| Capture | `capture_input`'s recording of a session's input, real and sent, as `simulate_input` events with gaps as waits; not the Movie Maker video, which is a recording |
| Gesture | A high-level input tool (click, drag, type_text, hold/release) that sends the right sequence of events over frames, as against the raw event list of `simulate_input` |
| Headless tool | A scene or node edit done by a one-off `godot --headless --script headless/operations.gd` run, with its request and result passed as JSON files: `create_scene`, `save_scene`, `delete_nodes`, `attach_script`, `duplicate_node`, `load_sprite`, `add_node`, `set_node_properties`, `get_node_properties`, `get_node_signals`, `connect_signal`, `disconnect_signal`, `export_mesh_library`, and `batch_scene_operations` (several of them in one run); `validate` and `get_scene_file_tree` run the same way |
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
| Version | The server's `<semver>+<7-char sha>` (`0.1.0+cae43a3`): the semver is `VersionPrefix` in `Directory.Build.props`, the sha is the commit it was built from; reported in the MCP handshake, on `run_project`/`restart_project`/`attach_project` results and in the install folder's `VERSION`, and quoted in every godot-mcp issue |
| Session, session name | One run or attached game the server drives, known by a name (by default its project folder's, fitted to the name rule; delve's "server" and "client-1" share one folder); a tool may omit the name while only one session exists. `list_sessions` lists them |
| Error feed | Engine and script errors (with file, line and Godot's stack) raised while a tool call ran, attached to that call's result as `errors` so no failure is silent; `get_errors` reads a session's errors and warnings from a cursor |
| Errors frame | The id-less `{type: "errors", entries, dropped}` frame the bridge sends every frame and before each reply, carrying what its `Logger` caught |
| Compact output | A result held to a size budget: a screenshot as a path or a 480 px preview, a long list or log paged with a total, long lines and values cut with their length |
| Quiet run | The default for `run_project`: the window created unfocused, then moved off-screen and made click-through, on the Dummy audio driver, so it takes no focus, shows nothing, plays nothing and receives no real input; `options.quiet: false` gives a normal window. Sessions on one folder share the setting; an attached game is quiet only with `attach_project`'s `quiet: true`, which parks and caps it but cannot hide it at start or silence it |
| Tool annotations | The MCP hints each tool declares (`readOnlyHint`, `destructiveHint`, `openWorldHint`) so a client's permission rules can allow the reads |
| Frame control | Pausing, stepping N frames and scaling time in a running game, so a screenshot or check lands on an exact frame (`frame_control`; `wait_for` waits for a node, a property, a signal or an expression) |
| Profile, preset | A project's `godot-mcp.json` beside `project.godot`: its launch defaults, and named launch presets that `run_project`'s `options.preset` layers over them |
| Batch drive | `batch_drive`: one tool call that plays a list of steps (runtime tools and assertions) against a running game and stops at the first failed step |
| Recording, mark, clip | A recording is a run launched with `options.record`, filmed by Godot's Movie Maker from launch to quit into one AVI; a mark (`record_mark` start or stop) notes a movie frame; a clip is the part between a start and its stop, encoded to an MP4 with ffmpeg when the run ends |
| Idle frames, dropIdle | Frames Movie Maker writes while nothing on screen changes, mostly the wall-clock time an agent spends between tool calls; `options.dropIdle` drops each frame identical to the one before from every clip |
| Headless run | A `godot --headless --script headless/operations.gd` run on a project's files, with no game started and the autoloads freed, that backs `validate` and `get_scene_file_tree` |
| Check-once wait | `wait_for` with `timeoutMs: 0`: the condition is checked once, now, even while the game is paused |
| Hang watchdog | The server's report, when a request times out, of whether the game's main thread is still running (it answers a ping) or stuck (it does not), with the stuck process's CPU, threads and last stderr lines, in place of a bare timeout |
| Load-adjusted time (the server's ceilings) | The time every server timeout and ceiling counts: sampled once a second, each second of wall time adds the share of the machine that work other than the server's own leaves free (at least 5%), so it keeps wall time on an idle machine and slows under load (`LoadClock`) |
| Backstop (the server's ceilings) | The wall-time limit behind every load-adjusted ceiling of the server, 5 x the ceiling, which ends a wait however loaded the machine is; its error says it was the backstop |
| Stall kill (tool processes) | The server's kill of a build, import, headless run or ffmpeg cut that has written no output and used no CPU in its process tree for 120 s, ahead of its ceiling |
| Release (a bridge request's) | The load-adjusted time after which the server sends `cancel` for a step, monitor, wait or C# call still running in the game; the request carries 5 x it as `backstopMs`, the bridge's own real-time limit |
| Baseline | A stored screenshot (and its crop) a new capture is compared against, giving the changed pixels, their bounding box and a diff image (`save_screenshot_baseline`, `compare_screenshot`) |
| Snapshot | A capture of a live subtree's nodes with their shown properties and groups, held by the session under an id (`snapshot_subtree`) for `diff_snapshots` to compare; not a screenshot baseline |
| uiChanged baseline | Not a screenshot baseline: the snapshot of the UI (visible Controls, focus owner, top popup) the bridge takes when the first input gesture since launch, or since the last met `wait_for {uiChanged}`, starts, which that wait compares with |
| gdtest | The headless GDScript unit-test run (`pwsh run.ps1 gdtest`, `tests/bridge`): many bridge-logic tests in one Godot process, as against an integration test's launch per test |
| Complexity baseline | `tools/gdcomplexity-baseline.txt`: the GDScript functions still allowed over the complexity or length limit, a list that can only shrink |
| Gate, ceiling | A gate is one command run by `tools/gate.ps1` with its own log; its ceiling is how long it may run on the load-adjusted clock before the gate kills it with every process it started and exits 124 (`gate: TIMED OUT`); the gate also kills a run that stalls or reaches the backstop |
| Stall | A gate run whose log has not grown and whose processes (with any MSBuild or compiler server started during the run) have used no CPU for `-StallSeconds`, 120 by default; the gate kills it as hung (`gate: STALLED`) |
| Load-adjusted clock | The clock a gate's ceiling counts on: each few seconds it advances by the share of the machine the run's own processes did not have to share with other work, so it keeps wall time on an idle machine and slows while other agents load it |
| Backstop | The gate's last-resort kill at five times the ceiling in wall time (`gate: BACKSTOP`); with a low "machine free" figure it means the machine was busy, and the run is re-run once alone |
| Slot | One of the `(logical processors - 1) / 4` places machine-wide a gate must hold to run (`GATE_SLOTS` overrides the count; a waiting gate logs `gate: waiting for a slot`); a gate inside another gate uses its parent's |
| Gates (CI) | The checks CI runs before it publishes anything: the warnings-as-errors build, the unit tests and `gdtest`, in `.github/actions/gates` |
| Package, release | The package is the release zip `run.ps1 package` builds (the published server, `skill/`, `VERSION`); a release is a GitHub Release a pushed `vX.Y.Z` tag makes, carrying the package and `install.ps1` |
| Parity | The first version covers every godot tool the user's projects call, so it replaces the old server everywhere at once |
| Spike | A throwaway build that proves or disproves the unverified steps of a design before any tool is built on it |
| Shim, C# helper | In the C#-runtime-tools design: the shim is a small native GDExtension the bridge loads at run time to reach Godot's .NET runtime; the helper is the managed assembly the shim loads into it, which reaches C# members Godot's call cannot |
| Handle | In the C#-runtime-tools design: an id for a C# object the helper keeps from an earlier result, so a later call can target it |
