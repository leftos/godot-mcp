# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Wave 1: capture and drive bug reports from opening-hand

Shared: `RuntimeTools.Capture.cs`, `RuntimeTools.Input.cs`, `RuntimeTools.Time.cs`, `RuntimeTools.cs` and their bridge modules (`godot_mcp_capture.gd`, `godot_mcp_input.gd`, `godot_mcp_time.gd`). Gate: `pwsh run.ps1 test` and the touched `itest` groups; the evidence is a driven run against a fixture.

- [ ] #39 `run_project` with `--resolution` larger than the screen: the window is clamped without a warning. Decided (user, 2026-09-29): the server passes the last `--resolution` to the bridge, which sets that exact size after start; the bridge's hello carries the window's size, the launch result reports `window {width, height}` and warns when it still differs
- [ ] #37 `take_screenshot` with `crop`: no way to take the crop above 1x. Decided (user, 2026-09-29): launch at 2x (#39) and crop in viewport coordinates, mapped by the bridge through the stretch transform, so `hover`'s rect crops as-is; `crop` means viewport coordinates everywhere (`take_screenshot`, `capture_frames`, the baselines), replacing screenshot pixels; builds on #39
- [ ] #41 `wait_for` used as a timed pause (`expression: "false"`) in a plain session stretches with load (2000 ms ran 10016 ms), so a shot meant to land mid-effect lands after it; wanted: a pause in game time or frames that load does not stretch. Decided (user, 2026-09-29): two new `wait_for` conditions, `{gameMs: N}` (met once N ms of game time, summed delta with `time_scale` applied, have passed) and `{frames: N}` (met after N process frames), each taking `options.screenshot` as any wait does. Its other half, `capture_frames` missing from the implementer agent, is fixed: the user-level agents' tool lists now carry every tool

### Singles

- [ ] `stress_input` in a recording: its reply timeout (`RuntimeTools.Stress.cs` L177, `InputTimeout` + events + `GapMs` of real time) skips `SendInputAsync`'s recorded allowance, so a gapped run in a slow recorded game can be cut short
- [ ] `simulate_input`'s `wait {ms}` events (and `stress_input`'s gaps and replays) run on a `SceneTreeTimer` (`godot_mcp_input.gd` ~L629, `ignore_time_scale`): measure whether it counts clip time under Movie Maker (4.7.2's SceneTree.xml says real elapsed time) before the docs claim either
- [ ] `TimeTests.CaptureFramesFollowsTimeScale` fails under machine load: 2 s of game time at `time_scale` 4 took 1.94 s of wall time against its 1.5 s bound

- [ ] #42 `batch_scene_operations`' `add_node` refuses a `ColorRect` with `layout_mode: 0` plus `anchor_right`/`anchor_bottom` ("anchor_right takes only with layout_mode 1"), a set the Godot editor wrote in delve-the-dungeon's `DomeScratch.tscn`; the refusal from 8a9180c (`headless/scene_props.gd`) has to accept what the editor writes, or at least name the `layout_mode` the editor would store. Cause unknown: find when 4.7.2's editor stores anchors under `layout_mode` 0

- [ ] An agent-sweep skill: bring the `tools:` list of every agent that drives Godot up to date, the user-level ones and those in every repo under `D:\` (11 today: opening-hand 4, in-the-sky 5, delve-the-dungeon 2) (user, 2026-09-29). Decided (user, 2026-09-29): it runs at install, when new tools become callable; godot-mcp publishes each tool's class (read, drive, edit headless, edit live) from its `ReadOnly`/`Destructive` attributes, each agent file carries a marker naming the classes it takes, and an agent without one is skipped and reported; the skill lives in this repo at `skills/godot-agent-sweep/`, linked into `~/.claude/skills` by `pwsh run.ps1 install`; an agent's classes are one body line, `<!-- godot-mcp tool classes: read, drive -->`; in a repo the sweep commits only the agent files it touched (`chore: sync godot-mcp tools`) on the current branch, never pushes, and skips a repo whose agent files have uncommitted changes, reporting it

- [ ] `hover` over a `MenuBar` or `PopupMenu` item: `_tooltip_owner` (`bridge/godot_mcp_input.gd`) does not copy the menu branch of `Viewport::_gui_get_tooltip` (4.7.2 `viewport.cpp`, above the owner assignment in L1566-1596), so a menu item's tooltip may read null
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
