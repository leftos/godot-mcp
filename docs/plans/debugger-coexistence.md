# Handoff: make godot-mcp coexist with a live debugger (DebugMCP + netcoredbg)

Written 2026-09-26 by a session in `C:\Users\lefto`, which read godot-mcp's code but ran nothing in it. Every godot-mcp claim below is from reading, with its `path:line`; confirm each before designing on it.

## Context

The user's Godot C# projects (opening-hand, delve-the-dungeon, in-the-sky) now register a second MCP server beside `godot`: `debugmcp`, Microsoft DebugMCP's standalone CLI (`npm i -g debugmcp`, v0.1.0), driving Samsung's `netcoredbg` 3.2.0 as the C# debug adapter. Its tools are `mcp__debugmcp__*` (breakpoints, logpoints, stepping, variables, evaluate). The user-level skill `~/.claude/skills/debug-live/SKILL.md` documents how it is set up; read it first.

Two DebugMCP facts, read from its source at commit `df6f2e8`, shape the work:

- `start_debugging` takes only a file, a working directory and an adapter name. `request: "attach"` and `processId` come from the adapter registration (`debugmcp adapter add ... --launch '<json>'`), so attaching to a new game means re-registering with its pid first (`src/cli/cliConfigurationManager.ts`).
- `stop_debugging` disconnects with `terminateDebuggee: true` whether it launched or attached (`src/cli/cliDebuggingExecutor.ts:123-128`): **stopping the debugger kills a game godot-mcp holds.**

No breakpoint has yet been hit in a Godot game this way; the first end-to-end attempt is part of this work.

Checked in godot-mcp (2026-09-26, 4da6c65): `list_sessions` prints `session.ProcessId` (`SessionRegistry.cs` L558, `SessionInfo` L603), the wrapper's pid for a run and null for an attach; the hello's `GameProcessId` is not printed. `DesktopProcess` does create Godot suspended and resumes it with `ResumeThread`.

## The three changes

The decision on each is yours and the user's; this names the problem and what was found.

1. **Every session reports the game's pid.** An agent needs the pid to register the attach. `list_sessions` reports "its process id (null for an attached game)" (`src/GodotMcp.Server/Tools/ProjectTools.cs:198`), yet the bridge's hello carries the game's own pid (`bridge/godot_mcp_bridge.gd:219`), read into `GodotSession.GameProcessId` (`src/GodotMcp.Server/Session/GodotSession.cs:60`, set at `:358`). `GodotSession.cs:55` says `ProcessId` is a wrapper, not the game, so check which of the two `list_sessions` prints for a launched run too: the attach needs the game's.
2. **A game paused at a breakpoint is reported as paused, not stuck.** At a breakpoint netcoredbg freezes every thread, the bridge's included. A timed-out request then runs `HangProbe` (2 s ping, `src/GodotMcp.Server/Session/HangProbe.cs:47`), which reports the game as not answering, and the stop path treats an unanswered ping as a stuck main thread (`GodotSession.cs:454-472`). An agent reading that restarts a game that is only paused. The idea on the table: before calling a game hung, check whether a debugger is attached to its process (`CheckRemoteDebuggerPresent`, or the PEB flag), and when one is, fail the tool fast with "paused under a debugger; continue it before driving the game" instead of waiting out the timeout. Also decide whether `stop_project` / `restart_project` should refuse or warn while a debugger is attached.
3. **A launched game can wait for the debugger (only if an early breakpoint is needed).** A breakpoint in `_Ready` or an autoload runs before any attach lands. `DesktopProcess` already creates Godot suspended (`src/GodotMcp.Server/Session/DesktopProcess.cs:41`, `:70`, `:261`); an option could hold it there until a debugger attaches, with a timeout. The alternative that needs no godot-mcp change is to launch Godot under netcoredbg (a DebugMCP registration whose `program` is the Godot .NET executable, `args` carrying `--path <project>`) and then `attach_project` to it; try that first and build this only if it falls short.

## Decided (user, 2026-09-26, each the recommended option)

1. `list_sessions` keeps `processId` (the wrapper, which `stop_project` kills) and adds `gameProcessId`, the hello's pid, for runs and attaches alike.
2. Before each runtime call, when a debugger is attached to the game's pid (`CheckRemoteDebuggerPresent`), a short ping (about 500 ms) goes first; no answer fails the call at once with "paused under a debugger; continue it before driving the game". A game with no debugger attached is untouched.
3. `stop_project` and `restart_project` proceed while a debugger is attached, and their result carries a warning that the debugger's session ended with the game.
4. Change 3 waits on the trial: launch Godot under netcoredbg through DebugMCP, then `attach_project` to it; a hold-until-attached option is built only if that falls short.

## Out of scope

Wrapping or proxying DebugMCP's tools inside godot-mcp, or adding a debugger of its own: the two servers stay separate.

## Done means

- A written trial: a game from `run_project`, its pid from `list_sessions`, a DebugMCP attach registration, a breakpoint in a C# script hit, a godot tool call made while paused (and what it returned), `continue_execution`, the game driven again. Record the result in the `debug-live` skill's "A Godot game" section, replacing its "untested" note.
- Changes 1 and 2 landed with tests, or filed as issues on `leftos/godot-mcp` if the user defers them; change 3 decided either way.
- Any DebugMCP or netcoredbg defect found is reported to the user, not worked around silently.
