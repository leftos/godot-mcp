# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 45fcccb -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).


## Next

A track, then singles; the singles' order is not a ranking.

### Track: C# runtime tools

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26). Decided (user, 2026-09-26): design it now, as a proposal drafted before any build. Proposal and decisions: [csharp-runtime-tools.md](./csharp-runtime-tools.md) (a helper loaded at run time through a NativeAOT GDExtension shim; `cs_members`/`cs_get`/`cs_set`/`cs_call` and `run_csharp`); its spike proved all three unproven steps (2026-09-26, the proposal's §6); the build is planned as steps S1-S9 (the proposal's §7); S1 (projects, publish, install), S2 (the marshalling core), S4 (CsProbe additions) and S8a (the snippet compiler) landed 2026-09-26; S3 (the bridge loader, the helper's ping, the helper cache and the `csharp` itest group) landed too; next is S5 (`cs_members`)

### Singles

- [ ] Measure the `headless` itest group against its runner `--timeout 4m`: a full `itest` on 2026-09-26 ended it at 4m 00s with all 111 tests passed (exit 3), while two other trees built at once; if it is near the limit alone, split the group in `run.ps1`'s `$itestGroups`
- [ ] Carry DECISIONS.md 14 (games listen only to the pads players choose) into the shared Godot conventions (godot-conventions-sync) and a plan line in opening-hand and delve-the-dungeon; this server's part landed 2026-09-26
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
