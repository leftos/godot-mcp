# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] In a recording, the Time module's step, monitor and capture deadline (`_begin`, `bridge/godot_mcp_time.gd` ~L168-172, a `SceneTreeTimer` its comment says "runs in real time") runs in clip time, so at 240 fps its `backstopMs` fires after a quarter of its length in wall time: measure whether it can beat the server's cancel, and correct the comment

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] `TempDirectoryTests.DisposeRetriesAFileHeldBriefly` (`tests/GodotMcp.Tests/TestSupport/TempDirectoryTests.cs:19`, a 2 s wall-clock bound) fails under the full unit suite with an IOException on held.txt and passes alone: seen twice on 2026-09-29
- [ ] Two servers releasing one folder's `override.cfg` at once can lose an owner: `OverrideFile.Release` rewrites the owners line without the `override-folders.txt` lock that `Write` takes through `OverrideFolders.RecordWhile`
- [ ] Servers sharing a folder with different `quiet`, `shutOutRealGamepads` or bridge path: the last to write the `override.cfg` wins its content, since only sessions of one server are checked against each other (`SessionRegistry.CheckSameSetting`)
- [ ] `bridge/godot_mcp_gamepad.gd:300` calls `bridge._gestures._dispatch(event)`, another module's private method; the input module now has a public `dispatch` for the raw event player
- [ ] `bridge/godot_mcp_input.gd` is at 998 of gdlint's 1000 max-file-lines, so its next addition needs another split (the raw event player moved to `godot_mcp_raw_events.gd` for `scroll`); the input module's public wrappers (`send_button`, `to_window`, …) twin private methods that could simply be renamed public
- [ ] No itest covers `GODOT_MCP_OFF` where it matters: a headless run beside an attach session whose `attach.json` is present (`bridge/godot_mcp_bridge.gd` `_is_switched_off`); the gdtest covers the branch only
