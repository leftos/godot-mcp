# Main Plan
<!-- plan-doc-hygiene: 2026-09-28 b0c3120 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] A save keeps the source's stored set, and a save-as splices against the source's text ([#33](https://github.com/leftos/godot-mcp/issues/33) friction 2; ruled in [DECISIONS.md](../DECISIONS.md) 20): Godot's `pack` adds `layout_mode = 0` to a Control subclass in position mode (the ClassDB default is 3 for subclasses, built parentless), and every save (`save_scene`, the in-place edits, `batch_scene_operations`) writes it. Pass `source` from `_save_checked` (`headless/scene_ops.gd`) into `SceneEdit.save` / `pack_native`, and after packing drop each pair the source record did not store whose value equals what the freshly opened source node reads, with an instantiate-and-compare self-check that falls back to the full pack and a `warning`; make save-as splice against the source's text (`headless/scene_files.gd` L34, L67-70) and change TOOLS/ARCHITECTURE's "`save_scene` is never spliced" for save-as
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
