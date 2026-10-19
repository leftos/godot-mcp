# Scratch runs: a step's end, and runs in the background

Status: ruled, not built. GMCP-181 (a step's end) lands first; GMCP-200 (background runs) builds on it. Both land on `main`, one commit each.

Part A's shape is the owner's ruling; every other ruling below is an orchestrator decision under CLAUDE.md's autonomy rule (the recommended option), recorded on its issue and open to the owner.

## 1. Need

- **opening-hand** (GMCP-181, inbox 20261016-164213): its awaited steps (`X:\dev\opening-hand\src\Duel.Client\Scripts\Diagnostics\ScratchScene.cs:184-189, 262-276, 386-418`) return from `PlayStep` as the step starts, and a second `PlayStep` while one runs throws. A fixed pace either wastes time or calls the next step early and goes red.
- **delve-the-dungeon** (GMCP-181 comment, inbox 20261019-215103): a scene costs steps × pace whether or not a step is done; `GelaosDiceScratch` is 50 steps at 10 s where its beats animate for 6.5–8 s.
- **delve-the-dungeon** (GMCP-200, inbox 20261019-213927): one `run_scratches` call over seven scenes held an agent about 30 minutes, and its orchestrator could not tell progress from a hang.

## 2. Code facts

From the map of `src/GodotMcp.Server/Session/ScratchRun.cs` and `Tools/ScratchTools.cs`:

- Per step the runner calls `PlayStep(i)` on the scene root through `call_method` (`ScratchRun.cs:663`), waits the pace in game time (`WaitAsync`, `:664`, `:779-799`), then calls `GetStatus` (`:665`) and closes the step's window with its own marker line `[godot-mcp] scratch step <i> end <nonce>` (`:125`, `:697`). After the last step `PaceAfterAsync` (`:678-683`) waits that step's pace once more for late errors.
- The protocol is checked by `has_method` on the root (`CheckProtocolAsync`, `:556-577`), so a C# protocol method must be public.
- A scene's ceiling is the sum of each played step's pace plus 10 s, plus the last pace, plus 10 s (`:251-252`), on the load-adjusted clock; a scene that waits less than its pace never comes nearer to it.
- No server code runs work past the end of its tool call and posts a result later; MCP progress notifications are unused. `SessionRegistry.Shutdown` (`SessionRegistry.cs:434-466`) stops every session's game at server exit. `ProjectPrep.LogFolder` is `<project>/.godot/godot-mcp/`; `AttachFile` writes atomically (`AttachFile.cs:23`).

## 3. Design

### A. A step's end (GMCP-181)

**Settled (owner ruling, 2026-10-19):** each step declares its own done-condition, a `wait_for` condition the scene gives beside the step's action, instead of one generic `IsStepRunning()` that knows nothing of the step ("not going to be anywhere contextual enough"). A first build of a generic `IsStepRunning()` poll was dropped unlanded; it also cost two bridge round trips per 50 ms poll, which the scene's ceiling did not count.

- **The step's `until` is an optional fifth protocol method**, `GetStepUntil(int) -> Dictionary`, public on the scene root (GDScript `func GetStepUntil(i: int) -> Dictionary`, C# `public Godot.Collections.Dictionary GetStepUntil(int i)`). It answers a `wait_for` condition, in the same shape `wait_for`'s `condition` takes (`{node, signal}`, `{node, expression}`, `{node, property, equals}`, `{text}` and the rest), or an empty dictionary (or null) for a step paced as today. The protocol check looks for it with `has_method` beside the four; a scene without it plays exactly as today. A scene base class builds it from each step's own declaration, e.g. `AddStep("roll dice", () => Dice.Roll(), until: {signal: "Settled", node: "Dice"})`.
- **The runner reads a step's `until` just before playing it** (a node it names may exist only once earlier steps have run).
- **A step with an `until` plays inside its wait**: the runner sends one `wait_for` with that condition, `timeoutMs` the step's pace in milliseconds, and `options.call {node: <root>, method: "PlayStep", args: [i]}`, so the bridge calls `PlayStep` in the frame the checks start and the condition is checked from there, once a frame, in the game. There is no server-side polling. Then `GetStatus` and the window close as today.
- **A `signal` until is connected before `PlayStep` runs**: `wait_for` now takes `options.call` with a signal wait too, connecting the signal first and calling the method in the next frame, so a signal the call emits at once is caught. This is a change to `wait_for` itself, open to every caller.
- **An `until` not met within the pace is red, and the scene stops there**, even under `keepGoing` (its later steps would read this step's frames, and opening-hand's next `PlayStep` would throw): `step <i> '<name>': its until was not met within its pace of <p> s; give it a longer scratch.pace, or check the condition GetStepUntil gives`.
- **A refused `until`** (a node or signal that does not exist, a malformed condition) turns the step red the same way, with `wait_for`'s own error: `step <i> '<name>': its until was refused: <wait_for's error>`.
- **An answer that is neither a dictionary nor null** turns the step red the same way: `GetStepUntil(<i>) returned <value>, not a wait_for condition`.
- **The pace after the last step is unchanged** (the full last pace, for late errors), and so is the scene's ceiling (a step's wait never passes its pace).
- **The pace's clock**: for a step with an `until`, the pace is the wait's `timeoutMs`, counted as `wait_for` counts it; a paced step still waits its pace in game time.
- **Result**: a step with an `until` carries `until: {met, elapsedMs, frames}` in `details` (the wait's own figures); its `gameMs` is left out. A paced step's entry is unchanged.

### B. Background runs (GMCP-200)

- **`options.background: true`** on `run_scratches`. The plan is checked and every refusal returned as today; then the call returns at once with `{runId, statusFile, logFile, scenes}` (`scenes` the names in play order), and the run goes on in the server, cancelled only by its own stop, never by the call's end.
- **Run id**: `<UTC yyyyMMddTHHmmss>-<4 hex>`, e.g. `20261019T222144-3f2a`.
- **Files**, both in `<project>/.godot/godot-mcp/`:
  - `scratch-<runId>.json`, written atomically at the run's start, at each scene's start and end, and at each step's start: `{runId, state, serverPid, startedAt, updatedAt, scenes: [{scene, state, step?, stepName?, total?, elapsedSeconds?, remainingSecondsAtMost?, verdict?}], result?}`. The run's `state` is `running`, `done`, `stopped` or `interrupted`; a scene's is `queued`, `playing` or `done`. `result` is the object a call without `background` returns, present once the state is `done`. `startedAt` and `updatedAt` are UTC ISO 8601.
  - `scratch-<runId>.log`, one line appended per event, each starting `godot-mcp scratch run <runId> ` (never `[godot-mcp] scratch `, the runner's window marker): `scene <scene> start`, `scene <scene> step <i>/<total> <name>`, `scene <scene> <verdict> in <seconds> s`, and last the **completion line**: `godot-mcp scratch run <runId> finished: done, passed <true|false>, green <n>, red <n>, killed <n>, known <n>`, or `finished: stopped`, or `finished: interrupted`. A harness waits on `scratch run \S+ finished:`.
- **Progress**: a playing scene's `remainingSecondsAtMost` is the pace of each played step not yet started, plus the current step's, plus the last pace; launch and prep are left out. There is no run-wide figure.
- **`scratch_status {projectPath, runId, stop?}`**, a new tool: returns the run's status object (from the live run, else from its file); `stop: true` ends the run as a cancelled call ends today (its games stopped), writes `stopped` and the completion line, and returns the status.
- **One background run per project**: while one runs, `run_scratches` on that project is refused: `background scratch run <runId> is still running on this project; read it with scratch_status, or end it with scratch_status {stop: true}`.
- **Server exit**: `Shutdown` stops every background run within its cap and writes `interrupted` and the completion line. A server killed outright leaves `running`; `serverPid` lets a reader tell.
- **Old files** are left in place.
- **No feature branch**: the mode is opt-in and additive, and CLAUDE.md lands every item on `main`.

## 4. Steps

1. **GMCP-181**: `GetStepUntil` (part A), and `wait_for {signal}` with `options.call` in `bridge/godot_mcp_time.gd` and the server's refusal; a gdtest for the signal-with-call wait; tests in `ScratchVerdictTests`/`ScratchRunOrderTests` where pure, itest `ScratchRunnerTests` with new fixture scenes (GDScript scenes whose steps end on a signal and on an expression, green well under their pace; one whose until is never met; one whose until is refused; a C# one in CsProbe), and a `TimeTests` case for the signal-with-call wait; TOOLS.md (run_scratches and wait_for), SKILL.md, ARCHITECTURE.md, the glossary's Pace and Scratch protocol, DECISIONS.md decision 28. Reply to opening-hand and delve-the-dungeon by inbox.
2. **GMCP-200**: background runs and `scratch_status` (part B); unit tests on the status writer and the remaining-time figure; itest in `ScratchRunnerTests` (a run started in the background, its status read mid-run, its completion line, a stop); TOOLS.md, SKILL.md, ARCHITECTURE.md (tool table, session lifecycle at shutdown), README.md, the glossary (background run, run id, status file, completion line), a new DECISIONS.md entry. Reply to delve-the-dungeon by inbox.
