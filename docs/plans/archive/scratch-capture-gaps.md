# run_scratches capture: a step list per scene, and a shot inside a step (GMCP-589, GMCP-590)

Design note for open work, from the game repo career-driver. Symbols and headings only here; `path:line` lives in the last section, read at one commit.

PROTOCOL: 1 overlay: none (docs/agents/explore.md absent), read CLAUDE.md; 2 README: done; 3 Task Index: the capture rows sit in long lines the search tool omitted, not read in full (unmeasured); 4 design docs: DECISIONS items 51 and 54 and the archived gmcp-516-step-shot.md, done; 5 source: done; 6 search: done for tests and fixtures.

## Today

### How options.capture is parsed and reaches a scene

- `run_scratches` takes `options.capture` as `ScratchCaptureOptions {steps?, screenshot}` (ScratchTools.cs). `PlanCapture` requires `options.windowed: true` and `screenshot: true`, then `CaptureTokens` reads `steps`: a lone token or an array, each token a whole number from 0 up (`IsNumber`) or a string, duplicates dropped, an empty array refused.
- The plan holds it as `ScratchCapture(Steps)`, one `ScratchStepToken[]` or null (null shoots every played step). Nothing keys it by scene: every scene of the plan gets the same list.
- Resolution happens per scene, after the game booted and every step name was read: `ScratchRun.ReadNamesAsync` calls `ResolveCapture`, which calls the static `CaptureSteps(capture, scene, names)`. A number token is checked against the step count (`CaptureIndex`), a string is read as a plain index, else an exact name (`StepOf`, the helper `options.steps` also uses).

  A miss is `NoStep`: `options.capture.steps token "x" of <Scene> matches no step; its steps are ...`. The text comes back as the scene's error, so the scene is red at boot, before any step. A name that matches two steps is refused too ("names N steps; key it by index").
- The resolved set is `_shots`. `WarnOfUnplayedShots` adds a scene warning (`CaptureNotPlayed`) for a shot step an `options.steps` partial run does not play; that one is not red.
- `options.steps` already has the per-scene form: `PlanSteps` / `StepsByScene` accept `{"<Scene>": tokens-or-batches}`, refuse a key that is not a scene of the plan ("scene 'X' is not in the plan; its scenes are ..."), and `ScratchPlan.StepsFor(scene)` picks the scene's list; a scene the map leaves out plays every step.
- The CLI (`ScratchCommand`) builds `ScratchCaptureOptions(Steps: tokens, Screenshot: true)` from `--capture` and repeated `--capture-step`; it has no per-scene form.

### How a shot is taken for a step

