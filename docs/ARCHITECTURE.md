# Architecture

How the server and the bridge fit together today, for anyone about to change them. Symbols are named by file and member, not by line. Toolchain, tests and footguns are in [DEVELOPMENT.md](./DEVELOPMENT.md); terms are in the [glossary](./README.md#glossary).

## Components

| Part | Where | Job |
|---|---|---|
| Host | `src/GodotMcp.Server/Program.cs` | The MCP SDK over stdio (stdout is the protocol; every log goes to stderr). Registers two singletons, `BridgeListener` and `SessionRegistry`, and calls `SessionRegistry.Shutdown` on application stop and process exit |
| Tools | `src/GodotMcp.Server/Tools/` | `[McpServerTool]` methods: `ProjectTools` (lifecycle, output, `list_sessions`) and the `RuntimeTools` partials (`RuntimeTools.cs` reads and scripts, `.Input.cs` mouse and keyboard, `.Gamepad.cs` pads). They validate arguments, resolve their `session` through the registry, then call that `GodotSession` |
| Session | `src/GodotMcp.Server/Session/` | `SessionRegistry` holds the named sessions and the rules between them (names, one folder shared by several sessions, the override's lifetime). A `GodotSession` is one of them: a run (`GodotRun`: process, stdout and stderr ring buffers of `GodotRun.OutputCapacity` lines, connection) or an attached game (`GodotSession.Attach.cs`), with its own input gate. `OverrideFile`, `AttachFile` and `GitExclude` own the files a session writes into a project; `GodotCommandLine` builds the launch |
| Wire | `src/GodotMcp.Server/Wire/` | `BridgeListener` (one loopback TCP listener on a free port for the server's lifetime, with one accept loop that routes each bridge by the token in its hello), `BridgeConnection` (id-keyed requests; id-less errors frames go to the session's `ErrorFeed`), `FrameCodec`/`FrameDecoder`, `HandshakeExpectation` |
| Bridge | `bridge/godot_mcp_bridge.gd`, `bridge/godot_mcp_gamepad.gd` | The autoload injected into the game: dials the server, answers commands, plays input; the gamepad half emulates pads and the real-pad shut-out |

## A request's path

1. A tool method checks its arguments (failures become `McpException`, whose message is all the client sees).
2. It resolves its session: the named one, else the only live session, else the only session there is; with none it throws "No Godot session is running…", and with several and no name it refuses and lists them. Then it calls `GodotSession.SendAsync(command, params, timeout, ct)` on that session.
3. `BridgeConnection` sends a frame (4-byte big-endian length, UTF-8 JSON) carrying a request id and waits for the reply with that id; a reply for a timed-out id is dropped.
4. The bridge's `_handle_frame` dispatches on `command` and replies `{id, ok: true, …}` or `{id, ok: false, error}` (`_reply_error`).
5. The tool shapes the reply into its result: JSON text (`JsonSerializerDefaults.Web`), and for `take_screenshot` an image block read from the PNG file the bridge saved. Lists and logs are held to a budget (compact output): `get_ui_elements` pages by `offset`/`limit`, `get_debug_output` by `limit`/`before` with long lines cut, a `run_script` value over 20000 characters becomes `{valuePreview, valueLength}`.

**The error feed.** The bridge's `Logger` (`bridge/godot_mcp_logger.gd`, registered with `OS.add_logger` in the bridge's `_init`) receives every engine and script error and warning on whatever thread raised it, and only appends it, under a `Mutex`, to a queue capped at 200 (overflow is counted as `dropped`). On the main thread the bridge flushes the queue as an id-less errors frame, `{type: "errors", entries, dropped}`, every frame and before every reply, so an error raised while a command ran is on the wire before its reply. `BridgeConnection` hands errors frames to the session's `ErrorFeed` (the last 500 entries, each with a per-session `seq`). A runtime tool marks the feed before sending and attaches the errors (not warnings) after the mark to its result as `errors: [{seq, message, file, line, function, engine?, stack, count?}]`, shaped by `ErrorReport` (repeats collapsed with a count, at most 20 then `errorsOmitted`, messages cut at 2000 characters); the call still succeeds, except `run_script`, which fails when its script does not compile, or returns null with an error located in its own source (`gdscript://…`). An error whose location is not a script (`push_error`'s own C++ site, an engine `ERR_FAIL` such as `get_node` on a missing path, C# errors) is moved to the most recent backtrace frame, and the C++ site is kept as `engine: "<file>:<line>"`. A handler failure on an errors frame is logged and the frame dropped; the read loop carries on. Backtraces are filled in debug builds, or when the project sets `debug/settings/gdscript/always_track_call_stacks`. Launched and attached games report alike; stderr is kept only for `get_debug_output`.

## Session lifecycle

- **Names**: `run_project` (`options.session`) and `attach_project` (`session`) name the new session, by default after the project folder (1-64 of letters, digits, `.`, `_`, `-`). Runs and attaches share one name space. A live name (running, attached, or still launching or waiting to attach) is refused; a stopped one is replaced. A stopped run stays listed so `get_debug_output` can read it; a detached session is forgotten.
- **One folder, several sessions**: the marked `override.cfg` is written by the first live session on a folder and removed when the last one ends (stop, detach, the game exiting, a failed start, server exit). A new session whose `shutOutRealGamepads` or `quiet` differs from the live ones' is refused (an attach counts as not quiet), and so is a second attach on a folder while one is still waiting.
- **Run** (`run_project` → `GodotSession.LaunchAsync`): `OverrideFile.Write` writes the marked `override.cfg` naming the bridge as an autoload (refusing a user's unmarked one), `GitExclude` hides it, and `StartRunAsync` creates a random token, starts Godot with `GODOT_MCP_PORT` and `GODOT_MCP_TOKEN` in its environment (plus `GODOT_MCP_QUIET` unless `options.quiet` is false, and `GODOT_MCP_SHUT_OUT_REAL_GAMEPADS` when asked). A quiet run also gets `--audio-driver Dummy` before the user's engine arguments (a later `--audio-driver` of theirs wins), and its override adds `[display]` `window/size/no_focus=true` with an off-screen initial position, so the window is created unfocused, and waits for the handshake.
- **Attach** (`attach_project` → `AttachAsync`): `AttachFile` writes the one-use `.godot/godot-mcp/attach.json` ({port, token, shutOutRealGamepads}) and the override, and waits for a game launched after it to dial in; the attach file is deleted once the wait ends.
- **Handshake**: each session registers its expectation with `BridgeListener.AcceptBridgeAsync`. The listener's one accept loop reads each hello `{type: "hello", token, projectPath}` (5 s timeout, sockets handled concurrently), finds the waiter by a dictionary lookup on its token and checks the hello with `HandshakeExpectation` (the token, then the path). The lookup is not constant-time; the token is 32 random bytes on loopback only. An unknown token or a mismatch is logged and its socket closed; the waiter keeps waiting.
- **Stop** (`stop_project`): a `shutdown` command, a kill after 3 s, the override removed if no other live session uses the folder.
- **Detach** (`detach_project`): the connection closed, the override removed as for stop, the game left running. The attach file is not touched: each attach removes its own when its wait ends, so a detach never deletes another attach's file on the same folder.
- **Game exits on its own**: `OnRunExitedAsync` releases the override as for stop.
- **Server exits**: `SessionRegistry.Shutdown` kills every run and removes every folder's files.

## Tools

| Tool | File | Bridge command | Result |
|---|---|---|---|
| `run_project` | `ProjectTools.cs` | (launch) | `{session, projectPath, processId, quiet}` |
| `attach_project` | `ProjectTools.cs` | (attach) | `{session, projectPath}` |
| `detach_project` | `ProjectTools.cs` | — | `{session, projectPath, overrideRemoved}` |
| `stop_project` | `ProjectTools.cs` | `shutdown` | `{session, projectPath, exitCode, killed, overrideRemoved}` |
| `get_debug_output` | `ProjectTools.cs` | — | `{session, projectPath, running, exitCode, stdout[], stderr[], stdoutTotalLines, stderrTotalLines, stdoutFirstLine, stderrFirstLine}`: up to `limit` (100) lines ending before line `before`, or the last ones |
| `list_sessions` | `ProjectTools.cs` | — | `{sessions: [{name, projectPath, kind: "run" \| "attach", live, processId}]}`, by name |
| `take_screenshot` | `RuntimeTools.cs` | `screenshot` | a text block with the file paths and sizes, plus an image block (a preview at most `previewMaxWidth`, 480, wide) unless `responseMode` is `path_only` |
| `get_ui_elements` | `RuntimeTools.cs` | `ui_elements` | `{elements, total, offset, next?}`, a page of the bridge's list |
| `run_script` | `RuntimeTools.cs` | `run_script` | `{value}` (or `{valuePreview, valueLength}`); a compile failure, or a null value with an error in the script's own source, fails with the errors |
| `get_errors` | `RuntimeTools.cs` | — | `{errors: [{seq, type, …}], next, dropped}`: the session's errors and warnings after `since`, oldest first |
| `click`, `drag`, `type_text`, `key`, `mouse_button`, `simulate_input` | `RuntimeTools.Input.cs` | `input` | `{pointer, heldButtonMask}` |
| `gamepad_button`, `gamepad_axis`, `gamepad_stick` | `RuntimeTools.Gamepad.cs` | `input` | as the input tools |

Every runtime tool's result also carries `errors` when the call raised any (see the error feed). Every tool but `run_project`, `attach_project` and `list_sessions` takes a last `session` parameter (`run_project` has it in `options`). Input calls are serialised per session by the session's input gate. `McpServerSmokeTests` pins the tool list.

## The bridge

- **Start**: in `_init`, `_find_endpoint` reads the port and token from the environment, else from `res://.godot/godot-mcp/attach.json`; with one, the error-feed logger is registered there (and never removed: Godot drops script loggers at shutdown); with neither it warns and frees itself. With `GODOT_MCP_QUIET`, `_park_window` makes the window click-through and moves it off-screen: Windows clamps the override's off-screen initial position onto the primary screen at creation, so the window shows there, unfocused, until the bridge's `_ready`.
- **Commands** (`_handle_frame`): `ping`, `screenshot` (saves `.godot/godot-mcp/screenshots/<stamp>-<pid>.png` and a Lanczos preview when wider than asked; replies with paths, never bytes), `ui_elements`, `run_script` (compiles a script with `execute`, returns its value), `input` (gestures and raw events, under `_handle_input`), `shutdown`.
- **Real input**: injected mouse events carry the injected mark, and `_input` swallows unmarked (real) mouse events while a gesture plays or an injected button is held. Keys are never swallowed, and injected keys carry no mark (their device stays 16, which the built-in `ui_*` actions require). A quiet run keeps real input out by never receiving it: unfocused from creation, click-through and off-screen. `godot_mcp_gamepad.gd`'s `shut_out_real_pads` keeps real pads out when asked.

## Tool annotations

Every tool declares `openWorldHint: false`. `readOnlyHint: true`: `get_debug_output`, `list_sessions`, `take_screenshot` (its PNG goes under the ignored `.godot/`), `get_ui_elements`, `get_errors`. `destructiveHint: true`: `stop_project` (kills a process) and `run_script` (runs arbitrary code). Every other tool is `destructiveHint: false`. A new tool sets all three.

## Adding a runtime tool

1. A method in the right `RuntimeTools` partial with a last `session` parameter (five schema parameters at most): validate, resolve the session, then `SendAsync` on it.
2. A handler branch in the bridge's `_handle_frame` (or `_handle_input` for a gesture).
3. A unit test for the server-side checks (`tests/GodotMcp.Tests/Tools/`).
4. An integration test against the InputProbe fixture (`tests/GodotMcp.IntegrationTests`), and the name in `McpServerSmokeTests`' list.
5. Its annotations (`ReadOnly`, `Destructive`, `OpenWorld = false`) and `McpServerSmokeTests`' annotation check.
6. This file's tool table and, for a new term, the glossary.
