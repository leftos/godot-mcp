# Output destinations for stills and clips (GMCP-171)

Where `save_screenshot` stills and `record_mark` / `stop_project` clips may be written. Two game repos asked for it: an agent driving from a git worktree loses every clip and still with `git worktree remove --force` (opening-hand), and a tracked docs image inside the checkout is refused (delve-the-dungeon, DELVE-400). This file is the design from the invariant pass; the item's issue links it.

The `drive` class is not a sandbox: `run_project` passes `engineArgs` to Godot unchanged, so `--log-file <path>` (and `--write-movie <path>` on a run without `options.record`) already writes anywhere (`GodotCommandLine.cs:174`, `:194-201`; Godot 4.7.2 `main/main.cpp` L1517-1525, L1961-1972). The path rule below guards against mistakes and misled agents, not a hostile local process. That route is its own item (D6).

## Today (`src/GodotMcp.Server/Tools/ScreenshotTarget.cs`, the only site; called at `RuntimeTools.cs:96`, copied at `:123`)

1. The raw string ends in `.png`, any case (`:34-37`).
2. `res://` is stripped and such a path stays in the project (`:40-41`, `:46`).
3. `Path.GetFullPath` against the project folder unless `Path.IsPathRooted` (`:42`).
4. Outside the project, only `<nearest folder holding a .git entry>\.tmp` (`:61-77`; `FindCheckout` `:83-94`, a folder walk, git never asked).
5. A `:` below the root is refused (`:119-122`); an 8.3 short name is refused (`:125-128`, `:227-228`).
6. Only the project's own first-level `.godot` is refused (`:130-133`, `:224-225`); `.tmp/.godot` is accepted.
7. Every component's link is followed and each final target must be inside an allowed place (`CheckLinks` `:140-165`, `CheckTmpStep` `:186-204`, `FinalLinkTarget` `:207-218`).
8. An existing file is refused unless `overwrite` (`:54`); the write is `File.Copy(..., overwrite)` after `Directory.CreateDirectory` (`:98-109`).

Clips are always written under `<project>/.godot/godot-mcp/recordings/` (`Recording.cs:48-55`, `RealtimeRecording.cs:77-92`); no argument names another path.

Gaps the new rule closes (each is an invariant below):

- G1: `.git/` is writable when the project sits at the repo root (only `.godot` is checked).
- G2: `IsPathRooted` accepts `X:shot.png` and `\shot.png`, which resolve against the server's current folder (`:42`).
- G3: a dangling link is compared as text, so a link to a missing target behind a junction passes the walk.
- G4: a link to a network share comes back as `UNC\srv\share\…`, which `GetRelativePath` resolves against the current folder (`:152-153`, `:190-191`).
- G5: the check-to-write gap spans the whole capture, and `File.Copy` writes into a destination symlink's target.
- G6: Windows device names (`NUL.png`, `COM1.png`) are not refused (baseline names are: `ScreenshotBaseline.cs:57-62`).
- G7: the copy trusts the bridge reply's `path` as its source (`RuntimeTools.cs:122`).
- Polish: the tool description at `RuntimeTools.cs:74-75` reads "an existing file is true;", missing "refused unless options.overwrite is".

## The rule (orchestrator decision; R1 of the invariant pass)

Allowed places:

- the project, as today (`res://` paths stay here only, D5);
- the project's own checkout W, as a whole tree, only when W tracks `<project>/project.godot` (`git ls-files --error-unmatch`); otherwise only W's `.tmp/`, as today (D4);
- the main checkout M's `.tmp/` only (D3). M is the first entry of `git worktree list --porcelain -z`, used only when it is not bare, is not W, and its list includes W.

Refused anywhere inside them: a `.git` folder, and a `.godot` folder outside a `.tmp`.

## Invariants and their sites

