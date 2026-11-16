# Game-drawn tooltips in `hover`

GMCP-613. A game that draws its own tooltips (world-of-intrigue's MarkedText, which shows term tooltips through the game's own TooltipLayer rather than `Control.tooltip_text`) gets `tooltip: null` from `hover`, since Godot shows no tooltip popup. This design lets the game report the tooltip it draws.

## Rulings

Settled by the orchestrator as the recommended option (CLAUDE.md, "Work here runs autonomously").

1. **The hook is the state method on a node in the `mcp_tooltip` group.** A node opts in by joining the group `mcp_tooltip`; its tooltip is what its state method returns: `_mcp_state()` in GDScript, `_McpState()` in C#, read by the bridge's state reader (`godot_mcp_state.gd` `read_one`) exactly as `get_game_state` and `wait_for {state}` read it, the C# helper included. No new method name, attribute or helper op: the state method already reads both languages in one frame. Ruled out: a `[GodotMcpTool]` game tool, which is C# only and needs the server to prepare a helper request per hover; a new `_mcp_tooltip` method, which needs a second helper op for the same read.
2. **The value has three meanings.**
   - `null`, or an empty Dictionary: no game tooltip is due or showing for the pointer.
   - A Dictionary with `pending: true`: a tooltip is due (the game's own delay is running); keep waiting.
   - A Dictionary with a `text` key (a String, or null for a tooltip with no text): the tooltip showing. `x`, `y`, `width`, `height`, when all four are numbers, are its rect in viewport coordinates.

   Anything else (a non-Dictionary, a Dictionary with neither `text` nor `pending: true`) is a malformed tooltip state. Keys are matched exactly, so a C# node returns a `Dictionary<string, object?>` or an anonymous type with these lower-case names.
3. **Godot's own tooltip wins.** The game nodes are read only when no Control owns a Godot tooltip at the pointer (`tooltip_owner` null), including when the pointer is over no Control at all (a tooltip the game draws for a world object). When a Control owns one, `hover` behaves as today.
4. **The read is polled each frame,** first right after the hover settles, then on each `process_frame`, under the same limit as Godot's tooltip: `timeoutMs`, else `gui/timers/tooltip_delay_sec` plus 1 s, at most 10 s, in clip frames in a recording. Every node in the group is read on each check, in tree order, leaving out the bridge's own nodes.
   - A node showing a tooltip ends the wait: the first in tree order is reported.
   - When no node shows one and none is pending, `hover` answers at once, as it does today over a Control with no tooltip (the RichTextLabel near-miss warning still applies).
   - When a node is still pending at the limit: `tooltip: null` and the warning `the game tooltip of <path> was still pending after <n> ms`.
   - A node whose state cannot be read (the state reader's error) or is malformed ends the wait: `tooltip: null` and the warning `<path> is in the mcp_tooltip group but its tooltip cannot be read: <error>` or `<path> is in the mcp_tooltip group but its state is not a tooltip: it has neither text nor pending: true`.
   - A game paused is not special-cased: the state method runs on each frame as `wait_for {state}` runs it.
5. **The result.** `tooltip` is `{text, x, y, width, height, owner, state}` for a game tooltip: `owner` the `mcp_tooltip` node that answered (`{path, class}`), `state` its whole state value as `get_game_state` would read it (so a game field such as `locked` is there), and the four rect keys only when the state gave all four. A Godot tooltip has no `state` key, which is how the two are told apart. `options.tooltip: false` skips the read, as it skips Godot's wait.
6. **The server sends the state helper on every hover** (`extension` or `csharpError`, by `AddStateHelper(parameters, csharp.PrepareForState(projectDir))`), as `wait_for` does for a state condition, so a C# node's `_McpState` is read by the helper. The helper loads only when a C# node is read.

## Steps

1. Bridge, server, fixtures and tests (one brief).
2. Docs: `docs/TOOLS.md` "`hover`", the glossary (`mcp_tooltip` group), `skills/godot-mcp/SKILL.md`, `docs/ARCHITECTURE.md`, `README.md`, a DECISIONS.md entry once landed.
