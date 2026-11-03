# godot-mcp

An MCP server (C# / .NET 10, `src/GodotMcp.Server`) and an in-game bridge (GDScript, `bridge/`) that let agents run, see and drive the user's Godot projects; it replaces the third-party `godot-mcp-runtime`. This file is a router: each line names the document that owns a question.

## Start here

- `docs/README.md`: the map and the glossary.
- The plan is Linear team GMCP; the `godot-mcp-nextup` profile names its project order, its release step and its gates. `docs/plans/MAIN.md` is a generated snapshot, never edited by hand.
- `README.md`: the user-facing page. `CHANGELOG.md`: what each version changed.
- `docs/DECISIONS.md`: the user's decisions and the engine facts behind them; read it before reversing one.
- `docs/TOOLS.md`: the agent-facing guide to every tool. `.claude/rules/tool-docs.md` names the docs a tool change updates.
- `docs/ARCHITECTURE.md`: the components, a request's path, the session lifecycle, every tool's bridge command, the recipe for a new tool. Read it before exploring the code.
- `docs/DEVELOPMENT.md`: toolchain, commands, gates, tests, releases, and the footguns: read them before touching process launching, a tool's signature, logging, or the bridge's input handling.
- Everyday commands: `pwsh run.ps1 build|test|itest [-Filter "*Class"] [-Since origin/main]`.

## Non-negotiables

- The bridge never enters a project's tracked files: a game gets it through a marked `override.cfg` hidden by `.git/info/exclude`, and an `override.cfg` without the marker is the user's and is refused, never overwritten; an editor session gets it from the installed editor host passed to `--script`.
- The server's stdout is the MCP protocol: every log goes to stderr.
- Every request on the wire carries an id; a reply for a timed-out id is dropped.
- Input tools take viewport coordinates and map them to the window; a drag's motion events carry `button_mask` and `relative`.
- Behaviour of Godot is cited from its 4.7.2 source or docs, never assumed: the source is `F:\Godot\src-4.7.2`; `F:\Godot\repo` is a stale 4.5.1 checkout.
- Every build and test runs through `run.ps1`.

## Workflow

- Work here runs autonomously. Only agents file this repo's tickets, so a design or briefing decision is settled by the best educated guess (the recommended option), recorded in the design doc and as a comment on its issue. When a guess is not enough, the question goes to the ticket's filer and the item is **block**ed on it; the owner is asked only for a revert, a history edit or a force-push.
- Commits are pre-approved once gates are green, the diff is reviewed and docs are updated. The repo is public at `github.com/leftos/godot-mcp`; a landed item is committed on `main` and pushed to `origin/main`, no PR.