| # | Invariant | Sites |
|---|---|---|
| I1 | After following every link on the way, the destination entry included, the final path lies under an allowed place; a relative symbolic link is resolved against its own folder and walked. A link whose target does not exist is refused (G3). A link target that is not a fully qualified local path is refused, a network target before it is opened, from the immediate `LinkTarget` (G4). | `ScreenshotTarget.cs:32-55`, `:140-218`; the clip-path check at `record_mark start` (`RuntimeTools.Record.cs:51-57`, `:62-63`); the check again at delivery (I7) |
| I2 | Never inside `.git`, any case, trailing `.` or space. | `CheckParts` `:116-134`, the link-target checks `:152-161`, `:198-201` |
| I3 | Never under a `.godot` folder outside a `.tmp`, anywhere in W. | `IsUnderDotGodot` `:224-225` and its three callers |
| I4 | A path is project-relative or fully qualified on a local drive: no drive-relative, root-relative, UNC, `\\.\` or `\\?\` path (G2). | `ScreenshotTarget.cs:42` |
| I5 | Stills end in `.png`, clips in `.mp4`; device names are refused (G6). | `:34-37`; the clip check; the pattern of `ScreenshotBaseline.cs:32-62` |
| I6 | Without `overwrite`, an existing file is never replaced. With it, the folder entry is replaced and nothing is written through a link or hard link: write a temporary file (`FileMode.CreateNew`, random name, same folder), then `File.Move(temp, dest, overwrite)`; a temporary left by a failure is deleted. | replaces `File.Copy` at `:103`; the same helper delivers clips |
| I7 | The path is checked again in the call that writes. A clip's check at `start` only fails fast; the one that counts runs at delivery. | `ScreenshotTarget.Save` `:98-109`; `RealtimeRecording.RemuxAsync` after a successful remux (`:942-948`); `RecordingCut.CutClipsAsync` / `DeleteFullMovie` (`:136-165`, `:291-314`) |
| I8 | Only the server writes a path the caller named. ffmpeg (`-y`), Godot's `--write-movie` and the bridge's `save_png` keep writing under `.godot/godot-mcp/`. | `Recording.cs:48-55`, `RealtimeRecording.cs:77-92` unchanged |
| I9 | The copy's source is ours: the reply's path lies under the session's `.godot/godot-mcp/screenshots/` (G7). | `RuntimeTools.cs:120-126` |
| I10 | Every path a result reports holds the file it names. | `RealtimeStarted` (`RealtimeRecording.cs:474-482`), `RealtimeClip.Path` (`:973`), `RecordingResult.Clips` (`GodotSession.Realtime.cs:326`, `RecordingCut.cs:297-302`), `list_sessions` (`GodotSession.Realtime.cs:179-184`) |
| I11 | The allowed places come from git and are cross-checked (W in M's worktree list; W tracks the project). A clip's places are computed at `start` and kept with it, so no git runs at shutdown. | replaces `FindCheckout` `:83-94`, through `GitRunner` |
| I12 | A failed delivery never loses the clip: it stays in `recordings/` with a warning naming both paths. Delivery runs inside the recording's finish, so every ending gets it: stop (`GodotSession.cs:231`), restart (`:654`), game exit (`:1339-1340`), detach (`GodotSession.Attach.cs:184`), shutdown (`SessionRegistry.cs:590-611`, within its 10 s cap). | `RealtimeRecording.FinishAsync` (`:876-893`), `RecordingCut.FinishAsync` (`:105-129`) |

## Clips (D1, D2, D7)

`record_mark start` takes `options.path` and `options.overwrite`, on a real-time start and on a Movie Maker start mark (which today refuses all options; `fps`, `maxSeconds` and `audio` stay refused there). The path is checked at `start` and again at delivery. The server moves the finished `.mp4` into place (a same-volume rename fits the shutdown cap; stills keep copying, since `capturePath` and the preview stay). A Movie Maker clip that fell back to `.avi` (`RecordingCut.cs:213-222`) stays in `recordings/` with a warning. A delivery that fails is a warning, and the result's clip path names where the file is.

## Refusal and warning texts

- `path <path> resolves outside the project <P>, its checkout <W> and the main checkout's .tmp folder <M>\.tmp; save inside one of them` (clauses that do not apply are dropped; with no checkout, today's `…; save inside the project`)
- when W does not track the project: `… and the checkout's .tmp folder <W>\.tmp (the checkout <W> does not track <P>\project.godot, so only its .tmp folder is open); save inside either`
- `path <path> is inside a .git folder, which git owns; save outside it`
- `path <path> is under .godot/, which the editor owns; pick a tracked folder` (today's text, now applied across W)
- `path <path> is relative to a drive's current folder or the current drive's root; give a full path or one relative to the project`
- `path <path> names the Windows device <NAME>; pick another file name`
- `path must end in .mp4: <path>`
- `path <path> leads through a link to <resolved>, outside <places>; save inside one of them`
- `path <path> leads through a link at <link> whose target <target> does not exist`
- `path <path> leads through a link at <link> to the network path <target>; links to network paths are not followed`
- `path <path> leads through a link to <target>, whose name <part> ends in a space or a dot; Windows reads such a name two ways, so it is not written` (Windows only), and for a name in the path as written `path <path> has a folder or file name ending in a space or a dot (<part>); Windows reads such a name two ways, so it is not written` (Windows only)
- `path <path> leads through a link at <link> that cannot be followed: the link's reparse data is malformed` (and `…: <message>` for another read failure)
- delivery warning: `the clip could not be moved to <dest>: <reason>; it is kept at <recordings path>`

## Steps

1. Stills: the rule, I1-I6, I9, I11, the refusal texts, the tool description, `ScreenshotPathValidationTests` extended with a real main checkout plus `git worktree add` (as `GitExcludeTests.cs:35-36`) and junctions by `mklink /J` (as `:473-494`).
2. Clips: `options.path`/`overwrite` on `record_mark start`, I7, I10, I12, delivery inside both finishes, unit tests in `RealtimeRecordingTests` and through `RecordingCut.CutClipsAsync`'s `run` parameter, and the itests.

The test list for each step is the invariant pass's (kept in the step briefs). The swap tests make their change between the two calls inside the test, so nothing depends on timing.

## Open decisions, settled by the orchestrator

| # | Decision | Settled |
|---|---|---|
| D1 | When a clip's path is named | `record_mark start {options: {path, overwrite}}`, checked at start and at delivery |
| D2 | Move or copy a clip | move; stills keep copying |
| D3 | The main checkout: whole tree or `.tmp/` | `.tmp/` only |
| D4 | When W's whole tree opens | W tracks `<project>/project.godot`; else only its `.tmp/` |
| D5 | `res://` widens? | no, project only |
| D6 | `engineArgs` writes anywhere (G8) | its own item; the held folder handles of I7's strongest form wait for it |
| D7 | A failed delivery | a warning, with the clip path naming where the file is |

## Docs the change updates

`docs/DECISIONS.md` decision 32 (`:430-433`, whose premise G8 undercuts) and decision 35's last bullet (`:466`); `docs/TOOLS.md:295-309` and `:1315`; `docs/ARCHITECTURE.md:465`; the `save_screenshot` description at `RuntimeTools.cs:73-83`; `skills/godot-mcp/SKILL.md`.
