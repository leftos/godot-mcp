# Main Plan
<!-- plan-doc-hygiene: 2026-09-25 d164264 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b, Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) and most of Wave 2 (step 10 frame control and `wait_for`, step 12 inspection, step 14's hang watchdog) have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 3: the run's lifecycle (`GodotRun`, `GodotCommandLine`, `GodotSession`, `ProjectTools`), then the tools built on it

- [ ] Step 11: the edit-build-look loop: `restart_project` (rebuild C# when sources changed, relaunch with the same scene and arguments) and fresh-worktree prep (a missing `.godot/` import cache or C# build is made before launch, under ceilings). Reuses step 12's `CsProbe` C# fixture. Its design is in the plan's decision 11 and the explorer's 4.7.2 citations in `.tmp/explore-wave3/`
- [ ] Step 14: screenshot baselines (compare a screenshot or crop to a stored baseline; a difference score and a diff image). Independent of step 11; it reuses step 10's split screenshot capture (`_save_screenshot`, `_crop`)
- [ ] Step 13: project profiles (`godot-mcp.json` per project: main scene, arguments, resolution, background, named launch presets such as delve's server and clients) and the batch drive tool (one call runs input, wait_for, call_method, assertions and screenshots in order, stopping at the first failed assertion). Depends on step 11; its screenshot-baseline assertion on step 14

### Wave 4: singles

- [ ] Bring `godot_mcp_bridge.gd`'s four complexity-baseline entries within the limits (`_input` 10, `_handle_frame` 12, `_play_event` 9, `_to_json` 12, measured 2026-09-26) and empty `tools/gdcomplexity-baseline.txt`; `_to_json` goes with the JSON move below
- [ ] Before step 5: move the bridge's `_to_json` and the `Inspect` module's `from_json` into a static `bridge/godot_mcp_json.gd` with gdtest unit tests, so step 5's headless tools share one set of conversion rules
- [ ] `tools/gate.ps1`'s comment-based help does not render (`Get-Help` shows no DESCRIPTION): a `#requires` line touches `<#`, as the shebang did in `run.ps1`; separate them with a blank line


- [ ] Step 15: in-engine recording (video and audio from inside the engine, frame-perfect, headless or hidden). Absorbs the "in-engine recording" item from Later
- [ ] Step 5: the 16 headless scene and node tools and validate, all 16 (user, 2026-09-26), in three briefs after step 11 (it needs `ToolProcess` and prep) and step 12 (it shares the JSON-to-Variant rules): (a) the process runner, `headless/operations.gd` core, `validate` and `get_scene_file_tree` (renamed: step 12's `get_scene_tree` reads the running game); (b) the scene and property tools; (c) the signal tools, `export_mesh_library`, `batch_scene_operations`. Settled: a save restores the scene's header UID (`ResourceSaver.set_uid`; a `--script` save strips it); project autoloads are freed in `_initialize` and a headless run is refused while a session is live on the folder (a `--script` run reads `override.cfg`); prep runs first, and a save whose scene has an unbuilt C# script is refused; node paths are relative to the scene root; the signal tools take `target {nodePath, method}`; results come back in a JSON file, 60 s ceiling (120 s for batch); `create_scene` and `save_scene`'s `newPath` refuse an existing file without `options.overwrite`. The explorer's 4.7.2 citations are in `.tmp/explore-step5/`
- [ ] Agent-friendly capability docs in this repo: what every tool does, when to reach for it, and worked drives (user, 2026-09-26); after step 5, so it covers every tool
- [ ] A godot MCP tutorial for agents in opening-hand and delve-the-dungeon, explaining every available tool, written with the cutover (step 6) (user, 2026-09-26)
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
