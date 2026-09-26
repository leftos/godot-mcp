# Main Plan

Open work only, in working order: the next item is the first line from the top. The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

- [x] Step 0: scaffold (solution, server and test projects, the GDScript bridge folder, the gates, the InputProbe fixture project)
- [x] Step 1: wire and lifecycle (listener, handshake, override.cfg inject/clean/refuse, .git/info/exclude; run_project with pass-through arguments, stop_project, get_debug_output)
- [x] Gate gaps from step 1: wall-clock ceilings (`tools/gate.ps1`), complexity ≤ 8 enforced (CA1502), the handshake's refusal cases tested, GitExclude through `--show-prefix`
- [x] Step 2: runtime reads (take_screenshot with crop and preview, get_ui_elements, run_script with diagnostics)
- [x] Step 3: input (click, drag, type_text, key/button hold and release, raw simulate_input; viewport-to-window mapping); the fixture drag is the headline test
- [x] Step 4: attach_project and detach_project, plus input hardening from step 3: real mouse events are swallowed while injected input plays or holds a button, input calls are serialised, the input tools report handler errors raised during a gesture (as run_script does), and unit tests for the server-side input validation
- [ ] Step 4b: gamepad input (user, 2026-09-25: "make sure you can also simulate gamepad input"): buttons (press, release, tap), sticks and triggers (axis values held or swept over frames), per joypad device, raw events in `simulate_input`. To settle first from Godot 4.7.2's source: whether an injected `InputEventJoypadButton`/`InputEventJoypadMotion` updates `Input.is_joy_button_pressed`/`get_joy_axis`, and whether a game that checks `Input.get_connected_joypads()` sees an injected pad as connected (and if not, what can make it so). Fixture: a node that reads buttons, axes and input actions bound to the pad, and a UI focus move by d-pad. Built after step 4, whose implementer holds the bridge's input code now

Sixteen features the user added 2026-09-25 ("world's our oyster"), all before the cutover (user's call). The structural ones come first, because they change every tool's signature or result:
- [ ] Step 7: several sessions, keyed by a name (one per worktree or agent; delve's server plus two clients at once); every runtime tool takes the session name, defaulting to the only one. Absorbs the "several sessions" item from Later
- [ ] Step 8: error feed on every result (engine and script errors with the stack Godot gives, raised during any call, attached to its result; `get_errors(since)`), plus compact outputs (a size budget per result: screenshots default to path only or a small preview, long lists and logs truncated with a count and paging)
- [ ] Step 9: tool annotations (MCP `readOnlyHint` / `destructiveHint` on every tool, so permission rules can allow the reads), plus quiet by default (agent runs start in background mode with audio muted unless asked; real input ignored)
- [ ] Step 10: time: frame control (pause, resume, step N frames, time_scale, screenshot at a frame) and `wait_for` (a node exists, a property equals, a signal fires, an expression is true; with a timeout)
- [ ] Step 11: the edit-build-look loop: `restart_project` (rebuild C# when sources changed, relaunch with the same scene and arguments) and fresh-worktree prep (a missing `.godot/` import cache or C# build is made before launch, under ceilings)
- [ ] Step 12: inspection: the running game's scene tree (filtered by path, class, group), `inspect_node`, `set_property`, and `call_method` (JSON arguments; reaches C# public methods through Godot's call, e.g. `ScratchScene.PlayStep`)
- [ ] Step 13: project profiles (`godot-mcp.json` per project: main scene, arguments, resolution, background, named launch presets such as delve's server and clients) and the batch drive tool (one call runs input, wait_for, call_method, assertions and screenshots in order, stopping at the first failed assertion)
- [ ] Step 14: hang watchdog (a bridge that stops answering is reported as a stuck main thread with the process state and last stderr lines; stop always force-kills) and screenshot baselines (compare a screenshot or crop to a stored baseline; a difference score and a diff image)
- [ ] Step 15: in-engine recording (video and audio from inside the engine, frame-perfect, headless or hidden). Absorbs the "in-engine recording" item from Later
- [ ] Step 5: the 16 headless scene and node tools and validate
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method` (members Godot's call cannot reach, such as `internal` ones)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
