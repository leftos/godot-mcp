# Main Plan
<!-- plan-doc-hygiene: 2026-09-25 d164264 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b, Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) and most of Wave 2 (step 10 frame control and `wait_for`, step 12 inspection, step 14's hang watchdog), step 14's screenshot baselines, step 11's prep and `restart_project`, Wave 3 (step 13: project profiles and `batch_drive`) and step 15's recording (Movie Maker from launch, `record_mark` clips) have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 4: singles

- [ ] Step 5: the 16 headless scene and node tools and validate, all 16 (user, 2026-09-26), in three briefs after step 11 (it needs `ToolProcess` and prep) and step 12 (it shares the JSON-to-Variant rules): (a) the process runner, `headless/operations.gd` core, `validate` and `get_scene_file_tree` (renamed: step 12's `get_scene_tree` reads the running game); (b) the scene and property tools; (c) the signal tools, `export_mesh_library`, `batch_scene_operations`. Settled: a save restores the scene's header UID (`ResourceSaver.set_uid`; a `--script` save strips it); project autoloads are freed in `_initialize` and a headless run is refused while a session is live on the folder (a `--script` run reads `override.cfg`); prep runs first, and a save whose scene has an unbuilt C# script is refused; node paths are relative to the scene root; the signal tools take `target {nodePath, method}`; results come back in a JSON file, 60 s ceiling (120 s for batch); `create_scene` and `save_scene`'s `newPath` refuse an existing file without `options.overwrite`. For (a) (user, 2026-09-26): `validate` with no targets sweeps every versioned `.gd`, `.tscn` and `.tres` (at most 500; 1-50 named targets otherwise, no inline source), reports C# from prep's build as `csharp: {errors}` and still checks GDScript; prep also imports when a `class_name` script is newer than `.godot/global_script_class_cache.cfg` (a `--script` run reads class names only from it); `get_scene_file_tree` reads `PackedScene.get_state()` without instantiating, sub-scenes expanded, paged like the runtime `get_scene_tree`; the runner holds the prep lock through the Godot run. The explorer's 4.7.2 citations are in `.tmp/explore-step5/`
- [ ] Agent-friendly capability docs in this repo: what every tool does, when to reach for it, and worked drives (user, 2026-09-26); after step 5, so it covers every tool
- [ ] A godot MCP tutorial for agents in opening-hand and delve-the-dungeon, explaining every available tool, written with the cutover (step 6) (user, 2026-09-26)
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
