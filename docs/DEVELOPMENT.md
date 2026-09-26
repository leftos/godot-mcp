# Development

## Toolchain

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401 (`global.json`, `latestFeature`) | test runner: Microsoft.Testing.Platform |
| Godot | 4.7.2 stable | `$env:GODOT_PATH`, else `F:\Godot\Godot_console.exe`; the server and the tests look it up the same way (`Session/Installation.cs`) |
| CSharpier | 1.3.0 (local tool) | `dotnet tool restore` |
| gdlint | gdtoolkit 4.5.0 | `uv tool install "gdtoolkit>=4,<5"` |
| prek | any | `prek install` once per clone |

Package versions live in `Directory.Packages.props` (looked up on nuget.org 2026-09-25): ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting 10.0.12, xunit.v3 4.0.1 (MTP v2 by default; no Microsoft.NET.Test.Sdk or xunit.runner.visualstudio, which serve only VSTest).

## Commands

Everything runs from the repo root through `run.ps1`, and every command runs under `tools/gate.ps1` (`pwsh tools/gate.ps1 -Log <path> -TimeoutSeconds <n> [-Tail <n>] -- <command>`). A gate writes `.tmp/<name>.log`, prints the tail and ends with `gate: passed.` or `gate: FAILED (status n).`. A command that reaches its ceiling is killed with its process tree and exits 124 with `gate: TIMED OUT`. That means it hung, not that it was slow: read the log, never raise the ceiling.

| Command | What it runs | Ceiling | Measured (2026-09-25) |
|---|---|---|---|
| `pwsh run.ps1 build` | `dotnet build GodotMcp.slnx -warnaserror` | 300 s | 1-4 s warm |
| `pwsh run.ps1 test [-Filter "*Class"]` | unit tests (`tests/GodotMcp.Tests`) | 180 s, plus MTP `--timeout 3m` | 26 tests, about 2 s |
| `pwsh run.ps1 itest [-Filter "*Class"]` | integration tests against the real Godot (`tests/GodotMcp.IntegrationTests`) | 300 s, plus MTP `--timeout 4m` | 21 tests, about 44 s |
| `pwsh run.ps1 format` | `dotnet format style --severity info`, then CSharpier | 180 s per pass | |
| `pwsh run.ps1 publish` | framework-dependent win-x64 to `bin/publish/godot-mcp.exe`, with `bin/publish/bridge/` beside it | 300 s | |
| `prek run --all-files` | CSharpier check, `dotnet build -warnaserror`, unit tests, gdlint on `bridge/` and `headless/` | | sees tracked files only |

`-Filter` becomes `--filter-class`; a class filter needs the full name or a wildcard.

## Tests

- **Unit** (`tests/GodotMcp.Tests`): frame encoding and reassembly, the handshake's refusals (`HandshakeExpectationTests`: a wrong token, a wrong project path, a first frame that is not a hello), `override.cfg` write/refuse/replace/remove, the `.git/info/exclude` line in a repo, in a worktree and under 8.3 short names (`git rev-parse --show-prefix`), Godot command-line building.
- **Integration** (`tests/GodotMcp.IntegrationTests`): each test copies `tests/fixtures/InputProbe` to a `git init`ed temp folder under `%TEMP%\godot-mcp-tests\`, launches the real Godot and checks the tree is clean after stop. `SessionLifecycleTests` covers launch, arguments, stop, a refused `override.cfg` and background runs. `RuntimeReadTests` covers screenshots (full, crop, preview, path_only), `get_ui_elements`, and `run_script` values, parse errors and runtime errors. `InputTests` covers a drag that drops, a drag under the 10 px threshold that does not, a drag and clicks (by element and by point) in a letterboxed window (`--resolution 1000x900`), `type_text "Hello World!"`, Shift held and released, and a drag assembled from `mouse_button` and raw `simulate_input` motions. `SessionHarness` is `IAsyncDisposable`: disposing it stops a live run and waits for it to exit, because `Godot_console.exe` exits before the `Godot.exe` it wraps releases the probe folder. Parallelism is off in `xunit.runner.json` (`"parallelMode": "none"`): the xUnit 4.0.1 assembly attributes the docs name do not compile (`CollectionBehavior` is CS0619, `Parallelization` does not exist). `McpServerSmokeTests` drives the built server over stdio with the SDK's `McpClient`.
- Shared helpers are in `tests/GodotMcp.TestSupport` (a library, not a test project).

## Footguns

- **A child process of the server must not inherit its stdin.** stdin is the MCP pipe; on Windows a child started while the server blocks reading it stalls until the read returns (`run_project` hung until stdin closed). Godot and git are started with their own stdin, closed at once.
- **Only an `McpException`'s message reaches the client.** Any other exception becomes "An error occurred invoking '<tool>'.", so tools rethrow session errors as `McpException`.
- **Logging goes through `[LoggerMessage]` methods in `Log.cs`**: CA1848 and CA1873 are errors under `latest-recommended`. Everything logs to stderr; stdout is the protocol.
- **GDScript's JSON parser reads every number as a float**, so the bridge echoes `int(id)` and the server accepts integral floats as ids.
- **Cyclomatic complexity is capped at 8** by CA1502, a warning and so a build error. The threshold is in `CodeMetricsConfig.txt` at the root, which `Directory.Build.props` passes to every project as AdditionalFiles: the analyzer reads that file, not `.editorconfig`.
- **`run_script` waits about 0.5 s longer when a script returns null**, while it polls stderr for errors the script may have raised.
- **Screenshots are saved under `<project>/.godot/godot-mcp/screenshots/`**, which both projects already ignore, as `<UTC stamp>.png` (plus `_preview.png` when scaled). A crop is in the screenshot's own pixels, which are viewport pixels when the project does not stretch.
- **Input points are viewport coordinates**, converted to window coordinates in one place (`_to_window` in the bridge) through `get_viewport().get_screen_transform()`. For the root window it equals `get_final_transform()`; measured in a 1000x900 window over 640x360 content: `[X: (1.5625, 0), Y: (0, 1.561111), O: (0, 169)]`, a 169 px bar above and below.
- **Every injected event is a new object followed by `Input.flush_buffered_events()`**, so accumulated input neither merges nor delays it (Godot merges consecutive motions with the same mask, `input_event.cpp` L1031-1075, and warns when one object is sent twice in a frame, `input.cpp` L1540).
- **A real cursor over the game window can interleave with injected input** and break a gesture: one letterboxed click failed that way while the tests were written. Isolating injected input is the next step's work.
- **`run_project` has six parameters** (five in the tool schema plus a `CancellationToken`): the SDK builds the tool's flat schema from the method signature, so nesting them would change the schema agents see.
