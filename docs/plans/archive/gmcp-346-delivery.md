# Clip and still delivery: the invariant pass behind GMCP-346

The actors, invariants and per-point fixes for GMCP-346 (record_mark delivery: folders made before a failed move, the shutdown cap mid-copy, two untested branches), from an `oracle` pass against main 0a16d4eb (read and traced, no tests run). GMCP-343's part has landed and is kept for its invariants. The rulings are on the issue; this file moves to `docs/plans/archive/` once GMCP-346's last step lands.

## Corrections to the issue texts
- GMCP-343 lines are stale: `CheckReached` is `OutputDestination.cs:600-620`, its name scan `:603`; `Follow`'s `Path.GetFullPath(link.Target, current)` is `:502`; the stored-target check is `:499`.
- GMCP-343.2: `Path.GetFullPath("", base)` returns `base` in .NET 10 and throws nothing (`Path.Windows.cs`, `IsEffectivelyEmpty` returns basePath). The defect is that a malformed link is interpreted (as pointing at its own folder) instead of refused.
- GMCP-346.1: a destination already taken, or a folder standing there, makes no folders (its folder exists). The failing cases that do make folders: source missing, source locked without delete sharing, a cross-volume copy failing, a path too long (346.5), a race.
- GMCP-346.4: `Recording.CheckDestination` is `Recording.cs:106-126`, the comparison `:118`.

## ACTORS
- Start check (`ScreenshotTarget.Resolve` :33-37 from `RuntimeTools.cs:100`; `ResolveClipTarget` `RuntimeTools.Record.cs:135-144` from :73 and :158): runs git (`OutputPlaces.Find` :46-59), opens entries for attributes only (`FinalPath.Of` :85), reads link data without following (`ReadLink` :131). Writes nothing. Runs at the save_screenshot call before the capture; record_mark start before the bridge call (:158-161, bridge at :168).
- Still write (`ScreenshotTarget.Save` :48-71): re-checks (:56), then `CopyReplacing` (:91-112): creates folders (:94), temp with CreateNew (:99), moves it over the destination (:105), deletes the temp on failure. Folders it made are left.
- Clip delivery (`ClipDelivery.Deliver` :24-46, from `RealtimeRecording.RemuxAsync` :966-968 and `RecordingCut.Place` :186): re-checks against start-time places (:35), then `MoveReplacing` (:122-136): creates folders (:124), renames with `MoveFileExW` (:217); across volumes copies to a temp, moves it, deletes the source (:130-135).

  Runs at every ending: stop (`GodotSession.cs:224`), restart (:659), game exit (:1344), detach (`GodotSession.Attach.cs:184`), shutdown (real-time recordings only, GMCP-345).
