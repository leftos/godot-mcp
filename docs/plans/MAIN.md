# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Wave 1: capture and drive bug reports from opening-hand

Shared: `RuntimeTools.Capture.cs`, `RuntimeTools.Input.cs`, `RuntimeTools.Time.cs`, `RuntimeTools.cs` and their bridge modules (`godot_mcp_capture.gd`, `godot_mcp_input.gd`, `godot_mcp_time.gd`). Gate: `pwsh run.ps1 test` and the touched `itest` groups; the evidence is a driven run against a fixture.

- [ ] #40 `wait_for` in a recorded session (`options.record`): a 1500 ms timeout ran 6290 ms and 240 frames. Cause: the clip runs 60 frames a second while the server's release (load-adjusted) and the bridge's `_poll` bound (`backstopMs`, real time) count wall time. Decided (user, 2026-09-29): in a recorded session `timeoutMs` counts clip time, `ceil(timeoutMs x 60 / 1000)` movie frames, with no new option; the result keeps `elapsedMs` in real ms and adds `clipMs`; in scope too, every other real-ms duration while recording (a drag's `durationMs`, `hover`'s tooltip wait, the gamepad sweep). Builds on #38 (both edit `godot_mcp_input.gd`)
- [ ] #38 `hover` returns `tooltip: null` when the tooltip Godot shows belongs to an ancestor of the hovered control, and returns before the tooltip delay has passed. Decided (user, 2026-09-29): walk up the parents as `Viewport::_gui_get_tooltip` does, and the `tooltip` object gains `owner {path, class}`
- [ ] #39 `run_project` with `--resolution` larger than the screen: the window is clamped without a warning. Decided (user, 2026-09-29): the server passes the last `--resolution` to the bridge, which sets that exact size after start; the bridge's hello carries the window's size, the launch result reports `window {width, height}` and warns when it still differs
- [ ] #37 `take_screenshot` with `crop`: no way to take the crop above 1x. Decided (user, 2026-09-29): launch at 2x (#39) and crop in viewport coordinates, mapped by the bridge through the stretch transform, so `hover`'s rect crops as-is; `crop` means viewport coordinates everywhere (`take_screenshot`, `capture_frames`, the baselines), replacing screenshot pixels; builds on #39

### Singles

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