Capture produces stills only; there is no clip in `run_scratches` capture (clips are `record_mark`'s, a separate tool). Three paths in `ScratchRun`, chosen per shot step:

- An until step (`GetStepUntil` gives a condition): played inside one `wait_for` on the until (`PlayInsideUntilAsync`, `PlayStep` its `call`), with `screenshot` set in the wait (`UntilPlay.Screenshot`). The bridge's `_poll_and_capture` / `_with_capture` capture the met frame into `result.screenshot`; the step ends at the same frame. `GetStepShotUntil` is never read for an until step (DECISIONS 51).
- A paced step whose root has `GetStepShotUntil(i)` returning a condition: `PlayPacedStepAsync` reads it (`ReadShotUntilAsync`, `ShotUntilWait`), builds a `wait_for` with `PlayStep` as the call, the condition, `screenshot`, the pace as `timeoutMs` and `limitGameMs`, plus `endMarkerAtGameMs` so the bridge holds a met wait to the pace (`PlayToShotAsync`). The shot is the first frame the condition holds; a condition that never holds within the pace is the warning `ShotNotHeld`, not a red step.
- Any other paced step: the pace's wait, then `PacedShotAsync` sends the bridge's `screenshot` command for the next drawn frame, after the pace.

`ShotAsync` copies the capture into the run's shot folder (`ShotPath`: `<project>/.godot/godot-mcp/scratch-<runId>/<scene folder>/<index>-<step>.png`, `ShotFolders`, `ReplayFolder`, `FileSafe`). The result names it as `details[].screenshot` with `window` and `viewport` sizes; a failed shot is a step warning (`ScratchVerdict.ShotFailed`), never red. The 20 newest shot folders are kept.

### The step window's cut (DECISIONS 54) as it bears on a shot

- A step is one `wait_for` whose `call` is `PlayStep`; its window ends at the wait's last check in game time, where the bridge prints the end marker (`_end_window`, server-only `endMarker`). A draw-checked wait (a shot condition, a captured until) is met at `frame_post_draw`, and its cut lands at the next `process_frame`. A shot met before the pace is held by the bridge to the pace (`endMarkerAtGameMs`, `_hold_to_window_end`).
- So any new shot must be taken inside the step's one wait without moving that cut: a shot taken while the wait still polls is free; a second wait or a round trip inside the step would put runner traffic in the window, which 54 ruled out.

## GMCP-589: a step list per scene

Symptom: five scenes, `["display-tab-ui-scale-row", "safe-area-32x9", "style-switch"]`; each name exists in one scene, so every scene is red at boot with "matches no step". Cause: one list, resolved strictly against every scene.

### Design A: a per-scene map (recommended)

- Schema: `capture.steps` also accepts an object `{"<Scene>": token-or-array}`, the shape `options.steps` takes. `ScratchCaptureOptions.Steps` is already a `JsonElement?`, so the wire type does not change; the tool description (Capture, Steps) says so. The array and lone-token forms stay as they are and keep applying to every scene.
- Plan: `ScratchCapture` gains `StepsByScene` beside `Steps` (as `ScratchPlan` has both), and a `StepsFor(scene)`.

  `PlanCapture` needs the plan's scene names, so it takes `resPaths` as `PlanSteps` does and reuses the key check, extracted so options.steps, ceilings and memoryCeilings keep their texts (each names its own option; the capture text is "options.capture.steps scene 'X' is not in the plan; its scenes are ...").

  Values go through `CaptureTokens` per scene, so the token grammar and the empty-array refusal are unchanged; the error text gains the scene (`options.capture.steps for <Scene>: ...`).
- Resolution: `ResolveCapture` asks `StepsFor(_scene.Name)`; a scene with no entry shoots every played step (as a scene left out of `options.steps` plays every step) or none. Open decision below.
- A typo: a misspelled step name in a scene's list is red for that scene before any step, as today; a misspelled scene key is refused before anything launches. Nothing is silent.
- `WarnOfUnplayedShots` and `_shots` need no change; the warning still names steps a partial run did not play.
- CLI: `--capture-step` stays an every-scene list; a per-scene form there (`--capture-step Scene=token`) is optional and not needed for the issue.
- Tests: `ScratchValidationTests` gets the map form: a key outside the plan is refused; a lone token and an array per scene; an empty array for a scene refused with the scene named; a map with `windowed` missing still refused first; the existing array cases unchanged; `ScratchRun.CaptureSteps` / `StepsFor` take each scene's list and a scene left out gets the default.

  `ScratchShotTests` (warning for unplayed shots) gets a per-scene case. One integration test in `ScratchLookRunTests`: two scenes, each with a step the other lacks, one map, both green, each with its own shot only.

### Design B: skip a name that matches no step in a scene

- Schema: unchanged. Validation: `CaptureSteps` keeps the grammar but turns `NoStep` into a skip; a per-scene warning would say "options.capture.steps token X matches no step of <Scene>; skipped".
- A typo: becomes a warning in every scene (a name exists in one scene, so with five scenes four warn for a correct token), or a silent miss if the warning is dropped. A scene where nothing matches shoots nothing (it must not fall back to every step) and the reason is easy to miss.

  To keep typos catchable the runner needs a run-level check "a token matched no step in any scene", which is only knowable after the last scene booted, when the earlier scenes have played; a long run learns it at the end.
- Tests: the two `ACaptureStep...` and `CaptureSteps...` cases flip from error to warning; a typo test needs the run-level check.

### Recommendation

Design A. It follows the `options.steps` precedent, keeps the strict, early failure for a typo, and costs a plan-level type and one resolver lookup. B trades the guard that a typo is caught for a convenience the map already gives. A lenient mode could be added later as its own option; it is not part of this issue.

## GMCP-590: a shot inside a step

Symptom: a settings step shot at its `until` is shot after the page closed, so no still of the page open mid-step exists. The existing mid-step tool, `GetStepShotUntil`, covers paced steps only; an until step is shot at its until and never reads it (DECISIONS 51). The archived gmcp-516-step-shot.md ruled out "a capture hook the step calls" for a paced step, because the game code would depend on the bridge and the shot would be pushed.

### Design 1: GetStepShotUntil also answers for an until step (recommended)

- Protocol: no new method. For a shot step with a `GetStepUntil` condition, `ReadShotUntilAsync` is asked as well (`has_method` is still asked only on a run with `options.capture`). The answer is a `wait_for` condition on the step's own timeline, e.g. `{exists: "SettingsPage"}` or a `gameMs` kind; it names the mid-step moment.
- Wire: the until's `wait_for` (`PlayInsideUntilAsync`) gains a server-only param, say `shotWhen`, holding the shot condition built as `PlayStepWait` builds a condition (without a second `call`).

  In the bridge, `_poll_and_capture` checks the shot probe at each draw beside the main probe (`_check_at_draws` already does that for one probe), captures the first draw where it holds into `result.midShot`, and keeps polling the until. The main probe is unchanged, the end marker is unchanged. `make_probe` takes the sub-dictionary as it takes the main params.
- Fit with the cut (54): the capture happens while the one wait still runs, at `frame_post_draw`, so no round trip lands inside the window and the cut stays at the until's last check. A shot condition holding on the until's own met frame is the draw-checked case 54 already describes (the cut lands one frame later).
- Outputs: the same file `<index>-<step>.png` in the same shot folder, the same `details[].screenshot`, `window`, `viewport`. For this step the mid-step shot replaces the met-frame shot (one shot per step, no result shape change).

  A shot condition that does not hold before the until is met or hangs is the `ShotNotHeld` warning and the step is unshot; falling back to the met frame would reproduce the page-closed image this issue complains about. Bridge refusal and connection failure texts follow `its shot condition's wait was refused / failed` (DECISIONS 51).
- Limits: the moment must be expressible as a `wait_for` condition (a node exists, a property, an expression, text, a state key, `gameMs` since `PlayStep`). A page that opens and closes inside one frame cannot be seen. One shot per step.
- Files, server side: `Session/ScratchRun.cs` (`PlayInsideUntilAsync`, `ReadShotUntilAsync`, `ShotUntilWait`, `CaptureAsync`, `StepPlay`), `Tools/ScratchTools.cs` (Capture description). Bridge side: `bridge/godot_mcp_time.gd` (`wait_for`, `_poll_and_capture`, `_check_at_draws`, `_with_capture`, doc comment listing the scratch-only params). Scene side: the game's root implements `GetStepShotUntil` for that step, GDScript or C#.
- Tests: `tests/bridge` gdtest for the bridge (a wait with `shotWhen` captures the first holding draw while the main condition stays unmet, then the main wait ends met; a never-holding shot condition leaves no `midShot`; the end marker and `endMarked` are unchanged);

  `ScratchUntilTests` for the server side (an until step with a shot condition builds the wait with `shotWhen`; the refusal texts; a step not shot never reads the method);

  `ScratchLookRunTests` plus a fixture under `tests/fixtures/InputProbe/scratch_shot` (an until step that shows a rect for part of the step and hides it at the until; the shot's centre pixel shows the rect, the sibling without a shot condition shows the hidden state); `scratch_window_cut_shot.gd` is the model for the cut check.

### Design 2: named marks a step lists

- Protocol: `GetStepShotMarks(i)` returns `{"<mark>": condition, ...}`; the bridge captures each mark's first holding draw, in one wait, for until and paced steps alike.
- Outputs: several shots per step, `<index>-<step>.<mark>.png`; `details[]` gains a `screenshots` list beside or instead of `screenshot`; `ShotAsync`, `StepShot`, `ScratchVerdict` details, the compact and full details renderers and the 20000-character budget all learn a list. A larger bridge change too (several probes).
- It is design 1 plus multiplicity. Take it only when a step needs two shots; design 1's `shotWhen` can grow into it (a map instead of one condition) without a protocol break.
- Tests: those of design 1 plus the file naming, the details shape, and the budget cut.

### Design 3: a signal the scene emits

- Protocol: the scene root declares a signal (`scratch_shot(name)`); the bridge connects to it when a step's wait starts (found by `has_signal`, a server-only `shotSignal` param on the wait) and captures the next drawn frame after each emission. The scene decides the frame in code; no condition has to describe it; several shots are natural.

  The scene needs no bridge reference (a signal nobody listens to costs nothing), which answers the first objection of the 516 note, but a C# root must declare a `[Signal]` delegate, and the shot is still pushed by game code, which that note did not like.
- Wire: `shotSignal` into `wait_for`, `result.shots: [{name, path}]`; the bridge needs a listener that outlives the call and is disconnected when the wait ends (the `Hold` and `signal_wait` modules are the patterns). Files named `<index>-<step>.<name>.png`, so the outputs change as in design 2.
- Fit with the cut: a capture requested from `_process` is taken at that frame's `frame_post_draw`, inside the wait; no extra traffic. An emission after the wait ended has no listener and is dropped.
- Tests: gdtest for connect and disconnect and the capture on emission; a fixture scene; the server's has_signal probe at boot; the details shape tests.

### Recommendation

Design 1. It reverses only one clause of decision 51 (an until step never reads `GetStepShotUntil`), keeps the protocol pull-based as the rest of it is, adds one server-only wait param and one bridge capture, and changes no output. Design 2 is its extension when one step needs several shots. Design 3 is the answer for a moment no condition can name (one frame, a transient effect); reach for it only if design 1 proves too weak in the game.

## Rulings

Settled by the recommended option, as this repo's autonomy rule says.

- GMCP-589 takes design A, the per-scene map. A scene the map leaves out has every played step shot, as `options.steps` plays every step. The CLI keeps its every-scene `--capture-step`.
- GMCP-590 takes design 1, `GetStepShotUntil` for an until step through a server-only `shotWhen` on the until's wait. The mid-step shot replaces the until's met-frame shot. A shot condition that never holds leaves the step unshot with the `ShotNotHeld` warning. The new decision item amends decision 51's clause that an until step never reads `GetStepShotUntil`.
- The duplicate decision number 51 is fixed in the commit that adds GMCP-590's item.

## Open decisions (settled above)

- GMCP-589: a scene the capture map leaves out shoots every played step (the options.steps rule, recommended for symmetry) or none (safer for a long run, but a different rule from options.steps). Recommended: every played step, as `options.steps` plays every step.
- GMCP-589: whether the CLI gets a per-scene `--capture-step`. Recommended: not now.
- GMCP-590: the mid-step shot replaces the until's met-frame shot (recommended) or comes in addition under a second file name.
- GMCP-590: a shot condition that never holds: the step unshot with the `ShotNotHeld` warning (recommended), as for a paced step, or the met-frame shot as a fallback.
- Both issues together: if the map form lands, GMCP-590's `GetStepShotUntil` stays a scene-side protocol method and needs no per-scene option.

## Docs, skill and tests each change touches

Both:

- `docs/TOOLS.md` (`run_scratches`, the `options.capture` paragraph and the scratch protocol entry for `GetStepShotUntil`)
- `docs/DECISIONS.md` (a new item that amends 51 for 590; a short item or an addendum to the options.steps rule for 589)
- `docs/ARCHITECTURE.md` (the run_scratches rows and the capture sentence)
- `skills/godot-mcp/SKILL.md` (the capture and `GetStepShotUntil` lines)
- `CHANGELOG.md`, the `Capture` / `Steps` descriptions in `Tools/ScratchTools.cs`, `docs/README.md` glossary ("Scratch protocol", "Shot folder", and a new "Shot condition" entry).

- 589 only: TOOLS.md says `steps` is an array of names or indexes and a name the scene lacks is refused like an unknown `options.steps` name; add the map form there, and the CLI note if the CLI changes.
- 590 only: TOOLS.md says `GetStepShotUntil` is for a paced step; DECISIONS 51 says an until step never reads it; both change. `docs/DEVELOPMENT.md` if the gdtest or fixture lists name the shot fixtures (unmeasured).

## DOC DRIFT

- DECISIONS.md has two items numbered 51 (the `GetStepShotUntil` item and the wrapped-prep-ceiling item, GMCP-496): the second should be 52 and the rest renumbered, or the numbers made unique; DECISIONS.md owns the fix. Anything citing "decision 51" is ambiguous (the 52 text cites "decision 51" for the ceiling).
- No other disagreement was found between the capture text read (the tool description in ScratchTools.cs, DECISIONS 51 and 54, the archived 516 design) and the code.

## TASK INDEX GAP

The question took a search of the tree plus ScratchTools.cs, ScratchRun.cs, the bridge's time module and the tests, beyond the Task Index row.

The row that should exist: "change what `run_scratches` `options.capture` accepts, or where a step's shot is taken":

- `docs/TOOLS.md` `run_scratches` (capture)
- `Tools/ScratchTools.cs` (`PlanCapture`, `CaptureTokens`, `ScratchCaptureOptions`)
- `Session/ScratchRun.cs` (`ScratchCapture`, `CaptureSteps`, `ResolveCapture`, `PlayInsideUntilAsync`, `PlayPacedStepAsync`, `ShotAsync`)
- `bridge/godot_mcp_time.gd` (`_poll_and_capture`)
- DECISIONS 51 and 54; tests `ScratchValidationTests`, `ScratchShotTests`, `ScratchUntilTests`, `ScratchLookRunTests`, `tests/fixtures/InputProbe/scratch_shot/`.

## MISSING ENTRY POINT

The glossary has no entry for a "shot condition" (the `GetStepShotUntil` answer) though the glossary's "Scratch protocol" entry and TOOLS.md use it. Otherwise none.

## Anchors at 789b8f6e74883bf07740bb80ea1a685f8f61ed42

- src/GodotMcp.Server/Tools/ScratchTools.cs:552 PlanCapture doc, :557 PlanCapture, :579 CaptureTokens, :601 CaptureToken, :655 PlanSteps, :713 StepsByScene, :1635 Capture description, :1659 ScratchCaptureOptions, :1033 and :1143 the other scene-key refusals
- src/GodotMcp.Server/Session/ScratchRun.cs:170-182 StepsByScene and StepsFor, :205-210 Capture and ScratchCapture, :432 ShotUntilMethod
- src/GodotMcp.Server/Session/ScratchRun.cs:816 CaptureSteps, :839 CaptureIndex, :884 StepOf, :906 NoStep, :978 ShotUntilWait, :1003 ShotNotHeld, :1015 PlayStepWait, :1035 ShotPath, :1063 ShotFolders, :1112 CaptureNotPlayed
- src/GodotMcp.Server/Session/ScratchRun.cs:1713 HasMethodAsync for GetStepShotUntil, :1759 ReadNamesAsync, :1769 ResolveCapture call, :1807 WarnOfUnplayedShots, :1819 ResolveCapture, :2039 ShotAsync, :2071 CaptureAsync, :2091 PacedShotAsync, :2112 PlayPacedStepAsync, :2130 ReadShotUntilAsync, :2152 PlayToShotAsync, :2306 PlayInsideUntilAsync, :2331 StepPlay
- src/GodotMcp.Server/Session/ScratchVerdict.cs:47 and :269 the step's shot path
- src/GodotMcp.Server/Agents/ScratchCommand.cs:371 --capture-step, :740 CaptureOptions
- bridge/godot_mcp_time.gd:479 wait_for, :652 _poll_and_capture, :674 _end_window, :705 _hold_to_window_end, :950 _with_capture, :975 _save_capture
- docs/DECISIONS.md:770 item 51, :791 item 54; docs/plans/archive/gmcp-516-step-shot.md
- tests/GodotMcp.Tests/Tools/ScratchValidationTests.cs:1281-1366 capture validation, :1497 CaptureOf; tests/GodotMcp.Tests/Session/ScratchShotTests.cs:171; tests/GodotMcp.Tests/Session/ScratchUntilTests.cs:170-222; tests/GodotMcp.IntegrationTests/ScratchLookRunTests.cs:134, :184, :202; tests/fixtures/InputProbe/scratch_shot/scratch_shot_until.gd, scratch_shot_never.gd; tests/fixtures/InputProbe/scratch/scratch_window_cut_shot.gd
- docs/TOOLS.md:756-770 capture text, :608 shot-wait errors, :662 CLI flags; skills/godot-mcp/SKILL.md:83; docs/README.md glossary "Scratch protocol" and "Shot folder"