- Shutdown cap (`SessionRegistry.StopGamesAtShutdownAsync` :617-638, `ShutdownCap` :27): after 10 s prints to stderr (:628-633), `Shutdown` (:558-593) finishes, process exits; an exit mid-copy leaves the temp and folders made. Every recording warning is dropped there (`GodotSession.Realtime.cs:148-166` discards the outcome; :664-674 prints only exceptions).
- ffmpeg, Godot `--write-movie`, the bridge's `save_png`: write only under `<project>/.godot/godot-mcp/` (I8).
- The game, other processes/accounts (git, editor, another server, AV, indexers, cloud agents), the user: can create/delete/link entries, swap a folder for a junction, plant a link or hard link at the destination, create it first, delete the source, hold files open without delete sharing, use `\\?\` names ending in a space or dot, `git clean`.

## INVARIANTS (sites on main)
- I1 final path under an allowed place: `DestinationCheck.Run` :307-324, `PlaceOf`/`Holding` :335-346, `Walk` :415-434, `Follow` :489-509, `CheckFinalPath` :552-574, `CheckReached` :600-620, `OutputPlaces.Find` :46-59, `FinalOf` :94-112.
- I2/I3 (no .git; no .godot outside .tmp): `IsInsideDotGit` :364-365, `IsUnderDotGodot` :371-408, `CheckReached` :611-619.
- I6 never write through a link; a temp left by a failure is deleted: `CopyReplacing` :91-112, `MoveReplacing` :122-136, `FinalPath.Rename` :215-224.
- I7 the check that counts is the writing call's: `ScreenshotTarget.cs:56`, `ClipDelivery.cs:35`.
- I10 a reported path holds the file it names: `RealtimeRecording.cs:989-994`, `RecordingCut.cs:163`, `:321-346`, `GodotSession.Realtime.cs:194`, `:333`.
- I12 a failed delivery keeps the clip with a warning: the delivery sites plus `SessionRegistry.cs:664-674`.
- Dotted-name rule: `CheckNames` :203-209, `CheckOneReading` :216-224, `CheckLinkedNames` :234-251, called at :499 and :603.
- N1 (new) a failed write leaves the destination's folder tree as it found it: every folder it made removed deepest first if empty; one that cannot be removed is named in the refusal or warning. Sites `OutputDestination.cs:94`, `:124`, cross-volume `:132`, doc comments `:83-88`, `:114-118` ("A failed move deletes nothing" `:117`), callers `ScreenshotTarget.cs:65`, `ClipDelivery.cs:36`, `TOOLS.md:304`, `:1337`.
- N2 (new) two clips of one recording never deliver to one final file: compared by final path (links followed) at the mark and again at delivery. Sites `OutputTarget` :10, `Check` :57-81, `CheckFinalPath` :573, `Recording.CheckDestination` :106-126 (from :144 and `RuntimeTools.Record.cs:161`), `RecordingCut.CutClipsAsync` :147-163, `Place` :173-193, `ClipDelivery.Deliver` :24-46, `TOOLS.md:1331`.
- N3 (new) no temp of ours outlives the server's exit (or the exit's message names it): `SessionRegistry.cs:617-638` (:628-633), `:664-674`, `GodotSession.Realtime.cs:148-166`, `RealtimeRecording.cs:966-968`, `MoveReplacing` :130-133.
- N4 (new) the set of refused paths stays the same; a dotted name is refused wherever on a reached path, the place's own folders included; the refusal names the place when the name is the place's. Containment is never decided by `GetRelativePath` across a dotted name. Sites :499, :603, `CheckLinkedNames` :234-251, `Holding` :345-346, `OutputPlaces.FinalOf` :103, `Normalize` :186.
- N5 (new) a link whose reparse data names no target is malformed and refused before it is followed: `FinalPath.SubstituteName` :178-195, `Follow` :502.
- N6 (new) every path handed to a Win32 call reaches the entry .NET's own calls reach, whatever its length: `FinalPath.Of` :85, `ReadLink` :131, `Rename` :217.

## GMCP-343

### 343.1 a place whose real folder name ends in a dot or space
Real, in two reachable forms: (a) a non-last segment keeps a trailing space or dot through `Normalize` (`OutputPlaces.cs:186`), e.g. `X:\games\demo \game`; every reached position is scanned from the drive root (:430 or :451, then :603), so even `shot.png` is refused as "leads through a link to X:\games\demo \game\shot.png, whose name demo …". (b) the place's final form has the name (project/checkout reached through a link into `X:\games\demo.\game`):

`FinalOf` keeps it, `CheckFinalPath` (:573 then :603) refuses, and so does :499 for an absolute link in that place. A place whose own last name is `demo.` cannot be a place: `Normalize` trims it to `demo`.

"Scan only below the place" is UNSAFE: `GetRelativePath` normalizes, so `Holding` (:345-346) treats the trimmed sibling `X:\games\demo\game` as inside the dotted place; a caller's absolute path into the sibling would pass once the :603 scan skipped the place's text, `CreateFileW` would open the sibling, and `CopyReplacing` would write there (breaks I1). Today's root-to-leaf scan holds it.

Fix: keep the refused set exactly; change only the wording. In `CheckReached` and the :499 stored-target check, when the dotted name found falls inside the text of an allowed place's `Folder` or `WalkFrom` (given or final form) that the scanned path starts with, throw a place refusal instead of the link refusal. Match textually (case-insensitive ordinal, bounded by a separator), never through `GetRelativePath`. Text:

`path <path> lies under <the project|its checkout|the main checkout's .tmp folder> <F>, whose name <part> ends in a space or a dot; Windows reads such a name two ways, so named stills and clips are not written there`.

