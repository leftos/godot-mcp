# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# godot-mcp

An MCP server (C# / .NET 10, `src/GodotMcp.Server`) and an in-game bridge (GDScript, `bridge/`) that let agents run, see and drive the user's Godot projects; it replaces the third-party `godot-mcp-runtime`. This file is a router: each line names the document that owns a question.

## Start here

- `docs/README.md`: the map and the glossary.
- `docs/plans/MAIN.md`: open work, in order; the design and the user's decisions are in `docs/plans/2026-09-25-first-version.md`.
- `docs/TOOLS.md`: the agent-facing guide to every tool; a change to a tool's arguments, defaults or edges updates it in the same commit.
- `docs/DEVELOPMENT.md`: toolchain, commands, gates, what each test class covers, and the footguns: read the footguns before touching process launching, a tool's signature, logging, or the bridge's input handling.
- Everyday commands: `pwsh run.ps1 build`, `pwsh run.ps1 test` (unit), `pwsh run.ps1 itest` (real Godot); `-Filter "*ClassName"` runs one test class. Each writes `.tmp/<command>.log` (a full `itest` writes `.tmp/itest-<group>.log` per class group) and prints its tail.

## Architecture

- `docs/ARCHITECTURE.md`: the components, a request's path from tool to bridge and back, the session lifecycle, every tool with its bridge command and result, and the recipe for a new tool. Read it before exploring the code; a change to any of those updates it in the same commit.

## Non-negotiables

- The bridge never enters a project's tracked files: it is injected through a marked `override.cfg` hidden by `.git/info/exclude`, and an `override.cfg` without the marker is the user's and is refused, never overwritten (user, 2026-09-25).
- The server's stdout is the MCP protocol: every log goes to stderr.
- Every request on the wire carries an id; a reply for a timed-out id is dropped.
- Input tools take viewport coordinates and map them to the window; a drag's motion events carry `button_mask` and `relative`.
- Behaviour of Godot is cited from its 4.7.2 source or docs, never assumed; `F:\Godot\repo` is a stale 4.5.1 checkout.
- Commit messages go in a file (`git commit -F .tmp/commit-msg.txt -- <paths>`); enumerate files, never `git add -A`; never `--amend` or `--no-verify`.
- Temporary files go in the untracked `.tmp/`.
- Every build and test runs through `run.ps1`, which runs it under `tools/gate.ps1` with a ceiling. A run that reaches the ceiling has hung: read the log, never raise the ceiling or re-run to read output differently.
- Cyclomatic complexity ≤ 8 per method (CA1502; the threshold is `CodeMetricsConfig.txt`), ≤ 100 lines per method, warnings are errors.

## Workflow

- Source and test edits go to the `implementer` agent with a brief naming the files, the change and a proving command; the main session owns docs, plans and commits.
- Commits are pre-approved: commit a step whose gates are green, whose diff is reviewed and whose docs are updated, without asking. Anything unusual still asks: a revert, a history edit, a force-push (user, 2026-09-25).
- The repo is public at `github.com/leftos/godot-mcp`; a landed item is committed on `main` and pushed straight to `origin/main`, no PR (user, 2026-09-26).
- Never commit while an implementer has work in the tree.
