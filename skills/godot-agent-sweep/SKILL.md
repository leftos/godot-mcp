---
name: godot-agent-sweep
description: Use when an agent that drives Godot through the `godot` MCP server lacks a godot tool it needs, when adding or changing an agent file (`~/.claude/agents/*.md` or a repo's `.claude/agents/*.md`) that lists `mcp__godot__*` tools, when a godot-mcp install prints `unmarked, skipped` or an agent-sweep error, or when asked to sync, sweep or refresh the agents' godot tools.
---

# Godot agent sweep

An agent spawns with only the tools its `tools:` line names, so an agent missing a godot tool falls back to a workaround. The sweep keeps every agent's `mcp__godot__*` entries equal to the tools of the classes it declares. `pwsh run.ps1 install` and the release installer run it after installing, since a new tool is callable only once its release is installed.

## Mark an agent

Put one line in the agent file's body, after the front matter:

```markdown
<!-- godot-mcp tool classes: read, drive -->
```

| Class | What its tools do |
|---|---|
| `read` | look without changing anything: the scene tree, node properties, screenshots, frame captures, errors, `cs_get`, `get_game_state` |
| `drive` | start, stop and play the game: `run_project`, `stop_project`, input, gamepad, `frame_control`, `wait_for`, baselines |
| `edit-live` | change the running game's state: `set_property`, `call_method`, `run_script`, `run_csharp`, `cs_set`, `cs_call`, `call_game_tool`, `batch_drive` |
| `edit-scene` | edit `.tscn` files headless: `add_node`, `set_node_properties`, `save_scene`, and the rest of the scene tools; also `save_screenshot`, which writes a PNG into the project |

`godot-mcp --list-tools` prints every tool with its class. Pick the classes by the agent's role: a reviewer or explorer `read`; a playtester `read, drive`; a debugger that edits no file `read, drive, edit-live`; an implementer all four.

The sweep then owns the agent's godot entries: it adds the tools of its classes, removes any other `mcp__godot__*` entry (a tool of another class, or one the server no longer serves), sorts them, and leaves every other entry and every other byte of the file alone. The `tools:` value must be one comma-separated line.

## Run it

```bash
godot-mcp --sweep-agents --dry-run            # what would change, writing nothing
godot-mcp --sweep-agents                      # apply
godot-mcp --sweep-agents --root D:\           # also every D:\<repo>\.claude\agents
```

`godot-mcp` is the installed `%LOCALAPPDATA%\godot-mcp\godot-mcp.exe`. The sweep always covers `~/.claude/agents`; each `--root` folder and each folder in `GODOT_MCP_SWEEP_ROOTS` (`;`-separated, set for the user) adds its child repositories' `.claude/agents`.

In a git repository it commits only the tracked agent files it changed, as `chore: sync godot-mcp tools` on the current branch, with hooks, and never pushes; the commit is never killed, however long its hooks run.

A repository whose would-change agent files have uncommitted changes is left untouched and reported `repo has uncommitted agent files, skipped`: commit or discard them, then run the sweep again. Each repository is visited once and printed in git's own spelling of its folder, so a `subst` drive (`D:\` for `X:\dev`) prints as its target.

The marker is found anywhere in the body, a fenced code block included: an agent that documents the marker is marked by it.

## Read its output

| Line | Meaning |
|---|---|
| `<file>: +a, b -c` | tools added and removed |
| `<file>: sorted` | only the order changed |
| `<file>: unmarked, skipped` | the file names godot tools but has no marker: add one |
| `<file>: written, not committed (not tracked by git)` | the file is untracked or ignored: written, left for you to add; not an error |
| `<file>: error: <reason>` | an unknown class, a marker with none, or a `tools:` value that is not one line; the file is untouched |
| `<repo>: committed <sha>` | the repository's commit |
| `<repo>: commit failed, the edits are left in place: …` | a hook refused it; the edits wait in the tree |
| `<root>: no such folder, skipped` | a stale `--root` or `GODOT_MCP_SWEEP_ROOTS` entry |

Exit 0 on success, 1 when a file errored or a commit failed, 2 for a usage error (an unknown `--` option included). An install whose sweep exits non-zero reports it and exits non-zero once the server and skills are installed; a sweep that cannot start at all is reported and the install still succeeds.
