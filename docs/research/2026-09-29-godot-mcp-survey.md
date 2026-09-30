# Survey of other Godot MCP servers: ideas worth borrowing

Surveyed 2026-09-29. Stars and last-push dates from the GitHub API that day; tool lists from each repo's README unless a line says the code was read.

## Scope and method

- Found through `gh search repos "godot mcp"`, `gh search repos godot-mcp` and `"godot model context protocol"` (about 90 distinct repos, most of them forks of Coding-Solo/godot-mcp or experiments under 20 stars), the Godot Asset Library API (`filter=mcp`, 18 addons for 4.7), and Exa searches for comparison posts (Erodenn's [comparison.md](https://github.com/Erodenn/godot-mcp-runtime/blob/main/docs/comparison.md), the [Flockbay comparison](https://flockbay.com/blog/best-godot-mcp-server/) of 2026-09-28, and a practitioner write-up on [saschb2b.com](https://www.saschb2b.com/blog/godot-mcp-ai-game-creation)).
- Our baseline: `README.md` and the 65 `### tool` headings with their **Does:** lines in `docs/TOOLS.md`; `docs/DECISIONS.md` decision 17 (no editor bridge) and `docs/plans/MAIN.md` line 23 (profiler, autoload-editing and file-parsing tools "if a need shows up") were read so the ideas below do not re-propose settled or already-listed items without saying so.
- Tool counts are the vendors' own and are not audited. Where code was read to confirm a claim, the file and lines are given.

## The servers

| Server | Stars | Last push | Architecture | Tools (self-reported) | Notes |
|---|---|---|---|---|---|
| [Coding-Solo/godot-mcp](https://github.com/Coding-Solo/godot-mcp) | 5884 | 2026-04-16 | Headless CLI, one bundled `godot_operations.gd` | 14 | Launch editor, run, debug output, create scene, add node, load sprite, MeshLibrary, UIDs. No runtime driving. The ancestor of godot-mcp-runtime and of ours. |
| [hi-godot/godot-ai](https://github.com/hi-godot/godot-ai) | 2696 | 2026-09-29 | Editor plugin (WebSocket) + Python server | 46 tools, 120+ ops ([docs/TOOLS.md](https://github.com/hi-godot/godot-ai/blob/main/docs/TOOLS.md)) | Broadest active free one. Domain rollups (`<domain>_manage` with `op`), MCP resources, in-editor test framework, frame-timed `input_sequence`, error "doorbell" on every response, custom tools registered by other addons. C# is text-only. Telemetry opt-out. |
| [yurineko73/Godot-MCP-Native](https://github.com/yurineko73/Godot-MCP-Native) | 811 | 2026-08-03 | Editor plugin that is itself the MCP server (HTTP/stdio), plus runtime probe; `gdmcp` CLI | 155 | Editor debugger integration: breakpoints, stack frames, locals, step/continue; runtime probe; project health audits; test discovery and run. |
| [ee0pdt/Godot-MCP](https://github.com/ee0pdt/Godot-MCP) | 616 | 2025-03-19 | Editor plugin + Node server | ~19 | Stale. Early MCP resources (`godot://script/current`). |
| [youichi-uda/godot-mcp-pro](https://github.com/youichi-uda/godot-mcp-pro) | 614 | 2026-09-24 | Editor plugin (WebSocket) + paid closed Node server; game side via file IPC autoload | 187 | Public repo holds only the addon. Mode tiers (full/3d/lite/minimal), CLI mode with progressive `--help`. Runtime: `watch_signals`, `monitor_properties`, `click_button_by_text`, `find_nearby_nodes`, `move_to`, record/replay, assertions, perf monitors, Android deploy, patch PCK export. |
| [DaxianLee/godot-mcp](https://github.com/DaxianLee/godot-mcp) | 538 | 2026-01-14 | In-editor HTTP addon | n/a | Non-commercial licence; README not studied beyond Erodenn's summary (runs the game, reads errors, no input). |
| [tugcantopaloglu/godot-mcp](https://github.com/tugcantopaloglu/godot-mcp) | 470 | 2026-07-13 | Coding-Solo fork + committed TCP autoload (port 9090) | 157 | Breadth by `game_*` one-liners (lights, sky, CSG, HTTP, websockets, RPC...). Some real extras: raycast, input state, touch, item-level UI controls (`game_ui_tree`, `game_ui_item_list`, `game_ui_tabs`, `game_ui_menu`), audio buses and playing streams, `validate_scripts` over git-changed files, `manage_input_map`, `export_project`, `create_project` (incl. .NET). |
| [tomyud1/godot-mcp](https://github.com/tomyud1/godot-mcp) | 434 | 2026-08-24 | Editor plugin (WebSocket) + npx server | 75 | Browser project visualizer; its own undo. |
| [fennaraOfficial/fennara-godot-ai](https://github.com/fennaraOfficial/fennara-godot-ai) | 297 | 2026-09-29 | Addon + Rust CLI + local daemon; optional in-editor chat | "a small set" | Write-file-returns-diagnostics loop, scene edit scripts, runtime sessions and logs. Strips its autoload on export. |
| [HaD0Yun/Doyunha-Gopeak](https://github.com/HaD0Yun/Doyunha-Gopeak) | 262 | 2026-09-12 | Bun CLI + optional editor and runtime addons; Godot LSP (6005) and DAP (6006) | 95+ | `compact` profile with `tool.catalog` that activates groups on demand; LSP diagnostics/completion/hover/symbols; DAP breakpoints and stack traces. |
| [IvanMurzak/Godot-MCP](https://github.com/IvanMurzak/Godot-MCP) | 261 | 2026-09-27 | C# editor addon over SignalR to a server (cloud relay by default, or local) | 42 | .NET only. Project-defined tools by `[AiToolType]`/`[AiTool]` attributes; C# reflection find/call; runtime mode inside a game build (opt-in); runtime error capture incl. unobserved `Task` exceptions; `screenshot-isolated` renders one node from a chosen angle. |
| [Glade-tool/glade-mcp](https://github.com/Glade-tool/glade-mcp) | 225 | 2026-09-02 | Editor bridge for Unity and Godot | 115 Godot | Gameplay scaffolders (controllers, save system), lexical `find_references`/`rename_symbol`, `run_gameplay_probe` PASS/FAIL, editor main-thread watchdog that names a modal dialog. |
| [satelliteoflove/godot-mcp](https://github.com/satelliteoflove/godot-mcp) | 171 | 2026-09-25 | Editor addon (WebSocket), game reached through Godot's debugger protocol | 21 tools, 86 actions | The most careful playtest design: freeze/step/`step_until` with inputs riding inside the step, `runtime_state` digest with a `_mcp_state()` / `mcp_watch` opt-in convention, watch windows with signal timelines, profiler time series with spike detection, `godot_scene3d` spatial data, `godot_validate_meshes`, version-matched `godot_docs`, read/write tool split, `--read-only`. |
| [3ddelano/gdai-mcp-plugin-godot](https://github.com/3ddelano/gdai-mcp-plugin-godot) | 101 | 2026-09-11 | Paid closed editor plugin ($19) | ~30 | Editor-mediated; screenshots of editor and game; debugger output. Code not public. |
| [wgt19861219/godot-mcp-enhanced](https://github.com/wgt19861219/godot-mcp-enhanced) | 98 | 2026-09-13 | Headless CLI + editor WebSocket + game TCP bridge | 46 tools, 271 actions | `diff_scenes`, `merge_scene` (three-way `.tscn` merge with ext/sub resource id remap), `validate_project` (missing resources, bad `preload` paths, orphan `.import`), GUT `run_tests`, `collision_overlay`, UID scan/fix. Many "runtime" tools act in a throwaway headless context only. |
| [bradypp/godot-mcp](https://github.com/bradypp/godot-mcp) | 90 | 2025-05-31 | Headless CLI | ~10 | Stale; read-only mode. |
| [wangdiandao/godot-devtool](https://github.com/wangdiandao/godot-devtool) | 90 | 2026-06-29 | Node server; native, headless, editor WS and runtime routes | 235 | `tools/list` exposes only a router (`get_capabilities`); safety policy file, audit log, rollback suggestions; `generate_ci_snippet`. About 100 of its tools are "Exact-name compatibility route" aliases of Godot MCP Pro's names. |
| [Erodenn/godot-mcp-runtime](https://github.com/Erodenn/godot-mcp-runtime) | 80 | 2026-09-30 | Injected transient TCP autoload, headless ops | ~30 | The server ours replaced. See "What godot-mcp-runtime had that we do not". |
| [regiellis/godot-mcp-go](https://github.com/regiellis/godot-mcp-go) (Swallowtail) | 65 | 2026-09-26 | Go CLI + editor addon (WebSocket JSON-RPC, also streamable HTTP `/mcp` in the editor); game via file IPC autoloads | 332 commands | CLI first, MCP optional. `res://mcp_commands/*.gd` become CLI commands and MCP tools; MCP prompts (`discover-then-drive`, `bug-hunt`...); `engine doc-search`; `qa run` with baselines and PDF reports. |
| [salvo10f/godotiq](https://github.com/salvo10f/godotiq) | 54 | 2026-08-03 | Python server + addon | 38 | Analysis tools are a paid "Pro" bundle; README is a stub. |
| [NPGameDev/godot-mcp-toolkit](https://github.com/NPGameDev/godot-mcp-toolkit) | 53 | 2026-09-21 | Editor plugin (WebSocket) + npm bridge; runtime autoload on its own port | 112 tools, 150+ ops | GDScript extension API with hot reload; LSP-backed symbols/references; breakpoints; per-call nonce envelopes against prompt injection; read-only mode; audit log; placeholder texture/sound generation. |
| [ryanmazzolini/minimal-godot-mcp](https://github.com/ryanmazzolini/minimal-godot-mcp) | 47 | 2026-09-27 | Bridges the editor's LSP (and DAP console) | few | GDScript diagnostics only. |
| [aigengame/godot-agent](https://github.com/aigengame/godot-agent) (`gda`) | 43 | 2026-09-30 | Python CLI; headless commands + live daemon with an in-game harness | ~70 commands | `scene validate` (static) vs `scene preflight` (boot, run `_ready`); `project find-references`/`dependencies`/`find-unused-resources`; `export run` reports files it left in the project, `export smoke` runs the exported artifact; `perf monitors --frames` with budget verdicts; `game rect` (a Control's layout output). |
| [FunplayAI/funplay-godot-mcp](https://github.com/FunplayAI/funplay-godot-mcp) | 40 | 2026-07-31 | Editor plugin over HTTP | core/full profiles | `execute_code` with default-on safety checks; nothing distinctive for us. |
| [beckettlab/beckett-godot-mcp](https://github.com/beckettlab/beckett-godot-mcp) | 25 | 2026-09-19 | Editor plugin is the MCP server (HTTP); runtime autoload | 55 free, 91 paid | Reflection-generic tools instead of per-domain ones; `render_probe` and `set_debug_draw` (code read, below); validate-before-write for GDScript; `build_csharp`; MCP resources and prompts; audit ring; paid tier adds `click_node3d`/`click_world`, test runner, asset-library installer. |
| [link1345/gua](https://github.com/link1345/gua) | 17 | 2026-09-30 | GDExtension runtime adapter (Godot 4.7, Unity 6) + .NET test packages + `gui-mcp` | n/a | Playwright model: Semantic UI Tree, locators by ID/role/text/state, waits, assertions; World Object Tree opted in through a group plus metadata; per-connection input leases that auto-release; player-profile exposure policy for AI players in release builds. |
| [buildepicshit/Wick](https://github.com/buildepicshit/Wick) | 20 | 2026-08-17 | .NET MCP server + addon | 5 groups | Roslyn-enriched C# exceptions (method body, surrounding lines, caller chain), build diagnostics with source context, Roslyn find-symbol/references. |
| [masteryee-labs/Open-Godot-MCP](https://github.com/masteryee-labs/Open-Godot-MCP) | 13 | 2026-07-23 | Python server + editor addon | ~35 tools, ~130 actions | Multiplayer test harness: launch host/client instances, inject latency/loss/jitter (code read, below), peer simulation; DAP, LSP, profiler; screenshot retention policy; parent-process watchdog against orphaned servers on Windows. |
| [kobolingfeng/godot-mcp-x](https://github.com/kobolingfeng/godot-mcp-x) | 5 | 2026-07-01 | Editor plugin + Node server; runtime autoload dials in | 168 | A rework of Godot MCP Pro: scene-relative paths, source-side paging, non-default properties only, "did you mean" suggestions for types, properties, methods and node paths. |
| [pzalutski-pixel/godotlens-mcp](https://github.com/pzalutski-pixel/godotlens-mcp) | 8 | 2026-07-27 | Editor LSP + DAP (`--editor --headless --lsp-port --dap-port`) | ~15 | `gdscript_references` (engine-resolved call sites, not grep), hover, engine API, sync-file, DAP run/output. |
| [Breakpoint MCP](https://github.com/jlivingston-Cipher/godot-breakpoint-mcp) (Asset Library 5335) | n/a | 2026-09-06 (asset date) | Editor addon + npm host + runtime autoload | 291 | From its Asset Library description only: deterministic playtesting with RNG seeding, LSP (and OmniSharp for C#), DAP (and netcoredbg for C#), a "Pause Agent" toggle. |

Also seen, not studied: docs-only servers ([Nihilantropy/godot-mcp-docs](https://github.com/Nihilantropy/godot-mcp-docs), nuskey8, tkmct, james2doyle), [TransitionMatrix/godot-dap-mcp-server](https://github.com/TransitionMatrix/godot-dap-mcp-server) (DAP only), [Vollkorn-Games/godot-mcp](https://github.com/Vollkorn-Games/godot-mcp) (transient autoload like ours), Summer Engine and Flockbay (their own Godot-based engines, not stock Godot), Ziva (paid in-editor agent).

## Feature matrix

Legend: **Y** has it, **P** partial or a narrow form, **-** none found, **?** unclear from the README. Columns: **Ours**; **CS** Coding-Solo; **GAI** godot-ai; **NAT** Godot-MCP-Native; **PRO** Godot MCP Pro; **TUG** tugcantopaloglu; **SAT** satelliteoflove; **ERO** godot-mcp-runtime; **GOP** GoPeak; **IVM** IvanMurzak; **SWT** Swallowtail; **BEK** Beckett (free tier); **NPT** NPGameDev toolkit. README-level unless the idea list below cites code.

| Capability | Ours | CS | GAI | NAT | PRO | TUG | SAT | ERO | GOP | IVM | SWT | BEK | NPT |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Live editor integration | - (decision 17) | P (launch) | Y | Y | Y | P (launch) | Y | P (launch) | Y | Y | Y | Y | Y |
| Scene and node editing | Y (headless, in-place text) | Y | Y | Y | Y | Y | P (by design) | Y | Y | Y | Y | Y | Y |
| Script create/edit tools | - (agent's own file tools) | - | Y | Y | Y | Y | - | P | Y | Y | Y | Y | Y |
| Script and scene validation | Y (incl. C# build) | - | Y | Y | Y | Y | - | Y | Y | P | Y | Y | Y |
| C# support in runtime tools | Y (`cs_*`, `run_csharp`) | - | - | - | - | P | - | - | - | Y (reflection) | P | P | P |
| Run/stop, output, errors | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y | Y |
| Attach to a running game | Y (arm/attach) | - | P (editor play) | P | P | - | P | Y | ? | - | P | P | P |
| Quiet/background runs | Y | - | - | - | - | - | - | Y | - | - | - | - | - |
| Runtime input (keys, mouse, actions) | Y | - | Y | Y | Y | Y | Y | Y | Y | - | Y | - (paid) | Y |
| Gamepad injection | Y | - | Y | ? | - | Y | Y | - | ? | - | ? | - | ? |
| Semantic UI targets (by name/text) | P (by name/path) | - | P | ? | Y (by text) | - | - | Y (by name) | ? | - | ? | - | ? |
| Screenshots of the game | Y | - | Y | Y | Y | Y | Y | Y | Y | P | Y | Y | Y |
| Video recording | Y (MP4 clips) | - | - | - | P (PNG frames) | - | - | - | - | - | - | - | - |
| Visual regression baselines | Y | - | - | P | P | - | - | - | - | - | Y | - (paid) | - |
| Live tree, properties, calls | Y | - | Y | Y | Y | Y | Y | P | Y | P | Y | Y | Y |
| Run code in the game | Y (GDScript, C#) | - | Y | Y | Y | Y | Y | Y | ? | Y | Y | - | Y |
| Pause, frame step, time scale | Y | - | Y | Y | P | P | Y | - | ? | - | ? | - | ? |
| Step with inputs inside, step-until | P | - | P | - | - | - | Y | - | - | - | - | - | - |
| Wait for condition | Y | - | P | Y | P | Y | Y | - | ? | - | ? | Y | ? |
| Watch values over time | P (one property) | - | - | - | Y | - | Y | - | ? | - | ? | Y | ? |
| Signal emission log over a window | P (one signal wait) | - | - | - | Y | P | Y | - | - | - | - | - | - |
| Input record and replay | Y | - | - | - | Y | - | - | - | - | - | ? | - (paid) | - |
| Random input stress | Y | - | - | - | P | - | - | - | - | - | - | - | - |
| Performance monitors | - | - | Y | Y | Y | Y | Y | - | Y | - | Y | Y | ? |
| Function-level script profiler | - (MAIN.md L23) | - | - | P | - | - | P | Y | - | - | ? | - | - |
| Breakpoints, stack, locals | - (external debugger attach) | - | - | Y | - | - | - | - | Y (DAP) | - | Y | - | Y |
| LSP symbols, references, rename | - | - | P (outline) | P (textual) | - | - | - | - | Y | - | - | - | Y |
| Project settings, autoloads, InputMap | - (MAIN.md L23) | - | Y | Y | Y | Y | P | P (autoloads, settings read) | ? | - | Y | P | Y |
| ClassDB / docs lookup | Y (`describe_class`) | - | Y | Y | - | P | Y (web docs) | - | Y | P | Y | Y | Y |
| Test framework runner | - | - | Y (own) | Y | P (scenarios) | - | - | - | ? | - | Y (qa) | - (paid) | ? |
| Export and build | P (C# build only) | - | - | Y | Y | Y | - | - | Y | - | Y | - (paid) | ? |
| Animation authoring | - | - | Y | P (runtime) | Y | P | Y | - | Y | - | Y | - | Y |
| TileMap / GridMap cells | - | - | Y | P (runtime) | Y | P | Y | - | Y | - | Y | - | Y |
| Shaders, materials, themes | - | - | Y | P | Y | P | - | - | Y | - | Y | - | ? |
| Signals in scene files | Y | - | Y | Y | Y | Y | P | Y | Y | - | Y | Y | ? |
| 3D spatial / render diagnosis | P (`preview_scene` frames 3D) | - | - | - | P | P (raycast) | Y | - | - | P (isolated render) | ? | Y | Y (spatial map) |
| Project-defined custom tools | - | - | Y | - | - | - | P (`_mcp_state`) | - | - | Y | Y | - | Y |
| MCP resources / prompts | - | - | Y (resources) | Y (resources) | - | - | - | - | - | Y | Y (prompts) | Y (both) | ? |
| Tool annotations (read-only hints) | Y | - | ? | ? | - | - | Y (read/write split) | ? | ? | - | ? | Y | Y |

## Ideas we lack, ranked

Ranked by what an agent driving the user's games would reach for, against the cost of building it into the runtime bridge plus headless tools.

1. **A debug channel: script profiler and breakpoints over Godot's remote debugger.**
   - What: rank the most expensive GDScript functions over a window (self and inclusive ms, calls, the worst frame), and set breakpoints, read the stack and its locals, step, continue.
   - Who: [godot-mcp-runtime](https://github.com/Erodenn/godot-mcp-runtime/blob/main/docs/tools.md#profiling-requires-run_project-with-profiling-true) `profile_project`, `start_profiler`, `stop_profiler`; breakpoints in [Godot-MCP-Native](https://github.com/yurineko73/Godot-MCP-Native#debug-3-core--68-advanced) (`set-debugger-breakpoint`, `get-debug-stack-variables`, `debug-step-*`), and through the editor's DAP in GoPeak, Open-Godot-MCP and GodotLens.
   - How there: godot-mcp-runtime binds a loopback listener, launches with `--remote-debug tcp://127.0.0.1:<port>` and decodes the Variant packets itself (`src/utils/profiler.ts`, 884 lines, code read: `net.createServer` L333, `profiler:servers` write L772), answering every break with `continue`. The DAP route needs a running editor.
   - Fit: our server launches every run game, so it can host the same listener with no editor. Godot 4.7.2's `core/debugger/remote_debugger.cpp` carries `debug_enter` (L430), `step`/`next`/`continue` (L459-474), `stack_dump` (L495), `get_stack_frame_vars` (L497), `breakpoint` (L534), `evaluate` (L549), and `main/main.cpp` takes `-b/--breakpoints` (L1824) and `--profiling` (L1526). The profiler half is already MAIN.md line 23. Caveats: a paused break stops the bridge too, so every runtime tool must learn the "paused under a debugger" state we already report for external debuggers; C# breakpoints are not on this channel (a .NET debugger such as the user's own is).

2. **Performance monitors over a frame window, with spike and budget verdicts.**
   - What: FPS, frame/process/physics time, draw calls, objects, nodes, memory, sampled over N frames with percentiles, frames over budget and spikes.
   - Who: [satelliteoflove](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/profiler.md) `godot_profiler` `start`/`get_data` (a ring of the last 300 frames plus whole-run aggregates, histogram, spike detection); [gda](https://github.com/aigengame/godot-agent#live-commands--via-gda-daemon-godot-46-macoslinux) `perf monitors --frames` "with statistics and budget verdicts"; Godot MCP Pro `get_performance_monitors` (addon code read: `pro_inspector.gd` `_cmd_get_performance_monitors` L1761).
   - Fit: a bridge command reading `Performance.get_monitor` each frame, shaped like `monitor_property` and run by the same time module; cheap, and it answers "did my change drop frames" without the full profiler.

3. **Watch several values and signals over a window, returned as one timeline.**
   - What: sample chosen fields of several nodes at a rate and record every emission of chosen signals, then return per-field summaries (start, end, min, max, mean, slope; transitions for strings) and a time-sorted timeline.
   - Who: [satelliteoflove](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/runtime-state.md) `watch_start`/`watch_collect` (a 200-event budget shared fairly per signal); Godot MCP Pro `watch_signals` (code read: `pro_inspector.gd` L478-600 connects a callable per signal on each node, filters by name, logs `{time_ms, node, signal, args}`, disconnects at the end; five or more arguments are dropped).
   - Fit: generalises our `monitor_property` (one property) and `wait_for {signal}` (one signal); belongs in the bridge's time module and composes with `batch_drive`.

4. **A cheap state digest through an opt-in game convention.**
   - What: one call returns positions, velocities, animation state and game-defined data for the nodes that matter, instead of a screenshot.
   - Who: [satelliteoflove](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/runtime-state.md) `digest`: nodes in an `mcp_watch` group or with `func _mcp_state() -> Dictionary`, with a visibility-tier fallback; [Gua](https://github.com/link1345/gua#world-object-tree) World Object Tree: nodes in the `gua_world_object` group with `gua_world_*` metadata.
   - Fit: a runtime tool that calls `_mcp_state()` on opted-in nodes (a C# `_McpState` too, through our C# helper) and falls back to `snapshot_subtree`'s property set. Costs nothing for games that do not opt in; the skill would teach the convention.

5. **Inputs that ride inside a frame step, and step-until with a report.**
   - What: `step` N ms or frames with an input timeline applied inside the stepped window, or `step_until` a GDScript predicate, then re-pause and return `report` expressions evaluated on the last frame.
   - Who: [satelliteoflove](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/game-time.md) `godot_game_time` (its doc notes inputs injected while frozen miss their `is_action_just_pressed` edge, so they must ride inside the step); godot-ai `input_sequence` (frame-numbered action steps) is the running-game form.
   - Fit: our `frame_control step` plus `wait_for {expression}` plus `batch_drive` cover most of it; the gaps are input inside a paused step and one call that stops paused on a predicate and reports. Check `batch_drive` first; this may be an option on `frame_control` rather than a tool.

6. **Render diagnosis: why a 3D node is not on screen, and debug draw modes.**
   - What: `render_probe` walks the visibility chain, world AABB, frustum, far plane and visibility range, layers against the camera's `cull_mask`, per-surface material cull mode and the winding the camera sees; `set_debug_draw` switches the viewport to unshaded, lighting, overdraw, wireframe or normal buffer for the next screenshot. `godot_validate_meshes` finds inside-out winding, dropped triangles, degenerate UVs and NaN normals in ArrayMesh surfaces; `godot_scene3d` returns global transforms and AABBs.
   - Who: [Beckett](https://github.com/beckettlab/beckett-godot-mcp) (code read: `addons/beckett/tools/runtime_observe_tools.gd` L150-167, `addons/beckett/runtime/mcp_runtime.gd` L1802-1858 maps names to `Viewport.DEBUG_DRAW_*` and notes Compatibility ignores wireframe); [satelliteoflove](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/validate-meshes.md) `godot_validate_meshes` and [`godot_scene3d`](https://github.com/satelliteoflove/godot-mcp/blob/main/docs/tools/scene3d.md).
   - Fit: read-only bridge commands; `Viewport.debug_draw` is one property, so `set_property` on the root viewport may already reach it (to test). The cheapest step is documentation: `--debug-collisions`, `--debug-paths`, `--debug-navigation`, `--debug-avoidance` (4.7.2 `main/main.cpp` L670-673) already pass through `engineArgs`.

7. **Aim input at 2D and 3D world nodes, not only Controls.**
   - What: a click or hover target given as a Node2D/Node3D, projected to viewport coordinates through the active camera.
   - Who: Beckett's paid tier `click_node3d` / `click_world` (closed, not verified); Godot MCP Pro `find_nearby_nodes` (code read: `pro_inspector.gd` L1122-1220, a radius search around a point).
   - Fit: a `{node}` target beside `{element}` and `{x, y}`: the bridge maps `Camera3D.unproject_position` or the canvas transform to viewport coordinates, then the existing hit check applies. Cheap, and it removes coordinate guessing from screenshots in world-space games.

8. **Semantic UI targets by visible text, and item-level access inside list Controls.**
   - What: target `{text: "Start"}` (optionally a class or role) instead of a path; and reach the rows of a `Tree`, the items of an `ItemList`/`OptionButton`, the tabs of a `TabBar`, the entries of a `PopupMenu`, which are drawn inside one Control and so have no path.
   - Who: Godot MCP Pro `click_button_by_text` (code read: `pro_inspector.gd` L1013-1080, first `Button` whose text matches, partial by default) and `assert_screen_text`; [Gua](https://github.com/link1345/gua) locators by ID, role, text, value and state; tugcantopaloglu `game_ui_tree`, `game_ui_item_list`, `game_ui_tabs`, `game_ui_menu` (README only).
   - Fit: `get_ui_elements` already reports `text`; a text target is a lookup over the same walk with our ambiguity refusal. Items would add rects from `Tree.get_item_area_rect`, `ItemList.get_item_rect`, `TabBar.get_tab_rect` (method names to check against 4.7.2) and an `{element, item}` target. `hover` already reads embedded `PopupMenu` item tooltips by point.

9. **Hear the game: which sounds played.**
   - What: list the playing `AudioStreamPlayer`/`2D`/`3D` nodes with stream and position, and the bus levels.
   - Who: tugcantopaloglu `game_get_audio` ("audio bus layout and playing streams"); Godot-MCP-Native `list-runtime-audio-buses`/`get-runtime-audio-bus`. The saschb2b write-up lists "Complete silence" among an agent's walls.
   - Fit: a read tool (or a `wait_for {audio}` condition) so an agent can check "the hit sound fired" while the run is quiet or muted (#50). Unverified: whether bus peak levels read anything under the Dummy audio driver a quiet run uses; the playing-players list does not depend on it.

10. **Test framework runs with parsed results.**
    - What: run a project's GUT or gdUnit4 suites headless and return pass/fail per test.
    - Who: godot-ai `test_run` (its own `McpTestSuite` framework in `plugin/addons/godot_ai/testing/`, files seen in the tree); Godot-MCP-Native `list-project-tests`/`run-project-tests`; godot-mcp-enhanced `run_tests` (GUT); Swallowtail `qa run`.
    - Fit: a headless tool running the installed framework's own CLI through the gate and parsing its JUnit XML; no framework of our own. Worth it only if the user's games use GUT or gdUnit4 (to ask).

11. **Project config tools: InputMap actions, autoloads, settings.**
    - What: add, change and remove InputMap actions with correctly serialised events, autoloads and settings.
    - Who: godot-mcp-runtime `list_autoloads`/`add_autoload`/`remove_autoload`/`update_autoload`, `get_project_settings` (dropped by us); gda `project add-input-action` (keys, joy buttons, axes, deadzone, physical); godot-ai `input_map_manage`, `autoload_manage`. tugcantopaloglu's 3.1 notes show why it is not trivial: its earlier InputMap writes produced duplicate `action=` lines that malformed `project.godot`.
    - Fit: headless tools using `ProjectSettings.save()`; already MAIN.md line 23. The real value is the `InputEvent` serialisation; an agent can edit plain settings by hand.

12. **Signal wiring checks in `validate`, and a boot preflight.**
    - What: report scene connections whose method exists on no target, targets outside the scene, and `_on_*` handlers nothing connects to; separately, boot a scene and run `_ready` to catch first-frame failures.
    - Who: godot-mcp-runtime `validate` `checks: [{type: "signals"}]` and `{type: "structure"}` (schema of expected node types and properties) ([docs/tools.md](https://github.com/Erodenn/godot-mcp-runtime/blob/main/docs/tools.md#validation-validate)); gda `scene validate` versus `scene preflight`.
    - Fit: a `signals` check is a headless walk of the instantiated scene we already load in `validate`; `preview_scene` already boots a scene and returns its errors, so preflight is mostly covered.

13. **Project-defined tools.**
    - What: a game declares its own agent-callable commands (spawn a boss, grant items, jump to a level) with descriptions and typed parameters, listed and called like built-ins.
    - Who: Swallowtail `res://mcp_commands/*.gd`; godot-ai `custom_manage` (third-party addons register, promoted ones become `custom_<name>` tools); IvanMurzak `[AiToolType]`/`[AiTool]` C# attributes; NPGameDev extension API with hot reload; gda's `GDA_CALLABLE` allow-list for `game call`.
    - Fit: `call_method` and `run_script` already reach any method; what is missing is discovery. A small version: a `list_game_commands` tool reading a convention (a `mcp_commands` group or a static list on a node) with descriptions, no dynamic MCP tools.

14. **Source context on errors, C# especially.**
    - What: an error or exception arrives with the failing method's body, a few lines around the line, and the caller chain.
    - Who: [Wick](https://github.com/buildepicshit/Wick) (Roslyn); IvanMurzak captures unobserved `Task` exceptions.
    - Fit: our `errors` carry file, line and stack; adding a short source excerpt per frame is server-side file reading, both languages. Check first whether our C# path already sees unobserved `Task` exceptions.

15. **Export and smoke the exported build.**
    - What: export a preset, report what the export left in the project, and run the artifact headless and bounded.
    - Who: gda `export run` / `export smoke`; tugcantopaloglu and Godot MCP Pro `export_project`; Godot MCP Pro `export_patch_pck`.
    - Fit: a headless tool under the gate. Unverified: whether an exported build reads an `override.cfg`, which decides if the bridge can drive it.

16. **Project analysis: references, dependencies, unused and missing resources.**
    - Who: gda `project find-references`, `dependencies`, `find-unused-resources`; Godot-MCP-Native `scan-missing-resource-dependencies`, `scan-cyclic-resource-dependencies`; Godot MCP Pro `find_unused_resources`, `detect_circular_dependencies` (names present in `analysis_commands.gd` L7-11).
    - Fit: headless `ResourceLoader.get_dependencies` walk; low priority, since an agent can grep `res://` and `uid://` references and `validate` already catches broken loads.

17. **Three-way `.tscn` merge.**
    - What: resolve a git conflict in a scene by remapping `ExtResource`/`SubResource` ids.
    - Who: godot-mcp-enhanced `merge_scene` and `diff_scenes` (README only).
    - Fit: our in-place scene writer already understands sections and ids; only worth it if the user's repos hit scene conflicts.

18. **Multiplayer network conditions.**
    - What: inject latency, loss and jitter between instances.
    - Who: Open-Godot-MCP (code read: `addons/open_godot_mcp/runtime/network_conditioner.gd`, a `MultiplayerPeerExtension` wrapping the real peer and queueing packets).
    - Fit: we already run sessions side by side; a conditioner only affects the high-level multiplayer API. Niche unless a user game is networked.

19. **Seeded runs.**
    - What: seed the global RNG at launch so a run replays.
    - Who: Breakpoint MCP (Asset Library description only).
    - Fit: the bridge could call `seed(n)` early, but `randomize()` in game code and separate `RandomNumberGenerator` instances escape it, so it would promise less than it says. Low.

20. **GDScript language-server queries.**
    - What: engine-resolved references, hover, rename.
    - Who: GodotLens, GoPeak, NPGameDev, minimal-godot-mcp; all need an editor (a `--editor --headless --lsp-port` process works).
    - Fit: decision 17 rules out an editor bridge, not a headless LSP process, but it is a second long-lived Godot per project and Claude Code has its own code tools. Low.

## What godot-mcp-runtime had that we do not

From its [docs/tools.md](https://github.com/Erodenn/godot-mcp-runtime/blob/main/docs/tools.md):

- Profiling (`profile_project`, `start_profiler`, `stop_profiler`): idea 1.
- Project config without a Godot process (`list_autoloads`, `add_autoload`, `remove_autoload`, `update_autoload`, `get_project_settings`, `get_project_files`, `search_project`, `get_scene_dependencies`): idea 11 and 16; files and search are the agent's own tools.
- `validate` `checks` for scene structure against a schema and for signal wiring: idea 12.
- `simulate_input` per-action results: `hit`, `signals` fired by a clicked Control within the settle frame, `focus`, the resulting `value` of a focused `LineEdit`, a `changes` delta of visible Controls, and `still_held`. Ours has `pressedOn`/`releasedOn`/`hoveredOn`, `heldButtonMask` and `wait_for {uiChanged}`; the fired-signals list and the text value are not reported.
- A `run_script`/`run_project` security gate: a tiered static scan of GDScript before it runs, MCP elicitation prompts, and a strict mode (`GODOT_MCP_STRICT`) for unattended use.
- `launch_editor`, `list_projects`, `check_project`.

## Where we are already ahead

For balance, what no surveyed server matched: C# runtime access (`cs_members`, `cs_get`, `cs_set`, `cs_call`, `run_csharp`) beyond IvanMurzak's reflection calls; MP4 recordings with marks and idle drop; quiet runs that keep real input out; arming a folder and joining a game already running; `stress_input` with seeded replay; tooltip reading through `hover`; pad injection on a free device id; errors riding on every runtime result (godot-ai's `new_errors_since_last_call` doorbell is the nearest, and only a count); scene edits that keep the file's own text; load-adjusted timeouts.

## Hype and thin wrappers

- tugcantopaloglu's 157 tools: most `game_*` creation tools (`game_sky`, `game_csg`, `game_light_3d`, `game_http_request`, `game_websocket`, `game_rpc`, `game_debug_draw`...) are one node creation or property set each, reachable through `run_script` or `call_method`.
- Godot MCP Pro `move_to` and `navigate_to` are game-specific heuristics (code read: `pro_inspector.gd` L1221-1470): `move_to` holds `KEY_W` (and Shift to run) and turns a `SpringArm3D` or camera parent toward the target; `navigate_to` suggests WASD keys and assumes "~5 units/sec" (L1307-1309) and "400px mouse movement ≈ PI radians" (L1311-1313). Its `run_test_scenario` and `assert_screen_text` are sequences over existing commands (`pro_test_commands.gd` L26, L197).
- godot-devtool's 235 tools include about 100 whose description is "Exact-name compatibility route for ..." (its README table), aliases of Godot MCP Pro's names.
- godot-mcp-enhanced's "runtime" TileMap, particle, navigation and AnimationTree tools act in a throwaway headless context and "do not persist to the .tscn" (its own notes); `validate_gdd` and `chain_verify` are prompt helpers, not engine tools.
- Particle, lighting and animation "presets" (fire, smoke, shake, pulse) in Godot MCP Pro, godot-ai and others, and Glade's gameplay scaffolders, are content templates rather than capabilities.
- Open-Godot-MCP's Agnes/NVIDIA vision and generation tools upload local files to a third-party host (uguu.se) for the vision call (its README); off by default.
- Tool-count marketing in general: several READMEs compare raw counts; Beckett, satelliteoflove and NPGameDev argue the other way (fewer, reflection-generic tools), which matches our design.
- Tool-surface budgeting (GoPeak `compact` + `tool.catalog`, Godot MCP Pro's modes, godot-devtool's router-only `tools/list`, NPGameDev's on-demand groups) works around clients with 100-tool caps. Claude Code defers tool loading, so at 65 tools this is not a need for us.

## Not verified

- Godot MCP Pro's server is closed; only its public addon was read. GDAI MCP and Beckett's paid tier are closed; their tools are taken from READMEs.
- GoPeak, NPGameDev, Swallowtail, godot-mcp-enhanced, Glade and tugcantopaloglu tool claims (including the item-level UI tools and `game_get_audio`) are README-level; no code was read.
- godot-ai's `test_run` and custom tools: files seen in the repo tree, behaviour not run.
- Whether `AudioServer` bus peak levels read anything under the Dummy audio driver (idea 9).
- Whether an exported build reads `override.cfg` (idea 15).
- The `Tree`/`ItemList`/`TabBar` rect method names in 4.7.2 (idea 8).
- Whether our C# path reports unobserved `Task` exceptions (idea 14).
- Breakpoint MCP was read from its Asset Library description only.
