# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Before 0.8.0

The user's cut list (2026-09-29): 0.8.0 is cut once the line below has landed.

- [ ] `simulate_input`'s `wait` and `stress_input`'s gaps count clip time in a recording (measured: a `SceneTreeTimer` ran 120 frames for 2000 ms at 240 and 20 fps; DEVELOPMENT.md's recorded-run footgun). Left: after the popup-tooltip item lands (it holds `godot_mcp_input.gd`), correct `_play_wait`'s "real time" doc comment (`godot_mcp_input.gd` ~L626) and `gapMs`'s "real time" descriptions (`RuntimeTools.Stress.cs` ~L38, ~L367), and pin it with `RecordingTests.ARawWaitInARecordingLastsItsLengthInMovieFrames` (slowed game, `simulate_input wait 1000`, 60 to 80 process frames between reads)
- [ ] Cut and install 0.8.0

### Singles

- [ ] In a recording, the Time module's step, monitor and capture deadline (`_begin`, `bridge/godot_mcp_time.gd` ~L168-172, a `SceneTreeTimer` its comment says "runs in real time") runs in clip time, so at 240 fps its `backstopMs` fires after a quarter of its length in wall time: measure whether it can beat the server's cancel, and correct the comment

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
