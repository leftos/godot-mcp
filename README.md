# godot-mcp

An [MCP](https://modelcontextprotocol.io) server that lets a coding agent (Claude Code, or any MCP client) run your Godot 4 game, see it, play it, inspect and change it while it runs, and edit its scenes, for GDScript and C# projects alike. It injects a small bridge into the game when it launches it, and never touches your project's tracked files.

What changed in each version is in [CHANGELOG.md](CHANGELOG.md).

## What your agent can do with it

**Run and watch the game**

- Launch the game (or one scene), restart it, stop it, or attach to a game you started from the editor, or (once the folder is armed) to one already running; run several sessions side by side.
- Launch quietly by default: off-screen, unfocused, silent and deaf to your own mouse, keyboard and pads, so the agent can play while you work.
- Preview a single scene as a picture without playing it, 2D or 3D, framing a 3D scene that has no camera.
- Take screenshots, whole or cropped, or a series at set moments of an animation in one call, and save baselines to catch visual regressions.
- Record a run to video and cut the marked moments into MP4 clips, with the idle time between the agent's steps left out on request.
- Read the game's errors with file, line and stack on every call, plus its full output log.

**Play it**

- Click, drag, hover (and read the tooltip), scroll with the wheel or a trackpad pan, type text and press keys, aimed at a Control by name, at a 2D or 3D world node through its camera, or at viewport coordinates.
- Press InputMap actions, and drive virtual gamepad buttons, sticks and triggers.
- Replay arbitrary input event sequences, record what a person played and replay it, and throw random input at the game to find what breaks it.
- Pause, step single frames and change the time scale, so a check lands on an exact frame.
- Wait for a condition (a node appears, a property changes, a signal fires) instead of sleeping.

**Look inside it while it runs**

- Browse the live scene tree, list the UI with its on-screen rects, and inspect any node's properties.
- Set properties, call methods, and run a GDScript snippet in the game.
- Watch a value over frames, or snapshot a subtree before and after an action and diff the two.
- Look up any class's methods, properties and signals.
- In C# games: list a C# object's members (private ones and overloads included), read and set members Godot cannot reach, call any method (generics, records, async `Task`s), and run a C# snippet that uses the game's own types.
- Run a whole scripted sequence of steps and checks in one call.

**Edit the project without running it**

- Check that scripts and scenes load, including a C# file through the real build, with compiler errors quoted.
- Create and save scenes; add, delete, duplicate, reorder and reparent nodes, touching only the lines an edit changes; attach scripts; load sprites; set properties; connect and disconnect signals; many edits in one batch.
- Read a scene file's node tree, a node's properties and its signal connections.
- For C# projects, build the game's assembly and run Godot's import first when either is stale.

The full guide to every tool, with the edges that bite, is [docs/TOOLS.md](docs/TOOLS.md).

## Requirements

- Windows 10 or 11 (the server is built for win-x64).
- [Godot 4.7.2](https://godotengine.org/download), the console executable (`..._console.exe`); the .NET edition if your game uses C#.
- The [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0); the installer offers to install it through winget when it is missing.
- Optional: [ffmpeg](https://ffmpeg.org) on `PATH` (`winget install Gyan.FFmpeg`), to cut recordings into clips.
- An MCP client; the steps below use [Claude Code](https://claude.com/claude-code).

## Install it: tell your agent

Paste this into Claude Code, in the folder of the Godot game you want it to drive, with the Godot path filled in:

```text
Install the godot-mcp server for this project:
1. Run this in PowerShell (it downloads the latest release into %LOCALAPPDATA%\godot-mcp, installs the .NET 10 runtime through winget if it is missing, copies the godot-mcp and godot-agent-sweep skills into ~/.claude/skills, and brings the server's tools to every agent marked for them):
   & ([scriptblock]::Create((irm https://github.com/leftos/godot-mcp/releases/latest/download/install.ps1))) -InstallDotNet
   It must end with "install: server at ... (version ...)" and print a `claude mcp add` line.
2. Run that `claude mcp add` line from this project's folder, with GODOT_PATH set to <path to Godot_v4.7.2-stable_win64_console.exe>.
3. Tell me to restart Claude Code, then check that the `mcp__godot__*` tools are listed and load the godot-mcp skill before the first call.
```

After the restart, ask for anything in the list above ("run the game and click Start", "why does the inventory close when I drag an item?"). The agent loads the `godot-mcp` skill, which teaches it the drive loop.

To update, run the same install line again, and restart the agent session to pick up the new server. `-Version X.Y.Z` installs a given release instead of the latest; `-ZipPath <file>` installs a downloaded zip offline. Every release, with what changed, is on the [releases page](https://github.com/leftos/godot-mcp/releases).

### From source

To build it yourself, you need the [.NET 10 SDK](https://dotnet.microsoft.com/download), PowerShell 7 (`pwsh`), and Visual Studio Build Tools 2022 with the C++ workload (for the C# helper's native shim). Clone the repository and run `pwsh run.ps1 install` in it: it builds the server into the same `%LOCALAPPDATA%\godot-mcp` and links the skill folder back to the clone, so `git pull` and `pwsh run.ps1 install` update both. Then register it as in step 2.

## Configuration

- `GODOT_PATH`: the Godot executable the server launches. When it is not set, the server takes a `Godot*console*.exe` from the first `PATH` folder that has one; with neither, it refuses and says how to set it. `run_project`'s result names the Godot it used.
- `FFMPEG_PATH`: ffmpeg for recordings, when it is not on `PATH`.
- A `godot-mcp.json` beside `project.godot` sets the project's launch defaults (scene, arguments, window size, quiet) and named presets; see [docs/TOOLS.md](docs/TOOLS.md#project-profiles-godot-mcpjson).

## Working on godot-mcp itself

Start at [docs/README.md](docs/README.md) (the map and the glossary) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (toolchain, commands, tests). Agents working in this repository follow [CLAUDE.md](CLAUDE.md).

## License

See [LICENSE](LICENSE).
