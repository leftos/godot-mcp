# Architecture

How the server and the bridge fit together today, for anyone about to change them. Symbols are named by file and member, not by line. Toolchain, tests and footguns are in [DEVELOPMENT.md](./DEVELOPMENT.md); terms are in the [glossary](./README.md#glossary).

## Components

| Part | Where | Job |
|---|---|---|
| Host | `src/GodotMcp.Server/Program.cs` | The MCP SDK over stdio (stdout is the protocol; every log goes to stderr). Registers two singletons, `BridgeListener` and `GodotSession`, and calls `GodotSession.Shutdown` on application stop and process exit |
| Tools | `src/GodotMcp.Server/Tools/` | `[McpServerTool]` methods: `ProjectTools` (lifecycle and output) and the `RuntimeTools` partials (`RuntimeTools.cs` reads and scripts, `.Input.cs` mouse and keyboard, `.Gamepad.cs` pads). They validate arguments, then call `GodotSession` |
| Session | `src/GodotMcp.Server/Session/` | `GodotSession` holds the one live run (`GodotRun`: process, stdout and stderr ring buffers of `GodotRun.OutputCapacity` lines, connection) or the one attached game (`GodotSession.Attach.cs`). `OverrideFile`, `AttachFile` and `GitExclude` own the files a session writes into a project; `GodotCommandLine` builds the launch |
| Wire | `src/GodotMcp.Server/Wire/` | `BridgeListener` (one loopback TCP listener on a free port for the server's lifetime), `BridgeConnection` (id-keyed requests), `FrameCodec`/`FrameDecoder`, `HandshakeExpectation` |
| Bridge | `bridge/godot_mcp_bridge.gd`, `bridge/godot_mcp_gamepad.gd` | The autoload injected into the game: dials the server, answers commands, plays input; the gamepad half emulates pads and the real-pad shut-out |

## A request's path

1. A tool method checks its arguments (failures become `McpException`, whose message is all the client sees).
2. It calls `GodotSession.SendAsync(command, params, timeout, ct)`. `FindLiveConnection` picks the attached game, else the live run, else throws "No Godot session is running…".
3. `BridgeConnection` sends a frame (4-byte big-endian length, UTF-8 JSON) carrying a request id and waits for the reply with that id; a reply for a timed-out id is dropped.
4. The bridge's `_handle_frame` dispatches on `command` and replies `{id, ok: true, …}` or `{id, ok: false, error}` (`_reply_error`).
5. The tool shapes the reply into its result: JSON text (`JsonSerializerDefaults.Web`), and for `take_screenshot` an image block read from the PNG file the bridge saved.

Errors raised in the game reach a result only through stderr: `GodotSession.MarkStderr` / `GetStderrSince` bracket a call, and `RuntimeTools.FindScriptErrors` picks `SCRIPT ERROR` / `Parse Error` lines. `run_script` fails on them when it has no value; the input tools append them to a result that still succeeds. An attached session has no stderr, so it reports none.

## Session lifecycle

- **Run** (`run_project` → `GodotSession.LaunchAsync`): under the session's gate, `RetirePreviousRunAsync` refuses while a run is live (and clears a dead one), `OverrideFile.Write` writes the marked `override.cfg` naming the bridge as an autoload (refusing a user's unmarked one), `GitExclude` hides it, and `StartRunAsync` creates a random token, starts Godot with `GODOT_MCP_PORT` and `GODOT_MCP_TOKEN` in its environment (plus `GODOT_MCP_BACKGROUND` / `GODOT_MCP_SHUT_OUT_REAL_GAMEPADS` when asked), and waits for the handshake.
- **Attach** (`attach_project` → `AttachAsync`): the same retire step, then `AttachFile` writes the one-use `.godot/godot-mcp/attach.json` ({port, token, shutOutRealGamepads}) and the override, and waits for a game launched after it to dial in; the attach file is deleted once the wait ends.
- **Handshake**: `BridgeListener.AcceptBridgeAsync` accepts, reads the hello `{type: "hello", token, projectPath}` and checks it with `HandshakeExpectation` (the token in fixed time, then the path); a mismatch is logged and its socket closed. One accept waits at a time.
- **Stop** (`stop_project`): a `shutdown` command, a kill after 3 s, the override removed. The finished run stays so `get_debug_output` can read it.
- **Detach** (`detach_project`): the connection closed, the override and attach file removed, the game left running.
- **Game exits on its own**: `OnRunExitedAsync` removes the override if that run is still the current one.
- **Server exits**: `Shutdown` kills the run and removes its files.

## Tools

| Tool | File | Bridge command | Result |
|---|---|---|---|
| `run_project` | `ProjectTools.cs` | (launch) | `{projectPath, processId, background}` |
| `attach_project` | `ProjectTools.cs` | (attach) | `{projectPath}` |
| `detach_project` | `ProjectTools.cs` | — | `{projectPath, overrideRemoved}` |
| `stop_project` | `ProjectTools.cs` | `shutdown` | `{projectPath, exitCode, killed, overrideRemoved}` |
| `get_debug_output` | `ProjectTools.cs` | — | `{projectPath, running, exitCode, stdout[], stderr[], stdoutTotalLines, stderrTotalLines}`, the last `limit` lines |
| `take_screenshot` | `RuntimeTools.cs` | `screenshot` | a text block with the file paths and sizes, plus an image block unless `responseMode` is `path_only` |
| `get_ui_elements` | `RuntimeTools.cs` | `ui_elements` | the bridge's `{elements: […]}` |
| `run_script` | `RuntimeTools.cs` | `run_script` | the script's value as JSON, or the compile/runtime errors from stderr |
| `click`, `drag`, `type_text`, `key`, `mouse_button`, `simulate_input` | `RuntimeTools.Input.cs` | `input` | `{pointer, heldButtonMask}`, plus any script errors the gesture raised |
| `gamepad_button`, `gamepad_axis`, `gamepad_stick` | `RuntimeTools.Gamepad.cs` | `input` | as the input tools |

Input calls are serialised by `RuntimeTools.InputGate`. `McpServerSmokeTests` pins the tool list.

## The bridge

- **Start**: `_find_endpoint` reads the port and token from the environment, else from `res://.godot/godot-mcp/attach.json`; with neither it warns and frees itself. `GODOT_MCP_BACKGROUND` makes `_enter_background` park the window off-screen, unfocusable, click-through and borderless.
- **Commands** (`_handle_frame`): `ping`, `screenshot` (saves `.godot/godot-mcp/screenshots/<stamp>.png` and a Lanczos preview when wider than asked; replies with paths, never bytes), `ui_elements`, `run_script` (compiles a script with `execute`, returns its value), `input` (gestures and raw events, under `_handle_input`), `shutdown`.
- **Real input**: `_input` swallows unmarked real mouse events while a gesture plays or an injected button is held; injected events carry the injected mark. The real keyboard is never swallowed. `godot_mcp_gamepad.gd`'s `shut_out_real_pads` keeps real pads out when asked.

## Adding a runtime tool

1. A method in the right `RuntimeTools` partial: validate, then `session.SendAsync`.
2. A handler branch in the bridge's `_handle_frame` (or `_handle_input` for a gesture).
3. A unit test for the server-side checks (`tests/GodotMcp.Tests/Tools/`).
4. An integration test against the InputProbe fixture (`tests/GodotMcp.IntegrationTests`), and the name in `McpServerSmokeTests`' list.
5. This file's tool table and, for a new term, the glossary.
