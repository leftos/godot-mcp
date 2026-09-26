# godot-mcp docs

The map. An MCP server (C#, .NET 10) and an in-game bridge (GDScript) that let agents run, see and drive the user's Godot projects, written to replace the third-party `godot-mcp-runtime`.

- [`plans/MAIN.md`](./plans/MAIN.md): open work, in order.
- [`plans/2026-09-25-first-version.md`](./plans/2026-09-25-first-version.md): the first version's design, the user's decisions and the steps.
- [`ARCHITECTURE.md`](./ARCHITECTURE.md): the components, a request's path, the session lifecycle, every tool and the recipe for a new one.
- [`DEVELOPMENT.md`](./DEVELOPMENT.md): toolchain, commands, gates, test coverage and footguns.

## Glossary

| Term | Meaning |
|---|---|
| Bridge | The GDScript autoload (`bridge/godot_mcp_bridge.gd`) that runs inside a launched game, dials the server and carries out its commands: screenshots, UI listing, input, scripts |
| Injection | How the bridge gets into a run without touching the project: the server writes a marked `override.cfg` in the project folder naming the bridge as an autoload, hides it through `.git/info/exclude`, and deletes it when the run stops. Godot reads `override.cfg` for game runs only, never for the editor or `--import` |
| Marker | The first-line comment that identifies an `override.cfg` as the server's own; one without it is the user's and is never touched |
| Handshake | The bridge's first frame: the session token and the project path, which the server checks so a stale bridge from another run is refused |
| Gesture | A high-level input tool (click, drag, type_text, hold/release) that sends the right sequence of events over frames, as against the raw event list of `simulate_input` |
| Headless tool | A scene or node edit done by a one-off `godot --headless --script headless/operations.gd` run, with its request and result passed as JSON files (step 5; not built) |
| InputProbe | The fixture Godot project the integration tests launch (`tests/fixtures/InputProbe`): a label, a button, a red square, a drag source, a drop target, a LineEdit and a 12x12 button at known places, at a 640x360 base size stretched `canvas_items`/`keep`, so launching it with `--resolution 1000x900` letterboxes it. For gamepads it has the actions `probe_jump` (A) and `probe_right` (left stick right), a `PadProbe` counting jumps, and a `Menu` column of three buttons with focus on the first |
| Attached session, attach file | A session on a game `run_project` did not start: `attach_project` writes the injection `override.cfg` and a one-use `<project>/.godot/godot-mcp/attach.json` ({port, token}) and waits for a game launched after it to dial in; `detach_project` ends it and leaves the game running. It has no captured output |
| Shut out (real gamepads) | The opt-in `shutOutRealGamepads` mode that keeps a machine's real pads from reaching a run, by marking the game unfocused; the bridge half is `bridge/godot_mcp_gamepad.gd` |
| Injected mark | The `InputEvent.device` value `0x6D6370` every injected mouse event carries, so the bridge can swallow the real mouse while a gesture plays |
| Session name | The key a run is known by once several games run at once (a worktree's, or delve's "server" and "client-1"); a runtime tool defaults to the only session (step 7; not built) |
| Error feed | Engine and script errors raised while a tool call ran, attached to that call's result so no failure is silent (step 8; not built) |
| Compact output | A result held to a size budget: a screenshot as a path or a small preview, a long list or log truncated with a count and a way to page (step 8; not built) |
| Quiet by default | Agent runs start off-screen, unfocused, muted and deaf to the real mouse unless asked otherwise (step 9; not built) |
| Frame control | Pausing, stepping N frames and scaling time in a running game, so a screenshot or check lands on an exact frame (step 10; not built) |
| Profile, preset | A project's `godot-mcp.json`: its launch defaults, and named launch presets (step 13; not built) |
| Batch drive | One tool call that plays a list of steps (input, waits, method calls, assertions, screenshots) and stops at the first failed assertion (step 13; not built) |
| Hang watchdog | The server's report that a game's main thread has stopped answering the bridge, in place of a bare timeout (step 14; not built) |
| Baseline | A stored screenshot a new one is compared against, giving a difference score and a diff image (step 14; not built) |
| Gate, ceiling | A gate is one command run by `tools/gate.ps1` with its own log; its ceiling is the wall-clock limit after which the gate kills the process tree and exits 124 |
| Parity | The first version covers every godot tool the user's projects call, so it replaces the old server everywhere at once |
