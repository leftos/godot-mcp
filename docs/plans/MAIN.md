# Main Plan
<!-- plan-doc-hygiene: 2026-09-28 b0c3120 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] A C# field named like an engine property still reaches an instanced node: an instancing scene's override of `scale` on an instanced node with a C# script is set after the script attaches, so it lands on the field (unmeasured; see DEVELOPMENT.md's footgun on C# fields hiding engine properties). Measure it headless, and store the override as `SceneEdit.pack_native` does for plain nodes. Measured and ruled ([DECISIONS.md](../DECISIONS.md) 19); in build
- [ ] Scene-edit frictions from delve-the-dungeon ([#33](https://github.com/leftos/godot-mcp/issues/33)): `attach_script` keeps the exported values (and their `ext_resource` lines) whose names the new script still declares; a `save_scene` save-as writes nodes as the source had them, adding no `layout_mode = 0`; a new script's `ext_resource` carries its `uid`, generated as the editor does, or the result says it is missing. Measured and ruled ([DECISIONS.md](../DECISIONS.md) 20: editor-rule exports, the source's stored set, a splicing save-as, a generated `.uid`); builds after the item above, since both change `pack_native`
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
