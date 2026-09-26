# Main Plan
<!-- plan-doc-hygiene: 2026-09-25 d164264 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b, Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) and most of Wave 2 (step 10 frame control and `wait_for`, step 12 inspection, step 14's hang watchdog), step 14's screenshot baselines and step 11's prep and `restart_project` have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 3: the run's lifecycle (`GodotRun`, `GodotCommandLine`, `GodotSession`, `ProjectTools`), then the tools built on it

- [ ] Step 13: project profiles (`godot-mcp.json` per project: main scene, arguments, resolution, background, named launch presets such as delve's server and clients) and the batch drive tool (one call runs input, wait_for, call_method, assertions and screenshots in order, stopping at the first failed assertion). Builds on step 11's stored `LaunchRequest` and prep; its screenshot-baseline assertion uses step 14's `compare_screenshot` (`match`, the stored crop)

### Wave 4: singles

- [ ] `godot_mcp_json.gd` `_dictionary_from_json` builds a typed Dictionary whose key or value type is an Object (hint `"int;Node"`), which the property set then refuses; refuse it up front as `_array_from_json` does for an Object element, with a gdtest (review of the JSON move, 2026-09-26)
- [ ] Measure whether prep's `dotnet build` fails on a locked assembly while another session's game on the same folder has it loaded (delve's server and clients); Godot loads assemblies into memory (`PluginLoadContext.cs` L59-69), so it should not, but it is unverified. If it does, refuse the build as the import is refused, with a test (step 11 review, 2026-09-26)
- [ ] The bridge's `_save_preview` enlarges an image narrower than `max_width` when called directly; only `_save_screenshot` guards against it. Move the guard into `_save_preview`, with a gdtest (step 14 report, 2026-09-26)
- [ ] `SessionRegistryTests`' still-starting restart test needs Godot at `GODOT_PATH` or the default path (it parks a launch on the prep lock after `FindGodot`), the only unit test that does; make it Godot-free, e.g. by taking the lock before the Godot lookup or faking the lookup (step 11b report, 2026-09-26)

- [ ] Step 15: in-engine recording (video and audio from inside the engine, frame-perfect, headless or hidden). Absorbs the "in-engine recording" item from Later
- [ ] Step 5: the 16 headless scene and node tools and validate, all 16 (user, 2026-09-26), in three briefs after step 11 (it needs `ToolProcess` and prep) and step 12 (it shares the JSON-to-Variant rules): (a) the process runner, `headless/operations.gd` core, `validate` and `get_scene_file_tree` (renamed: step 12's `get_scene_tree` reads the running game); (b) the scene and property tools; (c) the signal tools, `export_mesh_library`, `batch_scene_operations`. Settled: a save restores the scene's header UID (`ResourceSaver.set_uid`; a `--script` save strips it); project autoloads are freed in `_initialize` and a headless run is refused while a session is live on the folder (a `--script` run reads `override.cfg`); prep runs first, and a save whose scene has an unbuilt C# script is refused; node paths are relative to the scene root; the signal tools take `target {nodePath, method}`; results come back in a JSON file, 60 s ceiling (120 s for batch); `create_scene` and `save_scene`'s `newPath` refuse an existing file without `options.overwrite`. The explorer's 4.7.2 citations are in `.tmp/explore-step5/`
- [ ] Agent-friendly capability docs in this repo: what every tool does, when to reach for it, and worked drives (user, 2026-09-26); after step 5, so it covers every tool
- [ ] A godot MCP tutorial for agents in opening-hand and delve-the-dungeon, explaining every available tool, written with the cutover (step 6) (user, 2026-09-26)
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
