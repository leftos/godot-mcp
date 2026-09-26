# godot-mcp docs

The map. An MCP server (C#, .NET 10) and an in-game bridge (GDScript) that let agents run, see and drive the user's Godot projects, written to replace the third-party `godot-mcp-runtime`.

- [`plans/MAIN.md`](./plans/MAIN.md): open work, in order.
- [`plans/2026-09-25-first-version.md`](./plans/2026-09-25-first-version.md): the first version's design, the user's decisions and the steps.
- `DEVELOPMENT.md`: toolchain, commands and gates (written with step 0).

## Glossary

| Term | Meaning |
|---|---|
| Bridge | The GDScript autoload (`bridge/godot_mcp_bridge.gd`) that runs inside a launched game, dials the server and carries out its commands: screenshots, UI listing, input, scripts |
| Injection | How the bridge gets into a run without touching the project: the server writes a marked `override.cfg` in the project folder naming the bridge as an autoload, hides it through `.git/info/exclude`, and deletes it when the run stops. Godot reads `override.cfg` for game runs only, never for the editor or `--import` |
| Marker | The first-line comment that identifies an `override.cfg` as the server's own; one without it is the user's and is never touched |
| Handshake | The bridge's first frame: the session token and the project path, which the server checks so a stale bridge from another run is refused |
| Gesture | A high-level input tool (click, drag, type_text, hold/release) that sends the right sequence of events over frames, as against the raw event list of `simulate_input` |
| Headless tool | A scene or node edit done by a one-off `godot --headless --script headless/operations.gd` run, with its request and result passed as JSON files |
| InputProbe | The fixture Godot project the integration tests launch (`tests/fixtures/InputProbe`): today a label, a button and a red square at known places; step 3 adds a drag source, a drop target and a LineEdit |
| Gate, ceiling | A gate is one command run by `tools/gate.ps1` with its own log; its ceiling is the wall-clock limit after which the gate kills the process tree and exits 124 |
| Parity | The first version covers every godot tool the user's projects call, so it replaces the old server everywhere at once |