Tests (`OutputDestinationTests`): T1 `AProjectInAFolderNamedWithATrailingSpaceIsRefusedNamingTheFolder` (project at `<temp>\par \game` made through `\\?\`; `Resolve(project, "shot.png")`; red today: the message blames a link). T2 `AProjectReachedThroughALinkToADottedFolderIsRefusedNamingItsFinalPath` (project is a link to `<temp>\real.\game`; red today).

T3 `APathIntoTheTrimmedSiblingOfADottedFinalProjectIsRefused` (sibling `<temp>\real\game` exists; an absolute path into it is refused and the sibling stays empty; green today, guards the loosening; a mutation removing the scan turns it red).

For T2 the stored target must keep the dot: create the link with a `\\?\` target through `MakeSymbolicLink` (:653-663, skips without the symlink privilege) and assert `FinalPath.ReadLink(link)!.Target` contains `real.` as :238, :257-258 do; whether `mklink /J` keeps a dotted target is unverified.

Files: `OutputDestination.cs`, maybe `OutputPlaces.cs` (one place listing every place's text), `OutputDestinationTests.cs`, `TOOLS.md:321`, CHANGELOG.

### 343.2 an empty substitute name
`SubstituteName` accepts length 0 (`FinalPath.cs:188`) and `ParseLink` returns `StoredLink(kind, "", …)`; for a relative symlink `Follow` (:502) gets `current` back, so the link reads as its own folder. A mount point or absolute symlink with an empty name is already refused by `AbsoluteTarget` (:530-534). Nothing escapes (:567-571 or :594-597 refuse a differing read) but the malformed link is interpreted (N5). Fix: length 0 throws `Malformed()` at :188.

Test `FinalPathTests.AnEmptySubstituteNameIsMalformed`, Theory over `SymbolicLink("", RelativeFlag)` and `MountPoint("")`, with `AssertMalformed`; red today. Files `FinalPath.cs`, `FinalPathTests.cs`.

### 343.3 no end-to-end test of the reached-name check
`AReachedPathWithADottedNameIsRefusedBeforeContainment` (:265-274) calls the helper; the link tests (:227-245, :247-263) stop at :499. Fix: T1-T3 above reach it through :451→:603 and :573→:603.

## GMCP-346

### 346.1 folders made before a failed move
Folders are made only when the destination's folder is missing (:124 clips, :94 stills); stills share the gap. `AMoveDeletesNothingWhenItFails` (:524-538) cannot see it (its folder exists).

Fix: one helper for `CopyReplacing` and `MoveReplacing`: before creating, collect the missing ancestors (walk up while `!Directory.Exists`), create; on `IOException`/`UnauthorizedAccessException`, `Directory.Delete(f, recursive: false)` each deepest first, stop at the first that fails, and add "the folder <f> it made could not be removed: <why>" to the exception. Reword :83-88, :114-118. Tests:

`AFailedMoveRemovesTheFoldersItMade` (Theory: source missing; source held open with `FileShare.Read`; red today), `AFailedSaveRemovesTheFoldersItMade` (a stream whose Read throws; no `new` folder and no `.godot-mcp-*.tmp` left; red today). Files `OutputDestination.cs`, `OutputDestinationTests.cs`, `TOOLS.md:304`, `:1337`, CHANGELOG.

### 346.2 the shutdown cap cuts a copy short
After the cap (:619-634) the process exits; a delivery still in `source.CopyTo` (:102, synchronous, no token) dies, leaving `.godot-mcp-<random>.tmp` beside the destination and folders made; the source survives (deleted only at :135). Only real-time recordings deliver at shutdown today. Second gap: every warning at the exit is dropped (`GodotSession.Realtime.cs:164`). Fix (D5): at the exit, deliver by rename only:

`FinishRealtimeAsync` (called only from `SessionRegistry.cs:668`) marks the recording; `MoveReplacing` takes a required `allowCopy`; false and another volume throws `IOException` ("… on another volume; the server's exit leaves no time to copy it"), the usual delivery warning, clip kept in `recordings/`; `FinishRecordingAtShutdownAsync` prints the outcome's warnings to stderr (D6); the :628-633 message adds that a clip may be kept in `recordings/` instead of its destination.

Tests: the flag through `FakeRealtime`/`RealtimeEnvironment` with an injected rename reporting another volume (clip kept, warning in the outcome). The cap message cannot be tested without waiting out the static 10 s cap (forbidden wall-clock test): review only.

Files `OutputDestination.cs`, `ClipDelivery.cs`, `RealtimeRecording.cs:966-968`, `GodotSession.Realtime.cs:148-166`, `SessionRegistry.cs:628-633`, `:664-674`, `TOOLS.md:1324`, `:1335-1337`, `ARCHITECTURE.md:313`, `:319`.

### 346.3 tests not written
(a) cross-volume branch: an internal `MoveReplacing` overload taking the rename function (as `ClipDelivery.Deliver` takes `move` :24-29); tests force "another volume" and check the copy lands, the source is deleted, a hard link at the destination is not written through, a failure deletes the temp and keeps the source (green today except the N1 case; mutation proves them). A real two-volume test only gated on an env var. (b) a refused Movie Maker start never asks the game:

`_recording` is set only by a launch (`GodotSession.cs:672`) and `ActiveRecording` needs `HasGame` (`GodotSession.Recording.cs:28`); fix: an internal `MarkMovieAsync` overload taking the frame request as a delegate, used with `new Recording(path)`; test `AMovieStartWhosePathIsRefusedNeverAsksForTheFrame` (delegate never ran, no clip; green today; moving :158 below :168 turns it red).

### 346.4 one file by two spellings
:118 compares `FullPath` (`GetFullPath` :77, links not followed): `clips/a.mp4` and `j/a.mp4` (j a junction to clips) both pass; with overwrite clip 2's `MoveFileExW` replaces clip 1 and `Clips` (:330) lists two paths both holding clip 2 (breaks I10); a junction made after the marks also escapes a mark-time check. Fix: `Check` returns the final path it computes at :573 (off Windows the full path, GMCP-334's gap); `OutputTarget` carries it; `CheckDestination` compares it;

`RecordingCut` keeps the final paths delivered so far; `Deliver` refuses a clip whose freshly checked final path matches one: "the clip could not be moved to <dest>: clip <n> of this recording was delivered to <final>; it is kept at <clip>". Tests (`RecordingMarksTests`): `ASecondMarkNamingAnEarlierClipsFileThroughAJunctionIsRefused` (red today), `AClipWhosePathNowLeadsToAnEarlierDeliveredClipIsKept` (junction made between marks and cut;

`CutMakingClipsAsync` :428 must write different bytes per clip; red today). Files `OutputDestination.cs`, `ScreenshotTarget.cs:36`, `:56`, `RuntimeTools.Record.cs:143`, `Recording.cs`, `ClipDelivery.cs`, `RecordingCut.cs:136-193`, `TOOLS.md:1331`, `:1337`.

### 346.5 MoveFileExW gets plain paths
Confirmed: the installed `godot-mcp.exe` has the default manifest (asInvoker, no `longPathAware`); the csproj sets no `ApplicationManifest` (dotnet/runtime#43555); `LongPathsEnabled` is 1 here but Windows needs both. .NET's own calls add `\\?\` at 260+ chars or a trailing dot/space; the P/Invokes at :85, :131, :217 do not.

Outcomes (CreateFileW error unmeasured): not-found → a long existing entry reads as a plain folder; if the deepest existing folder is long, refused as "whose final path Windows cannot give"; if only the missing part is long, a still is written but a clip's `MoveFileExW` fails (kept with a warning, folders left, 346.1); error 206 → refused at `ReadLink`. No path past I1 found. Fix: `FinalPath` applies .NET's rule (prefix `\\?\` at 260+) to every path it passes.

Tests `AStillWhoseExistingFolderIsPastMaxPathIsAccepted`, `AMovePastMaxPathRenames`; red only if the test exe is not long-path aware either; read the built test exe's manifest first.

## OPEN DECISIONS (oracle's recommendation)
- D1 (343.1): wording only, refusals kept (recommended); accepting dotted folders needs literal containment everywhere plus `\\?\` walks (feat branch). Below-the-place scan ruled out (breaks I1).
- D2: "the place's own text" = all places, `Folder` and `WalkFrom`, given and final forms, matched textually.
- D3 (346.1): remove the folders a failed write made (recommended); a folder that cannot be removed is named.
- D4: same helper for stills (recommended).
- D5 (346.2): rename only at the exit (recommended).
- D6: print each recording's warnings to stderr at the exit (recommended).
- D7/D8: the rename and frame-request seams (recommended); two-volume test only env-gated.
- D9 (346.4): compare final paths at the mark and at delivery (recommended); a destination that is itself a symlink over-refuses, which is safe.
- D10 (346.5): `\\?\` inside `FinalPath` (recommended), not the manifest alone.
- D11: 346 owns the shutdown message; GMCP-345 drops its own sentence about the `.tmp`.

## DEPENDS ON
None consumed. GMCP-345 overlaps 346.2 (same `SessionRegistry.cs:628-633` edit): 346 first. GMCP-197 will consume 346.4's comparison and 346.3's seam if it places start marks with a path. GMCP-69 shares `RecordingCut.cs` (`EncodeArguments` :69-82), merge order only. GMCP-231/340/341/70/72/73 do not overlap.

## BRANCH
no for both (each step lands on main); D1's alternative would need `feat/dotted-places`.

## Order
Two briefs, GMCP-343 first (check only, no writes), then GMCP-346 (writes and bookkeeping), within 346: 346.1 with the 346.3a seam, 346.5, 346.4, 346.2, 346.3b.

## Found to add
At the server's exit every recording warning is dropped (`GodotSession.Realtime.cs:164`, `SessionRegistry.cs:670-673`), unless D6 folds into GMCP-346.
