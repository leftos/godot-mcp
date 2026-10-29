# Implementer overlay: godot-mcp

This repo's rules for the user-level `implementer` agent, its first read in place of `CLAUDE.md`, which holds the orchestrator's rules; it adds to the agent's own procedure and the preloaded `agent-standards` skill, and wins where it is more specific.

An MCP server (C# / .NET 10, `src/GodotMcp.Server`) and an in-game bridge (GDScript, `bridge/`) that let agents run, see and drive Godot projects.

## Where to read

- `docs/ARCHITECTURE.md`: the components, a request's path from tool to bridge and back, the session lifecycle, every tool with its bridge command and result, and the recipe for a new tool. Start from its Task Index row for the change, then the files it names.
- `docs/DEVELOPMENT.md`: toolchain, commands, gates, what each test class covers, and the **Footguns**: read them before touching process launching, a tool's signature, logging, or the bridge's input handling.
- `docs/TOOLS.md`: every tool's arguments, defaults, edges and result, as agents read them; a tool's description in code says the same.
- `docs/DECISIONS.md`: the owner's decisions and the engine facts behind them; never reverse one without the brief saying so.
- The design doc the brief names under `docs/plans/`, for the rulings the step was designed under.

## Commands

Every build and test runs through `run.ps1`, which runs it under `gate run` through the repo's wrapper `tools/gate.ps1` (which hands it to the installed gate, and on a machine without one runs the command directly, with no log, slot or ceiling) with a ceiling, writes `.tmp/<command>.log` and prints its tail:

```
pwsh run.ps1 build                        # warnings are errors
pwsh run.ps1 test [-Filter "*ClassName"]  # unit tests
pwsh run.ps1 gdtest                       # headless GDScript tests under tests/bridge
pwsh run.ps1 itest [-Filter "*ClassName"] # real Godot; -Since origin/main runs only the groups a change touches
pwsh run.ps1 pytest                       # a change under tools/
pwsh run.ps1 format-check                 # CSharpier and the style pass
pwsh run.ps1 drive -Calls <file.json>     # drive this tree's own server build against a game
prek run --all-files                      # the lint hooks (CSharpier, line length, gdformat, gdlint, gdcomplexity, ruff); no test, no build
```

A `run.ps1` command writes its `.tmp/<command>.log` itself, so a shell redirect never targets that same file: the gate then cannot replace its earlier log ("cannot remove the earlier …\.tmp\format-check.log … os error 32"). Redirect to another name, or not at all.

The gate's kill line (exit 124) says why: `STALLED` (no output and no CPU for 120 s) has hung, so read the log; `TIMED OUT` kept working past the load-adjusted ceiling, so read the log before touching the ceiling; `BACKSTOP` with a low "machine free" figure means the machine was busy, so re-run it once alone. Never re-run a command to read its output differently: read the log. The gate is developed in its own repo (`X:\dev\gate`); a finding about it goes to the orchestrator.

## Non-negotiables

- The bridge never enters a project's tracked files: it is injected through a marked `override.cfg` hidden by `.git/info/exclude`, and an `override.cfg` without the marker is the user's and is refused, never overwritten.
- The server's stdout is the MCP protocol: every log goes to stderr.
- Every request on the wire carries an id; a reply for a timed-out id is dropped.
- Input tools take viewport coordinates and map them to the window; a drag's motion events carry `button_mask` and `relative`.
- Behaviour of Godot is cited from its 4.7.2 source or docs, never assumed: the source is `F:\Godot\src-4.7.2`; `F:\Godot\repo` is a stale 4.5.1 checkout.
- Cyclomatic complexity ≤ 8 per method (CA1502; the threshold is `CodeMetricsConfig.txt`; gdcomplexity for GDScript), ≤ 100 lines per method, gdlint's 1000 lines per file, warnings are errors.
- Temporary files go in the untracked `.tmp/`.
- gdformat writes CRLF on Windows: convert a file it reformats back to LF and check `git ls-files --eol <file>` shows `w/lf` (DEVELOPMENT.md, Footguns).
