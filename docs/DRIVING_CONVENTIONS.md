# godot-mcp driving conventions

These are the rules godot-mcp follows so an agent can drive a running Godot game: the `godot` MCP server (`src/GodotMcp.Server`, run from its installed copy) launches or joins the game, and the in-game bridge (`bridge/godot_mcp_bridge.gd`, an autoload injected through `override.cfg`, with the C# helper under `src/GodotMcp.Dotnet*`) carries out every runtime call inside the game's process.

The tools are documented in [TOOLS.md](./TOOLS.md), the design in [ARCHITECTURE.md](./ARCHITECTURE.md) and the rulings behind it in [DECISIONS.md](./DECISIONS.md). The comment under each heading (`<!-- rule: slug -->`) is the rule's identity for the user-level `conventions-sync` skill, which trades driving lessons between this server and the owner's other projects that drive apps; a new rule may be written without one, and the next sync assigns it.

A rule pulled from another project that this repo does not meet yet still sits here, as the lesson to build, with its work on the plan.

No rulebook section is declined whole; the rules left out are those about posting OS input or taking the foreground (the bridge injects input inside the game, on a hidden desktop), webviews and CDP, a hook compiled into the app or found through a discovery file (the bridge is injected per run and dials in), window-capture recording (the engine's own movie writer records), and a control's semantic action (a press goes through real pointer input, as a player's does).

## Server plumbing

### Run from an installed, versioned release copy and link the skill from it
<!-- rule: run-server-from-copy -->

Client repos register the installed exe (`%LOCALAPPDATA%\godot-mcp\godot-mcp.exe`), never a build folder, and a landing never installs: only a cut release runs `install`, so every driven project runs a released build. A running server locks the installed exe (robocopy fails with status 8 or more), so the installer first stops every server running from the install folder and prints one line per stopped server naming its Claude session and project, so the user can `/mcp` reconnect it.

The agent-facing skill is a junction into the install folder, never into the checkout, so the skill an agent reads always matches the server it talks to (before 0.12.0 a checkout junction documented tools the installed server lacked). Every build stamps `X.Y.Z+<sha>` and returns it as `version` in every launch and attach result, so a friction issue names the build that actually ran rather than whatever the version file says was last installed.

The same rule holds for code loaded into the driven app: the in-app C# helper is loaded from a content-hash copy under `%LOCALAPPDATA%\godot-mcp-cache\dotnet\<hash>\`, because a running app holds the DLLs it loaded until it exits; never point the app at build output.
Source: CLAUDE.md:45, docs/DEVELOPMENT.md:34, docs/DEVELOPMENT.md:67, docs/DEVELOPMENT.md:77, docs/ARCHITECTURE.md:89, CHANGELOG.md:76, CHANGELOG.md:320, ~/.claude/skills/godot-mcp/SKILL.md:84-87. Seen: skill/server mismatch fixed in 0.12.0; locked-exe install failure documented as a footgun.

### Keep stdout for the protocol and the MCP pipe away from child processes
<!-- rule: stdio-protocol-only -->

stdout is the MCP protocol: every log goes to stderr (the console logger's `LogToStandardErrorThreshold` is `Trace`) through `[LoggerMessage]` methods. A child must not inherit the server's stdin: on Windows a child started while the server blocks reading the MCP pipe stalls until that read returns (`run_project` hung until stdin closed).

Every child (the app, git, dotnet, ffmpeg) gets its own stdin, closed at once; a child that needs specific handles inherits exactly those (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`), and all child starts are serialised under one lock so no child inherits another's pipe ends and holds its output open.

A grandchild can do the same: a leftover MSBuild node or compiler server held a build's redirected pipes open after the build exited, so builds run with the parent's MSBuild variables removed and node reuse off, and the reader stops 5 s after the process exits and logs that it did.
Source: CLAUDE.md:29, src/GodotMcp.Server/Program.cs:26, src/GodotMcp.Server/Session/DesktopProcess.cs:18, docs/DEVELOPMENT.md:78, docs/DEVELOPMENT.md:80, docs/DEVELOPMENT.md:124. Seen: the `run_project` stdin hang (documented footgun).

### Refuse a bad call by name, and make every refusal name what exists
<!-- rule: refusals-name-the-fix -->

Every tool binds with options that refuse an unmapped key, and a call-tool filter turns any binding failure into a message naming the argument path, the kind it takes and the value given (`run_project has no argument 'optoins'; it takes: …`, `options.prepare takes a string, not true.`). Only an `McpException`'s message reaches the client; any other exception becomes "An error occurred invoking '<tool>'", which is what agents saw before 0.5.1, so tools rethrow session errors as `McpException`.

Refusals carry the next step: a missing node names the base it was read from, the deepest node that exists and up to 10 of its children; a text target that matches nothing lists up to five near misses; a covered target names the cover and both rects; a call without a session among several lists the live names; a missing prerequisite names the command that clears it (a recording cut without ffmpeg says `winget install Gyan.FFmpeg`, a red build refuses the launch with the parsed compiler errors).

A refused call ran nothing, so the agent fixes the call instead of debugging the app.
Source: docs/TOOLS.md:48, docs/TOOLS.md:55, docs/ARCHITECTURE.md:44, docs/DEVELOPMENT.md:79, src/GodotMcp.Server/Tools/ArgumentErrors.cs:15, src/GodotMcp.Server/Session/RecordingCut.cs:18-20, CHANGELOG.md:208, CHANGELOG.md:212, CHANGELOG.md:284. Seen: bare "An error occurred" replies before 0.5.1.

### Count every ceiling in load-adjusted time, with a wall-time backstop and a stall kill
<!-- rule: load-adjusted-timeouts -->

Other agents' builds and test runs load the machine, and a fixed wall-clock timeout turns that load into false failures. Every ceiling the server enforces, caller-set `timeoutMs` and `waitSeconds` included, runs on a clock that keeps wall time on an idle machine and advances by the machine's free share while other work loads it (sampled once a second with `GetSystemTimes`, the server's own job-object work subtracted, never below 5%).

Every ceiling also ends at 5x its value in wall time (the backstop), and a child that neither writes a log line nor uses CPU for 120 s is killed as stalled; the error says which of the three fired, with the load figures (`did not finish within 300 s of load-adjusted time (wall 812 s, machine free 37% on average)`).

Durations of an action (a drag's `durationMs`) are not ceilings, and pings, the shutdown grace and kill graces stay in wall time. The consequence for agents: a timeout is never a pause, so holding for a span of app time needs an app-time wait.
Source: docs/ARCHITECTURE.md:54-62, docs/TOOLS.md:60, CLAUDE.md:35, CHANGELOG.md:238, CHANGELOG.md:243. Seen: introduced in 0.4.0; the itest lanes ran at 15-35% load-adjusted time against wall on a loaded machine (docs/DEVELOPMENT.md:26).

### Give every request an id, cancel what timed out, then say whether the app is busy or stuck
<!-- rule: cancel-timed-out-requests -->

Every frame carries a request id and every reply echoes it; a reply for a timed-out id is dropped, never matched to the next request (the third-party server this one replaced had no ids and parsed JSON out of stdout from the first bracket).

When a request's release passes, the server sends `cancel {request}` so the in-app hook ends the step, wait, watch or script at its next frame and is free for the next call; each such request also carries `backstopMs` (5x its release), the hook's own real-time limit should the cancel never arrive, and a lost connection cancels every running request.

A cancelled script is dropped in the app, and the app's global time scale and pause are put back to their values at the call's start. Then the timeout is diagnosed: a 2 s ping tells a busy app (the ping answers) from a stuck main thread, and a stuck one's error adds the app process's CPU over a 1 s sample, its thread count, its main thread's wait reason and the last 20 stderr lines, sampled from the app's own pid, never a launcher's.

With a debugger attached (`CheckRemoteDebuggerPresent`), each call first pings for 500 ms and fails at once as "most likely paused at a breakpoint". A timed-out call therefore fails up to 3 s after its timeout.
Source: docs/DECISIONS.md:11, docs/DECISIONS.md:44, docs/DECISIONS.md:58, docs/DECISIONS.md:136, docs/DECISIONS.md:144, docs/ARCHITECTURE.md:46-47, docs/ARCHITECTURE.md:52, docs/DEVELOPMENT.md:117, CHANGELOG.md:230, CHANGELOG.md:239. Seen: 0.4.0 (cancel) and 0.5.0 (timed-out `run_script` kept running and left the game paused) fixes.

### Bound every wait on both sides, and return its outcome as data with the last value seen
<!-- rule: waits-bounded-outcome-as-data -->

`wait_for` takes a `timeoutMs` of 0 to 120000 (`gameMs` and `frames` are bounded too), and the server waits for the bridge's answer for the wait's release plus 5 s, never a shorter request timeout, so the transport never cuts a full-length wait short; the bridge's own `backstopMs` ends the wait should the cancel be lost.

A wait that times out is a result, not an error: `{met: false, elapsedMs, frames, last}`, carrying the condition's last value. `batch_drive` returns `{passed, steps, failedAt?}` rather than an MCP error, so the agent reads `passed`. Where a timeout must be an error, it carries the last observation, so an agent can tell "never matched" from "matched the wrong value".
Source: src/GodotMcp.Server/Tools/RuntimeTools.Time.cs:20-25, src/GodotMcp.Server/Tools/RuntimeTools.Time.cs:36-40, src/GodotMcp.Server/Tools/RuntimeTools.Time.cs:77, bridge/godot_mcp_time.gd:663, src/GodotMcp.Server/Tools/RuntimeTools.Batch.cs:84. Seen: 0 here (seeded from yaat).

### Own the app's real process, not the launcher you started
<!-- rule: own-launched-processes -->

On Windows the process the server starts is `Godot_console.exe`, a wrapper that puts the app in a job object and exits only when every process in it has, and a wrapper killed from outside reads exit code 0 or -1 whatever the app did.

So the app reports its own pid in the hello, the server opens a handle on that pid at the hello (a pid opened later may already name another process), and the stop waits on that handle: a ping, a quit request, a kill after 3 s (30 s for a recording run, whose clean quit finalises the movie), `leftRunning` listing processes the app left behind, `quitMs` for how long a quit took, and a `gameExitCode` that is null after a kill.

A killed process reports exited 0.1-2 ms after `TerminateProcess` but keeps its working folder for another 18-38 ms, so wait for the process handle to be signalled (synchronous `WaitForExit`) before touching the folder (`stop_project` returning before that was a 0.4.0 fix). Every process the server starts joins its job object, and server exit kills every run and removes every file it wrote.
Source: docs/DEVELOPMENT.md:69-70, docs/DEVELOPMENT.md:116, docs/ARCHITECTURE.md:76, docs/ARCHITECTURE.md:85, docs/TOOLS.md:94, CHANGELOG.md:232, CHANGELOG.md:248. Seen: measured 2026-09-28; 0.4.0 and 0.5.0 fixes.

### Name every session and keep no default one
<!-- rule: no-default-session -->

One server process serves one client, and several agents in that client share it and cannot be told apart, so a default session one agent set would send another agent's calls, `stop_project` included, to its app.

`run_project` returns a session name and every runtime tool takes it as its last argument; with one session the name may be left out, with several the call is refused listing them, and the refusal and every `session` description tell agents to pass the returned name from the first call.

A new session defaults to the project folder's name, taking `<name>-2` when a live session on another folder (another worktree) holds it; a stopped run stays listed for its crash output and a restart by name until the name is reused. Input calls to one session are serialised by a per-session gate, while two sessions play input at once.
Source: docs/DECISIONS.md:33, docs/TOOLS.md:49, docs/ARCHITECTURE.md:68, docs/DEVELOPMENT.md:101, CHANGELOG.md:137, CHANGELOG.md:181. Seen: decided 2026-09-28 (#35) after the default-session design.

### Keep a fixed tool surface and give agents the tools at install
<!-- rule: fixed-tool-surface -->

App-defined commands reach agents through two fixed tools (`list_game_tools`, `call_game_tool`) rather than one dynamic MCP tool each, because an agent spawned with a `tools:` line never sees a tool added after it was configured, and whether a client re-lists on `list_changed` is unverified.

Each tool is given a class from its annotations (`read`, `drive`, `edit-live`, `edit-scene`), and the install rewrites the `tools:` line of every agent file marked with its classes (`--sweep-agents`), since an agent missing a tool falls back to a workaround.

Every tool sets all three hints explicitly (`openWorldHint` false), and a tool that runs app code to read (a state read, a watch expression) is annotated destructive with a `read` class override, so a client that auto-approves reads never runs app code unasked while read-only agents still get it. A smoke test pins every tool's hints.
Source: docs/DECISIONS.md:93, docs/DECISIONS.md:104, CLAUDE.md:47, docs/ARCHITECTURE.md:35, docs/ARCHITECTURE.md:166. Seen: documented.

### Probe the clip encoder by encoding, and keep paths out of any shell
<!-- rule: recording-shellout-hardening -->

The recording cut runs ffmpeg directly with an argument list (`ProcessStartInfo.ArgumentList`, no shell), so no `cmd` expansion can touch a path, and a failed cut reports ffmpeg's exit code and its log.

The encoder is fixed at `libx264`, though: choose `h264_nvenc` only when a 0.1 s encode of a generated 256x256 picture succeeds, else `libx264`, since an ffmpeg build lists nvenc on a machine without a GPU, and a 64x64 probe fails with "Frame Dimension less than the minimum supported value" even on an RTX 4090 (measured in another project), which would push every machine onto libx264.

Should the cut ever become a pipeline run under `cmd`, pass every path as a quoted `"%NAME%"` set in the cmd process's environment (cmd expands `%NAME%` even inside quotes but never rescans what it expanded, so a `%`, `!` or `&` in a folder name stays literal), and report a pipeline that dies within 300 ms of the start with its exit code and its last log lines.
Source: src/GodotMcp.Server/Session/RecordingCut.cs:69-81, src/GodotMcp.Server/Session/RecordingCut.cs:247-250, src/GodotMcp.Server/Session/ToolProcess.cs:169-174; cmd.exe variable-expansion rules. Seen: 0 here (seeded from yaat).

### Smoke-test the server over stdio and drive an unreleased build without installing it
<!-- rule: test-server-over-stdio -->

A smoke test drives the built server over stdio with the SDK's client and pins the tool list and every annotation. `run.ps1 drive -Calls <file.json>` runs the tree's own build against a real app from a JSON array of `{tool, arguments}` (a fresh server per run, images saved, the first failed call stops it, the server's stderr in its own log), so a change is seen in a real app before any release.
Source: docs/DEVELOPMENT.md:38, docs/ARCHITECTURE.md:166. Seen: documented.

### Test against the real engine on a hidden desktop, one app per class
<!-- rule: test-against-real-host -->

Integration tests launch the real engine on a hidden desktop of their own so no window shows; classes share one app per class with a reset before each test (a full run went from 591 s to 429 s); fixtures are copied into committed temp git repos and checked clean after stop.

Tests that assert wall-clock timings run in one lane, never two at once, and a wall-clock bound is set by the slow path it rules out, never a small multiple of the fast one: a lone process sees timers about 30 ms late on a 75%-busy machine, the full parallel suite has shown 2.5 s.
Source: docs/DEVELOPMENT.md:26, docs/DEVELOPMENT.md:48, docs/DEVELOPMENT.md:71-72, docs/DEVELOPMENT.md:109. Seen: measured speed-up and timer lateness recorded in DEVELOPMENT.md.

## Driving an app

### Drive the game from inside its process
<!-- rule: drive-owned-app-in-process -->

A survey of desktop-automation MCP servers (in another project) found none that drives an app from outside its process and fully avoids stealing focus; the ones that never do run a hook inside the app.

Here every runtime call goes through the bridge inside the game: the server listens on loopback and the bridge dials in with the session's token, and input, reads and writes all run in the game's own process. A game started without the bridge is joined by arming its folder first (arm-before-start), never by driving its window from outside.
Source: src/GodotMcp.Server/Wire/BridgeListener.cs:22, src/GodotMcp.Server/Session/GodotCommandLine.cs:316-317, docs/TOOLS.md:81-85. Seen: 0 here (seeded from yaat).

### Drive the real game, and write down what each lesser route cannot see
<!-- rule: real-app-over-frontend-only -->

Runtime tools always drive the real game with the bridge in it, never a stand-in. The lesser routes are written down with what they lack: headless tools read the project's files in a headless Godot without running the game, and a headless display never draws; `preview_scene` pauses one scene before its first frame, still runs its `_ready` and the autoloads, and adds a camera to a 3D scene that has none.

In another project a frontend-only route lacked whole subsystems while looking like the app, and a run that "showed nothing" was mistaken for a bug; so a route added here says exactly which subsystems it leaves out.
Source: src/GodotMcp.Server/Session/GodotCommandLine.cs:293-317, src/GodotMcp.Server/Tools/HeadlessTools.cs:11-15, docs/TOOLS.md:132-134, docs/DEVELOPMENT.md:95. Seen: 0 here (seeded from towercab-3d).

### Launch isolated and in the background, start driving when the bridge answers, and close what you opened
<!-- rule: launch-isolated-lifecycle -->

Point the game's data folder at a scratch folder, so the developer's saves, settings and logs under `user://` stay untouched; a game that reads its settings only at startup gets what the session needs written into the scratch folder before the launch. This is not built yet: a launch's `override.cfg` sets the autoload, the gamepad shut-out and the quiet display settings, and nothing moves `user://`.

The rest holds: the game is ready when the bridge says hello, not when the process starts (`run_project` returns once it connects); the agent starts the game itself, quiet and in the background, and stops it when finished; a game the user started is joined through arming and left running at detach.

Being able to launch and attach is what turns a "the human runs the game" step in a plan into an agent-run check. A server's relative defaults resolve against its own checkout, so a worktree session passes absolute paths (in another project screenshots had landed in the main checkout and the wrong build was launched).
Source: src/GodotMcp.Server/Session/OverrideFile.cs:129-138, src/GodotMcp.Server/Session/GodotCommandLine.cs:316-353, docs/TOOLS.md:66-68, docs/TOOLS.md:81-89. Seen: 0 here (seeded from towercab-3d, yaat).

### Build before launch, and build before stopping on a restart
<!-- rule: build-before-launch -->

Every launch, restart and headless call runs a prep first: it builds the app when its assembly is missing or older than its sources (newest tracked source time against the assembly, plus a stamp touched after each green build so a non-compile change does not rebuild every run) and runs the asset import when imported files are missing, a source asset changed since its import, or its import settings changed, writing logs named in the result's `prep`.

A red build refuses the launch with the parsed compiler errors and the configuration built.

A restart builds first, then stops, then launches, so a red build leaves the old app running, and the restarted session keeps its name, error sequence and output. A missing assembly first shows as misleading startup errors, so under a red build those symptom lines are dropped and a `csharp` block reports the cause.

Building beside a running app is safe (it loaded its assembly into memory); an import is not and is refused while another session's app runs on the folder. A project can route the build and import through its own wrapper (`prepWrapper`, its gate script).
Source: docs/DECISIONS.md:43, docs/ARCHITECTURE.md:70-71, docs/ARCHITECTURE.md:77, docs/DEVELOPMENT.md:126, docs/TOOLS.md:56, docs/TOOLS.md:102, CHANGELOG.md:53-54, CHANGELOG.md:278. Seen: 0.3.2 fix (misleading autoload errors).

### Start the app quiet, and make it quiet at launch rather than by swallowing input
<!-- rule: quiet-by-default -->

A default run is unfocused, out of sight, click-through, silent and frame-capped; `quiet: false` is only for when the user wants to watch or play along (with `mute` to watch without sound).

Each part is set at launch, because later is too late: the engine creates its main window focused before any app script runs and un-focusing it later does not release focus, so the window is created with no-focus.

Windows clamps an off-screen initial position onto the primary screen and the window shows about 0.9 s after start, before any app code, and the console wrapper drops `STARTUPINFO`'s `SW_HIDE`.

So on Windows a quiet run is started on a hidden desktop (`<station>\godot-mcp-<server pid>` in the server's own window station, `CreateProcessW` with `lpDesktop` naming that station: a child named on `WinSta0` from an ssh session or a service cannot start), where screenshots, input and recording all work (parking it off-screen from app code had left it visible about 300 ms at (0,0)).

Audio is off through the dummy audio driver, never by muting the app's buses, so the app's own mute stays testable and nothing leaks into its saved settings. A hidden window kept VSync and drew at the monitor's 239 Hz, so a quiet run caps at 60 fps unless the project sets its own cap.

Real input is kept out by the window never receiving it, not by swallowing it: an always-on swallow missed handlers that run before it and input polling, and killed input the app synthesises itself (on-screen keyboards, virtual cursors).
Source: docs/DECISIONS.md:38-39, docs/DEVELOPMENT.md:95, docs/ARCHITECTURE.md:72, docs/TOOLS.md:53, ~/.claude/skills/godot-mcp/SKILL.md:51. Seen: issue #6 (239 Hz); window timings measured with a 5 ms window poller.

### Say "hands off" before driving a game on the user's desktop
<!-- rule: say-hands-off-first -->

A quiet run sits on a hidden desktop that the user's mouse and keyboard never reach, so it needs no warning. A run on the user's own desktop does: a `quiet: false` run, or a game the user started and an agent joined.

The bridge swallows real mouse input only while a gesture plays or an injected button is held, and never swallows real keys, so a key the user presses or a mouse they move between calls can break the drive. Before driving such a game, tell the user "hands off"; the skill does not say so yet, since it offers `quiet: false` only for watching or playing along.
Source: ~/.claude/skills/godot-mcp/SKILL.md:51, ~/.claude/skills/godot-mcp/SKILL.md:64, docs/DEVELOPMENT.md:88-92. Seen: 0 here (seeded from towercab-3d).

### Mark injected pointer events and keep the pointer while a gesture plays
<!-- rule: mark-injected-input -->

Injected mouse events carry a device mark (`0x6D6370`), and while a gesture plays or an injected button is held the hook swallows unmarked (real) mouse buttons and motions, so a stray real mouse cannot break a drag (a drag still drops under a stream of real motions). Keys cannot carry the mark (the engine's built-in UI actions match only the keyboard's device id), so real keys are never swallowed and a quiet run keeps them out by never having focus.

The app's own hover re-picks (a cursor-state update, a drop's end, a scene change) read the OS cursor, which is garbage on a hidden desktop, so while injected input owns the pointer the hook re-picks at the injected pointer every frame before draw, with a motion no app handler sees; a real motion hands the pointer back (a 0.14.0 fix: hovers used to drop to the hidden cursor).

Every injected event is a new object followed by a flush, so the engine neither merges nor delays it, and every input call answers once its gesture has ended and two more frames have run.
Source: docs/DEVELOPMENT.md:88-92, bridge/godot_mcp_bridge.gd:57, bridge/godot_mcp_input.gd:98, docs/ARCHITECTURE.md:162, docs/TOOLS.md:146, CHANGELOG.md:24. Seen: 0.14.0 hover fix.

### Expect real devices to reach even a quiet app, and inject on a device id no real one holds
<!-- rule: real-devices-leak-in -->

An unfocused window still gets the machine's real gamepads, which feed the same all-device actions as injected input. This machine has four, one resting its stick at -0.98 and jittering; its jitter moved GUI focus between a click's press and release and dropped the click: a stress test lost one press in twenty about 3 runs in 20 with the pads live, and none in 549 clicks with them shut out.

Shutting real pads out is opt-in, since it costs focus-out notifications the app sees (one game mutes on them), and every shared test session uses it.

Injected pad input goes, unless told otherwise, on the lowest device id no real pad holds, kept for the app's life and reported as `device`, so the agent claims that pad in a pad-selection screen as a player would; an explicit id a real pad holds is injected with a warning. An injected pad never shows as connected, so an app that waits for a connected device needs an OS-level virtual device.
Source: docs/DEVELOPMENT.md:97-100, docs/DECISIONS.md:53, docs/DECISIONS.md:158-161, docs/TOOLS.md:214-216, ~/.claude/skills/godot-mcp/SKILL.md:52, CHANGELOG.md:25. Seen: the measured flake rate above.

### Take input coordinates in one space and map them in one place
<!-- rule: one-coordinate-space -->

The replaced server clicked in window pixels while its UI list reported viewport pixels, so a letterboxed window missed small buttons. Here every `x, y` an input tool takes, and every screenshot `crop`, is in viewport coordinates, the space the UI list reports rects in, converted to window coordinates in one function through the viewport's screen transform (measured for a 1000x900 window over 640x360 content: scale 1.5625, a 169 px bar above and below).

A screenshot's pixels are not input coordinates: that viewport captures 1000x562 in that window, so a point read off a screenshot misses; aim at an element, and for a world-space object aim at the node and let the hook project it through its camera, reporting where it aimed in `aimedAt`. A window the system would not size as asked is reported in a `warning`, since input and screenshots then work in the window it has.
Source: docs/DECISIONS.md:9, CLAUDE.md:31, docs/TOOLS.md:51, docs/DEVELOPMENT.md:85-86, docs/TOOLS.md:67, CHANGELOG.md:168, CHANGELOG.md:172. Seen: the replaced server's misses (2026-09-25).

### Resolve the target and check what the press will hit before pressing anything
<!-- rule: refuse-before-pressing -->

An element target is checked before any event: a hidden node, one being freed, or a bare name several nodes share is refused. After the aim motion, the control the app itself hovers at that point must be the target or its child, or the press is not sent and the error names what covers it with both rects (a tooltip over the centre counts; a centre outside the viewport is refused as off-screen, not covered).

A text target matches the exact shown text, trimmed and case-sensitive, with no substring fallback ("Play" would press "Play Again" on the wrong screen); several matches are refused with their rects rather than taking the first, since tree order is not screen order.

Results say what was really hit (`pressedOn`, `releasedOn`, `guiDragStarted`, `dropAccepted`), the first thing to read when a click seems to do nothing. Never reach a tab, row or menu entry by setting its state and emitting its signal: that skips the input a player makes; even popup-menu items, which expose no rect, are found by real pointer motions. A showing tooltip is closed before a press, or the tooltip, removed a frame late, is what the press reports hitting.
Source: docs/DECISIONS.md:83-87, docs/TOOLS.md:146, docs/TOOLS.md:152, docs/ARCHITECTURE.md:39, ~/.claude/skills/godot-mcp/SKILL.md:43, ~/.claude/skills/godot-mcp/SKILL.md:63, CHANGELOG.md:26-27, CHANGELOG.md:307. Seen: 0.1.2 (hidden or covered targets pressed silently) and 0.14.0 (off-screen read as covered) fixes.

### Judge an action by its effect: read what was hit and read values back
<!-- rule: check-action-effects -->

A tool that reported success is not proof the action worked; in another project two confirmation dialogs stacked up unseen while the agent concluded "save did nothing", and a set-text call reported success on a box that reverted.

Here every pointer result says what was really hit (`pressedOn`, `releasedOn`, `dropAccepted`), a dialog or menu an action opened is caught by `wait_for {uiChanged}`, which diffs the visible controls, the focus owner and the top popup against the baseline the gesture took, and a write is read back by the bridge itself (verify-writes-read-back). After an input, read these before judging the result; a target that vanished because the action closed it may be success.
Source: bridge/godot_mcp_input.gd:80, bridge/godot_mcp_input.gd:349, bridge/godot_mcp_input.gd:465, src/GodotMcp.Server/Tools/RuntimeTools.Time.cs:84. Seen: 0 here (seeded from yaat).

### Wait on a condition after every action, and treat a timeout as a result
<!-- rule: wait-for-conditions -->

A drive is start, look, act, wait, check, then stop. After each act, `wait_for` one condition (a node exists, a property equals, a signal fires, an expression holds), checked every frame, default 10 s, at most 120 s; a timeout is `met: false` with the last value, not an error.

When the effect cannot be named in advance (a dialog, a menu), `uiChanged` diffs the visible controls, the focus owner and the top popup against a baseline the first gesture takes. Because timeouts stretch under load, a timeout is never a pause: hold for app time with `gameMs` or `frames`, which count the app's own scaled delta.

When the moments count from an effect's start, pass the method that starts it as `options.call`, and the hook calls it in the frame the clock starts, with no round trip in between (#41: a `call_method` then a wait missed the effect's start).

A wait can capture the very frame it was met on, for a scene that moves faster than a following screenshot. Wait on the value needed, not "not empty" (a placeholder text holds too early), and read `failedChecks`, which says the condition errored before it held.
Source: ~/.claude/skills/godot-mcp/SKILL.md:12-19, ~/.claude/skills/godot-mcp/SKILL.md:44, docs/TOOLS.md:257-259, docs/DECISIONS.md:41, docs/DECISIONS.md:70, CHANGELOG.md:13, CHANGELOG.md:148-149. Seen: #41.

### Wait out a heavy load on the signal that it finished
<!-- rule: wait-after-heavy-transition -->

Some transitions stall a game long after the call that started them returned (a scene change, a level load); in other projects one stalled timers for about 10 s and another dropped input for about 10 s while its data loaded. Reads in that span describe the old scene or a half-loaded one, and input is lost.

A game's docs name each heavy transition and the signal that ends it, and the agent waits on that with `wait_for` (a signal, a node that exists once loading ends, a property or an expression) before reading state or sending input; a measured stall is a fact about one run, not a settle time the game guarantees.
Source: src/GodotMcp.Server/Tools/RuntimeTools.Time.cs:71-84, docs/TOOLS.md:257-259. Seen: 0 here (seeded from towercab-3d, yaat).

### Read and set state through the game's own commands, never by scraping text, held keys or UI paths
<!-- rule: hook-over-scraping -->

When a run needs a value the UI only renders as text, a pose it can reach only by holding keys, or setup behind a long or fragile UI path, that is a missing command in the game, not a scripting problem: in another project an agent read a camera heading by scraping status text and aimed with timed key presses, slow and imprecise, until the hook got get, set and look-at calls.

Here the game marks its own commands (`list_game_tools`, `call_game_tool`), opts nodes into a one-frame state read (`get_game_state`), and any live value is reachable through the node and C# member tools. Setup goes through those; the press under test still goes through real input as a player's would (refuse-before-pressing).
Source: src/GodotMcp.Server/Tools/RuntimeTools.CSharp.GameTools.cs:18-19, src/GodotMcp.Server/Tools/RuntimeTools.State.cs:18, src/GodotMcp.Server/Tools/RuntimeTools.CSharp.cs:19, docs/TOOLS.md:311-346. Seen: 0 here (seeded from towercab-3d, yaat).

### Force rare conditions through a dev-only command, and know what it replaces
<!-- rule: force-conditions-dev-panel -->

Do not wait for a rare real condition (a failure, a late state, a random event) to test what renders it: the game marks a dev-only command that forces it (its `GodotMcpToolAttribute`), and the agent calls it by name. Know what that command replaces: in another project a weather panel's Apply replaced all weather, clouds included, so a run that applied rain over an existing overcast lost the clouds.
Source: src/GodotMcp.Dotnet.Core/ToolMark.cs:12, docs/TOOLS.md:311-322. Seen: 0 here (seeded from towercab-3d).

### Know what a screenshot leaves out
<!-- rule: what-capture-misses -->

A capture is the main window's render target, so popups and tooltips that are separate OS windows are missing from it: every grab (screenshots, baselines, frame steps, waits, previews) pastes each visible non-embedded window at its offset from the main one (a 0.5.0 fix).

A main window parked off-screen made Windows clamp a native popup to (0,0) and lose that offset, one more reason a quiet run sits at (0,0) of a hidden desktop. A window that cannot draw (minimised, or a low-processor mode with nothing changed) produces no frame: a step is refused up front and a wait's screenshot answers a warning instead of an image.

A paused app starts no tooltip timer, so hover while it runs and pause after. A screenshot returns an inline preview at most 480 px wide plus the full-size path (`path_only` when only the file matters), saved under an ignored folder as `<UTC stamp>-<pid>.png`, so two apps on one folder never write the same file. Baselines are compared in the app with a per-channel tolerance (default 2/255, about 0.1 s at 720p), after pausing for a stable frame.
Source: docs/DECISIONS.md:37, docs/DECISIONS.md:45, docs/DECISIONS.md:57, docs/DEVELOPMENT.md:85, docs/DEVELOPMENT.md:102, docs/DEVELOPMENT.md:112, docs/TOOLS.md:122, CHANGELOG.md:231. Seen: 0.5.0 fix.

### Record in app time with marks, and keep every drive replayable
<!-- rule: record-and-replay -->

Recording uses the engine's own movie writer from launch at a fixed 60 fps, so app time stops matching wall time (a light scene records at about 240 fps of wall time, a heavy game below 60); while recording, gesture durations and wait timeouts count clip time, or they play at the wrong length in the clip (a 0.7.0 fix).

`record_mark` start/stop pairs become H.264 MP4 clips when the run ends; `dropIdle` cuts every frame identical to the one before, so the wall time between agent calls leaves the clip, at the cost of its audio and of deliberate stills; a clip whose encode fails falls back to a stream-copied AVI and says so.

For input, the hook records both a person's events and injected ones on its own single clock (merging server-logged gestures with app-logged events put two clocks a round trip apart) and streams them in batches every 250 ms or 100 events, so a capture survives a crash and replays through `simulate_input`. A random stress run draws with SplitMix64 rather than `System.Random(seed)`, which is not stable across .NET versions, so a failing seed replays exactly.
Source: docs/ARCHITECTURE.md:75, docs/DEVELOPMENT.md:93, docs/DECISIONS.md:52, docs/ARCHITECTURE.md:154, docs/TOOLS.md:196-198, docs/TOOLS.md:208-210, CHANGELOG.md:158, CHANGELOG.md:173, CHANGELOG.md:291-295. Seen: 0.7.0 and 0.8.0 fixes.

### Answer file dialogs before the action that opens them
<!-- rule: queue-file-answers -->

A native file dialog (a `FileDialog` with `use_native_dialog`, or `DisplayServer.file_dialog_show`) runs outside the game's windows, where injected input cannot reach it; in another project posted typing left such a picker empty and real keys were refused because the foreground belonged to it.

There is no tool for this yet. The lesson to build: while driven, the game opens no native dialog; the agent queues one answer (a path or a cancel) per dialog before the action, each dialog takes the oldest, and one opened on an empty queue fails at once.
Source: no FileDialog handling anywhere in bridge/ or src/ (searched); bridge/godot_mcp_bridge.gd:600-615 (the bridge's command table). Seen: 0 here (seeded from yaat).

### Name what the driver cannot reach, and the route for each
<!-- rule: name-out-of-reach-ui -->

The bridge reaches only what the engine draws and routes input to. Name each surface it cannot reach, and refuse it as such rather than failing obscurely: a native menu is refused as "an OS window of its own" with the settings that would embed it, and a world node in a native window is refused since input reaches only the root and its embedded windows. A refusal is a named error returned before any input is sent, never a half-sent gesture (the bridge's reach checks run before the first event).

Where the game can avoid opening a native surface while driven, it does, and a tool answers in its place: a native file dialog takes a path the agent queued instead of opening.

Native file dialogs are not named yet, in the docs or in a refusal, and no queue answers them yet, so an agent can still burn turns trying to click one; name them, with the route around them (queue-file-answers), beside the native menus.
Source: bridge/godot_mcp_popup_targets.gd:19-21, bridge/godot_mcp_popup_targets.gd:138, bridge/godot_mcp_targets.gd:55, docs/TOOLS.md:146. Seen: 0 here (seeded from towercab-3d).

### Read the game's output by its tail, and log when the frame rate drops
<!-- rule: unified-log-tail -->

`get_debug_output` keeps the last 500 lines of each of the game's streams and pages them by line number, so an agent reads the newest lines without pulling everything into context, and the bridge flushes the game's errors before every reply, so nothing logged during a call arrives after it.

Missing still: a frame-timing line logged only when the frame rate drops below a floor (50 fps in another project), so a slowdown is diagnosable from the output without noise. Allow for any flush interval before concluding a line is absent.
Source: src/GodotMcp.Server/Tools/ProjectTools.cs:285-297, bridge/godot_mcp_bridge.gd:897-906; no frame-rate check in bridge/ (searched). Seen: 0 here (seeded from towercab-3d).

### Log driving friction as it happens, in a tracked place, and build the tools from it
<!-- rule: friction-log-per-session -->

Every session that drives a game through godot-mcp files each friction as it happens (a tool missing, confusing, slow or wrong for the job) as a GitHub issue on this repo, one issue per friction, naming the build that ran (`version`), what happened against what was needed, and the workaround; an implementer reports it in a `godot-mcp friction:` line for its session to file, and a fix closes the issue.

Notes kept in a scratch file are lost to the next session. The friction drove the design here: sixteen features were built from it before the cutover.
Source: ~/.claude/skills/godot-mcp/SKILL.md:65-89, docs/DECISIONS.md:25. Seen: 0 here (seeded from towercab-3d, yaat).

## App-side hook

### Keep the bridge out of every shipped build
<!-- rule: dev-flag-gates-hook -->

The bridge is never compiled into the game, so no release build carries it and no runtime setting can turn it on there: it is an autoload named in an `override.cfg`, which only game runs read (the editor, import and export never do). Every extension of the bridge is dev-only the same way.
Source: src/GodotMcp.Server/Session/OverrideFile.cs:129-132, docs/DECISIONS.md:16-18. Seen: 0 here (seeded from towercab-3d).

### Inject the hook at launch, never commit it, and let it switch itself off
<!-- rule: inject-never-commit -->

The replaced server wrote its bridge into `project.godot` and its stop could leave it there, which had earned a commit rule in two game repos and the shared conventions. Here the hook is an autoload named by an `override.cfg` beside the project file, which app runs read and the editor, import and export never do, so it cannot reach a shipped build: dev-only by construction, with nothing compiled into the app.

The file's first line is a marker; it is hidden from git by `.git/info/exclude` and removed at stop or detach, and an `override.cfg` without the marker is the user's and is refused, never overwritten. Everything the hook and server write (screenshots, baselines, recordings, logs) goes under the ignored `.godot/godot-mcp/`, so after a drive the tree is clean.

The hook turns itself off where it is not wanted: headless tool runs set `GODOT_MCP_OFF=1` and it frees itself silently, and an app started by hand from a leftover override finds no server, warns, restores its parked window and frees itself.
Source: docs/DECISIONS.md:8, docs/DECISIONS.md:16-18, CLAUDE.md:28, ~/.claude/skills/godot-mcp/SKILL.md:50, docs/ARCHITECTURE.md:37, docs/ARCHITECTURE.md:69, docs/ARCHITECTURE.md:160, bridge/godot_mcp_dormant.gd:40. Seen: the replaced server's leftovers (decision record); 0.8.1 fix for the hand-started leftover.

### Reference-count a shared hook file across servers and sweep stale ones at start
<!-- rule: refcount-shared-hook -->

Several sessions share one project folder (a game server and two clients), so the injected file is written by the first live session and removed with the last, across server processes too.

Its second line lists the owning servers (`pid@start ticks`), a release takes this server off and deletes the file only when no live owner is left, every read-modify-write runs under an exclusive lock on a machine-wide list of folders (retried up to 2 s), and each server's startup sweep deletes marked files whose owners have all exited.

A session whose settings (quiet, real-pad shut-out, hook path) differ from a live owner's is refused naming that server's pid and how to match it; a warning with the last writer winning was rejected, since the first server's app would change under it and only the second agent would be told. Each of these came from an incident: one server deleted another's live file (0.8.1), and two servers ending sessions at once lost an owner and left the other's game without a bridge (0.11.1).
Source: docs/DECISIONS.md:34, docs/DECISIONS.md:78, docs/ARCHITECTURE.md:69, CHANGELOG.md:85, CHANGELOG.md:89, CHANGELOG.md:141-142. Seen: 0.8.1 and 0.11.1 fixes.

### Remove only your own hook instance
<!-- rule: install-owner-cleanup-own -->

A cleanup deletes the hook only while it is still this server's: `override.cfg` is removed only when no live owner is left on its owners line, and a file without the marker, which is the user's, is refused rather than overwritten or removed, so a second session or a newer hook is never deleted from under its owner.
Source: src/GodotMcp.Server/Session/OverrideFile.cs:119-127, docs/DECISIONS.md:34. Seen: 0 here (seeded from towercab-3d).

### Have the server listen and the hook dial in with a token
<!-- rule: hook-dials-in-token -->

The server binds `127.0.0.1:0` once for its lifetime and passes the port and a random 32-byte session token to the app in environment variables, which removed the replaced server's free-port race and its regex-editing of the port into script. One accept loop routes each hello by its token; the hello carries the token, the project path (a mismatch catches a stale hook), the app's own pid (for the watchdog and a debugger's attach) and the window's size. Frames are a 4-byte length plus UTF-8 JSON.

The hook dials once when it starts and says hello in its first frame, after the app's own start-up code, and never redials; the listener holds a hello read open while any session waits for a hook, so an app frozen at a start-up breakpoint keeps its connection, and a launch's handshake allows 15 s of load-adjusted time.

The hook's JSON parser reads every number as a float, so it echoes ids as integers and the server accepts integral floats; replies from the in-app C# helper travel as untouched strings.
Source: docs/DECISIONS.md:134-137, docs/DECISIONS.md:146, docs/ARCHITECTURE.md:74, docs/DEVELOPMENT.md:81, docs/DEVELOPMENT.md:115, src/GodotMcp.Server/Session/GodotSession.cs:37. Seen: documented.

### Arm a folder before the app starts to join it later
<!-- rule: arm-before-start -->

The hook is loaded only when an app starts, so an app started without it can never be joined. `arm_project` writes the override plus `armed.json`, and every windowed app started on the folder from then on carries a dormant hook: it dials nothing and registers no logger, writes `dormant/<pid>.json`, and checks twice a second, even while paused, for its own `join-<pid>.json`; a headless run (a smoke, a test runner) never goes dormant, so it is never joined by mistake.

`attach_project` joins the one dormant app, takes a pid to choose among several and refuses with the list otherwise; joins of different pids wait at once. A detach returns the app to dormant, releasing every key, button, action and axis the drive left held (keys were missed until 0.11.1) and unparking a window a quiet join parked.

Arming as a side effect of an attach that might time out was rejected. A plain attach still works for an app started after the call: the server writes a one-use attach file the hook reads at start.
Source: docs/DECISIONS.md:71-79, docs/ARCHITECTURE.md:78-81, docs/DEVELOPMENT.md:118, docs/TOOLS.md:81-85, CHANGELOG.md:91, CHANGELOG.md:103. Seen: #48; 0.11.1 fix.

### Hand over live objects through a few generic entry points
<!-- rule: one-typed-window-global -->

The bridge does not wrap engine calls one by one: it hands over the live scene tree and the game's C# objects through a few generic tools (the node tools `inspect_node`, `set_property` and `call_method`, and the C# member tools `cs_get`, `cs_set` and `cs_call`), each documented in one place, plus a few task helpers.
Source: src/GodotMcp.Server/Tools/RuntimeTools.Inspect.cs:68, src/GodotMcp.Server/Tools/RuntimeTools.Inspect.cs:105, src/GodotMcp.Server/Tools/RuntimeTools.CSharp.cs:19, docs/TOOLS.md:263-328. Seen: 0 here (seeded from towercab-3d).

### Let the app opt in by name, with no package and no fallback
<!-- rule: opt-in-by-name -->

An app exposes its own debug commands by marking methods with an attribute class of its own, matched by the name `GodotMcpToolAttribute` in any namespace through `CustomAttributeData`; a shared annotations package was rejected (a tracked build change and a runtime dependency for a dev-only feature, and version skew with the installed server), as were a manifest file (it drifts from the code) and every public method (plumbing offered beside the cheats).

The list is read from the live app, so it never offers a command the running build lacks, and a stale build is reported rather than refused; arguments are an object keyed by parameter name, checked against the schema before app code runs, and a command returns what happened instead of the server waiting out a settle window.

App state works the same way: nodes join a group and define a state method, one call reads them all in one frame (two requests merged would read frames apart and diff noise), and `keep` plus a diff names each changed leaf (`seats[1].hp`). With nothing marked there is no fallback to script variables, which are layout knobs, not state: an empty list and a hint naming the generic tools.
Source: docs/DECISIONS.md:91-96, docs/DECISIONS.md:99-106, src/GodotMcp.Dotnet.Core/ToolMark.cs:12, CHANGELOG.md:39, CHANGELOG.md:60-62. Seen: documented.

### Answer every request with a stable error code and a hint, and cancel it when its connection goes
<!-- rule: coded-errors-never-throw -->

The bridge should turn every failure (a malformed frame, a missing id or command, an unknown command, a handler error) into a reply with a stable code, a message, a suggested fix and typed details, with an unknown command's hint listing the registered commands; codes may be added but never respelled.

Half of this holds: a lost connection cancels every running request, and errors reply `{id, ok: false, error}`. The rest does not yet: a reply carries a message string and no code, a frame that is not a request or has no id is dropped with a warning instead of answered, and `unknown command '<name>'` lists nothing.
Source: bridge/godot_mcp_bridge.gd:404-411, bridge/godot_mcp_bridge.gd:482-496, bridge/godot_mcp_bridge.gd:903-906, src/GodotMcp.Server/Wire/BridgeConnection.cs:391. Seen: 0 here (seeded from yaat).

### Feed the app's errors into every tool result
<!-- rule: error-feed-on-results -->

The hook registers an in-process logger as it initialises. The logger runs on whatever thread raised the error, with no engine lock, so it only appends under a mutex to a queue capped at 200 (overflow counted as `dropped`), and the main thread flushes the queue as an id-less frame every frame and before every reply, so an error raised during a command is on the wire before that command's reply.

The server keeps the last 500 entries with a per-session sequence number; each runtime tool marks the feed before sending and attaches the errors after the mark as `errors` (message, file, line, stack; repeats collapsed, 20 at most), and the call still succeeds.

So a handler that threw still "succeeds": agents read `errors` in every result before the next step, and the key is absent when there were none. `get_errors(since)` pages the rest; stdout is kept only for crash reading. The limits: errors are attributed by sequence window, so two calls in flight on one session may report each other's, and errors raised after the shutdown reply never arrive.
Source: docs/DECISIONS.md:36, docs/DECISIONS.md:166-169, docs/ARCHITECTURE.md:50, docs/DEVELOPMENT.md:83-84, docs/TOOLS.md:50, ~/.claude/skills/godot-mcp/SKILL.md:18, bridge/godot_mcp_bridge.gd:191. Seen: documented.

### Fail loudly when what the bridge reads is not ready
<!-- rule: hook-throws-when-unready -->

A bridge call whose engine state is not there refuses with a cause instead of returning empty data: a frame step or a capture on a window that cannot draw is refused naming why ("is it minimized?", low-processor mode) rather than answering a blank image, and a missing node names the deepest node that does exist.
Source: bridge/godot_mcp_time.gd:54-73, bridge/godot_mcp_frame.gd:153-157, docs/TOOLS.md:48. Seen: 0 here (seeded from towercab-3d).

### Convert every write by the declared type and read it back
<!-- rule: verify-writes-read-back -->

The engine's own conversions fail silently: a native setter given the wrong type changes nothing with no log, a typed script member that cannot take the value is a silent false, and C# turns a Dictionary into a zero vector. So the hook converts JSON by the property's or argument's declared type itself, sets it, reads it back, and when the read-back differs (a clamping setter, a read-only property) puts the old value back and fails with `before` and `after`.

The declared type is not always the type read back (85 engine properties differ: a String declared, a StringName read), so the comparison treats those pairs by their text. The C# path (`cs_set`) follows the same read-back and put-back order.
Source: docs/DECISIONS.md:42, docs/DEVELOPMENT.md:106-107, docs/ARCHITECTURE.md:141, docs/ARCHITECTURE.md:145. Seen: documented.

### Never block the app's main thread waiting on the app
<!-- rule: never-block-main-thread -->

Every hook command runs on the app's main thread, so a call that blocks on work queued for a later frame deadlocks.

An unfinished `Task` therefore answers `pending` and the hook polls it once a frame until it finishes, the server cancels it, or its backstop passes; a C# snippet gets its timeout as a `CancellationToken` that reaches it through that per-frame poll (a snippet looping without yielding cannot see it, and the docs say so); a script's suspended coroutine is answered from its completion signal, never awaited inline.

Long observations live in the hook, bounded as they record: an unbounded per-frame series held the main thread about 200 ms in a spike, so a watch keeps each track's first 200 and last 50 change points and counts the rest. Agents are told the reverse too: a sampling `run_script` keeps every other call, and a watch's frames, from reaching the app while it runs.
Source: docs/DEVELOPMENT.md:67, docs/DECISIONS.md:61, docs/DECISIONS.md:126, docs/ARCHITECTURE.md:39, docs/ARCHITECTURE.md:161, docs/TOOLS.md:22, docs/TOOLS.md:253. Seen: the 200 ms spike measurement.

### Convert viewport points with the viewport's own transform, not a display scale
<!-- rule: backing-store-ratio -->

Map viewport coordinates to window pixels with the viewport's own screen transform, because the stretch mode and content scale set that ratio and it can differ from the display's DPI scale. In another project, a canvas converted by the device pixel ratio instead of its own backing-store ratio drew overlay labels at half size and position.
Source: bridge/godot_mcp_input.gd:752-757, bridge/godot_mcp_frame.gd:63. Seen: 0 here (seeded from towercab-3d).
