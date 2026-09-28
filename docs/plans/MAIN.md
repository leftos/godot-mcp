# Main Plan
<!-- plan-doc-hygiene: 2026-09-27 5acd617 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

### Current: a faster test suite (user, 2026-09-28)

Measured 2026-09-28 on a loaded machine: unit 894 tests in 37 s, bounded by `ToolProcessTests` (56 s of summed test time, serial within its class); itest ~8.5 min fully serial, 452 s of test time in a 470 s run of four groups, so fixtures cost little and concurrency is the lever.

- [ ] Run the unit and integration tests as the built test programs in Release rather than through `dotnet test` in Debug (user's global rule, 2026-09-28): `run.ps1` `test`, `itest` and `-Filter` runs, the build step and the docs that name `bin/Debug`
- [ ] Run the itest groups 2–3 at a time in `run.ps1`, timing-sensitive groups kept apart; the Godot-launch-under-load flake below may have to be fixed first

## Next

The singles' order is not a ranking.

### Singles

- [ ] `take_screenshot` misses popups and tooltips in a project that sets `display/window/subwindows/embed_subwindows=false`, since each is its own OS window outside the root viewport's texture (measured 2026-09-27 for #9): composite each visible non-embedded `Window` onto the capture at its offset from the root, through one capture helper so crops, baselines and frame steps inherit it; check the offset on a visible desktop first, since a native popup's `position` read (0,0) on the hidden one (user, 2026-09-27: build later)
- [ ] A Godot launch in the itests died once with exit 0xC06D007F (a delay-load failure) before the bridge connected (`CaptureTests.SentPadInputIsCaptured`, full `itest` 2026-09-26, beside two other trees' itests; passed on the rerun): find what fails to load if it recurs. Recurred in kind 2026-09-27: a `TimeTests` run beside another tree's `HeadlessTests` failed all 38 at the fixture's launch ("the bridge did not connect within 15 s", no stderr, no Godot process ever seen by a 10 s sampler), while the same branch passed alone and on a quiet machine; the same load also gave a `WaitForNodeExists` flake and a test host outliving the 300 s ceiling, and `pwsh run.ps1 format` passing its 180 s ceiling with an empty log as the first command in a cold worktree (it passed once the tree was built)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26); asked again 2026-09-27, still waiting on more use (user). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
