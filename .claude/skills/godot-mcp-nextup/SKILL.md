---
name: godot-mcp-nextup
description: Profile for the user-level `nextup` skill in the godot-mcp repo — loaded by `nextup` at its step 0 for this project's plan order and gates. Not a loop of its own; invoke `/nextup`.
---

# godot-mcp profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is godot-mcp-specific.

siblings: none
linear: godot-mcp

## Plan and tracker

- The plan lives in Linear: every task is a Linear issue in team GMCP, per `~/.claude/docs/plan-operations.md`; `docs/plans/MAIN.md` is its generated snapshot, never edited by hand. Project order, which is the order the queue is worked: `Ideas from the survey and the projects`, `Singles`, `Docs`. The next item is the first Todo of the first project that has one; inside a project, bug reports and requests from the game repos come ahead of the backlog.
- A steer or a finding the item does not fix gets an **add**, in the project whose files it shares, else in `Singles`.
- Tracker: **triage** as plan-operations says (GitHub issues reach the team through Linear's sync; an untriaged one is top-level with no project), each untriaged issue placed in the project that shares its files, else in `Singles`.
- **No owner interview.** `nextup`'s interview and decision round are replaced here by CLAUDE.md's autonomy rule: each open question, branch verdict and design question is settled by the recommended option, written into the design doc and a comment on the issue, and dispatched on. A question a guess cannot settle goes to the ticket's filer (an issue comment, or an inbox request to the filing repo) with the item **block**ed on it, never to the owner.
- A design for open work stays in `docs/plans/<name>.md`, linked from its issue. Once its last step lands, its rulings go into `docs/DECISIONS.md` and the file moves to `docs/plans/archive/`.
- Pre-loop hook: **trade driving lessons with the other projects.** Run the user-level `conventions-sync` skill once with `--stack driving`, before the queue is read. When it changed anything, `docs/DRIVING_CONVENTIONS.md` and `docs/.conventions-sync-driving.json` land on main as their own `docs:` commit, staged by name, before the first worktree is cut; the skill commits its side of `~/.claude` itself.

## Gates

Each runs through `run.ps1`, which wraps it in `tools/gate.ps1` and writes `.tmp/<command>.log`: `pwsh run.ps1 build` (warnings are errors), `pwsh run.ps1 test` (unit), `pwsh run.ps1 gdtest` (headless GDScript tests), `pwsh run.ps1 pytest` (for a change under `tools/`), `pwsh run.ps1 format-check`, and `pwsh run.ps1 itest -Since origin/main` for a landing (the full `pwsh run.ps1 itest` at a release); `prek run --all-files` runs the lint hooks (CSharpier, line length, the build, gdformat, gdlint, gdcomplexity, ruff) and no test.
