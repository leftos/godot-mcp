# Main Plan
<!-- plan-doc-hygiene: 2026-09-29 688b4ab -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] A killed server's marked `override.cfg` outlives it (#43, #45): its shutdown clean-ups (`Session/SessionRegistry.cs` L204-206, L633) never run, so the next run started outside the server loads the bridge, and a quiet one opens off-screen (`Session/OverrideFile.cs` L61-66). Agreed fix: the marker line records its owning servers' PIDs (with start time, against PID reuse; one folder can hold sessions from several servers), and a server that starts or touches a folder removes a marked file whose owners are all dead; and the bridge, when it finds no server (`bridge/godot_mcp_bridge.gd` L126-134), moves a parked quiet window back on screen before it frees itself. Close both issues on landing; the game repos' `Test-Path` checks (`no-mcp-bridge-at-commit`) can then go
- [ ] Headless tools run beside a live session on the folder (#44): today `ClearFolder` (`Session/HeadlessRunner.cs` L95-107) refuses them. Agreed fix: launch the headless Godot with an environment variable that keeps the bridge off (checked before `_find_endpoint`, so an attach file is not dialled either), leave the live session's `override.cfg` in place, and allow every headless tool, edits included; TOOLS.md L337 (every tool in "Headless scene editing" refused) then stops contradicting L342 (`describe_class` answered by the live game)
- [ ] The default session name collides across worktrees of one repo (#46): it is the folder's leaf name (`Session/SessionRegistry.cs` L229-240), so a second `Sky.Client` is refused at L458. Agreed fix: keep the leaf name, and when a live session of that name holds a different path, add `-2`, `-3`, … ; `run_project`'s result already names the session it made (`session`, TOOLS.md L59), and TOOLS.md says how the default is picked
- [ ] In a recording, the Time module's step, monitor and capture deadline (`_begin`, `bridge/godot_mcp_time.gd` ~L168-172, a `SceneTreeTimer` its comment says "runs in real time") runs in clip time, so at 240 fps its `backstopMs` fires after a quarter of its length in wall time: measure whether it can beat the server's cancel, and correct the comment

- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
