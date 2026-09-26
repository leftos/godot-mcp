# godot-mcp

An MCP server (C# / .NET 10, `src/GodotMcp.Server`) and an in-game bridge (GDScript, `bridge/`) that let agents run, see and drive the user's Godot projects; it replaces the third-party `godot-mcp-runtime`. This file is a router: each line names the document that owns a question.

## Start here

- `docs/README.md`: the map and the glossary.
- `docs/plans/MAIN.md`: open work, in order; the design and the user's decisions are in `docs/plans/2026-09-25-first-version.md`.
- `docs/DEVELOPMENT.md`: toolchain, commands, gates.

## Non-negotiables

- The bridge never enters a project's tracked files: it is injected through a marked `override.cfg` hidden by `.git/info/exclude`, and an `override.cfg` without the marker is the user's and is refused, never overwritten (user, 2026-09-25).
- The server's stdout is the MCP protocol: every log goes to stderr.
- Every request on the wire carries an id; a reply for a timed-out id is dropped.
- Input tools take viewport coordinates and map them to the window; a drag's motion events carry `button_mask` and `relative`.
- Behaviour of Godot is cited from its 4.7.2 source or docs, never assumed; `F:\Godot\repo` is a stale 4.5.1 checkout.
- Commit messages go in a file (`git commit -F .tmp/commit-msg.txt -- <paths>`); enumerate files, never `git add -A`; never `--amend` or `--no-verify`.
- Temporary files go in the untracked `.tmp/`.

## Workflow

- Source and test edits go to the `implementer` agent with a brief naming the files, the change and a proving command; the main session owns docs, plans and commits.
