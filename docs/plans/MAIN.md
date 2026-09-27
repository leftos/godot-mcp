# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 917722f -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

Steps 0 to 4b, Wave 1 (steps 7-9: named sessions, the error feed and compact outputs, tool annotations and quiet runs) and most of Wave 2 (step 10 frame control and `wait_for`, step 12 inspection, step 14's hang watchdog), step 14's screenshot baselines, step 11's prep and `restart_project`, Wave 3 (step 13: project profiles and `batch_drive`) step 15's recording (Movie Maker from launch, `record_mark` clips) and step 5 (the 16 headless scene tools, `validate` and `batch_scene_operations`) have shipped. Sixteen features the user added 2026-09-25 ("world's our oyster") come before the cutover (user's call). Every wave's command acceptance is `pwsh run.ps1 test` and `pwsh run.ps1 itest`; its human check is a drive of the InputProbe fixture through the new tools.

### Wave 4: singles

- [ ] A godot MCP tutorial for agents in opening-hand and delve-the-dungeon, explaining every available tool, written with the cutover (step 6) (user, 2026-09-26). Form (user, 2026-09-26): a skill shipped in this repo, updated in the same commit as `docs/TOOLS.md`, installed into `~/.claude/skills` and named by both repos' CLAUDE.md
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean). Decided (user, 2026-09-26): registered per project with `claude mcp add --scope local godot`, pointing at an installed copy of `bin/publish` that a new `pwsh run.ps1 install` refreshes, so a publish never fights a running server; the `no-mcp-bridge-at-commit` rule becomes a leftover check (no marked `override.cfg` in the client folder after a drive, checked with `Test-Path`), synced through `godot-conventions-sync`; the user-level `debugger` and opening-hand's `playtester` get every runtime tool and `validate`, no headless scene editor; the tutorial skill reaches `~/.claude/skills/godot-mcp` as a directory junction to this repo's copy. Settled from defaults: re-publish before registering (`bin/publish` predates `headless/`); remove the old server's `.mcp/` ignores and folders; delve's launch-then-attach and input workarounds give way to `userArgs`, `click` and `type_text`; per-game drive lessons stay in each repo's DEVELOPMENT.md, the skill stays generic

## Later (not in the first version)

Grouped into waves by shared files after the cutover; within the singles, order is not a ranking.

### Wave 5: JSON conversion

Shared: `bridge/godot_mcp_json.gd`, `bridge/godot_mcp_inspect.gd`, `tests/bridge`, `run.ps1` (`gdtest`). Acceptance: `pwsh run.ps1 gdtest`, `pwsh run.ps1 itest`; human check: `set_node_properties` and `set_property` on an exported `Array[int]` in the fixture.

- [ ] Typed-array and dictionary exports in `set_node_properties` (found 2026-09-26 landing the save guard): an exported `Array[int]` carries `PROPERTY_HINT_TYPE_STRING`, not `PROPERTY_HINT_ARRAY_TYPE` (4.7.2 `gdscript_parser.cpp` L4977-4985), so `_array_from_json` passes it untyped and it probably fails the read-back as `Array[Node2D]` did (unmeasured); an Object-typed Dictionary is refused with no reason; the running game's `set_property` and `call_method` (`bridge/godot_mcp_inspect.gd`) ignore the refusal reason `from_json` now returns. Decided (user, 2026-09-26): Resource element types stay refused, the reason naming the class
- [ ] `pwsh run.ps1 gdtest` imports `tests/bridge` first, so a test can cover `from_json` finding a script class through the project's global class list (brief 5b-A, 2026-09-26). Decided (user, 2026-09-26): the import's `.uid` files beside `tests/bridge`'s scripts are committed. Waits on the prek-hang finding, since the import runs inside the `gdtest` hook

### Wave 6: ideas from a peer server

Shared: `bridge/` (new inspect and input handlers), `headless/operations.gd`, the server's tool classes. Acceptance: `pwsh run.ps1 test`, `pwsh run.ps1 itest`; human check: a drive of the InputProbe fixture through each new tool.

- [ ] Ideas from hybridindie/godot-mcp (MIT; surveyed 2026-09-26, the user chose all four, after the cutover), each reimplemented rather than copied:
  - [ ] `describe_class`: a class's properties with defaults, methods with typed arguments, signals and enums from ClassDB, and for an unknown name the closest script classes from `ProjectSettings.get_global_class_list()`; headless and in the bridge (theirs: `godot/addons/godot_mcp/handlers/class_info.gd`)
  - [ ] Snapshot and diff: snapshot a subtree (runtime or scene file), act, then diff to `{added, removed, changed: [{node, property, before, after}]}`; server-side, with a small LRU of snapshot ids (theirs: `mcp_server/snapshots.py`)
  - [ ] `monitor_property` (N per-frame samples, changes only, `dropped_duplicates`) and `simulate_action` (inject an InputMap action) (theirs: `mcp_runtime_probe.gd` L392-435)
  - [ ] Input recording in the replay format, and a seeded random-input stress test returning `{survived, seed}` (theirs: `mcp_runtime_probe.gd` L481-520, `docs/tool-contracts.md` L1199-1207)

### Singles

- [ ] `drag` and `click` results say what they hit: the Control under the press and the release, and whether a GUI drag started (#4)
- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see step 4b)
- [ ] A `git commit` in a worktree (`../godot-mcp.wt/mesh-library`, 2026-09-26) hung after every prek hook passed: `prek` stayed alive with no children, and after `dotnet build-server shutdown` it exited but the hook's `sh` did not; the cause is unknown (not the MSBuild nodes alone). Reproduce and fix, or record the footgun
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. The test window flash is the first candidate. Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
