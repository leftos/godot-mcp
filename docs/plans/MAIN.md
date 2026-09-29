# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Wave 1: capture and drive bug reports from opening-hand

Shared: `RuntimeTools.Capture.cs`, `RuntimeTools.Input.cs`, `RuntimeTools.Time.cs`, `RuntimeTools.cs` and their bridge modules (`godot_mcp_capture.gd`, `godot_mcp_input.gd`, `godot_mcp_time.gd`). Gate: `pwsh run.ps1 test` and the touched `itest` groups; the evidence is a driven run against a fixture.

- [ ] #41 `wait_for` used as a timed pause (`expression: "false"`) in a plain session stretches with load (2000 ms ran 10016 ms), so a shot meant to land mid-effect lands after it; wanted: a pause in game time or frames that load does not stretch. Decided (user, 2026-09-29): two new `wait_for` conditions, `{gameMs: N}` (met once N ms of game time, summed delta with `time_scale` applied, have passed) and `{frames: N}` (met after N process frames), each taking `options.screenshot` as any wait does. Its comment's shape (`call_method` then `capture_frames` at 0.3 s misses, the round trip falling outside the capture's clock) is decided too (user, 2026-09-29): `capture_frames` and the two game-time waits take `options.call {node, method, args}`, which the bridge runs in the frame their clock starts, so `at` and `gameMs` count from the call; a call that throws answers its error and no frames. Its other half, `capture_frames` missing from the implementer agent, is fixed: the user-level agents' tool lists now carry every tool

### Singles

- [ ] `restart_project`'s result (`RestartResult`) carries no `window` or size `warning`, though the relaunch is resized as `run_project`'s is
- [ ] Integration tests that launch their own game with a 45 s timeout (`ProfileTests.APresetSetsTheSessionAndTheWindowSize`, the own-launch cases in `RuntimeReadTests`) against the DEVELOPMENT.md footgun's 180 s
- [ ] `TempDirectory.Dispose` (`tests/GodotMcp.IntegrationTests/TempDirectory.cs` L32) throws `IOException` ("being used by another process") under machine load: seen twice on `TakeScreenshotPlacesANonEmbeddedPopupWhereTheWindowShowsIt(canvas_items, 1280, 720)`; it does not retry

- [ ] `stress_input` in a recording: its reply timeout (`RuntimeTools.Stress.cs` L177, `InputTimeout` + events + `GapMs` of real time) skips `SendInputAsync`'s recorded allowance, so a gapped run in a slow recorded game can be cut short
- [ ] `simulate_input`'s `wait {ms}` events (and `stress_input`'s gaps and replays) run on a `SceneTreeTimer` (`godot_mcp_input.gd` ~L629, `ignore_time_scale`): measure whether it counts clip time under Movie Maker (4.7.2's SceneTree.xml says real elapsed time) before the docs claim either
- [ ] `TimeTests.CaptureFramesFollowsTimeScale` fails under machine load: 2 s of game time at `time_scale` 4 took 1.94 s of wall time against its 1.5 s bound



- [ ] `hover` over a `MenuBar` or `PopupMenu` item: `_tooltip_owner` (`bridge/godot_mcp_input.gd`) does not copy the menu branch of `Viewport::_gui_get_tooltip` (4.7.2 `viewport.cpp`, above the owner assignment in L1566-1596), so a menu item's tooltip may read null
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
