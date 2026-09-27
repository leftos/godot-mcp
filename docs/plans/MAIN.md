# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 45fcccb -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).


## Next

A track, then a wave, then singles; the singles' order is not a ranking.

### Track: C# runtime tools

- [ ] C#-aware runtime tools beyond `call_method`, for members Godot's call cannot reach: signatures with types Godot cannot marshal (generics, plain C# classes), static members, and overloads that share a name and argument count (`internal` methods are reached; step 12, 2026-09-26). Decided (user, 2026-09-26): design it now, as a proposal drafted before any build. Proposal and decisions: [csharp-runtime-tools.md](./csharp-runtime-tools.md) (a helper loaded at run time through a NativeAOT GDExtension shim; `cs_members`/`cs_get`/`cs_set`/`cs_call` and `run_csharp`); its spike proved all three unproven steps (2026-09-26, the proposal's §6); the build is planned as steps S1-S9 (the proposal's §7); S1 (projects, publish, install), S2 (the marshalling core), S4 (CsProbe additions) and S8a (the snippet compiler) landed 2026-09-26; S3's loader, helper ping and helper cache landed too; next is S3's integration tests (the `csharp` itest group)

### Wave 1: gamepads

Shared: `src/GodotMcp.Server/Tools/RuntimeTools.Gamepad.cs`, the bridge's pad injection, `tests/GodotMcp.IntegrationTests/Fixtures/SharedProbeSession.cs`, `GamepadTests`. Gate: `pwsh run.ps1 test` and `pwsh run.ps1 itest`; human check: none (the real-pad cases are measured by the itests on the user's desktop).

- [ ] Gamepad selection (user, 2026-09-26; DECISIONS.md 14): none of the user's Godot games treats every connected gamepad as input; the rule goes into the shared Godot conventions (godot-conventions-sync) and a plan line in opening-hand and delve-the-dungeon. This server's part: when `device` is not given, the pad tools inject on the lowest id 0-15 no real pad holds (the game's `Input.get_connected_joypads`) and report it, refusing when none is free, so an agent claims that pad in the game's selection step as a player would. Decided (user, 2026-09-26; DECISIONS.md 14): the id is kept while free, the same rule holds in shut-out mode, an explicit real pad's id is injected with a warning, and raw `joypad_*` events follow the rule
- [ ] `SharedProbeSession.ResetAsync` releases every pressed action, re-arming ones a real pad's resting stick still holds, so the pad's next jitter moves GUI focus and can drop a click in any test on the plain session (measured 2026-09-26, the DEVELOPMENT.md real-pads footgun); decided (user, 2026-09-26): every shared session launches shut out of real pads, and a test that needs real pads live launches its own game

### Singles

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
