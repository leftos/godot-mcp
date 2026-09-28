# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# godot-mcp

An MCP server (C# / .NET 10, `src/GodotMcp.Server`) and an in-game bridge (GDScript, `bridge/`) that let agents run, see and drive the user's Godot projects; it replaces the third-party `godot-mcp-runtime`. This file is a router: each line names the document that owns a question.

## Start here

- `docs/README.md`: the map and the glossary.
- `docs/plans/MAIN.md`: open work, in order.
- `README.md`: the user-facing page: what the server lets an agent do, and the prompt that installs it; a new tool or capability, or a change to install or requirements, updates it in the same commit.
- `CHANGELOG.md`: what each version changed, newest first.
- `docs/DECISIONS.md`: the user's decisions and the engine facts behind them; read it before reversing one.
- `docs/TOOLS.md`: the agent-facing guide to every tool; a change to a tool's arguments, defaults or edges updates it in the same commit.
- `skills/godot-mcp/SKILL.md`: the tutorial agents in the game repos load (linked into `~/.claude/skills` by `pwsh run.ps1 install`); a new tool, or a change to the drive loop or the rules that bite, updates it in the same commit as `docs/TOOLS.md`.
- `docs/DEVELOPMENT.md`: toolchain, commands, gates, what each test class covers, and the footguns: read the footguns before touching process launching, a tool's signature, logging, or the bridge's input handling.
- Everyday commands: `pwsh run.ps1 build`, `pwsh run.ps1 test` (unit), `pwsh run.ps1 itest` (real Godot); `-Filter "*ClassName"` runs one test class; `pwsh run.ps1 drive -Calls <file.json>` drives this tree's own server build against a game without installing it; `pwsh run.ps1 package` builds the release zip. A release is a pushed `vX.Y.Z` tag (the procedure is DEVELOPMENT.md's "CI and releases"); pushing a tag publishes to everyone, so ask first. Each writes `.tmp/<command>.log` (a full `itest` writes `.tmp/itest-<group>.log` per class group) and prints its tail.

## Architecture

- `docs/ARCHITECTURE.md`: the components, a request's path from tool to bridge and back, the session lifecycle, every tool with its bridge command and result, and the recipe for a new tool. Read it before exploring the code; a change to any of those updates it in the same commit.

## Non-negotiables

- The bridge never enters a project's tracked files: it is injected through a marked `override.cfg` hidden by `.git/info/exclude`, and an `override.cfg` without the marker is the user's and is refused, never overwritten.
- The server's stdout is the MCP protocol: every log goes to stderr.
- Every request on the wire carries an id; a reply for a timed-out id is dropped.
- Input tools take viewport coordinates and map them to the window; a drag's motion events carry `button_mask` and `relative`.
- Behaviour of Godot is cited from its 4.7.2 source or docs, never assumed; `F:\Godot\repo` is a stale 4.5.1 checkout.
- Commit messages go in a file (`git commit -F .tmp/commit-msg.txt -- <paths>`); enumerate files, never `git add -A`; never `--amend` or `--no-verify`.
- Temporary files go in the untracked `.tmp/`.
- Every build and test runs through `run.ps1`, which runs it under `tools/gate.ps1` with a ceiling. The ceiling counts load-adjusted time, so a run slowed by other agents is not killed for it. The kill line (exit 124) says why: `STALLED` (no output and no CPU for 120 s) has hung, so read the log; `TIMED OUT` kept working past the ceiling even allowing for load, a busy loop or a ceiling set too tight, so read the log before touching the ceiling; `BACKSTOP` (5 times the ceiling in wall time) with a low "machine free" figure means the machine was busy, so re-run it once alone. Never re-run a command to read its output differently. The gate is a copy of `~/.claude/tools/gate/gate.ps1`, which `sync-gate.ps1` keeps in step: change it there, never here.
- Cyclomatic complexity ≤ 8 per method (CA1502; the threshold is `CodeMetricsConfig.txt`), ≤ 100 lines per method, warnings are errors.

## Workflow

- Source and test edits go to the `implementer` agent with a brief naming the files, the change and a proving command; the main session owns docs, plans and commits.
- Commits are pre-approved: commit a step whose gates are green, whose diff is reviewed and whose docs are updated, without asking. Anything unusual still asks: a revert, a history edit, a force-push.
- The repo is public at `github.com/leftos/godot-mcp`; a landed item is committed on `main` and pushed straight to `origin/main`, no PR.
- A landing never bumps `VersionPrefix`: its bullets go under `## Unreleased` at the top of `CHANGELOG.md` (no number, no date; opened above the last tagged version when absent), with `### Added` / `### Changed` / `### Fixed`, each bullet one sentence of at most 25 words in the agent's words (tool and option names, no class or file names), per the `changelog-and-commit` skill's rules; a landing with no behaviour change takes no bullet. The release decides the version, judiciously, by what an agent can now do: the minor for a new capability an agent would reach for, a new tool, option or mode; the patch for everything else, a fix, a clearer refusal or error, a new field in an existing result, a wider reach of an existing tool. The sha is stamped by the build.
- A landing never installs: `pwsh run.ps1 install` runs from the main checkout only once a release is cut (its tag pushed), so the game repos run released builds only. The install stops the servers running from the install folder itself, since force-updating a running MCP server is fine, and prints an `install: stopped the godot-mcp server …` line for each; every such line goes into the reply to the user, naming the Claude session and its project folder, so they can `/mcp` reconnect those sessions.
- Never commit while an implementer has work in the tree.
