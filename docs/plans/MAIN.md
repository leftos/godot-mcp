# Main Plan
<!-- plan-doc-hygiene: 2026-09-25 d164264 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 1: the tool surface (every tool in `Tools/`, `GodotSession`, the bridge's `_handle_frame`)

These change every tool's signature or result, so they run one after another, before the rest.

- [ ] Step 7: several sessions, keyed by a name (one per worktree or agent; delve's server plus two clients at once); every runtime tool takes the session name, defaulting to the only one. Absorbs the "several sessions" item from Later
- [ ] Step 8: error feed on every result (engine and script errors with the stack Godot gives, raised during any call, attached to its result; `get_errors(since)`), plus compact outputs (a size budget per result: screenshots default to path only or a small preview, long lists and logs truncated with a count and paging)
- [ ] Step 9: tool annotations (MCP `readOnlyHint` / `destructiveHint` on every tool, so permission rules can allow the reads), plus quiet by default (agent runs start in background mode with audio muted unless asked; real input ignored)

### Wave 2: driving the running game (new `RuntimeTools` partials and bridge handlers)

- [ ] Step 10: time: frame control (pause, resume, step N frames, time_scale, screenshot at a frame) and `wait_for` (a node exists, a property equals, a signal fires, an expression is true; with a timeout)
- [ ] Step 12: inspection: the running game's scene tree (filtered by path, class, group), `inspect_node`, `set_property`, and `call_method` (JSON arguments; reaches C# public methods through Godot's call, e.g. `ScratchScene.PlayStep`)
- [ ] Step 13: project profiles (`godot-mcp.json` per project: main scene, arguments, resolution, background, named launch presets such as delve's server and clients) and the batch drive tool (one call runs input, wait_for, call_method, assertions and screenshots in order, stopping at the first failed assertion). Depends on steps 10 and 12

### Wave 3: the run's lifecycle (`GodotRun`, `GodotCommandLine`, `GodotSession`, `ProjectTools`)

- [ ] Step 11: the edit-build-look loop: `restart_project` (rebuild C# when sources changed, relaunch with the same scene and arguments) and fresh-worktree prep (a missing `.godot/` import cache or C# build is made before launch, under ceilings)
- [ ] Step 14: hang watchdog (a bridge that stops answering is reported as a stuck main thread with the process state and last stderr lines; stop always force-kills) and screenshot baselines (compare a screenshot or crop to a stored baseline; a difference score and a diff image)

### Wave 4: singles

- [ ] Step 15: in-engine recording (video and audio from inside the engine, frame-perfect, headless or hidden). Absorbs the "in-engine recording" item from Later
- [ ] Step 5: the 16 headless scene and node tools and validate
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method` (members Godot's call cannot reach, such as `internal` ones)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
