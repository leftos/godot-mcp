# A scratch step's shot at its own moment (GMCP-516)

## Problem

A paced scratch step that changes its scene's state, checks it and then restores it (world-of-intrigue's `GlobeScratch`) is shot at the end of its pace, after the restore, so the image shows the scene as it was before the step. The scratch protocol has no check or restore phase and no hook: `PlayStep(i)` is one call, and the server's paced shot (`ScratchRun.PacedShotAsync`) asks for the next drawn frame once the pace has passed.

## Decision (orchestrator decision, open to the owner)

A scene root may give an optional `GetStepShotUntil(int)` that returns a `wait_for` condition, in the same shape as `GetStepUntil` returns one.

For a paced step that `options.capture` shoots and whose `GetStepShotUntil` returns a condition, the server plays the step as one `wait_for` with `PlayStep(i)` as its call (as an until step is played), the shot condition as its condition and `options.screenshot` set, so the frame the condition first holds is the step's shot; the step's pace still sets when the next step plays, counted from `PlayStep`.

A condition that does not hold within the step's pace leaves the step unshot, as a shot that fails does: a scene warning, never a red step.

- An until step keeps its shot at its until's met frame; its `GetStepShotUntil` is not read.
- A step `options.capture` does not shoot never reads `GetStepShotUntil`, so the walk costs nothing extra.
- `GetStepShotUntil` returning null (or the root lacking it) leaves the step shot at the end of its pace, as today.

## Rejected

- A capture hook the step calls (a bridge autoload method or signal): game code would depend on the bridge being present, a C# scene would need an interop path, and the shot would be pushed rather than pulled, unlike the rest of the protocol.
- Telling the scene to give the step a `GetStepUntil` on its checked state: the until also ends the step, so the next step plays while the restore is still pending.
