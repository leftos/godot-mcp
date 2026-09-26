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

Everything runs from the repo root through `run.ps1`. Each command writes `.tmp/<name>.log`, prints its last 15 lines and exits with the command's own status.

| Command | What it runs | Limit | Measured (2026-09-25) |
|---|---|---|---|
| `pwsh run.ps1 build` | `dotnet build GodotMcp.slnx -warnaserror` | none yet | 1-4 s warm |
| `pwsh run.ps1 test [-Filter "*Class"]` | unit tests (`tests/GodotMcp.Tests`) | `--timeout 3m` | about 5 s |
| `pwsh run.ps1 itest [-Filter "*Class"]` | integration tests against the real Godot (`tests/GodotMcp.IntegrationTests`) | `--timeout 4m` | about 21 s; `SessionLifecycleTests` 8 s, `McpServerSmokeTests` 13 s |
| `pwsh run.ps1 format` | `dotnet format style --severity info`, then CSharpier | none yet | |
| `pwsh run.ps1 publish` | framework-dependent win-x64 to `bin/publish/godot-mcp.exe`, with `bin/publish/bridge/` beside it | none yet | |
| `prek run --all-files` | CSharpier check, `dotnet build -warnaserror`, unit tests, gdlint on `bridge/` and `headless/` | | sees tracked files only |

`-Filter` becomes `--filter-class`; a class filter needs the full name or a wildcard.

## Tests

- **Unit** (`tests/GodotMcp.Tests`): frame encoding and reassembly, `override.cfg` write/refuse/replace/remove, the `.git/info/exclude` line in a repo and in a worktree, Godot command-line building.
- **Integration** (`tests/GodotMcp.IntegrationTests`): each test copies `tests/fixtures/InputProbe` to a `git init`ed temp folder under `%TEMP%\godot-mcp-tests\`, launches the real Godot and checks the tree is clean after stop. Parallelism is off in `xunit.runner.json` (`"parallelMode": "none"`): the xUnit 4.0.1 assembly attributes the docs name do not compile (`CollectionBehavior` is CS0619, `Parallelization` does not exist). `McpServerSmokeTests` drives the built server over stdio with the SDK's `McpClient`.
- Shared helpers are in `tests/GodotMcp.TestSupport` (a library, not a test project).

## Footguns

- **A child process of the server must not inherit its stdin.** stdin is the MCP pipe; on Windows a child started while the server blocks reading it stalls until the read returns (`run_project` hung until stdin closed). Godot and git are started with their own stdin, closed at once.
- **Only an `McpException`'s message reaches the client.** Any other exception becomes "An error occurred invoking '<tool>'.", so tools rethrow session errors as `McpException`.
- **Logging goes through `[LoggerMessage]` methods in `Log.cs`**: CA1848 and CA1873 are errors under `latest-recommended`. Everything logs to stderr; stdout is the protocol.
- **GDScript's JSON parser reads every number as a float**, so the bridge echoes `int(id)` and the server accepts integral floats as ids.
- **`run_project` has six parameters** (five in the tool schema plus a `CancellationToken`): the SDK builds the tool's flat schema from the method signature, so nesting them would change the schema agents see.
