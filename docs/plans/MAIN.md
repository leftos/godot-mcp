# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 917722f -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).


## Next

Grouped into waves by shared files; within the singles, order is not a ranking.

### Wave 6: ideas from a peer server

Shared: `bridge/` (new inspect and input handlers), `headless/operations.gd`, the server's tool classes. Acceptance: `pwsh run.ps1 test`, `pwsh run.ps1 itest`; human check: a drive of the InputProbe fixture through each new tool.

- [ ] Ideas from hybridindie/godot-mcp (MIT; surveyed 2026-09-26, the user chose all four, after the cutover), each reimplemented rather than copied:
  - [ ] `describe_class`: a class's properties with defaults, methods with typed arguments, signals and enums from ClassDB, and for an unknown name the closest script classes from `ProjectSettings.get_global_class_list()`; headless and in the bridge (theirs: `godot/addons/godot_mcp/handlers/class_info.gd`)
  - [ ] Snapshot and diff: snapshot a subtree (runtime or scene file), act, then diff to `{added, removed, changed: [{node, property, before, after}]}`; server-side, with a small LRU of snapshot ids (theirs: `mcp_server/snapshots.py`)
  - [ ] `monitor_property` (N per-frame samples, changes only, `dropped_duplicates`) and `simulate_action` (inject an InputMap action) (theirs: `mcp_runtime_probe.gd` L392-435)
  - [ ] Input recording in the replay format, and a seeded random-input stress test returning `{survived, seed}` (theirs: `mcp_runtime_probe.gd` L481-520, `docs/tool-contracts.md` L1199-1207)
  - Decided (user, 2026-09-26): `simulate_action {action, mode: tap|press|release, strength}` is a thin separate tool on `simulate_input`'s action event, server-side only. Snapshots are of the running game only, kept per session (16, dropped on stop or restart), capturing the inspector-shown properties and groups, and a diff without an after id re-reads live. The stress test draws uniformly, seeded, from a pool the caller gives (actions, keys, UI elements), at most 1000 events; survived means still running and answering at the end, and the result lists new errors with the iteration each first appeared at. Recording has both sources behind one recorder: a person's real input (quiet off or attached) and the gestures the server sends; it returns `simulate_input` events with gaps as `wait {ms}`, motion off by default, capped at 2000. Orchestrator defaults (2026-09-26): `describe_class` lists own members unless `inherited`, describes script classes too (warning when a C# build is red), pages methods with `offset`/`limit`, and suggests the 5 closest names; `monitor_property` is one synchronous call of 1-600 samples (default 60), changes only.

### Singles

- [ ] `wait_for` a UI change nobody can name in advance: met when the visible Controls, focus owner or top popup differ from the call's start, returning what appeared (#7). Decided (user, 2026-09-26): `wait_for {uiChanged: true}` compares against a snapshot the bridge takes when each pointer or key gesture starts (visible Controls, focus owner, top popup); met on the first differing frame, returning `{appeared, disappeared}` (paths, at most 20 each plus counts) and `focus`/`popup` `{before, after}` when those changed; tooltips, drag previews and the bridge's own nodes do not count. Builds on #4's hit reading in `bridge/godot_mcp_input.gd`. Decided (user, 2026-09-26): the baseline is taken when the first input gesture starts after launch or after the previous `uiChanged` wait, later gestures leave it alone, and the wait uses it up; with no gesture since, the call is refused
- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26). Decided (user, 2026-09-26): design it now, as a proposal drafted before any build
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. The test window flash is the first candidate; decided (user, 2026-09-26): investigate it on stock 4.7.2 now. Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
