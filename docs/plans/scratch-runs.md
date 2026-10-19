# Scratch runs: a step's end, and runs in the background

Status: ruled, not built. GMCP-181 (a step's end) lands first; GMCP-200 (background runs) builds on it. Both land on `main`, one commit each.

Every ruling below is an orchestrator decision under CLAUDE.md's autonomy rule (the recommended option), recorded on its issue and open to the owner.

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

- **The signal is an optional fifth protocol method**, `IsStepRunning() -> bool`, public on the scene root (GDScript `func IsStepRunning() -> bool`, C# `public bool IsStepRunning()`). The protocol check looks for it with `has_method` beside the four; a scene without it plays exactly as today. The name matches opening-hand's own field. A step-end log line (delve's proposal) is not used: it would share the runner's marker form, and DECISIONS.md decision 28 ruled out judging steps by searching the scene's log.
- **An opted-in step ends when the scene says so.** After `PlayStep(i)` returns, the runner waits 50 ms of game time, calls `IsStepRunning()`, and repeats until it answers `false`; then it calls `GetStatus` and closes the window as today. The step's pace is the ceiling on the game time waited.
- **A step still running at its pace is red, and the scene stops there**, even under `keepGoing` (its next `PlayStep` would throw in opening-hand): `step <i> '<name>' was still running after its pace of <p> s; give it a longer scratch.pace, or have IsStepRunning() report its end`.
- **An answer that is not a bool** turns the step red the same way: `IsStepRunning() returned <value>, not a bool`.
- **The pace after the last step is unchanged** (the full last pace, for late errors).
- **The scene's ceiling is unchanged.**
- **Result**: a step's `gameMs` in `details` is the game time the runner waited for it (the pace for a paced scene); a scene entry gains `awaited: true` when its root has `IsStepRunning`.

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

1. **GMCP-181**: `IsStepRunning` (part A); tests in `ScratchVerdictTests`/`ScratchRunOrderTests` where pure, itest `ScratchRunnerTests` with new fixture scenes (an awaited GDScript scene green well under its pace, one whose `IsStepRunning` never clears, a C# one in CsProbe); TOOLS.md, SKILL.md, ARCHITECTURE.md, the glossary's Pace and Scratch protocol, DECISIONS.md decision 28. Reply to opening-hand and delve-the-dungeon by inbox.
2. **GMCP-200**: background runs and `scratch_status` (part B); unit tests on the status writer and the remaining-time figure; itest in `ScratchRunnerTests` (a run started in the background, its status read mid-run, its completion line, a stop); TOOLS.md, SKILL.md, ARCHITECTURE.md (tool table, session lifecycle at shutdown), README.md, the glossary (background run, run id, status file, completion line), a new DECISIONS.md entry. Reply to delve-the-dungeon by inbox.
