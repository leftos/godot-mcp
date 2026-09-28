# Main Plan
<!-- plan-doc-hygiene: 2026-09-28 b0c3120 -->

Open work only, in working order: the next item is the first line from the top; a finished line is deleted (git keeps the history). The user's decisions are in [DECISIONS.md](../DECISIONS.md).

## Now: the first version has shipped

The first version replaced godot-mcp-runtime at parity and went further: every planned step and the sixteen features the user added have landed, and opening-hand and delve-the-dungeon drive their clients through it (cutover 2026-09-26).

## Next

Waves run in order; bug reports sit ahead of the backlog inside each. The singles' order is not a ranking.

### Singles

- [ ] `add_node` with a script path as `nodeType` (a C# script on `Node2D`, via `batch_scene_operations`) writes a stray `scale = 1.0` float the call never asked for; write only the properties passed (#32)
- [ ] Review the heavy/light gate slot picks. `tools/gate.ps1` now requires `-Slot heavy|light`, and every call site here (`run.ps1`'s gated helpers and itest lanes, `tests/tools/test_drive.py`, the docs that quote the usage) was given a kind from outside this repo's agents, as a preliminary pick so the gates kept running. Check each kind against what the command really does (does it fan out across cores, or keep one or two threads busy for its whole run?), measure where unsure, and correct any that are wrong. The pool sizes (`GATE_HEAVY_SLOTS` / `GATE_LIGHT_SLOTS` defaults) belong to the machine-wide gate in `~/.claude/tools/gate/`, not to this repo.
- [ ] Timing tests that fail under other agents' load and pass on a retry: `BridgeListenerTests.ASilentConnectionIsRefusedOnceNoWaiterIsPending` and `HangProbeTests.ACallToAGamePausedUnderADebuggerFailsFast` (2.98 s against its 2 s bound) in the unit hook, `WatchdogTests.StopReturnsOnlyOnceAKilledGameHasLetGoOfItsFolder` past xunit's 60 s, `InspectionTests.DescribeClassFindsAScriptClassOfTheRunningGame` past its 45 s in a 17-minute full itest, `GitRunnerTests.AHungGitIsKilledAtItsCeiling` and `ToolProcessStallTests.AStallKillsASilentChild` in the unit hook: find what each times on wall clock
- [ ] `run_csharp`: give snippets a `CancellationToken` the timeout's cancel fires, so a timed-out snippet can stop itself (today its `Task` is forgotten and keeps running)
- [ ] An OS-level virtual gamepad, if a game ever queries `get_connected_joypads()` (not reachable from script; see [DECISIONS.md](../DECISIONS.md#gamepad-input-from-godot-472s-source))
- [ ] A patched Godot build for internal development (user, 2026-09-26: patches kept in a repo, rebuilt and reviewed on every upstream update). Agreed order (user, 2026-09-26): solve each need on stock 4.7.2 first; a need stock cannot meet gets a small patch sent upstream as a PR and carried only until it merges; the full patches repo and rebuild pipeline only if a patch upstream will not take. No candidate today: the test window flash, the first one, is gone on stock 4.7.2 (measured 2026-09-26: no window on the user's desktop from gdtest, filtered itests or the import prep, all behind the hidden desktop; see the DEVELOPMENT.md footgun on how Godot shows its window), and the user chose to keep this line idle until a need stock cannot meet appears (user, 2026-09-26). Open for that pipeline: the .NET build's GodotSharp packages, which the C# projects must resolve without a tracked-file change; tests on a patched engine against games shipped on stock export templates
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
