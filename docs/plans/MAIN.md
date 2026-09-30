# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] Ideas from the Godot MCP survey ([2026-09-29-godot-mcp-survey.md](../research/2026-09-29-godot-mcp-survey.md), which names the source servers and the open engine questions), chosen by the user 2026-09-29; each needs its own design pass before a brief:
  - [ ] Debug channel: host Godot's `--remote-debug` listener for a script profiler and GDScript breakpoints, stack, locals and stepping (survey idea 1; absorbs the profiler half of the "profiler, autoload-editing and file-parsing tools" line below)
  - [ ] Performance monitors over a frame window with spike and budget verdicts, and one timeline watch over several properties and signals (ideas 2, 3)
  - [ ] State digest: game nodes opt in (`_mcp_state()` or a group) for a compact read in place of a screenshot (idea 4)
  - [ ] Audio observation: which players play, bus levels; check first whether levels read under the Dummy driver (idea 9; pairs with #50)
  - [ ] Input aimed at 2D/3D world nodes through the camera, UI targets by visible text, and Tree/ItemList/TabBar/PopupMenu item targets (ideas 7, 8)
  - [ ] Step until a condition with input inside a paused step, checked against `batch_drive` first (idea 5); render diagnosis, starting with documenting the `--debug-collisions` family through `engineArgs` (idea 6)
  - [ ] godot-mcp-runtime's dropped tools: autoload and project-settings tools, `validate`'s signal-wiring and structure checks, a click that reports the signals it fired (absorbs the autoload half of the line below)
  - [ ] Run GUT or gdUnit4 suites headless with parsed results (idea 10)
- [ ] #50 silence comes only with `quiet`: a watched run, or a game joined on an armed folder, plays sound; wants a `mute` option independent of `quiet`. Decided (user, 2026-09-29): `options.mute` on `run_project`, `attach_project` and `arm_project`, the bridge muting the Master bus; `quiet` still implies it
- [ ] In a recording, the Time module's step, monitor and capture deadline (`_begin`, `bridge/godot_mcp_time.gd` ~L168-172, a `SceneTreeTimer` its comment says "runs in real time") runs in clip time, so at 240 fps its `backstopMs` fires after a quarter of its length in wall time: measure whether it can beat the server's cancel, and correct the comment

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
- [ ] `TempDirectoryTests.DisposeRetriesAFileHeldBriefly` (`tests/GodotMcp.Tests/TestSupport/TempDirectoryTests.cs:19`, a 2 s wall-clock bound) fails under the full unit suite with an IOException on held.txt and passes alone: seen twice on 2026-09-29
- [ ] Two servers releasing one folder's `override.cfg` at once can lose an owner: `OverrideFile.Release` rewrites the owners line without the `override-folders.txt` lock that `Write` takes through `OverrideFolders.RecordWhile`
- [ ] Servers sharing a folder with different `quiet`, `shutOutRealGamepads` or bridge path: the last to write the `override.cfg` (or `armed.json`, `ArmFile.Write`) wins its content, since only sessions and arms of one server are checked against each other (`SessionRegistry.CheckSameSetting`, `IsArmedAlready`)
- [ ] Two joins of different dormant games on one folder cannot wait at once: `SessionRegistry.CheckCanStart` refuses a second attach while one waits, though each join has its own `join-<pid>.json`
- [ ] `bridge/godot_mcp_gamepad.gd:300` calls `bridge._gestures._dispatch(event)`, another module's private method; the input module now has a public `dispatch` for the raw event player
- [ ] `bridge/godot_mcp_input.gd` is at 998 of gdlint's 1000 max-file-lines, so its next addition needs another split (the raw event player moved to `godot_mcp_raw_events.gd` for `scroll`); the input module's public wrappers (`send_button`, `to_window`, …) twin private methods that could simply be renamed public
- [ ] A game going dormant again after a detach releases the injected mouse buttons, pad buttons and axes it held (`release_all`, `bridge/godot_mcp_raw_events.gd`) but not keys a raw `simulate_input` key press left down: the input module does not record held keys
- [ ] A green `pwsh run.ps1 gdtest` prints a GDScript stack trace from `test_a_poll_deletes_a_malformed_join_file_and_stays_dormant` (`tests/bridge/test_dormant.gd:156`), the expected `push_warning`, which reads as a failure at a glance
- [ ] `bridge/godot_mcp_bridge.gd` is at 989 of gdlint's 1000 max-file-lines after #48 moved endpoint finding to `godot_mcp_dormant.gd` and window handling to `godot_mcp_window.gd`; its next addition needs another split
- [ ] No itest covers `GODOT_MCP_OFF` where it matters: a headless run beside an attach session whose `attach.json` is present (`bridge/godot_mcp_dormant.gd` `is_switched_off`); the gdtest covers the branch only
- [ ] No test covers an attached game's kill paths in `stop_project` (silent at the ping, still running after the grace: `StopAttachedAsync`, `GodotSession.Attach.cs`), only its quit; and the unit harness's fake hellos carry made-up pids (4242, 4101, 4102 in `tests/GodotMcp.Tests/Session/`), so an attach opens a handle on whatever real process holds that pid and a future unit stop of such a session could kill it: fake games should carry null or a pid the test owns
- [ ] `FindLiveConnection`'s closed-connection message (`GodotSession.cs`) still names only `detach_project, then attach_project again`; `stop_project` now ends an attached session too
