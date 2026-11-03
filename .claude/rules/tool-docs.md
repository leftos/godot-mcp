---
paths:
  - "src/GodotMcp.Server/**"
  - "bridge/**"
  - "skills/**"
---

# Docs a tool change updates

A change to a tool, the bridge or a shipped skill updates these in the same commit:

- `docs/TOOLS.md`, the agent-facing guide: a tool's arguments, defaults, edges or result.
- `skills/godot-mcp/SKILL.md`, the tutorial the game repos' agents load: a new tool, or a change to the drive loop or the rules that bite, beside `docs/TOOLS.md`.

  It is published with the server and linked into `~/.claude/skills` from the install folder by `pwsh run.ps1 install`, so the game repos see an edit only after a release's install. The publish ships `docs/TOOLS.md` beside it as `TOOLS.md` and refuses a shipped skill that names an absolute path, so it names files relatively or by URL.
- `skills/godot-agent-sweep/SKILL.md`: a change to the godot tool classes, the agent-file marker or the `--sweep-agents` output.
- `docs/ARCHITECTURE.md`: a change to the components, a request's path, the session lifecycle, a tool's bridge command or result, or the recipe for a new tool.
- `README.md`: a new tool or capability, or a change to install or requirements.
- `docs/agents/implementer.md`: a change to `CLAUDE.md`'s Non-negotiables or everyday commands, which the overlay repeats for the `implementer` agent.
