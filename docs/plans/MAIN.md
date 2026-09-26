# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 c51c22b -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b, Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) and most of Wave 2 (step 10 frame control and `wait_for`, step 12 inspection, step 14's hang watchdog), step 14's screenshot baselines, step 11's prep and `restart_project`, Wave 3 (step 13: project profiles and `batch_drive`) step 15's recording (Movie Maker from launch, `record_mark` clips) and step 5's first brief (`validate`, `get_scene_file_tree`) have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 4: singles

- [ ] Step 5: the 16 headless scene and node tools and validate, all 16 (user, 2026-09-26), in three briefs; (a) has shipped (the headless runner, `headless/operations.gd`, `validate`, `get_scene_file_tree`; ARCHITECTURE's Headless row). Left: (b) the scene and property tools, on `HeadlessRunner` and `operations.gd`, loading `bridge/godot_mcp_json.gd` by path (`_object_to_json` calls `Node.get_path()`, which errors off-tree, and a `Resource` becomes `{class, string}` with no path: both need a headless answer); (c) the signal tools, `export_mesh_library`, `batch_scene_operations`. Settled: a save restores the scene's header UID (`ResourceSaver.set_uid`; a `--script` save strips it); project autoloads are freed in `_initialize` and a headless run is refused while a session is live on the folder (a `--script` run reads `override.cfg`); prep runs first, and a save whose scene has an unbuilt C# script is refused; node paths are relative to the scene root; the signal tools take `target {nodePath, method}`; results come back in a JSON file, 60 s ceiling (120 s for batch); `create_scene` and `save_scene`'s `newPath` refuse an existing file without `options.overwrite`. The explorer's 4.7.2 citations are in `.tmp/explore-step5/`
- [ ] Agent-friendly capability docs in this repo: what every tool does, when to reach for it, and worked drives (user, 2026-09-26); after step 5, so it covers every tool
- [ ] A godot MCP tutorial for agents in opening-hand and delve-the-dungeon, explaining every available tool, written with the cutover (step 6) (user, 2026-09-26)
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
