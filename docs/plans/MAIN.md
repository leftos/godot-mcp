# Main Plan
<!-- plan-doc-hygiene: 2026-09-25 d164264 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b and Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 2: driving the running game (new `RuntimeTools` partials and bridge handlers)

- [ ] Step 10: time: frame control (pause, resume, step N frames, time_scale, screenshot at a frame) and `wait_for` (a node exists, a property equals, a signal fires, an expression is true; with a timeout)
- [ ] Step 12: inspection: the running game's scene tree (filtered by path, class, group), `inspect_node`, `set_property`, and `call_method` (JSON arguments; reaches C# public methods through Godot's call, e.g. `ScratchScene.PlayStep`)
- [ ] Step 13: project profiles (`godot-mcp.json` per project: main scene, arguments, resolution, background, named launch presets such as delve's server and clients) and the batch drive tool (one call runs input, wait_for, call_method, assertions and screenshots in order, stopping at the first failed assertion). Depends on steps 10, 11 and 12; its screenshot-baseline assertion on step 14

### Wave 3: the run's lifecycle (`GodotRun`, `GodotCommandLine`, `GodotSession`, `ProjectTools`)

- [ ] Step 11: the edit-build-look loop: `restart_project` (rebuild C# when sources changed, relaunch with the same scene and arguments) and fresh-worktree prep (a missing `.godot/` import cache or C# build is made before launch, under ceilings). Depends on step 12 (its `CsProbe` C# fixture)
- [ ] Step 14: screenshot baselines (compare a screenshot or crop to a stored baseline; a difference score and a diff image)

### Wave 4: singles

- [ ] Measure the full `pwsh run.ps1 itest` on a quiet machine: it took 3 m 30 s for 58 tests with three implementers running Godot at once (2026-09-26), against MTP's 4 min timeout and the gate's 300 s ceiling (95 s for 53 tests on 2026-09-25); if the suite itself has grown past the ceiling, ask the user how to split or speed it up rather than raising the ceiling

- [ ] Step 15: in-engine recording (video and audio from inside the engine, frame-perfect, headless or hidden). Absorbs the "in-engine recording" item from Later
- [ ] Step 5: the 16 headless scene and node tools and validate
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method` (members Godot's call cannot reach, such as `internal` ones)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
