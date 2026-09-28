# Main Plan
<!-- plan-doc-hygiene: 2026-09-28 17a2b30 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Wave 1: headless scene edits

Shared: `src/GodotMcp.Server/Tools/HeadlessTools.Scene.cs`, `HeadlessTools.Batch.cs`, `SceneBatchStep.cs` and the headless script they run. Gate: `pwsh run.ps1 test` and `pwsh run.ps1 itest` (the `scene` group); a human check reads the `.tscn` diff of a one-node edit.

- [ ] #24: a one-node `add_node` / `batch_scene_operations` / `attach_script` edit re-saves the whole `.tscn` in 4.7 form (`load_steps` dropped, `unique_id=` on every node, reordered properties); splice the changed sections into the original text, as decided in [DECISIONS.md](../DECISIONS.md) 15

### Wave 2: capture

Shared: the screenshot grabs in `bridge/godot_mcp_bridge.gd`, `godot_mcp_time.gd`, `godot_mcp_baseline.gd` and `godot_mcp_preview.gd`, and `src/GodotMcp.Server/Tools/RuntimeTools.cs` / `RuntimeTools.Time.cs`. Gate: `pwsh run.ps1 test`, `pwsh run.ps1 gdtest` and `pwsh run.ps1 itest` (the `reads` and `time` groups); a human check looks at the captured frames. Both items as decided in [DECISIONS.md](../DECISIONS.md) 15; the helper lands first.

- [ ] `take_screenshot` misses popups and tooltips in a project that sets `display/window/subwindows/embed_subwindows=false`, since each is its own OS window outside the root viewport's texture (measured 2026-09-27 for #9): one capture helper for every grab that composites each visible non-embedded `Window` at its offset from the root; check the offset on a visible desktop first, since a native popup's `position` read (0,0) on the hidden one
- [ ] #25: a `capture_frames` tool that captures at set game-time points (`at` or `{every, for}`) in one call, through the capture helper

### Release

- [ ] Cut a release (DEVELOPMENT.md's "CI and releases") once #23, #24, #25 and #26 have landed (user, 2026-09-28)

### Singles

- [ ] `BridgeListenerTests.ASilentConnectionIsRefusedOnceNoWaiterIsPending` failed once in a commit hook's unit run while three implementers loaded the machine, then passed on the retry: find what it times on wall clock
- [ ] #26: a `run_script` (and `call_method`) that times out keeps running in the game and corrupts the next run; stop it on timeout and restore `time_scale` and `paused`, as decided in [DECISIONS.md](../DECISIONS.md) 15
- [ ] `run_csharp`: give snippets a `CancellationToken` the timeout's cancel fires, so a timed-out snippet can stop itself (today its `Task` is forgotten and keeps running)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26); asked again 2026-09-27, still waiting on more use (user). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
