# Main Plan
<!-- plan-doc-hygiene: 2026-09-26 45fcccb -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).


## Next

The singles' order is not a ranking.

### Singles

- [ ] A `click` by element path lands on a same-named sibling (`CardSlot/CardFace` among auto-named instances) instead of the node the path names: resolve the path exactly, or refuse it as ambiguous (#15). The path lookup is exact (`_find_node`); the miss is after it: the click aims at the rect's centre and nothing checks the hit. Decided (user, 2026-09-27): an input target is hit-tested before the press and refused, naming both Controls and their rects, when Godot would press something other than the target, its descendant or the ancestor that takes a `mouse_filter` IGNORE target's clicks; a target hidden or being freed is refused; a bare name matching several nodes is refused for input targets only (listing their paths), `wait_for` and the inspect tools keep the first match. Orchestrator default: the aim point is computed after the tooltip-dismiss frame
- [ ] `run_script` reading a C# collection property (`List<HandSlot>`) fails with "Invalid access to property" and does not point to `cs_get`; say so in the error, and check the skill steers C# reads to the `cs_*` tools (#16). Orchestrator defaults (2026-09-27): the server appends the hint to a failed `run_script` when the project has a csproj and an error reads `Invalid access to property or key`, `Invalid call. Nonexistent function`, `Invalid get index` or `Invalid set index`; failures only, `run_script` only

- [ ] `take_screenshot` misses popups and tooltips in a project that sets `display/window/subwindows/embed_subwindows=false`, since each is its own OS window outside the root viewport's texture (measured 2026-09-27 for #9): composite each visible non-embedded `Window` onto the capture at its offset from the root, through one capture helper so crops, baselines and frame steps inherit it; check the offset on a visible desktop first, since a native popup's `position` read (0,0) on the hidden one (user, 2026-09-27: build later)
- [ ] A Godot launch in the itests died once with exit 0xC06D007F (a delay-load failure) before the bridge connected (`CaptureTests.SentPadInputIsCaptured`, full `itest` 2026-09-26, beside two other trees' itests; passed on the rerun): find what fails to load if it recurs
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] An editor bridge, so an agent can see a scene it edits and use editor features without running the game (user, 2026-09-26). Whether it is still wanted is decided once agents have used `preview_scene`, shipped 2026-09-26 (user, 2026-09-26); asked again 2026-09-27, still waiting on more use (user). Open questions: enabling an `EditorPlugin` through `override.cfg` rather than the tracked `project.godot`; headless saves racing an open editor's in-memory copy of the scene. Reference: hybridindie/godot-mcp routes every edit through `EditorInterface.get_editor_undo_redo()`, calls `EditorFileSystem.update_file` after writes and a deferred `scan()` after a new `class_name`, and refuses a move while `get_unsaved_scenes()` lists the scene; `get_editor_viewport_2d()`/`_3d()` give the scene view itself
